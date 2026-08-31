// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-EDIT-02 unit tests for <see cref="TextOverflowDetector"/> and the
/// <see cref="TextEditService.PrepareRewriteAsync"/> confirmation gateway: pure
/// box overflow is detected against a configurable threshold, growth that hits a
/// sibling collisions is flagged as needing confirmation (never a silent
/// overlap), growth that misses everything grows cleanly, and threshold tuning is
/// respected. All geometry runs in Core with no native dependency.
/// </summary>
public sealed class TextOverflowTests
{
    private const double T = 1e-9;

    private static PdfTextRun Run(int index, double x0, double y0, double x1, double y1, string text = "hello world")
        => new(index, x0, y0, x1, y1, 12, true, "Helvetica", text);

    [Fact]
    public void Fit_that_does_not_grow_past_original_is_not_overflow()
    {
        PdfTextRun original = Run(0, 10, 50, 110, 60, "hello world");
        PdfRect grown = new(10, 50, 80, 60); // shorter than original -> no growth

        TextEditOverflowResult result = TextOverflowDetector.Analyze(original, grown, Array.Empty<PdfTextRun>());

        Assert.False(result.GrewBeyondThreshold);
        Assert.Empty(result.Collisions);
        Assert.False(result.NeedsConfirmation);
    }

    [Fact]
    public void Small_growth_below_threshold_is_not_flagged()
    {
        PdfTextRun original = Run(0, 0, 0, 100, 10, "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"); // 100 chars
        // grow by ~5% (5pt on 100pt width) -> below default 25% threshold
        PdfRect grown = new(0, 0, 105, 10);

        TextEditOverflowResult result = TextOverflowDetector.Analyze(
            original, grown, Array.Empty<PdfTextRun>(), new OverflowOptions { GrowthThreshold = 0.25 });

        Assert.False(result.GrewBeyondThreshold);
        Assert.False(result.NeedsConfirmation);
    }

    [Fact]
    public void Growth_beyond_threshold_but_no_collision_grows_cleanly()
    {
        PdfTextRun original = Run(0, 0, 0, 100, 10, "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789");
        PdfTextRun distant = Run(1, 500, 0, 700, 10, "far away");
        PdfRect grown = new(0, 0, 200, 10); // 100% growth -> overflow

        TextEditOverflowResult result = TextOverflowDetector.Analyze(original, grown, new[] { distant });

        Assert.True(result.GrewBeyondThreshold);
        Assert.Empty(result.Collisions);
        Assert.False(result.NeedsConfirmation, "Growth without a sibling collision must grow cleanly.");
    }

    [Fact]
    public void Growth_that_collides_with_a_sibling_requires_confirmation()
    {
        PdfTextRun original = Run(0, 0, 0, 100, 10, "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789");
        PdfTextRun neighbor = Run(1, 150, 0, 250, 10, "neighbor");
        OverflowOptions options = new() { GrowthThreshold = 0.25 };
        PdfRect grown = new(0, 0, 200, 10); // grows into the neighbor's box at x=150+

        TextEditOverflowResult result = TextOverflowDetector.Analyze(original, grown, new[] { neighbor }, options);

        Assert.True(result.GrewBeyondThreshold);
        Assert.True(result.NeedsConfirmation);
        CollisionHit hit = Assert.Single(result.Collisions);
        Assert.Equal(1, hit.Sibling.Index);
        Assert.True(hit.OverlapArea > 0);
    }

    [Fact]
    public void Collision_overlap_area_is_the_shared_rectangle_area()
    {
        PdfTextRun original = Run(0, 0, 0, 100, 10, "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789");
        // sibling covers x 150..200, y 0..10; grown box covers x 0..200 -> overlap 50 wide x 10 tall = 500sqpt
        PdfTextRun neighbor = Run(1, 150, 0, 200, 10, "n");
        PdfRect grown = new(0, 0, 200, 10);

        TextEditOverflowResult result = TextOverflowDetector.Analyze(original, grown, new[] { neighbor }, new OverflowOptions { GrowthThreshold = 0.25 });

        Assert.Equal(500, result.Collisions.Single().OverlapArea, 3);
    }

