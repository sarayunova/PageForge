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
    /// <remarks>Takes no page height, unlike the redaction region: without a flip
    /// there is nothing to measure back from.</remarks>
    public FormFieldBoxViewModel(PdfFormField field, double renderDpi)
    {
        // No Y flip, unlike the redaction overlay: PdfFormField.Bounds already
        // arrive top-down. The old code flipped them, which mirrored every outline
        // onto the opposite half of the page - invisible until now, because this
        // surface never rendered the page underneath it.
        //
        // Measured against the fixture rather than assumed. In
        // tools/sample-pdf/corpus/form-application.pdf the FullName field is
        // 120..142 and Consent is 182..202; read top-down at 96 DPI those are 160px
        // and 243px from the page top, which is exactly where their labels sit. Read
        // bottom-up they land near the page foot, hundreds of pixels from anything.
        double scale = renderDpi / 72.0;
        Left = field.Bounds.X0 * scale;
        Top = field.Bounds.Y0 * scale;
        Width = Math.Max(0, (field.Bounds.X1 - field.Bounds.X0) * scale);
        Height = Math.Max(0, (field.Bounds.Y1 - field.Bounds.Y0) * scale);

        AccessibleName = $"{field.Label} field outline";
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public string AccessibleName { get; }
}
