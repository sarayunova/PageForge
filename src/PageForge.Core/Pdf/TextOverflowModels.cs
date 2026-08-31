// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// An axis-aligned rectangle in PDF page coordinates (origin bottom-left,
/// points). Used by the FR-EDIT-02 overflow/collision logic so the geometry is
/// pure and unit-testable without dragging a run record everywhere.
/// </summary>
public readonly record struct PdfRect(double X0, double Y0, double X1, double Y1)
{
    public double Width => Math.Max(0, X1 - X0);

    public double Height => Math.Max(0, Y1 - Y0);

    /// <summary>The area of the intersection with <paramref name="other"/>, or 0 when they do not overlap.</summary>
    public double OverlapArea(PdfRect other)
    {
        double ix = Math.Max(0, Math.Min(X1, other.X1) - Math.Max(X0, other.X0));
        double iy = Math.Max(0, Math.Min(Y1, other.Y1) - Math.Max(Y0, other.Y0));
        return ix * iy;
    }
}

/// <summary>
/// Tuning for the FR-EDIT-02 growth check. Growth beyond the original box by more
/// than <see cref="GrowthThreshold"/> (as a fraction of the original extent) and
/// at least <see cref="MinGrowthPoints"/> is considered overflow and triggers the
/// collision scan. These are Core-domain defaults; a shell may pass its own
/// values without changing the geometry rules.
/// </summary>
public sealed record OverflowOptions
{
    /// <summary>Fraction of the original extent beyond which growth is "overflow". Default 0.25 (25%).</summary>
    public double GrowthThreshold { get; init; } = 0.25;

    /// <summary>Absolute floor (in points) below which growth is never flagged. Default 2 points.</summary>
    public double MinGrowthPoints { get; init; } = 2.0;

    public static OverflowOptions Default { get; } = new();
}

/// <summary>
/// One sibling object whose bounding box is overlapped by a grown text box
/// (FR-EDIT-02). <see cref="OverlapArea"/> is in square points.
/// </summary>
public sealed record CollisionHit(PdfTextRun Sibling, double OverlapArea);

/// <summary>
/// The result of a FR-EDIT-02 growth/collision analysis. When
/// <see cref="GrewBeyondThreshold"/> is true, the analysis scanned the grown box
/// against the sibling objects and populated <see cref="Collisions"/>. A
/// collision (growth beyond threshold AND at least one overlapping sibling) means
/// the edit must not silently overlap: surface the warning and require explicit
/// confirmation (<see cref="NeedsConfirmation"/>).
/// </summary>
public sealed record TextEditOverflowResult
{
    /// <summary>True when the grown box exceeds the original by more than the threshold.</summary>
    public required bool GrewBeyondThreshold { get; init; }

    /// <summary>The largest growth fraction across width and height, as a fraction of the original extent.</summary>
    public required double GrowthFraction { get; init; }

    /// <summary>How far the grown box extends past the original on the right (positive = grew).</summary>
    public required double GrowthX { get; init; }

    /// <summary>How far the grown box extends past the original on the top (positive = grew).</summary>
    public required double GrowthY { get; init; }

    /// <summary>The grown box as analyzed (for rendering a warning outline).</summary>
    public required PdfRect GrownBox { get; init; }

    /// <summary>Overlapping sibling objects; non-empty only when <see cref="GrewBeyondThreshold"/>.</summary>
    public required IReadOnlyList<CollisionHit> Collisions { get; init; }

    /// <summary>The estimated grown box that ran the analysis (may differ from the engine's exact recalc).</summary>
    public required PdfRect EstimatedBox { get; init; }

    /// <summary>
    /// True when the edit would both overflow the original box and collide with a
    /// sibling — the case that must surface a warning and gate on confirmation
    /// (FR-EDIT-02). Pure overflow without collision grows the box cleanly and is
    /// safe to commit.
    /// </summary>
    public bool NeedsConfirmation => GrewBeyondThreshold && Collisions.Count > 0;
}

/// <summary>
/// The outcome of the FR-EDIT-02 confirmation gateway in
/// <see cref="TextEditService.PrepareRewriteAsync"/>. Carries the run the edit
/// targets and the overflow/collision analysis so the shell can render a warning
/// outline and either confirm (commit the rewrite) or reject it.
/// </summary>
public sealed record PreparedTextEdit(int RunIndex, string NewText, TextEditOverflowResult Analysis)
{
    /// <summary>True when the edit must not be committed without explicit user confirmation (FR-EDIT-02).</summary>
    public bool NeedsConfirmation => Analysis.NeedsConfirmation;

    /// <summary>The box the edit would occupy (for rendering a warning outline), or null when it fits without collision.</summary>
    public PdfRect? WarningBox => NeedsConfirmation ? Analysis.EstimatedBox : null;
}
