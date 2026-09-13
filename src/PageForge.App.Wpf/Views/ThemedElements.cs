// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Paints elements built in code from the theme's design tokens.
///
/// Phase U0 moved the shell's palette into <c>Themes/Tokens.*.xaml</c>, but only
/// the XAML was converted. Surfaces that build their chrome in code - the form
/// and redaction panels, which lay controls out over a rendered page - kept
/// literal greys from the pre-token days (#2d2d2d, #b0b0b0, white text). Those
/// are dark-theme values, so in light theme they rendered near-invisible: pale
/// grey text on a white panel.
///
/// <see cref="FrameworkElement.SetResourceReference"/> rather than a one-off
/// <c>FindResource</c> lookup is the point: it behaves like a DynamicResource
/// binding, so these elements follow a live theme swap the same way the XAML
/// ones do, instead of freezing whichever theme happened to be active when the
/// panel was built.
/// </summary>
internal static class ThemedElements
{
    /// <summary>
    /// Points a brush property at a theme token, and returns the element so this
    /// can be chained onto an object initializer.
    /// </summary>
    /// <param name="element">The element to paint.</param>
    /// <param name="property">The brush property, e.g. <c>TextBlock.ForegroundProperty</c>.</param>
    /// <param name="tokenKey">A brush key from the token dictionaries, e.g. "ContentMutedBrush".</param>
    public static T Themed<T>(this T element, DependencyProperty property, string tokenKey)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, tokenKey);
        return element;
    }
}
