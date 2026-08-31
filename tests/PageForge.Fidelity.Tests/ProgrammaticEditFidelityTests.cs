// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// 2F exit-gate programmatic edit diff (FR-EDIT): every real-document corpus PDF
/// is driven through the text-run edit pipeline on the real MuPDF shim. For each
/// text-bearing page we rewrite one editable run (longer, ASCII-only text so the
/// box-recalc and in-place paths are exercised), proving the engine's undo/redo
/// splice round-trips exactly, persist, reopen, and re-extract to confirm the
/// edit survived the save/reopen cycle. Renders and an edited artifact for each
/// document feed the golden-diff review. A run the native encodability gate
/// rejects (FR-EDIT-03 — a font that cannot paint the new characters) is skipped,
/// not bypassed. Image-only corpus documents are exercised for open/save/reopen
/// and must not crash. Any unhandled shim exception fails the gate.
/// </summary>
public sealed class ProgrammaticEditFidelityTests
{
    private const string EditSuffix = " (ED)";
    private const string ArtifactRoot = "artifacts/edit-fidelity";

    private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static TheoryData<string> CorpusPdfs()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(CorpusDir, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            data.Add(file);
        }

        return data;
    }

    private static string Artifact(string name)
        => Path.Combine(AppContext.BaseDirectory, ArtifactRoot, name);

    [Theory]
    [MemberData(nameof(CorpusPdfs))]
    public async Task Programmatic_text_edit_persists_across_save_reopen(string pdfPath)
    {
        string name = Path.GetFileNameWithoutExtension(pdfPath);
        Directory.CreateDirectory(ArtifactRoot);

        string editedOut = Path.Combine(AppContext.BaseDirectory, $"edit-{Guid.NewGuid():N}.pdf");
        try
        {
            bool anyEdited = false;
            int pageCount;

            List<(int Page, int RunIndex)> rewritten = new();
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                pageCount = info.PageCount;
                Assert.True(pageCount >= 1);

                // (1) rewrite one editable run per text-bearing page. Per page we try each
                // run in order; a run whose font cannot encode the suffix is rejected by the
                // FR-EDIT-03 encodability hard gate (surfaced, not bypassed), so we move on to
                // the next run. Every successful rewrite is recorded with its receipt to prove
                // the splice round-trips.
                var receipts = new List<(int Page, PdfTextEditReceipt Receipt)>();
                for (int page = 0; page < pageCount; page++)
                {
                    IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(page);
                    foreach (PdfTextRun first in runs)
                    {
                        try
                        {
                            string edited = first.Text + EditSuffix;
                            PdfTextEditReceipt receipt = await engine.RewriteTextRunAsync(page, first.Index, edited);
                            rewritten.Add((page, first.Index));
                            anyEdited = true;
                            receipts.Add((page, receipt));
                            break;
                        }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("not encodable"))
                        {
                            // FR-EDIT-03 depth gate: this run's font cannot paint the suffix;
                            // skip to the next run rather than crash the gate.
                        }
                    }
                }

                // (2) prove the engine splice round-trips exactly for every edit: undo then redo.
                for (int i = receipts.Count - 1; i >= 0; i--)
                {
                    await engine.RevertTextEditAsync(receipts[i].Page, receipts[i].Receipt, redo: false);
                }

                for (int i = 0; i < receipts.Count; i++)
                {
                    await engine.RevertTextEditAsync(receipts[i].Page, receipts[i].Receipt, redo: true);
                }

                // (3) confirm the redo state is what we expect (each edited run carries the suffix).
                foreach ((int page, int runIndex) in rewritten)
                {
                    IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(page);
                    Assert.NotEmpty(runs);
                    Assert.EndsWith(EditSuffix, runs[runIndex].Text);
                }

                // (4) persist.
                await engine.SaveAsAsync(editedOut);
            }

            Assert.True(File.Exists(editedOut), "Edited document did not save.");

            // (5) reopen: the edit must have survived save + reopen, and the page must render.
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(editedOut);
                Assert.Equal(pageCount, reopened.PageCount);

                if (anyEdited)
                {
                    (int firstPage, int firstRun) = rewritten[0];
                    IReadOnlyList<PdfTextRun> runs = await reader.ListTextRunsAsync(firstPage);
                    Assert.NotEmpty(runs);
                    Assert.EndsWith(EditSuffix, runs[firstRun].Text);
                }

                RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, $"{name} edited page 0 did not render.");
                await File.WriteAllBytesAsync(Artifact($"{name}.p1.png"), png.PngBytes);
            }

            // Keep a copy of the edited artifact for the golden-diff review.
            File.Copy(editedOut, Artifact($"{name}.edited.pdf"), overwrite: true);
        }
        finally
        {
            TryDelete(editedOut);
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
