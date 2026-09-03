// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.Extensions.Options;
using PageForge.Api.Data;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;

namespace PageForge.Api.Services;

/// <summary>
/// Real, engine-backed OCR processor (FR-BATCH-01 "live"). Fetches a job item's
/// source document version from the blob store, runs local OCR via the shared
/// MuPDF+Tesseract engine to produce a searchable PDF (or a .docx carrying the
/// recognized text plus page images), and returns the artifact bytes for
/// persistence as a new document version.
///
/// <see cref="OcrTargetFormat.SearchablePdf"/> and <see cref="OcrTargetFormat.Docx"/>
/// are produced by the local engine; the other <see cref="OcrTargetFormat"/>
/// targets are reported as unsupported rather than silently succeeding.
/// </summary>
public sealed class MuPdfOcrJobProcessor : IOcrJobProcessor
{
    private readonly IBlobStorage _blobs;
    private readonly SyncOptions _sync;

    public MuPdfOcrJobProcessor(IBlobStorage blobs, IOptions<SyncOptions> sync)
    {
        _blobs = blobs;
        _sync = sync.Value;
    }

    public async Task<OcrItemResult> ProcessAsync(
        OcrJob job, OcrJobItem item, DocumentVersion version, CancellationToken cancellationToken)
    {
        if (job.TargetFormat != OcrTargetFormat.SearchablePdf &&
            job.TargetFormat != OcrTargetFormat.Docx)
        {
            return new OcrItemResult(
                0,
                $"The local engine currently supports only SearchablePdf and Docx " +
                $"output; conversion to {job.TargetFormat} is not yet available.");
        }

        string? sourcePath = null;
        string? targetPath = null;
        OcrResult? ocrResult = null;
        try
        {
            sourcePath = await DownloadToTempAsync(version, cancellationToken);

            string workDir = Path.Combine(Path.GetTempPath(), "pageforge-ocr");
            Directory.CreateDirectory(workDir);
            string extension = job.TargetFormat == OcrTargetFormat.Docx ? ".docx" : ".pdf";
            targetPath = Path.Combine(workDir, $"ocr-{item.Id:N}{extension}");

            var engine = MuPdfEngine.Create();
            try
            {
                await engine.OpenAsync(sourcePath, cancellationToken);
                if (job.TargetFormat == OcrTargetFormat.Docx)
                {
                    OcrResult ocr = await engine.OcrToDocxAsync(targetPath, null, cancellationToken);
                    ocrResult = ocr;
                }
                else
                {
                    OcrResult ocr = await engine.OcrToPdfAsync(targetPath, null, cancellationToken);
                    ocrResult = ocr;
                }
            }
            finally
            {
                // Dispose the native context BEFORE reading the output: MuPDF's OCR
                // writer keeps the output handle open until its context is destroyed,
                // otherwise File.ReadAllBytes fails with a sharing violation.
                await engine.DisposeAsync();
            }

            byte[] content = await ReadFileWithRetryAsync(targetPath, cancellationToken);
            string contentType = job.TargetFormat == OcrTargetFormat.Docx
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : "application/pdf";
            return new OcrItemResult(
                ocrResult!.PageCount,
                ErrorMessage: null,
                Output: new OcrOutput(content, $"ocr-{item.Id:N}{extension}", contentType));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OcrItemResult(0, ex.Message);
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(targetPath);
        }
    }

    private async Task<string> DownloadToTempAsync(DocumentVersion version, CancellationToken ct)
    {
        using Stream content = await _blobs.GetAsync(_sync.Bucket, version.BlobKey, ct);

        string workDir = Path.Combine(Path.GetTempPath(), "pageforge-ocr");
        Directory.CreateDirectory(workDir);
        string path = Path.Combine(workDir, $"{version.Id:N}.pdf");
        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, ct);
        }
        return path;
    }

    private static async Task<byte[]> ReadFileWithRetryAsync(string path, CancellationToken ct)
    {
        // The pdfocr writer hands the output to an external tesseract process which
        // may keep the file locked for several seconds after OcrToPdfAsync returns
        // (even once the engine is disposed). Poll until the handle is released.
        const int attempts = 200; // ~10s at 50ms
        for (int i = 0; ; i++)
        {
            try
            {
                return await File.ReadAllBytesAsync(path, ct);
            }
            catch (IOException) when (i < attempts - 1)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; a leaked temp file is preferable to failing the job.
        }
    }
}