// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.Core.View;
using Xunit;

namespace PageForge.Core.Tests;

public sealed class PdfOutlineParserTests
{
    [Fact]
    public void Parse_flat_single_level()
    {
        var lines = new[] { "1\t1\t0\t0\tCover", "1\t3\t0\t0\tBody" };

        PdfOutline outline = PdfOutlineParser.Parse(lines);

        Assert.Equal(2, outline.Items.Count);
        Assert.Equal("Cover", outline.Items[0].Title);
        Assert.Equal(1, outline.Items[0].PageNumber);
        Assert.Equal(1, outline.Items[0].Depth);
        Assert.Equal(3, outline.Items[1].PageNumber);
    }

    [Fact]
    public void Parse_nested_tree_keeps_depth()
    {
        var lines = new[]
        {
            "1\t1\t0\t0\tChapter 1",
            "2\t2\t10\t15\tSection 1.1",
            "2\t3\t10\t15\tSection 1.2",
            "1\t4\t0\t0\tChapter 2",
        };

        PdfOutline outline = PdfOutlineParser.Parse(lines);

        Assert.Equal(4, outline.Items.Count);
        Assert.Equal(1, outline.Items[0].Depth);
        Assert.Equal(2, outline.Items[1].Depth);
        Assert.Equal(2, outline.Items[2].Depth);
        Assert.Equal(1, outline.Items[3].Depth);
    }

    [Fact]
    public void Parse_empty_and_blank_lines_returns_empty()
    {
        Assert.Empty(PdfOutlineParser.Parse(Array.Empty<string>()).Items);
        Assert.Empty(PdfOutlineParser.Parse(new[] { "", "   " }).Items);
    }

    [Fact]
    public void Parse_malformed_lines_are_skipped()
    {
        var lines = new[] { "garbage", "1\t2\tnope\t0\tBad", "1\t5\t0\t0\tGood" };

        PdfOutline outline = PdfOutlineParser.Parse(lines);

        Assert.Single(outline.Items);
        Assert.Equal("Good", outline.Items[0].Title);
    }

    [Fact]
    public void Parse_title_may_contain_tabs_without_corrupting_fields()
    {
        var lines = new[] { "1\t7\t0\t0\tTwo\tWords" };

        PdfOutline outline = PdfOutlineParser.Parse(lines);

        Assert.Single(outline.Items);
        Assert.Equal("Two\tWords", outline.Items[0].Title);
        Assert.Equal(7, outline.Items[0].PageNumber);
    }
}

public sealed class DocumentViewModelTests
{
    [Fact]
    public async Task Initialize_loads_layout_and_outline()
    {
        FakePdfEngine engine = new(3);
        engine.Outline = new PdfOutline(new[]
        {
            new OutlineItem("Cover", 1, 0, 0, 1),
            new OutlineItem("Body", 2, 0, 0, 1),
        });
        await using var vm = new DocumentViewModel(engine);

        await vm.InitializeAsync(@"C:\docs\book.pdf");

        Assert.Equal(3, vm.PageCount);
        Assert.Equal("book.pdf", vm.DisplayName);
        Assert.Equal(3, vm.PageSizes.Count);
        Assert.Equal(2, vm.Outline.Items.Count);
    }

    [Fact]
    public async Task GoToPage_clamps_to_valid_range()
    {
        await using var vm = new DocumentViewModel(new FakePdfEngine(5));
        await vm.InitializeAsync(@"C:\docs\book.pdf");

        vm.GoToPage(-10);
        Assert.Equal(0, vm.CurrentPage);

        vm.GoToPage(100);
        Assert.Equal(4, vm.CurrentPage);
    }

    [Fact]
    public async Task Rotation_wraps_to_360()
    {
        await using var vm = new DocumentViewModel(new FakePdfEngine(1));
        await vm.InitializeAsync(@"C:\docs\book.pdf");

        vm.AddRotation(4); // 360 -> normalizes to 0
        Assert.Equal(0, vm.Rotation);

        vm.AddRotation(1);
        Assert.Equal(90, vm.Rotation);

        vm.AddRotation(-1);
        Assert.Equal(0, vm.Rotation);
    }

    [Fact]
    public async Task SetZoom_clamps_bounds()
    {
        await using var vm = new DocumentViewModel(new FakePdfEngine(1));
        await vm.InitializeAsync(@"C:\docs\book.pdf");

        vm.SetZoom(100);
        Assert.Equal(8.0, vm.Zoom, 3);

        vm.SetZoom(0.001);
        Assert.Equal(0.1, vm.Zoom, 3);
    }

    [Fact]
    public async Task Search_finds_pages_and_builds_snippets()
    {
        FakePdfEngine engine = new(3);
        engine.OnPageText = i => i switch
        {
            0 => "contract introduction",
            1 => "the warranty clause is in this paragraph",
            _ => "nothing matching here",
        };
        await using var vm = new DocumentViewModel(engine);
        await vm.InitializeAsync(@"C:\docs\contract.pdf");

        IReadOnlyList<SearchHit> hits = await vm.SearchAsync("warranty", CancellationToken.None);

        SearchHit hit = Assert.Single(hits);
        Assert.Equal(1, hit.PageIndex);
        Assert.Contains("warranty", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_blank_query_returns_empty()
    {
        await using var vm = new DocumentViewModel(new FakePdfEngine(3));
        await vm.InitializeAsync(@"C:\docs\x.pdf");

        IReadOnlyList<SearchHit> hits = await vm.SearchAsync("   ", CancellationToken.None);

        Assert.Empty(hits);
    }
}
