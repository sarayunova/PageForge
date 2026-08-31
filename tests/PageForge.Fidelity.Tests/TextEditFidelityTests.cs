// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-EDIT-01/05/06 fidelity: drives the real shim's text-run primitives
/// end-to-end against the deterministic contract-multipage corpus fixture.
/// Confirms that the page's title run is found and rewritten in place, that the
/// new text comes back from re-extraction with the run's box recalculated, that
/// undo/redo splice the exact old/new operators, that the untouched content
/// keeps its text (FR-EDIT-06), that the edited document saves and reopens
/// without corruption, and that a new character the run's font cannot encode is
/// surfaced as a clean error (FR-EDIT-03 depth gate).
/// </summary>
public sealed class TextEditFidelityTests
{
    private const string TitleText = "PROFESSIONAL SERVICES AGREEMENT";
    private const string EditedTitle = "PROFESSIONAL SERVICES AGREEMENT (UPDATED)";

    private static string Corpus(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task Rewrite_undo_redo_and_persist_round_trip()
    {
        string src = Corpus("contract-multipage.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"textedit-{Guid.NewGuid():N}.pdf");
        string titlePng = Path.Combine(AppContext.BaseDirectory, $"textedit-edited-{Guid.NewGuid():N}.png");
        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(src);
                Assert.True(info.PageCount >= 1);

                IReadOnlyList<PdfTextRun> initialRuns = await engine.ListTextRunsAsync(0);
                Assert.Contains(initialRuns, r => r.Text == TitleText);
                Assert.Contains(initialRuns, r => r.Text.StartsWith("1. SCOPE OF SERVICES."));

                PdfTextRun title = initialRuns.Single(r => r.Text == TitleText);

                // (1) rewrite the title to a LONGER string (exercises box recalc)
                PdfTextEditReceipt receipt = await engine.RewriteTextRunAsync(0, title.Index, EditedTitle);
                Assert.NotEmpty(receipt.OldOperators);
                Assert.NotEmpty(receipt.NewOperators);
                Assert.True(receipt.NewLength > receipt.OldLength, "Edited title must be longer than the original.");
                Assert.Equal(0, receipt.StreamIndex);

                // The rewrite must change exactly this run, in place.
                IReadOnlyList<PdfTextRun> afterEdit = await engine.ListTextRunsAsync(0);
                Assert.Contains(afterEdit, r => r.Text == EditedTitle);
                Assert.DoesNotContain(afterEdit, r => r.Text == TitleText);
                Assert.Contains(afterEdit, r => r.Text.StartsWith("1. SCOPE OF SERVICES."));
                PdfTextRun edited = afterEdit.Single(r => r.Text == EditedTitle);
                Assert.True(edited.X1 > title.X1, "The recalculated run box must extend past the original width.");

                // (2) render the edited page and hand the artifact to the golden-read step
                RenderedPdfPage editedPng = await engine.RenderPageToPngAsync(0, 72);
                Assert.True(editedPng.PngBytes.Length > 100, "Edited page did not render.");
                await File.WriteAllBytesAsync(titlePng, editedPng.PngBytes);

                // (3) undo splices the exact old operators back
                await engine.RevertTextEditAsync(0, receipt, redo: false);
                IReadOnlyList<PdfTextRun> afterUndo = await engine.ListTextRunsAsync(0);
                Assert.Contains(afterUndo, r => r.Text == TitleText);
                Assert.DoesNotContain(afterUndo, r => r.Text == EditedTitle);

                // (4) redo re-applies the new operators
                await engine.RevertTextEditAsync(0, receipt, redo: true);
                IReadOnlyList<PdfTextRun> afterRedo = await engine.ListTextRunsAsync(0);
                Assert.Contains(afterRedo, r => r.Text == EditedTitle);

                // (5) persist and confirm the edit survives reopen
                await engine.SaveAsAsync(output);
            }

            Assert.True(File.Exists(output));

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(output);
                Assert.True(reopened.PageCount >= 1);

                IReadOnlyList<PdfTextRun> persisted = await reader.ListTextRunsAsync(0);
                Assert.Contains(persisted, r => r.Text == EditedTitle);
                Assert.Contains(persisted, r => r.Text.StartsWith("1. SCOPE OF SERVICES."));

                RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, "Saved edited document did not render.");
            }
        }
        finally
        {
            TryDelete(output);
            TryDelete(titlePng);
        }
    }

    [Fact]
    public async Task Unencodable_new_character_is_reported_cleanly()
    {
        await using (MuPdfEngine engine = MuPdfEngine.Create())
        {
            await engine.OpenAsync(Corpus("contract-multipage.pdf"));
            IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(0);
            PdfTextRun title = runs.Single(r => r.Text == TitleText);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.RewriteTextRunAsync(0, title.Index, $"PROFESSIONAL SERVICES \u03A9").AsTask());

            Assert.Contains("not encodable", ex.Message);
        }
    }

    [Fact]
    public async Task List_returns_font_metadata_for_editable_runs()
    {
        await using (MuPdfEngine engine = MuPdfEngine.Create())
        {
            await engine.OpenAsync(Corpus("contract-multipage.pdf"));
            IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(0);

            PdfTextRun title = runs.Single(r => r.Text == TitleText);
            Assert.True(title.FontSize > 0);
            Assert.False(string.IsNullOrEmpty(title.FontName));
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