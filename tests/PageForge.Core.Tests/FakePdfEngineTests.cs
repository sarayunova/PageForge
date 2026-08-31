// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

public sealed class FakePdfEngineTests
{
    [Fact]
    public async Task OpenAsync_returns_document_info_with_page_count()
    {
        await using IPdfEngine engine = new FakePdfEngine(3);

        PdfDocumentInfo info = await engine.OpenAsync(@"C:\docs\contract.pdf");

        Assert.Equal(3, info.PageCount);
        Assert.Equal("contract.pdf", info.DisplayName);
    }

    [Fact]
    public async Task GetPageSizeAsync_is_deterministic_letter_sized_region()
    {
        await using IPdfEngine engine = new FakePdfEngine(1);

        PdfPageRegion region = await engine.GetPageSizeAsync(0);

        Assert.Equal(595, region.WidthPt, 3);
        Assert.Equal(842, region.HeightPt, 3);
    }

    [Fact]
    public async Task Render_is_lazy_one_call_per_render()
    {
        await using IPdfEngine engine = new FakePdfEngine(5);
        await engine.RenderPageToPngAsync(0, 96);
        await engine.RenderPageToPngAsync(4, 72);

        await engine.RenderPageToPngAsync(0, 96);
    }

    [Fact]
    public async Task Render_is_serialized_even_across_concurrent_awaits()
    {
        FakePdfEngine engine = new(10, maxRenderCalls: 4);
        await using (engine)
        {
            await Task.WhenAll(
                engine.RenderPageToPngAsync(0, 72).AsTask(),
                engine.RenderPageToPngAsync(1, 72).AsTask(),
                engine.RenderPageToPngAsync(2, 72).AsTask(),
                engine.RenderPageToPngAsync(3, 72).AsTask());
        }
    }

    [Fact]
    public async Task Dispose_marks_engine_as_disposed()
    {
        FakePdfEngine engine = new(1);
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.OpenAsync("x.pdf").AsTask());
    }
}