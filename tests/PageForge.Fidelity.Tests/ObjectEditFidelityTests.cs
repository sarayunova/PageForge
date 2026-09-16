// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// 2E-native exit-gate object transform (FR-EDIT-04): the real MuPDF shim
/// content-walker lists every image/vector <c>Do</c> invocation on a page, and
/// <c>MoveResizeObjectAsync</c> splices a new <c>cm</c> matrix so the object is
/// relocated/resized. This drives every real-document corpus PDF through that
/// path on the real shim: list objects, move/resize the first object found,
/// prove the undo/redo splice round-trips exactly, persist, reopen and render.
/// A corpus with no <c>Do</c> objects (e.g. pure-text documents) contributes
/// nothing but still validates the list path returns an empty sequence; the
/// scanned-letter corpus provides the image-bearing pages that give the gate its
/// teeth. Any unhandled shim exception fails the gate.
/// </summary>
public sealed class ObjectEditFidelityTests
{
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
    public async Task Object_move_resize_persists_across_save_reopen(string pdfPath)
    {
        string name = Path.GetFileNameWithoutExtension(pdfPath);
        Directory.CreateDirectory(ArtifactRoot);

        string editedOut = Path.Combine(AppContext.BaseDirectory, $"objedit-{Guid.NewGuid():N}.pdf");
        try
        {
            bool moved = false;
            int pageCount;
            int movedPage = -1;
            string movedId = string.Empty;
            PdfTextEditReceipt? receipt = null;
            PdfRect movedBounds = new(120, 140, 320, 340);

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                pageCount = info.PageCount;

                for (int page = 0; page < pageCount && !moved; page++)
                {
                    IReadOnlyList<PdfPageObject> objects = await engine.ListObjectsAsync(page);
                    if (objects.Count == 0)
                    {
                        continue;
                    }

                    PdfPageObject target = objects[0];
                    movedPage = page;
                    movedId = target.Id;

                    // Bounds are in PDF points, so they must lie on the page. Asserted
                    // because it was once false by three orders of magnitude: the bbox
                    // was computed from the image's size in PIXELS, so a 1275x1650 scan
                    // on a 612x792pt page listed as 780300x1306800pt.
                    PdfPageRegion size = await engine.GetPageSizeAsync(page);
                    Assert.InRange(target.Bounds.X1, 0, size.WidthPt + 1);
                    Assert.InRange(target.Bounds.Y1, 0, size.HeightPt + 1);

                    // The engine must hand the listed id back verbatim.
                    receipt = await engine.MoveResizeObjectAsync(page, target.Id, movedBounds);
                    Assert.NotNull(receipt);
                    Assert.NotEmpty(receipt.OldOperators);
                    Assert.NotEmpty(receipt.NewOperators);
                    Assert.NotEqual(receipt.OldOperators, receipt.NewOperators);

                    // WHERE it landed, which nothing asserted before. The move used to
                    // write its six matrix numbers without the `cm` operator to consume
                    // them, so the object kept none of the placement it had and was
                    // painted through the identity CTM - a 1pt square in the page
                    // corner. Every assertion around this one still passed: a receipt
                    // was produced, it differed from the original, and the page still
                    // rendered.
                    PdfPageObject afterMove = Assert.Single(
                        await engine.ListObjectsAsync(page), o => o.Id == target.Id);
                    AssertBounds(movedBounds, afterMove.Bounds, $"{name} after move");

                    moved = true;
                }

                if (!moved)
                {
                    // No image/vector object anywhere in this document: the purity of the
                    // list path (empty sequence, no crash) is all this document must prove.
                    return;
                }

                // (2) prove the engine splice round-trips exactly: undo then redo.
                await engine.RevertTextEditAsync(movedPage, receipt!, redo: false);
                await engine.RevertTextEditAsync(movedPage, receipt!, redo: true);

                // (3) persist.
                await engine.SaveAsAsync(editedOut);
            }

            Assert.True(File.Exists(editedOut), "Edited document did not save.");

            // (4) reopen: verify the moved object's bounds survived save + reopen, and render.
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(editedOut);
                Assert.Equal(pageCount, reopened.PageCount);

                IReadOnlyList<PdfPageObject> objects = await reader.ListObjectsAsync(movedPage);
                Assert.NotEmpty(objects);

                // The move must survive the save/reopen with its geometry, not merely
                // leave an object behind. This read page 0 regardless of which page was
                // edited, so for a document whose first object is not on page 0 it was
                // asserting against an untouched page.
                AssertBounds(
                    movedBounds,
                    Assert.Single(objects, o => o.Id == movedId).Bounds,
                    $"{name} after save and reopen");

                RenderedPdfPage png = await reader.RenderPageToPngAsync(movedPage, 72);
                Assert.True(png.PngBytes.Length > 100, $"{name} moved-object page did not render.");
                await File.WriteAllBytesAsync(Artifact($"{name}.obj.p1.png"), png.PngBytes);
            }

            File.Copy(editedOut, Artifact($"{name}.objedit.pdf"), overwrite: true);
        }
        finally
        {
            TryDelete(editedOut);
        }
    }

    /// <summary>
    /// Compares two PDF rectangles to a quarter of a point. The tolerance is there
    /// because the matrix travels through the content stream as decimal text and
    /// back through a float transform, not to absorb a placement error: a quarter
    /// point is far below anything visible, and the failures this guards against
    /// were off by hundreds of points or by a factor of a thousand.
    /// </summary>
    private static void AssertBounds(PdfRect expected, PdfRect actual, string what)
    {
        const double tolerance = 0.25;
        Assert.True(
            Math.Abs(expected.X0 - actual.X0) <= tolerance &&
            Math.Abs(expected.Y0 - actual.Y0) <= tolerance &&
            Math.Abs(expected.X1 - actual.X1) <= tolerance &&
            Math.Abs(expected.Y1 - actual.Y1) <= tolerance,
            $"{what}: expected bounds ({expected.X0},{expected.Y0})-({expected.X1},{expected.Y1}), " +
            $"got ({actual.X0},{actual.Y0})-({actual.X1},{actual.Y1}).");
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
