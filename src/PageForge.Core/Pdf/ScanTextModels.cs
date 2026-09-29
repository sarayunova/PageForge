// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>An RGB colour with components in [0,1].</summary>
public readonly record struct RgbColor(double R, double G, double B)
{
    /// <summary>Perceptual brightness in [0,1] (Rec. 709 weights).</summary>
    public double Luminance => (0.2126 * R) + (0.7152 * G) + (0.0722 * B);
}

/// <summary>
/// One cover-and-replace edit of scanned text: paint <see cref="Cover"/> in the
/// page's <see cref="Background"/> colour, then show <see cref="Text"/> on top in
/// <see cref="Ink"/> with its baseline origin at
/// (<see cref="BaselineX"/>, <see cref="BaselineY"/>). All coordinates are PDF
/// points, origin bottom-left.
///
/// <see cref="ClearRegion"/> is where the old OCR words are taken out of the text
/// layer: the edited run's own box, stretched to the right by as much as the cover
/// will grow, so a search never finds words the page no longer shows.
///
/// This is deliberately not a rewrite of the scan: the image underneath is left
/// exactly as it was, so the original words are hidden rather than erased.
/// </summary>
public sealed record ScanTextReplacement(
    PdfRect Cover,
    RgbColor Background,
    RgbColor Ink,
    double FontSize,
    double BaselineX,
    double BaselineY,
    string Text,
    PdfRect ClearRegion);

/// <summary>
/// Turns an OCR text run plus sampled colours into a <see cref="ScanTextReplacement"/>.
/// Pure geometry so it can be unit-tested without an engine.
/// </summary>
public static class ScanTextPlanner
{
    // The OCR text layer reports a font size close to the real one for ordinary
    // text, whereas the run's BOX height is only as tall as its ink: for an all-caps
    // word that is the cap height (about 0.72 em), which would make the replacement
    // visibly smaller than its neighbours. Helvetica is a touch narrower than most
    // scanned faces, so a small allowance keeps the two looking alike.
    internal const double FontSizeFactor = 1.05;

    // Fraction of the box height the baseline sits above the bottom of the box when
    // the ink itself could not be measured (descender room).
    internal const double BaselineRise = 0.22;

    // Extra room around the old words so anti-aliased edges of the scan are covered.
    internal const double PadFraction = 0.12;

    /// <param name="measuredBaselineY">Where the baseline actually is (PDF points), when the
    /// ink could be measured; otherwise a fixed rise above the box bottom is assumed.</param>
    public static ScanTextReplacement Plan(
        PdfTextRun run, string newText, RgbColor background, RgbColor ink, double? measuredBaselineY = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(newText);

        double height = Math.Max(1.0, run.Y1 - run.Y0);
        double pad = height * PadFraction;
        double fontSize = Math.Max(run.FontSize > 0 ? run.FontSize : height / 0.72, 1.0) * FontSizeFactor;

        // Mirror the engine's growth rule (it widens the cover to the right when the
        // text overruns it) so the text layer can be cleared across the same span.
        double textEnd = run.X0 + (HelveticaWidth(newText) * fontSize);
        double coverRight = Math.Max(run.X1 + pad, textEnd + 1.0);

        var cover = new PdfRect(run.X0 - pad, run.Y0 - pad, coverRight, run.Y1 + pad);
        var clear = new PdfRect(run.X0, run.Y0, Math.Max(run.X1, coverRight - pad), run.Y1);
        return new ScanTextReplacement(
            cover,
            background,
            ink,
            FontSize: fontSize,
            BaselineX: run.X0,
            BaselineY: measuredBaselineY is double b && b > run.Y0 - pad && b < run.Y1 ? b : run.Y0 + (height * BaselineRise),
            Text: newText,
            ClearRegion: clear);
    }

    // Helvetica advance widths (Adobe AFM, units of 1/1000 em) for 0x20-0x7E. Latin-1
    // letters above that range are close enough to their base letters to use 556.
    private static readonly int[] HelveticaAscii =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    };

    /// <summary>The width of <paramref name="text"/> in Helvetica, in ems (multiply by the font size).</summary>
    internal static double HelveticaWidth(string text)
    {
        double units = 0;
        foreach (char c in text)
        {
            units += c >= 0x20 && c <= 0x7E ? HelveticaAscii[c - 0x20] : 556;
        }

        return units / 1000.0;
    }

    /// <summary>
    /// True when every character can be drawn by the replacement font (the Latin-1
    /// range PDF's WinAnsi encoding covers). The engine enforces the same rule; this
    /// lets the UI say so before asking for confirmation.
    /// </summary>
    public static bool IsEncodable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c < 0x20 || (c > 0x7E && c < 0xA0) || c > 0xFF)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Estimates the page's paper colour and ink colour around a run from rendered
