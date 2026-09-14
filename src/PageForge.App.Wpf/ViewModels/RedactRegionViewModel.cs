// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>
/// One marked redaction region, in the two forms the surface needs: where to draw
/// it, and what to call it.
///
/// The view used to build both from <see cref="PdfRect"/> inline - a Rectangle
/// positioned on a Canvas, and a TextBlock in the side list - so the PDF-to-screen
/// conversion existed twice and the two lists could disagree about what was on the
/// page. They are now two templates over one collection.
///
/// PDF coordinates have their origin at the bottom-left and are measured in
/// points; WPF measures from the top-left in device-independent pixels. Both
/// conversions happen here, once, at construction: the values are a snapshot for a
/// particular zoom, and the collection is rebuilt when that changes.
/// </summary>
public sealed class RedactRegionViewModel
{
    private const double PointsPerInch = 72.0;

    /// <param name="rect">The region in PDF points, origin bottom-left.</param>
    /// <param name="renderDpi">The DPI the page is currently rendered at.</param>
    /// <param name="pageHeightPx">The rendered page height, for the Y flip.</param>
    public RedactRegionViewModel(PdfRect rect, double renderDpi, double pageHeightPx)
    {
        double scale = renderDpi / PointsPerInch;

        Left = rect.X0 * scale;
        Top = pageHeightPx - (rect.Y1 * scale);
        Width = Math.Max(0, (rect.X1 - rect.X0) * scale);
        Height = Math.Max(0, (rect.Y1 - rect.Y0) * scale);

        // Points, not pixels: the label describes the region in the document's own
        // units, so it does not change meaning when the user zooms.
        Label = $"({rect.X0:F0}, {rect.Y0:F0}) → ({rect.X1:F0}, {rect.Y1:F0}) pt";
        AccessibleName = $"Redaction region {Label}";
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public string Label { get; }

    public string AccessibleName { get; }
}
