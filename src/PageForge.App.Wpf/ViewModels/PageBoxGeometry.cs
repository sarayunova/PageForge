// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>Where a PDF rectangle lands on screen, in device-independent pixels
/// measured from the top-left.</summary>
public readonly record struct ScreenBox(double Left, double Top, double Width, double Height);

/// <summary>
/// The one place PDF rectangles become screen rectangles.
///
/// Every overlay in the shell needs this: redaction regions, form-field outlines,
/// object-edit boxes. Each used to do it inline, so the conversion existed once per
/// surface and a fix to one would never reach the others. PDF measures in points
/// from a bottom-left origin; WPF measures in DIPs from the top-left, so both a
/// scale and a Y flip are involved - and the flip is the half that is easy to get
/// wrong in a way nothing notices, because the box simply appears on the other side
/// of the page.
/// </summary>
public static class PageBoxGeometry
{
    private const double PointsPerInch = 72.0;

    /// <param name="rect">The rectangle in PDF points, origin bottom-left.</param>
    /// <param name="renderDpi">The DPI the page is currently rendered at.</param>
    /// <param name="pageHeightPx">The rendered page height, which the flip is about.</param>
    public static ScreenBox ToScreen(PdfRect rect, double renderDpi, double pageHeightPx)
    {
        double scale = renderDpi / PointsPerInch;

        return new ScreenBox(
            Left: rect.X0 * scale,
            Top: pageHeightPx - (rect.Y1 * scale),
            Width: Math.Max(0, (rect.X1 - rect.X0) * scale),
            Height: Math.Max(0, (rect.Y1 - rect.Y0) * scale));
    }
}
