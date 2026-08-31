// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PageForge.App.Wpf.Views;

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
