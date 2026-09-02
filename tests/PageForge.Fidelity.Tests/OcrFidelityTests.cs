// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Cryptography;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-OCR-01 fidelity: drives local OCR end-to-end through the real shim against
/// the scan-letters corpus fixture (two image-only raster pages with no text
/// layer). Confirms that the source has no extractable text, that the engine
/// resolves the bundled trained data by itself, that the OCR run writes a
/// searchable PDF whose recognized words now come back from text extraction —
/// the whole point of the feature: scanned content becomes selectable and
/// searchable fully offline — and that the output still renders. Also pins the
/// vendored tessdata sha256 so a licensed-model swap can never slip in silently.
///
/// Known cosmetic noise: Tesseract 4.x prints "ObjectCache ... WARNING! LEAK!"
/// lines to stderr at process exit (Leptonica cache teardown during DLL unload).
/// It is harmless, appears only in console runs, and is NOT a product leak —
/// do not chase it.
/// </summary>
public sealed class OcrFidelityTests
{
    private const string TrainedDataPin = "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2";

    private static string Corpus(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task Ocr_makes_scanned_pages_searchable_offline()
    {
        string src = Corpus("scan-letters.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"ocr-{Guid.NewGuid():N}.pdf");
        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(src);
                Assert.Equal(2, info.PageCount);

                // The scan fixture has no text layer yet (this is the whole point).
                Assert.Equal(string.Empty, (await engine.GetPageTextAsync(0)).Text);
                Assert.Equal(string.Empty, (await engine.GetPageTextAsync(1)).Text);

                // Run local OCR with stock options — the engine must locate the
                // bundled trained data itself (app-local tessdata staging).
                OcrResult result = await OcrService.OcrAsync(engine, output);

                Assert.Equal(2, result.PageCount);
                Assert.Equal("eng", result.Language);
                Assert.True(Directory.Exists(result.DataDirectory),
                    $"OCR resolved to '{result.DataDirectory}' but the directory does not exist.");
                string trained = Path.Combine(result.DataDirectory, "eng.traineddata");
                Assert.True(File.Exists(trained), "The resolved data directory has no eng.traineddata.");
                Assert.Equal(TrainedDataPin, Sha256(trained));

                Assert.True(File.Exists(output), "The OCR output was not written.");
            }

            // Reopen the OCR output and confirm the recognized text is embedded,
            // extractable, and the pages still render identically to the visual scan.
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(output);
                Assert.Equal(2, reopened.PageCount);

                string page0 = (await reader.GetPageTextAsync(0)).Text;
                Assert.Contains("SCANNED", page0, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("PAGE", page0, StringComparison.OrdinalIgnoreCase);

                string page1 = (await reader.GetPageTextAsync(1)).Text;
                Assert.Contains("SCANNED", page1, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("PAGE", page1, StringComparison.OrdinalIgnoreCase);

                RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, "The searchable output did not render.");
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}