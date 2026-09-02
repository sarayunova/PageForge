// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns a high-level FR-OCR-01 operation (recognize the text
/// of the open document offline and write a searchable PDF) into a call on the
/// <see cref="IPdfEngine"/> seam, doing the validation that does not depend on
/// the native engine. Recognition runs entirely on this machine.
/// </summary>
public static class OcrService
{
    /// <summary>
    /// Runs local OCR over the open document and writes a new searchable PDF to
    /// <paramref name="outputPath"/> (FR-OCR-01). The open document is left open
    /// and unmodified. The output file must not already exist. Returns a receipt
    /// carrying the page count written and the language/model actually used.
    /// </summary>
    public static ValueTask<OcrResult> OcrAsync(
        IPdfEngine engine,
        string outputPath,
        OcrOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (File.Exists(outputPath))
        {
            throw new IOException(
                $"The OCR output '{outputPath}' already exists; choose a new path so the input is never overwritten.");
        }

        if (options?.Language is not null && options.Language.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0)
        {
            throw new ArgumentException("The OCR language must be a bare Tesseract language code.", nameof(options));
        }

        return engine.OcrToPdfAsync(Path.GetFullPath(outputPath), options, cancellationToken);
    }
}