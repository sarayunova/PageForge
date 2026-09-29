// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Editing;
using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// Cover-and-replace editing of scanned text: colour sampling, geometry, the
/// encodability gate, and the undoable command, all against the fake engine.
/// The real-engine behaviour (text layer emptied, new words painted) is covered
/// by the Fidelity suite.
/// </summary>
public sealed class ScanTextTests
{
    private static byte[] Image(int width, int height, (byte B, byte G, byte R) paper)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            px[(i * 4) + 0] = paper.B;
            px[(i * 4) + 1] = paper.G;
            px[(i * 4) + 2] = paper.R;
            px[(i * 4) + 3] = 255;
        }

        return px;
    }

    private static void Fill(byte[] px, int width, int left, int top, int right, int bottom, (byte B, byte G, byte R) c)
    {
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int o = ((y * width) + x) * 4;
                px[o] = c.B;
                px[o + 1] = c.G;
                px[o + 2] = c.R;
            }
        }
    }

    [Fact]
    public void Sampler_finds_cream_paper_and_dark_ink()
    {
        const int w = 60, h = 30;
        byte[] px = Image(w, h, (B: 200, G: 235, R: 250)); // cream
        Fill(px, w, 20, 10, 40, 20, (B: 20, G: 20, R: 20)); // a dark word

        (RgbColor bg, RgbColor ink) = ScanColorSampler.Sample(px, w, h, w * 4, 20, 10, 40, 20);

        Assert.InRange(bg.R, 0.97, 0.99);
        Assert.InRange(bg.B, 0.77, 0.79);
        Assert.True(ink.Luminance < 0.15, $"ink should be dark, was {ink}");
    }

    [Fact]
    public void Sampler_is_not_thrown_by_a_neighbouring_letter_in_the_ring()
    {
        const int w = 80, h = 30;
        byte[] px = Image(w, h, (B: 255, G: 255, R: 255));
        Fill(px, w, 38, 8, 42, 22, (B: 0, G: 0, R: 0));    // the word
        Fill(px, w, 44, 8, 47, 22, (B: 0, G: 0, R: 0));    // a letter just outside, inside the ring

        (RgbColor bg, _) = ScanColorSampler.Sample(px, w, h, w * 4, 38, 8, 42, 22);

        Assert.True(bg.Luminance > 0.95, $"paper should stay white, was {bg}");
    }

    [Fact]
    public void Sampler_uses_light_ink_on_a_dark_page()
    {
        const int w = 60, h = 30;
        byte[] px = Image(w, h, (B: 30, G: 30, R: 30));
        Fill(px, w, 20, 10, 40, 20, (B: 230, G: 230, R: 230));

        (RgbColor bg, RgbColor ink) = ScanColorSampler.Sample(px, w, h, w * 4, 20, 10, 40, 20);

        Assert.True(bg.Luminance < 0.2);
        Assert.True(ink.Luminance > 0.8);
    }

    [Fact]
    public void Sampler_never_returns_ink_that_matches_the_paper()
    {
        const int w = 60, h = 30;
        byte[] px = Image(w, h, (B: 255, G: 255, R: 255)); // blank: nothing to read ink from

        (RgbColor bg, RgbColor ink) = ScanColorSampler.Sample(px, w, h, w * 4, 20, 10, 40, 20);

        Assert.True(Math.Abs(bg.Luminance - ink.Luminance) >= 0.25);
    }

    [Fact]
    public void Sampler_survives_a_box_at_the_image_edge()
    {
        const int w = 40, h = 20;
        byte[] px = Image(w, h, (B: 255, G: 255, R: 255));

        (RgbColor bg, _) = ScanColorSampler.Sample(px, w, h, w * 4, -5, -5, 60, 30);

        Assert.True(bg.Luminance > 0.9);
    }

    [Fact]
    public void Baseline_sits_on_the_letters_feet_not_the_descender_tips()
    {
        const int w = 60, h = 40;
        byte[] px = Image(w, h, (B: 255, G: 255, R: 255));
        Fill(px, w, 10, 10, 50, 25, (B: 0, G: 0, R: 0));  // the body of the letters, rows 10-24
        Fill(px, w, 20, 25, 23, 31, (B: 0, G: 0, R: 0));  // a thin descender, rows 25-30

        int? row = ScanColorSampler.FindBaselineRow(
            px, w, h, w * 4, 10, 8, 50, 34, new RgbColor(1, 1, 1), new RgbColor(0, 0, 0));

        Assert.Equal(25, row);
    }

    [Fact]
    public void Baseline_of_a_blank_box_is_unknown()
    {
        const int w = 30, h = 20;
        byte[] px = Image(w, h, (B: 255, G: 255, R: 255));

        Assert.Null(ScanColorSampler.FindBaselineRow(
            px, w, h, w * 4, 0, 0, 30, 20, new RgbColor(1, 1, 1), new RgbColor(0, 0, 0)));
    }

    [Fact]
    public void Plan_uses_a_measured_baseline_when_it_is_plausible_and_ignores_a_wild_one()
    {
        var run = new PdfTextRun(0, 100, 700, 150, 712, 11.5, false, "GlyphLessFont", "Old");

        double measured = ScanTextPlanner.Plan(run, "New", new RgbColor(1, 1, 1), new RgbColor(0, 0, 0), 703.5).BaselineY;
        double wild = ScanTextPlanner.Plan(run, "New", new RgbColor(1, 1, 1), new RgbColor(0, 0, 0), 900).BaselineY;

        Assert.Equal(703.5, measured);
        Assert.InRange(wild, 700, 712);
    }

    [Fact]
    public void Plan_covers_more_than_the_run_and_sits_the_baseline_above_its_bottom()
    {
        var run = new PdfTextRun(3, 100, 700, 220, 712, 11.5, false, "GlyphLessFont", "Old words");

        ScanTextReplacement plan = ScanTextPlanner.Plan(run, "New words", new RgbColor(1, 1, 1), new RgbColor(0, 0, 0));

        Assert.True(plan.Cover.X0 < run.X0 && plan.Cover.X1 > run.X1);
        Assert.True(plan.Cover.Y0 < run.Y0 && plan.Cover.Y1 > run.Y1);
        Assert.True(plan.BaselineY > run.Y0 && plan.BaselineY < run.Y1);
        Assert.Equal(run.X0, plan.BaselineX);
        Assert.InRange(plan.FontSize, 8, 14);
        Assert.Equal("New words", plan.Text);
    }

    [Fact]
    public void Plan_clears_the_text_layer_across_the_whole_span_the_cover_grows_to()
    {
        var run = new PdfTextRun(0, 100, 700, 150, 712, 11.5, false, "GlyphLessFont", "Old");

        ScanTextReplacement shorter = ScanTextPlanner.Plan(run, "Ok", new RgbColor(1, 1, 1), new RgbColor(0, 0, 0));
        ScanTextReplacement longer = ScanTextPlanner.Plan(
            run, "A considerably longer replacement", new RgbColor(1, 1, 1), new RgbColor(0, 0, 0));

        // Never narrower than the old word, and never taller than its own line.
        Assert.Equal(run.X1, shorter.ClearRegion.X1);
        Assert.Equal(run.Y0, shorter.ClearRegion.Y0);
        Assert.Equal(run.Y1, shorter.ClearRegion.Y1);

        // A longer text takes the neighbours' words out of the layer too, because the
        // cover hides them.
        Assert.True(longer.ClearRegion.X1 > run.X1 + 50);
        Assert.True(longer.Cover.X1 >= longer.ClearRegion.X1);
    }

    [Theory]
    [InlineData("Plain ASCII, digits 123 and (brackets)", true)]
    [InlineData("Café naïve ü", true)]
    [InlineData("Price €5", false)]        // euro is outside the Latin-1 range
    [InlineData("Журнал", false)]  // Cyrillic
    [InlineData("tab\there", false)]
    public void Encodability_matches_what_the_replacement_font_can_draw(string text, bool expected)
    {
        Assert.Equal(expected, ScanTextPlanner.IsEncodable(text));
    }

    [Fact]
    public void OcrLayerFont_is_flagged_and_ordinary_fonts_are_not()
    {
        var ocr = new PdfTextRun(0, 0, 0, 10, 10, 10, false, "ABCDEF+GlyphLessFont", "x");
        var real = new PdfTextRun(1, 0, 0, 10, 10, 10, true, "Helvetica", "x");

        Assert.True(ocr.IsHiddenOcrText);
        Assert.False(real.IsHiddenOcrText);
    }

    [Fact]
    public async Task Command_removes_old_text_then_covers_and_undo_restores_the_snapshot()
    {
        var engine = new FakePdfEngine(1);
        var plan = new ScanTextReplacement(
            new PdfRect(98, 698, 224, 714), new RgbColor(1, 1, 1), new RgbColor(0, 0, 0), 10.3, 100, 702.6, "New",
            new PdfRect(100, 700, 222, 712));
        var stack = new EditCommandStack();

        var command = new ScanTextEditCommand(engine, 0, plan);
        await stack.PushAsync(command);

        // The text layer is emptied (text only: no black bar, images and line art untouched)...
        Assert.Equal(new[] { 0 }, engine.RedactedPages);
        RedactionOptions options = Assert.IsType<RedactionOptions>(engine.LastRedactionOptions);
        Assert.False(options.BlackBox);
        Assert.Equal(RedactionImageMethod.None, options.ImageMethod);
        Assert.Equal(RedactionLineArtMethod.None, options.LineArtMethod);
        Assert.Equal(RedactionTextMethod.Remove, options.TextMethod);

        // ...and the cover was painted afterwards.
        Assert.Equal(plan.Cover, command.CoveredBox);

        await stack.UndoAsync();
        Assert.Single(engine.RestoredSnapshots);

        await stack.RedoAsync();
        Assert.Equal(2, engine.RedactedPages.Count);
    }

    [Fact]
    public async Task Command_restores_the_snapshot_if_the_cover_step_fails()
    {
        var engine = new FakePdfEngine(1) { FailCoverReplace = true };
        var plan = new ScanTextReplacement(
            new PdfRect(0, 0, 10, 10), new RgbColor(1, 1, 1), new RgbColor(0, 0, 0), 9, 0, 1, "x",
            new PdfRect(0, 0, 10, 10));
        var command = new ScanTextEditCommand(engine, 0, plan);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await command.ExecuteAsync());

        // Text was removed before the failure, so leaving it would punch a hole in the page.
        Assert.Single(engine.RestoredSnapshots);
    }
}