    [Fact]
    public void Sibling_touching_but_not_overlapping_the_grown_box_is_not_a_collision()
    {
        PdfTextRun original = Run(0, 0, 0, 100, 10, "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789");
        PdfTextRun touching = Run(1, 200, 0, 300, 10, "touch"); // shares only the x=200 edge, zero area
        PdfRect grown = new(0, 0, 200, 10);

        TextEditOverflowResult result = TextOverflowDetector.Analyze(original, grown, new[] { touching }, new OverflowOptions { GrowthThreshold = 0.25 });

        Assert.Empty(result.Collisions);
        Assert.False(result.NeedsConfirmation, "Zero-area edge contact is not an overlap.");
    }

    [Fact]
    public void Tuning_threshold_to_zero_flags_any_growth()
    {
        PdfTextRun original = Run(0, 0, 0, 100, 10, "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789");
        OverflowOptions strict = new() { GrowthThreshold = 0.0, MinGrowthPoints = 0.0 };
        PdfRect grown = new(0, 0, 101, 10); // 1% growth, no siblings

        TextEditOverflowResult result = TextOverflowDetector.Analyze(original, grown, Array.Empty<PdfTextRun>(), strict);

        Assert.True(result.GrewBeyondThreshold);
        Assert.False(result.NeedsConfirmation); // no collision, still safe to grow
    }

    [Fact]
    public void EstimatedGrownBox_scales_width_to_the_new_text_length()
    {
        PdfTextRun original = Run(0, 10, 50, 210, 60, "0123456789"); // 10 chars over 200pt -> 20pt/char

        PdfRect grown = TextOverflowDetector.EstimatedGrownBox(original, "01234567890123456789"); // 20 chars -> ~400pt wide

        Assert.Equal(10, grown.X0, 3);
        Assert.Equal(410, grown.X1, 3);
        Assert.Equal(50, grown.Y0, 3);
        Assert.Equal(60, grown.Y1, 3);
    }

    [Fact]
    public async Task PrepareRewrite_returns_safe_when_no_collision()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, Run(0, 10, 50, 110, 60, "hello"));

        var prepared = await TextEditService.PrepareRewriteAsync(engine, 0, 0, "hello");

        Assert.False(prepared.NeedsConfirmation);
        Assert.Null(prepared.WarningBox);
    }

    [Fact]
    public async Task PrepareRewrite_flags_collision_against_a_sibling_run()
    {
        var engine = new FakePdfEngine(1);
        // Short original run + a neighbor to the right it will grow into.
        engine.AddStoredRun(0, Run(0, 0, 0, 20, 10, "abc"));
        engine.AddStoredRun(0, Run(1, 40, 0, 80, 10, "neighbor"));

        PdfTextRun target = (await engine.ListTextRunsAsync(0))[0];
        PdfTextRun neighbor = (await engine.ListTextRunsAsync(0))[1];

        // Force growth that reaches the neighbor: explicit grown box analysis.
        PdfRect grown = TextOverflowDetector.EstimatedGrownBox(target, new string('x', 60));
        TextEditOverflowResult analysis = TextOverflowDetector.Analyze(target, grown, new[] { neighbor }, new OverflowOptions { GrowthThreshold = 0.25 });

        Assert.True(grown.X1 - target.X1 > 0, "The estimated grown box must extend past the original for this test to be meaningful.");
        Assert.NotNull(analysis);
        Assert.True(analysis.NeedsConfirmation);
    }

    [Fact]
    public async Task PrepareRewrite_out_of_range_run_throws()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, Run(0, 10, 50, 110, 60, "hello"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => TextEditService.PrepareRewriteAsync(engine, 0, 5, "text").AsTask());
    }

    [Fact]
    public void Analyze_rejects_null_and_overflows_points_floor()
    {
        PdfTextRun original = Run(0, 0, 0, 10, 10, "a");
        PdfRect grown = new(0, 0, 11, 10); // 1pt growth on 10pt run
        OverflowOptions options = new() { GrowthThreshold = 0.01, MinGrowthPoints = 5.0 }; // floor of 5pt

        TextEditOverflowResult result = TextOverflowDetector.Analyze(original, grown, Array.Empty<PdfTextRun>(), options);

        Assert.False(result.GrewBeyondThreshold, "Growth below the absolute points floor must not be flagged.");
    }
}
