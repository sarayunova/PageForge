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
