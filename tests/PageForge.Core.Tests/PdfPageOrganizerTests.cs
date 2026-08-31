// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-PAGE unit tests: every page-organization operation must translate into the
/// correct ordered build-job (<see cref="PageBuildRef"/>) handed to the engine.
/// The engine seam (fake) records the job so the Core-domain logic is fully
/// testable without a native dependency.
/// </summary>
public sealed class PdfPageOrganizerTests
{
    private const string Src = "C:\\docs\\source.pdf";

    [Fact]
    public async Task Extract_writes_only_selected_pages_in_order()
    {
        var engine = new FakePdfEngine(5);

        await PdfPageOrganizer.ExtractAsync(engine, Src, new[] { 1, 3, 0 }, "out.pdf");

        Assert.NotNull(engine.LastBuild);
        Assert.Equal("out.pdf", engine.LastBuild.Value.OutputPath);
        Assert.Equal(new[] { 1, 3, 0 }, engine.LastBuild.Value.Pages.Select(p => p.PageIndex));
        Assert.All(engine.LastBuild.Value.Pages, p => Assert.Equal(0, p.RotationQuarterTurns));
        engine.LastBuild.Value.Pages.ColumnsAll(Src);
    }

    [Fact]
    public async Task Delete_keeps_every_page_except_deleted()
    {
        var engine = new FakePdfEngine(5);

        await PdfPageOrganizer.DeleteAsync(engine, Src, 5, new HashSet<int> { 0, 4 }, "out.pdf");

        Assert.Equal(new[] { 1, 2, 3 }, engine.LastBuild!.Value.Pages.Select(p => p.PageIndex));
        engine.LastBuild.Value.Pages.ColumnsAll(Src);
    }

    [Fact]
    public async Task Rotate_sets_rotation_only_for_listed_pages()
    {
        var engine = new FakePdfEngine(4);
        var rotations = new Dictionary<int, int> { [1] = 1, [3] = 2 };

        await PdfPageOrganizer.RotateAsync(engine, Src, 4, rotations, "out.pdf");

        IReadOnlyList<PageBuildRef> pages = engine.LastBuild!.Value.Pages;
        Assert.Equal(4, pages.Count);
        Assert.Equal(0, pages[0].RotationQuarterTurns);
        Assert.Equal(1, pages[1].RotationQuarterTurns);
        Assert.Equal(0, pages[2].RotationQuarterTurns);
        Assert.Equal(2, pages[3].RotationQuarterTurns);
    }

    [Fact]
    public async Task Reorder_builds_the_new_page_order()
    {
        var engine = new FakePdfEngine(4);

        await PdfPageOrganizer.ReorderAsync(engine, Src, new[] { 3, 0, 2, 1 }, "out.pdf");

        Assert.Equal(new[] { 3, 0, 2, 1 }, engine.LastBuild!.Value.Pages.Select(p => p.PageIndex));
    }

    [Fact]
    public async Task Split_writes_the_contiguous_range()
    {
        var engine = new FakePdfEngine(6);

        await PdfPageOrganizer.SplitAsync(engine, Src, 2, 5, "part.pdf");

        Assert.Equal(new[] { 2, 3, 4 }, engine.LastBuild!.Value.Pages.Select(p => p.PageIndex));
    }

    [Fact]
    public async Task Merge_combines_every_page_of_each_source_in_order()
    {
        var engine = new FakePdfEngine(0);
        var sources = new[]
        {
            new SourceWithCount("C:\\docs\\a.pdf", 2),
            new SourceWithCount("C:\\docs\\b.pdf", 3),
        };

        await PdfPageOrganizer.MergeAsync(engine, sources, "merged.pdf");

        IReadOnlyList<PageBuildRef> pages = engine.LastBuild!.Value.Pages;
        Assert.Equal(5, pages.Count);
        Assert.Equal(new[] { "a.pdf", "a.pdf", "b.pdf", "b.pdf", "b.pdf" },
            pages.Select(p => Path.GetFileName(p.SourcePath)));
        Assert.Equal(new[] { 0, 1, 0, 1, 2 }, pages.Select(p => p.PageIndex));
    }

    [Fact]
    public async Task Insert_splices_source_pages_at_position()
    {
        var engine = new FakePdfEngine(3);
        const string target = "C:\\docs\\target.pdf";
        const string source = "C:\\docs\\extra.pdf";

        await PdfPageOrganizer.InsertAsync(engine, target, 3, 1, source, new[] { 0, 1 }, "out.pdf");

        IReadOnlyList<PageBuildRef> pages = engine.LastBuild!.Value.Pages;
        Assert.Equal(new[] { 0, 0, 1, 1, 2 }, pages.Select(p => p.PageIndex));
        Assert.Equal(new[] { "target.pdf", "extra.pdf", "extra.pdf", "target.pdf", "target.pdf" },
            pages.Select(p => Path.GetFileName(p.SourcePath)));
    }

    [Fact]
    public void Insert_rejects_out_of_range_position()
    {
        var engine = new FakePdfEngine(3);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageOrganizer.InsertAsync(engine, Src, 3, 99, Src, new[] { 0 }, "out.pdf"));
    }

    [Fact]
    public void Build_rejects_empty_selection()
    {
        var engine = new FakePdfEngine(1);

        Assert.Throws<ArgumentException>(
            () => PdfPageOrganizer.BuildAsync(engine, "out.pdf", Array.Empty<PageBuildRef>()));
    }
}

public static class PageBuildRefAssertions
{
    public static void ColumnsAll(this IReadOnlyList<PageBuildRef> pages, string sourcePath)
    {
        Assert.All(pages, p => Assert.Equal(sourcePath, p.SourcePath));
    }
}
