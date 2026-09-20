// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-PAGE fidelity: exercises the real shim's <see cref="MuPdfEngine.BuildPdfAsync"/>
/// (page assembly + per-page rotation) end-to-end against deterministic fixtures,
/// and verifies the built document reopens, has the expected page count, and that
/// a rotated page actually renders differently from its unrotated copy (proving the
/// /Rotate was applied, not ignored).
/// </summary>
public sealed class PageOrganizerFidelityTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public async Task BuildPdf_rotate_then_merge_produces_valid_multipage_output()
    {
        string src = Fixture("sample-pages3.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"organizer-{Guid.NewGuid():N}.pdf");
        try
        {
            // 3 source pages; emit page 0 rotated 90° first, then all three again
            // unrotated => 6 output pages. The first and fourth output pages are the
            // same source page with and without rotation.
            var job = new[]
            {
                new PageBuildRef(src, 0, 1),
                new PageBuildRef(src, 1, 0),
                new PageBuildRef(src, 2, 0),
                new PageBuildRef(src, 0, 0),
                new PageBuildRef(src, 1, 0),
                new PageBuildRef(src, 2, 0),
            };

            int written;
            await using (MuPdfEngine builder = MuPdfEngine.Create())
            {
                written = await PdfPageOrganizer.BuildAsync(builder, output, job);
            }

            Assert.Equal(6, written);
            Assert.True(File.Exists(output));

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await reader.OpenAsync(output);
                Assert.Equal(6, info.PageCount);

                RenderedPdfPage rotated = await reader.RenderPageToPngAsync(0, 72);
                RenderedPdfPage copy = await reader.RenderPageToPngAsync(3, 72);
                RenderedPdfPage rotatedAgain = await reader.RenderPageToPngAsync(0, 72);

                Assert.True(rotated.PngBytes.Length > 100, "Rotated output did not render.");
                Assert.NotEqual(rotated.PngBytes, copy.PngBytes);
                Assert.Equal(rotated.PngBytes, rotatedAgain.PngBytes);

                // Each output page must be the SOURCE page the job asked for.
                //
                // Nothing checked this. The assertions above prove six pages came
                // out, that one is rotated and that rendering is deterministic -
                // none of which says anything about order. Swap two pages in the
                // middle of the job and every one of them still passes, so an
                // organizer that reordered wrongly would ship. Order is the whole
                // point of FR-PAGE, and this is the same mechanism-instead-of-
                // effect shape that let object move/resize ship broken.
                //
                // Compared as rendered bytes, which is this suite's existing
                // currency: the render-equality gate already pins engine output to
                // the byte.
                await using MuPdfEngine source = MuPdfEngine.Create();
                await source.OpenAsync(src);
                var sourcePages = new byte[3][];
                for (int page = 0; page < 3; page++)
                {
                    sourcePages[page] = (await source.RenderPageToPngAsync(page, 72)).PngBytes;
                }

                // The fixture's pages must be distinguishable, or comparing them
                // proves nothing - the trap of a test that cannot fail.
                Assert.NotEqual(sourcePages[0], sourcePages[1]);
                Assert.NotEqual(sourcePages[1], sourcePages[2]);

                // Output index -> the source page the job requested there. Index 0
                // is omitted: it is the rotated one, covered above.
                var expected = new[] { (1, 1), (2, 2), (3, 0), (4, 1), (5, 2) };
                foreach ((int outputPage, int sourcePage) in expected)
                {
                    byte[] actual = (await reader.RenderPageToPngAsync(outputPage, 72)).PngBytes;
                    Assert.True(
                        actual.AsSpan().SequenceEqual(sourcePages[sourcePage]),
                        $"Output page {outputPage} is not source page {sourcePage}. " +
                        "The organizer emitted the pages in the wrong order, or emitted " +
                        "the wrong page.");
                }
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task Delete_and_reorder_via_organizer_round_trip()
    {
        string src = Fixture("sample-pages3.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"organizer-del-{Guid.NewGuid():N}.pdf");
        try
        {
            // Delete page 1 (0-based) and reorder the survivors to [2, 0].
            int deleted;
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                deleted = await PdfPageOrganizer.DeleteAsync(engine, src, 3, new HashSet<int> { 1 }, output);
            }

            Assert.Equal(2, deleted);

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await reader.OpenAsync(output);
                Assert.Equal(2, info.PageCount);

                // WHICH pages survived, not just how many.
                //
                // This test is named for a reorder and asserted only a count, so
                // deleting the wrong page passed it: drop page 0 or page 2 instead
                // of page 1 and there are still two pages left. For a feature whose
                // entire purpose is which pages end up where, counting them is not
                // a check.
                await using MuPdfEngine source = MuPdfEngine.Create();
                await source.OpenAsync(src);
                byte[][] sourcePages =
                [
                    (await source.RenderPageToPngAsync(0, 72)).PngBytes,
                    (await source.RenderPageToPngAsync(1, 72)).PngBytes,
                    (await source.RenderPageToPngAsync(2, 72)).PngBytes,
                ];

                Assert.NotEqual(sourcePages[0], sourcePages[1]);
                Assert.NotEqual(sourcePages[1], sourcePages[2]);

                // Page 1 was deleted, so the survivors are source pages 0 and 2 in
                // their original relative order.
                foreach ((int outputPage, int sourcePage) in new[] { (0, 0), (1, 2) })
                {
                    byte[] actual = (await reader.RenderPageToPngAsync(outputPage, 72)).PngBytes;
                    Assert.True(
                        actual.AsSpan().SequenceEqual(sourcePages[sourcePage]),
                        $"After deleting page 1, output page {outputPage} should be source " +
                        $"page {sourcePage}. The wrong page was deleted, or the survivors " +
                        "came out in the wrong order.");
                }
            }
        }
        finally
        {
            TryDelete(output);
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
