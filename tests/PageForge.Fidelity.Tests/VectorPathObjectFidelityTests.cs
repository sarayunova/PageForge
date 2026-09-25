// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Linq;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-EDIT-04 follow-on: pf_list_objects used to walk only `Do` invocations
/// (embedded images and Form XObjects), so a raw vector shape - a rectangle,
/// line, or curve drawn straight in the content stream, which is how most PDF
/// authoring tools actually emit shapes rather than wrapping them in a Form
/// XObject - was invisible to the object-edit surface: nothing was ever listed
/// for it, so clicking it in the app selected nothing. This asserts the
/// corpus actually contains at least one such object (proving the assertions
/// below exercise the real code path rather than vacuously passing) and that
/// it can be selected, moved, resized, saved, reopened and rendered exactly
/// like an image object already could.
/// </summary>
public sealed class VectorPathObjectFidelityTests
{
    private const string ArtifactRoot = "artifacts/edit-fidelity";
    private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "corpus");

    private static string Artifact(string name)
        => Path.Combine(AppContext.BaseDirectory, ArtifactRoot, name);

    [Fact]
    public async Task Corpus_contains_a_selectable_vector_path_and_it_moves_and_persists()
    {
        Directory.CreateDirectory(ArtifactRoot);

        bool found = false;
        string editedOut = Path.Combine(AppContext.BaseDirectory, $"vecpath-{Guid.NewGuid():N}.pdf");
        PdfRect movedBounds = new(60, 80, 260, 260);
        int movedPage = -1;
        string movedId = string.Empty;
        int pageCount = 0;

        try
        {
            foreach (string pdfPath in Directory.GetFiles(CorpusDir, "*.pdf", SearchOption.TopDirectoryOnly))
            {
                await using MuPdfEngine engine = MuPdfEngine.Create();
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);

                for (int page = 0; page < info.PageCount; page++)
                {
                    IReadOnlyList<PdfPageObject> pageObjects = await engine.ListObjectsAsync(page);
                    PdfPageObject? vector = pageObjects.FirstOrDefault(o => o.Kind == PageObjectKind.Vector);
                    if (vector is null)
                    {
                        continue;
                    }

                    found = true;
                    pageCount = info.PageCount;
                    movedPage = page;
                    movedId = vector.Id;

                    // Bounds must lie on the page, same sanity check the `Do`
                    // object test applies - a bbox computed in the wrong units
                    // or space would show up here as wildly out of range.
                    PdfPageRegion size = await engine.GetPageSizeAsync(page);
                    Assert.InRange(vector.Bounds.X0, -1, size.WidthPt + 1);
                    Assert.InRange(vector.Bounds.Y0, -1, size.HeightPt + 1);

                    PdfTextEditReceipt receipt = await engine.MoveResizeObjectAsync(page, vector.Id, movedBounds);
                    Assert.NotEmpty(receipt.OldOperators);
                    Assert.NotEmpty(receipt.NewOperators);
                    Assert.NotEqual(receipt.OldOperators, receipt.NewOperators);

                    PdfPageObject afterMove = Assert.Single(
                        await engine.ListObjectsAsync(page), o => o.Id == vector.Id);
                    PdfRectAssert.Equal(movedBounds, afterMove.Bounds, "vector path after move");

                    // Undo/redo round-trip, same as the `Do` object gate.
                    await engine.RevertTextEditAsync(page, receipt, redo: false);
                    PdfPageObject afterUndo = Assert.Single(
                        await engine.ListObjectsAsync(page), o => o.Id == vector.Id);
                    PdfRectAssert.Equal(vector.Bounds, afterUndo.Bounds, "vector path after undo");

                    await engine.RevertTextEditAsync(page, receipt, redo: true);

                    await engine.SaveAsAsync(editedOut);
                    break;
                }

                if (found)
                {
                    break;
                }
            }

            Assert.True(found,
                "No corpus PDF listed a vector-path object; the assertions above never ran against a real one.");

            await using MuPdfEngine reader = MuPdfEngine.Create();
            PdfDocumentInfo reopened = await reader.OpenAsync(editedOut);
            Assert.Equal(pageCount, reopened.PageCount);

            IReadOnlyList<PdfPageObject> objects = await reader.ListObjectsAsync(movedPage);
            PdfRectAssert.Equal(
                movedBounds,
                Assert.Single(objects, o => o.Id == movedId).Bounds,
                "vector path after save and reopen");

            RenderedPdfPage png = await reader.RenderPageToPngAsync(movedPage, 72);
            Assert.True(png.PngBytes.Length > 100, "moved-vector-path page did not render.");
            await File.WriteAllBytesAsync(Artifact("vector-path.moved.p1.png"), png.PngBytes);
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
