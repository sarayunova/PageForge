// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-EDIT-01/06 unit tests for <see cref="TextEditService"/>: hit-testing picks
/// the run under the click (most specific on overlap), rewrite validates its
/// input before hitting the engine, and the fetch of page runs feeds both. The
/// fake engine provides runs and records engine calls, so no native dependency
/// is needed.
/// </summary>
public sealed class TextEditServiceTests
{
    [Fact]
    public async Task HitTest_returns_the_run_containing_the_point()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, new PdfTextRun(0, 10, 50, 110, 60, 12, true, "Helvetica", "hello"));

        PdfTextRun? hit = await TextEditService.HitTestAsync(engine, 0, 50, 55);

        Assert.NotNull(hit);
        Assert.Equal(0, hit.Index);
        Assert.Equal("hello", hit.Text);
    }

    [Fact]
    public async Task HitTest_returns_null_off_text()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, new PdfTextRun(0, 10, 50, 110, 60, 12, true, "Helvetica", "hello"));

        PdfTextRun? hit = await TextEditService.HitTestAsync(engine, 0, 200, 200);

        Assert.Null(hit);
    }

    [Fact]
    public async Task HitTest_prefers_the_smallest_overlapping_run()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, new PdfTextRun(0, 0, 0, 200, 200, 12, true, "Helvetica", "outer"));
        engine.AddStoredRun(0, new PdfTextRun(1, 0, 0, 100, 100, 12, true, "Helvetica", "inner"));

        PdfTextRun? hit = await TextEditService.HitTestAsync(engine, 0, 50, 50);

        Assert.NotNull(hit);
        Assert.Equal(1, hit.Index);
    }

    [Fact]
    public async Task RewriteRun_forwards_and_returns_receipt()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, new PdfTextRun(0, 10, 50, 110, 60, 12, true, "Helvetica", "hello"));

        PdfTextEditReceipt receipt = await TextEditService.RewriteRunAsync(engine, 0, 0, "world!");

        Assert.Equal(0, receipt.StreamIndex);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(receipt.OldOperators));
        Assert.Equal("world!", System.Text.Encoding.UTF8.GetString(receipt.NewOperators));
        var runs = await engine.ListTextRunsAsync(0);
        Assert.Equal("world!", Assert.Single(runs).Text);
    }

    [Fact]
    public async Task RewriteRun_rejects_empty_new_text()
    {
        var engine = new FakePdfEngine(1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => TextEditService.RewriteRunAsync(engine, 0, 0, string.Empty).AsTask());
    }

    [Fact]
    public async Task RewriteRun_rejects_negative_indexes()
    {
        var engine = new FakePdfEngine(1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => TextEditService.RewriteRunAsync(engine, -1, 0, "text").AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => TextEditService.RewriteRunAsync(engine, 0, -1, "text").AsTask());
    }
}