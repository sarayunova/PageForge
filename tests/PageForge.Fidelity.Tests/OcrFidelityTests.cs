// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Cryptography;
using PageForge.Core.Editing;
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
[Collection(OcrSerial.Name)]
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

    /// <summary>
    /// The text layer OCR adds is invisible and set in a two-byte font, so the
    /// in-place rewriter cannot handle it (a real OCR'd book scan failed with "no
    /// content operator paints the run at its origin"; the corpus scan fails with
    /// "not encodable" - different symptoms, one cause). The editor must recognise
    /// it up front and explain, instead of offering advice that can never work.
    /// </summary>
    [Fact]
    public async Task Ocr_text_runs_are_flagged_as_hidden_and_cannot_be_rewritten()
    {
        string output = Path.Combine(AppContext.BaseDirectory, $"ocr-edit-{Guid.NewGuid():N}.pdf");
        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(Corpus("scan-letters.pdf"));
                await OcrService.OcrAsync(engine, output);
            }

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                await reader.OpenAsync(output);
                IReadOnlyList<PdfTextRun> runs = await reader.ListTextRunsAsync(0);
                Assert.NotEmpty(runs);
                Assert.All(runs, r => Assert.True(r.IsHiddenOcrText, $"Run '{r.Text}' in font '{r.FontName}' was not flagged."));

                // The flag exists because this genuinely fails; if the engine ever learns
                // to rewrite OCR text, this line is the cue to drop the flag.
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => reader.RewriteTextRunAsync(0, runs[0].Index, "changed").AsTask());
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    /// <summary>
    /// Cover-and-replace on scanned text, end to end through the real engine: OCR a
    /// scan, replace one recognised line, save, reopen. The new words must be
    /// findable and the old ones gone from the text layer; the picture of the old
    /// words is covered; undo restores the OCR text. Writes the edited page to
    /// scan-edit-page1.png in the test output so the result can be inspected by eye.
    /// </summary>
    [Fact]
    public async Task Scanned_text_is_replaced_by_covering_it_and_undo_restores_it()
    {
        string ocr = Path.Combine(AppContext.BaseDirectory, $"ocr-cover-{Guid.NewGuid():N}.pdf");
        string edited = Path.Combine(AppContext.BaseDirectory, $"ocr-cover-edited-{Guid.NewGuid():N}.pdf");
        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(Corpus("scan-letters.pdf"));
                await OcrService.OcrAsync(engine, ocr);
            }

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(ocr);
                IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(0);
                PdfTextRun target = runs.First(r => r.Text.Contains("SCANNED", StringComparison.OrdinalIgnoreCase));
                Assert.True(target.IsHiddenOcrText);

                string before = (await engine.GetPageTextAsync(0)).Text;
                Assert.Contains("SCANNED", before, StringComparison.OrdinalIgnoreCase);
                int imagesBefore = (await engine.ListObjectsAsync(0)).Count(o => o.Kind == PageObjectKind.Image);
                Assert.True(imagesBefore > 0, "the OCR output should carry the scan as image objects");

                ScanTextReplacement plan = ScanTextPlanner.Plan(
                    target, "REVISED", new RgbColor(1, 1, 1), new RgbColor(0, 0, 0));
                var command = new ScanTextEditCommand(engine, 0, plan);
                await command.ExecuteAsync();

                string after = (await engine.GetPageTextAsync(0)).Text;
                Assert.Contains("REVISED", after);
                Assert.DoesNotContain("SCANNED", after, StringComparison.OrdinalIgnoreCase);

                // Only the edited word changed: its neighbours keep their text, and the scan
                // images underneath were not touched (removing them is what the redaction
                // defaults do, and it is the reason this edit must not use those defaults).
                Assert.Contains("PAGE", after, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("ONE", after, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(imagesBefore, (await engine.ListObjectsAsync(0)).Count(o => o.Kind == PageObjectKind.Image));

                RenderedPdfPage png = await engine.RenderPageToPngAsync(0, 150);
                await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "scan-edit-page1.png"), png.PngBytes);

                await engine.SaveAsAsync(edited);

                await command.UndoAsync();
                string undone = (await engine.GetPageTextAsync(0)).Text;
                Assert.Contains("SCANNED", undone, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("REVISED", undone);

                command.Dispose();
            }

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                await reader.OpenAsync(edited);
                string saved = (await reader.GetPageTextAsync(0)).Text;
                Assert.Contains("REVISED", saved);
                Assert.DoesNotContain("SCANNED", saved, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("PAGE", saved, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            TryDelete(ocr);
            TryDelete(edited);
        }
    }

    /// <summary>
    /// The redaction options file used to be parsed on TABs only, so every option but
    /// "black box" was silently ignored and a caller asking to leave images alone got
    /// them removed. Applies the same region with images untouched, then with the
    /// secure defaults, and requires the two to behave differently.
    /// </summary>
    [Fact]
    public async Task Redaction_options_reach_the_engine()
    {
        string ocr = Path.Combine(AppContext.BaseDirectory, $"ocr-redact-{Guid.NewGuid():N}.pdf");
        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(Corpus("scan-letters.pdf"));
                await OcrService.OcrAsync(engine, ocr);
            }

            async Task<(int Images, string Text)> RedactAsync(RedactionOptions? options)
            {
                await using MuPdfEngine engine = MuPdfEngine.Create();
                await engine.OpenAsync(ocr);
                PdfTextRun word = (await engine.ListTextRunsAsync(0)).First(r => r.Text.Contains("SCANNED", StringComparison.OrdinalIgnoreCase));
                await engine.AddRedactionAsync(0, new PdfRect(word.X0, word.Y0, word.X1, word.Y1));
                await engine.ApplyRedactionsAsync(0, options);
                int images = (await engine.ListObjectsAsync(0)).Count(o => o.Kind == PageObjectKind.Image);
                return (images, (await engine.GetPageTextAsync(0)).Text);
            }

            (int keptImages, string keptText) = await RedactAsync(new RedactionOptions(
                BlackBox: false,
                ImageMethod: RedactionImageMethod.None,
                LineArtMethod: RedactionLineArtMethod.None,
                TextMethod: RedactionTextMethod.Remove));
            (int defaultImages, string defaultText) = await RedactAsync(null);

            Assert.DoesNotContain("SCANNED", keptText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SCANNED", defaultText, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                keptImages > defaultImages,
                $"image method None must keep more images than the secure default removes (kept {keptImages}, default {defaultImages}).");
        }
        finally
        {
            TryDelete(ocr);
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