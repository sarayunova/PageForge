// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure, unit-testable transform math for FR-EDIT-04 image/vector move and
/// resize. Page coordinates are bottom-left origin, points, matching
/// <see cref="PdfRect"/>.
/// </summary>
public static class PageObjectGeometry
{
    /// <summary>
    /// Translates <paramref name="rect"/> by the given deltas, preserving its
    /// width and height exactly.
    /// </summary>
    public static PdfRect Translate(PdfRect rect, double dx, double dy) => new(rect.X0 + dx, rect.Y0 + dy, rect.X1 + dx, rect.Y1 + dy);

    /// <summary>
    /// Resizes <paramref name="rect"/> to an absolute target <paramref name="width"/>
    /// by <paramref name="height"/> without moving its bottom-left corner
    /// (<paramref name="rect"/>.X0/Y0 stay fixed).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A target dimension is negative.</exception>
    public static PdfRect ResizeFromBottomLeft(PdfRect rect, double width, double height)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Resize dimensions must be non-negative.");
        }

        return new PdfRect(rect.X0, rect.Y0, rect.X0 + width, rect.Y0 + height);
    }

    /// <summary>
    /// Resizes <paramref name="rect"/> by a scale factor around its center,
    /// preserving the current centre point. Negative factors are rejected.
    /// </summary>
    public static PdfRect ScaleFromCenter(PdfRect rect, double scale)
    {
        if (scale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be non-negative.");
        }

        double cx = (rect.X0 + rect.X1) / 2;
        double cy = (rect.Y0 + rect.Y1) / 2;
        double halfW = rect.Width * scale / 2;
        double halfH = rect.Height * scale / 2;
        return new PdfRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
    }

    /// <summary>
    /// Resizes <paramref name="rect"/> to a target width, preserving the current
    /// aspect ratio, anchored at the bottom-left corner.
    /// </summary>
    public static PdfRect ResizeToWidthAspect(PdfRect rect, double width)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Resize width must be non-negative.");
        }

        double aspect = rect.Width > 0 ? rect.Height / rect.Width : 1;
        return new PdfRect(rect.X0, rect.Y0, rect.X0 + width, rect.Y0 + width * aspect);
    }

    /// <summary>
    /// Whether <paramref name="target"/> (the destination box requested by the
    /// user) preserves the source aspect ratio within <paramref name="tolerance"/>.
    /// Used to decide whether a resize is uniformly scaled (image-safe) or warped.
    /// </summary>
    public static bool KeepsAspectRatio(PdfRect source, PdfRect target, double tolerance = 0.01)
    {
        if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return false;
        }

        double sourceAspect = source.Width / source.Height;
        double targetAspect = target.Width / target.Height;
        return Math.Abs(sourceAspect - targetAspect) <= tolerance;
    }
}
