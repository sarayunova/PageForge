// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows.Markup;

namespace PageForge.App.Wpf.Resources;

/// <summary>
/// The XAML side of <see cref="UiStrings"/>: <c>{loc:Str Toolbar_NextPage_Name}</c>.
///
/// Resolution happens once, when the markup is parsed, because these strings do
/// not change while the app runs - v1 ships English only and there is no
/// language switch to react to. If one is ever added, this is the single place
/// that has to start producing a binding instead of a value.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    /// <summary>Parameterless form, for <c>{loc:Str Key=...}</c>.</summary>
    public StrExtension()
    {
    }

    /// <summary>Positional form, for the usual <c>{loc:Str Key}</c>.</summary>
    /// <param name="key">The resource key to resolve.</param>
    public StrExtension(string key) => Key = key;

    /// <summary>The resource key in UiStrings.resx.</summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) => UiStrings.Get(Key);
}
