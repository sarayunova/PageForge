// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>
/// The outline drawn over one form field on the page.
///
/// Geometry only: it marks where a field is, and is deliberately not hit-testable.
/// The interactive control for the field lives in the side panel, and is still
/// built in code because it carries a live value and a write-back - that is a
/// different conversion and has not been done yet.
///
/// The screen conversion comes from <see cref="PageBoxGeometry"/>, the same one the
/// redaction overlay uses, so the Y flip exists once rather than once per surface.
/// </summary>
public sealed class FormFieldBoxViewModel
{
    public FormFieldBoxViewModel(PdfFormField field, double renderDpi, double pageHeightPx)
    {
        ScreenBox box = PageBoxGeometry.ToScreen(field.Bounds, renderDpi, pageHeightPx);
        Left = box.Left;
        Top = box.Top;
        Width = box.Width;
        Height = box.Height;

        AccessibleName = $"{field.Label} field outline";
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public string AccessibleName { get; }
}