/// pixels, so the cover blends into a scan whose "white" is really cream or grey.
/// </summary>
public static class ScanColorSampler
{
    /// <param name="bgra">Pixels, 4 bytes each in B,G,R,A order.</param>
    /// <param name="stride">Bytes per row.</param>
    /// <param name="left">Text box in pixels (inclusive left/top, exclusive right/bottom).</param>
    public static (RgbColor Background, RgbColor Ink) Sample(
        ReadOnlySpan<byte> bgra, int width, int height, int stride,
        int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, width);
        right = Math.Clamp(right, 0, width);
        top = Math.Clamp(top, 0, height);
        bottom = Math.Clamp(bottom, 0, height);

        // Paper: the median of a ring just outside the box. The ring is far more
        // likely to be blank than the box, and a median shrugs off the odd
        // neighbouring letter that the ring clips.
        int ring = Math.Max(2, (bottom - top) / 4);
        int rl = Math.Max(0, left - ring);
        int rr = Math.Min(width, right + ring);
        int rt = Math.Max(0, top - ring);
        int rb = Math.Min(height, bottom + ring);

        var rs = new List<byte>();
        var gs = new List<byte>();
        var bs = new List<byte>();
        for (int y = rt; y < rb; y++)
        {
            for (int x = rl; x < rr; x++)
            {
                if (x >= left && x < right && y >= top && y < bottom)
                {
                    continue;
                }

                int o = (y * stride) + (x * 4);
                bs.Add(bgra[o]);
                gs.Add(bgra[o + 1]);
                rs.Add(bgra[o + 2]);
            }
        }

        RgbColor background = rs.Count == 0
            ? new RgbColor(1, 1, 1)
            : new RgbColor(Median(rs) / 255.0, Median(gs) / 255.0, Median(bs) / 255.0);

        // Ink: the mean of the darkest few percent of pixels inside the box.
        var inside = new List<(double Lum, byte R, byte G, byte B)>();
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int o = (y * stride) + (x * 4);
                byte b = bgra[o];
                byte g = bgra[o + 1];
                byte r = bgra[o + 2];
                inside.Add((new RgbColor(r / 255.0, g / 255.0, b / 255.0).Luminance, r, g, b));
            }
        }

        RgbColor ink;
        if (inside.Count == 0)
        {
            ink = background.Luminance > 0.5 ? new RgbColor(0, 0, 0) : new RgbColor(1, 1, 1);
        }
        else
        {
            inside.Sort((a, b) => a.Lum.CompareTo(b.Lum));
            int take = Math.Max(1, inside.Count / 12);
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < take; i++)
            {
                r += inside[i].R;
                g += inside[i].G;
                b += inside[i].B;
            }

            ink = new RgbColor(r / take / 255.0, g / take / 255.0, b / take / 255.0);
        }

        // A box with no real ink (blank region, or ink no darker than paper) would
        // give invisible text; fall back to a colour that contrasts with the paper.
        if (Math.Abs(background.Luminance - ink.Luminance) < 0.25)
        {
            ink = background.Luminance > 0.5 ? new RgbColor(0, 0, 0) : new RgbColor(1, 1, 1);
        }

        return (background, ink);
    }

    /// <summary>
    /// Finds the baseline of the text in a box: the last pixel row, going down, that
    /// still holds a substantial share of the densest row's ink. Descenders and
    /// commas are sparse, so they fall below the threshold and the baseline stays on
    /// the letters' feet. Returns the row just below the baseline (an exclusive
    /// bottom edge in image rows), or null when the box holds no ink.
    /// </summary>
    public static int? FindBaselineRow(
        ReadOnlySpan<byte> bgra, int width, int height, int stride,
        int left, int top, int right, int bottom, RgbColor background, RgbColor ink)
    {
        left = Math.Clamp(left, 0, width);
        right = Math.Clamp(right, 0, width);
        top = Math.Clamp(top, 0, height);
        bottom = Math.Clamp(bottom, 0, height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        // A pixel counts as ink when it is nearer the ink colour than the paper.
        double cut = (background.Luminance + ink.Luminance) / 2.0;
        bool darkInk = ink.Luminance < background.Luminance;
        var counts = new int[bottom - top];
        int max = 0;
        for (int y = top; y < bottom; y++)
        {
            int n = 0;
            for (int x = left; x < right; x++)
            {
                int o = (y * stride) + (x * 4);
                double lum = new RgbColor(bgra[o + 2] / 255.0, bgra[o + 1] / 255.0, bgra[o] / 255.0).Luminance;
                if (darkInk ? lum < cut : lum > cut)
                {
                    n++;
                }
            }

            counts[y - top] = n;
            max = Math.Max(max, n);
        }

        if (max == 0)
        {
            return null;
        }

        double threshold = Math.Max(1, max * 0.35);
        for (int i = counts.Length - 1; i >= 0; i--)
        {
            if (counts[i] >= threshold)
            {
                return top + i + 1;
            }
        }

        return null;
    }

    private static byte Median(List<byte> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
