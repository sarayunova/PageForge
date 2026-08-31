// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure FR-EDIT-02 geometry: decides whether a text edit makes its run grow past
/// the original box by more than a configurable threshold and, when it does,
/// scans the grown box for intersection against neighboring (sibling) objects.
/// The rules here are Core-domain and UI-free so they are unit-testable directly
/// (TSD §6, FR-EDIT-02): "if the new box ... exceeds the original by more than a
/// configurable threshold, compute intersection against sibling objects'
/// bounding boxes; on intersection ... require explicit confirmation."
/// </summary>
public static class TextOverflowDetector
{
    /// <summary>
    /// The average per-character advance (in points) of a run, used to estimate
    /// where its box lands after a rewrite. Falls back to a single point per
    /// character for degenerate (empty or single-point-width) runs.
    /// </summary>
    public static double AverageAdvance(PdfTextRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        double width = Math.Max(0, run.X1 - run.X0);
        return width / Math.Max(1, run.Text?.Length ?? 1);
    }

    /// <summary>
    /// Estimates the bounding box a run will occupy after rewriting its text to
    /// <paramref name="newText"/>. In-place rewrites anchor at the run's original
    /// bottom-left and grow on the right (and, for multi-line/size changes, the
    /// top). Width is scaled by the average advance of the new text; the height
    /// is carried over from the original run. This is an estimate — the engine
    /// computes the exact recalculated box on commit.
    /// </summary>
    public static PdfRect EstimatedGrownBox(PdfTextRun run, string newText)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(newText);
        double advance = AverageAdvance(run);
        double x1 = run.X0 + (advance * Math.Max(1, newText.Length));
        return new PdfRect(run.X0, run.Y0, x1, run.Y1);
    }

    /// <summary>
    /// Analyzes an edit of <paramref name="original"/> whose resulting box is
    /// <paramref name="grownBox"/> against the <paramref name="siblings"/> (the
    /// neighboring objects on the page — the caller excludes the run being
    /// edited). Returns null when <paramref name="grownBox"/> does not actually
    /// extend past the original (no growth to warn about), otherwise a
    /// <see cref="TextEditOverflowResult"/> carrying the growth metrics and any
    /// collisions.
    /// </summary>
    public static TextEditOverflowResult Analyze(
        PdfTextRun original,
        PdfRect grownBox,
        IReadOnlyList<PdfTextRun> siblings,
        OverflowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(siblings);

        OverflowOptions opts = options ?? OverflowOptions.Default;

        PdfRect originalBox = new(original.X0, original.Y0, original.X1, original.Y1);
        double growthX = Math.Max(0, grownBox.X1 - originalBox.X1);
        double growthY = Math.Max(0, grownBox.Y1 - originalBox.Y1);
        double maxGrowth = Math.Max(growthX, growthY);

        if (maxGrowth < Math.Max(1e-9, opts.MinGrowthPoints))
        {
            return new TextEditOverflowResult
            {
                GrewBeyondThreshold = false,
                GrowthFraction = 0,
                GrowthX = growthX,
                GrowthY = growthY,
                GrownBox = grownBox,
                EstimatedBox = grownBox,
                Collisions = Array.Empty<CollisionHit>(),
            };
        }

        double growthFraction = Math.Max(
            originalBox.Width > 0 ? growthX / originalBox.Width : double.MaxValue,
            originalBox.Height > 0 ? growthY / originalBox.Height : double.MaxValue);

        bool grewBeyondThreshold = growthFraction > opts.GrowthThreshold;

        var collisions = new List<CollisionHit>();
        if (grewBeyondThreshold)
        {
            foreach (PdfTextRun sibling in siblings)
            {
                PdfRect siblingBox = new(sibling.X0, sibling.Y0, sibling.X1, sibling.Y1);
                double overlap = grownBox.OverlapArea(siblingBox);
                if (overlap > 1e-9)
                {
                    collisions.Add(new CollisionHit(sibling, overlap));
                }
            }
        }

        return new TextEditOverflowResult
        {
            GrewBeyondThreshold = grewBeyondThreshold,
            GrowthFraction = growthFraction,
            GrowthX = growthX,
            GrowthY = growthY,
            GrownBox = grownBox,
            EstimatedBox = grownBox,
            Collisions = collisions,
        };
    }
}
