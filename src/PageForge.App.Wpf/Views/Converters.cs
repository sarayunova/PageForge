// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Converts a PDF page dimension in points plus a render DPI into device pixels.
///
/// A page placeholder cannot take its size from the page image, because the
/// placeholder is what is shown while that image does not exist yet - binding to
/// it would give a zero-sized skeleton, which is indistinguishable from no
/// skeleton at all. The document knows each page's size in points long before it
/// is rendered, so the placeholder is sized from the paper rather than from the
/// picture of it.
///
/// A multi-binding rather than a computed view-model property so it re-evaluates
/// on zoom for free: RenderDpi already raises change, and the slot would
/// otherwise have to subscribe to its own image just to forward it.
/// </summary>
public sealed class PointsToPixelsConverter : IMultiValueConverter
{
    private const double PointsPerInch = 72.0;

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double points || values[1] is not double dpi)
        {
            // NaN is "Auto" to WPF. Falling back to 0 would collapse the
            // placeholder, which reads as a missing page rather than one that has
            // not arrived yet.
            return double.NaN;
        }

        return points * dpi / PointsPerInch;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the bound count is zero. For the empty-state text beside a
/// bound list, so it cannot be left on screen by a code path that forgot to clear
/// it - which is the failure mode a hand-built list invites.</summary>
public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the bound boolean is false. For placeholders, which show
/// precisely while the thing they stand in for is absent.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts the clockwise rotation in degrees (0/90/180/270) to a
/// <see cref="RotateTransform"/> used to lay out a rendered page.
/// </summary>
public sealed class RotationToTransformConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int degrees = value is int d ? d : 0;
        return new RotateTransform(degrees);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats the zoom (1.0) as a percent string (100%).</summary>
public sealed class ZoomPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double zoom = value is double z ? z : 1.0;
        return ((int)Math.Round(zoom * 100.0)).ToString(CultureInfo.InvariantCulture) + "%";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Indents an outline row by (depth * 14) DIPs.</summary>
public sealed class IndentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int depth ? Math.Max(0, depth) * 14.0 : 0.0) + (parameter as string == "plus" ? 0.0 : 0.0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
