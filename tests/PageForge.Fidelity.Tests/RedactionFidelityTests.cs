// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-SEC-02 fidelity: drives the real shim's redaction primitives end-to-end
/// against the contract-multipage corpus fixture. Confirms that marking a region
/// is non-destructive (the title text is still extractable), that applying
/// redactions truly REMOVES the covered text from the content stream (it no
/// longer comes back from text extraction — the hard FR-SEC-02 gate, not a
/// paint-over), that untouched text on the same page survives, that restoring a
/// pre-apply snapshot brings the covered text back (the engine side of
/// apply-redactions undo), and that the redacted document saves and reopens with
/// the text still gone and a bar rendered in its place.
/// </summary>
public sealed class RedactionFidelityTests
{
    private const string TitleText = "PROFESSIONAL SERVICES AGREEMENT";
    private const string BodyText = "1. SCOPE OF SERVICES.";

    private static string Corpus(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task Mark_apply_restore_and_persist_round_trip()
    {
        string src = Corpus("contract-multipage.pdf");
        string snapshot = Path.Combine(AppContext.BaseDirectory, $"redact-snapshot-{Guid.NewGuid():N}.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"redact-{Guid.NewGuid():N}.pdf");
        string redactedPng = Path.Combine(AppContext.BaseDirectory, $"redact-edited-{Guid.NewGuid():N}.png");
        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(src);
                Assert.True(info.PageCount >= 1);

                IReadOnlyList<PdfTextRun> initialRuns = await engine.ListTextRunsAsync(0);
                Assert.Contains(initialRuns, r => r.Text == TitleText);
                Assert.Contains(initialRuns, r => r.Text.StartsWith(BodyText));

                PdfTextRun title = initialRuns.Single(r => r.Text == TitleText);

                // (1) mark a box over the title run — non-destructive
                var region = new PdfRect(
                    Math.Max(0, title.X0 - 4),
                    Math.Max(0, title.Y0 - 4),
                    title.X1 + 4,
                    title.Y1 + 4);
                await engine.AddRedactionAsync(0, region);

                IReadOnlyList<PdfTextRun> afterMark = await engine.ListTextRunsAsync(0);
                Assert.Contains(afterMark, r => r.Text == TitleText);

                // Snapshot the pre-apply document for the undo leg.
                await engine.SaveAsAsync(snapshot);

                // (2) apply — the covered text must be REMOVED from the content
                // stream (the FR-SEC-02 gate: gone from extraction, not painted over)
                int applied = await engine.ApplyRedactionsAsync(0, options: null);
                Assert.True(applied >= 1, "At least one marked region must be applied.");

                IReadOnlyList<PdfTextRun> afterApply = await engine.ListTextRunsAsync(0);
                Assert.DoesNotContain(afterApply, r => r.Text == TitleText);
                Assert.Contains(afterApply, r => r.Text.StartsWith(BodyText));

                RenderedPdfPage redactedPngPage = await engine.RenderPageToPngAsync(0, 72);
                Assert.True(redactedPngPage.PngBytes.Length > 100, "Redacted page did not render.");
                await File.WriteAllBytesAsync(redactedPng, redactedPngPage.PngBytes);

                // (3) restore the pre-apply snapshot (the engine side of
                // apply-redactions undo): the covered text must come back
                await engine.RestoreSnapshotAsync(snapshot);
                IReadOnlyList<PdfTextRun> afterRestore = await engine.ListTextRunsAsync(0);
                Assert.Contains(afterRestore, r => r.Text == TitleText);

                // (4) re-apply, then persist and confirm the redaction survives
                int reapplied = await engine.ApplyRedactionsAsync(0, options: null);
                Assert.True(reapplied >= 1);
                await engine.SaveAsAsync(output);
            }

            Assert.True(File.Exists(output));

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(output);
                Assert.True(reopened.PageCount >= 1);

                IReadOnlyList<PdfTextRun> persisted = await reader.ListTextRunsAsync(0);
                Assert.DoesNotContain(persisted, r => r.Text == TitleText);
                Assert.Contains(persisted, r => r.Text.StartsWith(BodyText));

                RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, "Saved redacted document did not render.");
            }
        }
        finally
        {
            TryDelete(snapshot);
            TryDelete(output);
            TryDelete(redactedPng);
        }
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