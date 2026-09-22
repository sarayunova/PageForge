// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using System.Resources;

namespace PageForge.App.Wpf.Resources;

/// <summary>
/// Every string the user can read, resolved by key from UiStrings.resx
/// (TRD section 6: "UI strings externalized to resource files; English is the
/// only shipped language at v1").
///
/// Lookup is by key rather than through a generated class of properties. The
/// trade is deliberate: a generated file would catch a typo at compile time,
/// but it is ~250 properties of machine-written code to review forever, and it
/// pins the build to a generator. The compile-time check is replaced by
/// UiStringsTests, which fails when a key is used but not defined AND when a
/// resource is defined but never used. That is a stronger guarantee than the
/// generated class gives, because it also catches the string nobody removed.
///
/// <para>
/// A missing key returns the key itself rather than throwing. A resource lookup
/// is not worth crashing a running app over, and the guard test means a missing
/// key cannot reach a release: it fails the build first. What it must never do
/// is return empty, which would silently blank a button.
/// </para>
/// </summary>
public static class UiStrings
{
    private static readonly ResourceManager Manager = new(
        "PageForge.App.Wpf.Resources.UiStrings",
        typeof(UiStrings).Assembly);

    /// <summary>
    /// The text for <paramref name="key"/>, or the key itself if it is not
    /// defined. Never returns null or empty.
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            string? value = Manager.GetString(key, CultureInfo.CurrentUICulture);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (MissingManifestResourceException)
        {
            // The resource assembly did not ship or did not build. Showing the
            // key is ugly; failing to start is worse.
            return key;
        }
    }

    /// <summary>
    /// <see cref="Get(string)"/> with <see cref="string.Format(IFormatProvider, string, object[])"/>
    /// applied, for the messages that carry a file name or a page number.
    /// </summary>
    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
