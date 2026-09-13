// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PageForge.App.Wpf.Themes;

/// <summary>Which token dictionary the shell is showing.</summary>
public enum AppTheme
{
    /// <summary>Follow the Windows "Choose your default app mode" setting, and keep
    /// following it if the user changes it while PageForge is running.</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// Loads and swaps the theme token dictionary at runtime.
///
/// Tokens.Dark.xaml and Tokens.Light.xaml declare the same key set, so swapping
/// one merged dictionary for the other re-themes the whole shell — provided every
/// consumer references the tokens with DynamicResource. A StaticResource reference
/// to a token is resolved once at load and would silently keep the old theme's
/// brush, which is why the token dictionaries are documented as DynamicResource-only
/// and Typography.xaml (never swapped) is the only one safe for StaticResource.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly Uri DarkTokens =
        new("pack://application:,,,/Themes/Tokens.Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightTokens =
        new("pack://application:,,,/Themes/Tokens.Light.xaml", UriKind.Absolute);

    private static ResourceDictionary? _current;

    /// <summary>The theme the user asked for, which may be <see cref="AppTheme.System"/>.</summary>
    public static AppTheme Requested { get; private set; } = AppTheme.System;

    /// <summary>The theme actually on screen — never <see cref="AppTheme.System"/>.</summary>
    public static AppTheme Effective { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after a swap, so views holding non-token state can refresh.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Applies the startup theme and begins following the OS setting. Call once,
    /// before the first window is shown, so nothing renders with the wrong palette.
    /// </summary>
    public static void Initialize(AppTheme requested = AppTheme.System)
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply(requested);
    }

    /// <summary>Switches the shell to the given theme.</summary>
    public static void Apply(AppTheme requested)
    {
        Requested = requested;
        AppTheme effective = requested == AppTheme.System ? DetectSystemTheme() : requested;

        // Re-applying the same effective theme would rebuild every brush for no
        // visible change, and would churn on every unrelated preference event.
        if (_current is not null && effective == Effective)
        {
            return;
        }

        Effective = effective;

        var dictionary = new ResourceDictionary
        {
            Source = effective == AppTheme.Light ? LightTokens : DarkTokens,
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null)
        {
            int index = merged.IndexOf(_current);
            if (index >= 0)
            {
                // Replace in place: the token dictionary must keep its position
                // relative to Typography.xaml and (later) the control styles that
                // reference it, or lookup order would change with the theme.
                merged[index] = dictionary;
            }
            else
            {
                merged.Add(dictionary);
            }
        }
        else
        {
            // First application: insert ahead of anything already merged, so the
            // dictionaries that reference these tokens can find them.
            merged.Insert(0, dictionary);
        }

        _current = dictionary;

        // Keep WPF-UI's Fluent control templates on the same theme as our tokens.
        // They are two independent palettes painting one window: ours covers the
        // app's own surfaces, WPF-UI's covers every stock control it restyles, so
        // letting them drift would put light buttons on a dark toolbar.
        // global:: is required, not stylistic: this assembly's own root namespace is
        // PageForge.App.Wpf, so an unqualified "Wpf.Ui" binds to PageForge.App.Wpf.Ui
        // and fails to resolve.
        global::Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            effective == AppTheme.Light
                ? global::Wpf.Ui.Appearance.ApplicationTheme.Light
                : global::Wpf.Ui.Appearance.ApplicationTheme.Dark,
            updateAccent: false);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Reads the Windows app-mode preference. Defaults to dark when the value is
    /// absent or unreadable, matching FrameForge's dark-first stance rather than
    /// flashing a light shell at someone running a dark desktop.
    /// </summary>
    private static AppTheme DetectSystemTheme()
    {
        try
        {
            // 0 = dark, 1 = light. The value is absent on some Windows editions.
            if (Registry.GetValue(PersonalizeKey, AppsUseLightThemeValue, null) is int useLight)
            {
                return useLight == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch (Exception exception)
        {
            // A locked-down or policy-restricted registry must not stop the app
            // starting; it only means we cannot follow the OS.
            Diagnostics.AppLog.For(typeof(ThemeManager)).LogWarning(
                exception,
                "Could not read the Windows app theme; defaulting to dark.");
        }

        return AppTheme.Dark;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Only re-evaluate while actually following the OS; an explicit Light/Dark
        // choice must survive the user changing their Windows setting.
        if (Requested == AppTheme.System && e.Category == UserPreferenceCategory.General)
        {
            Application.Current?.Dispatcher.InvokeAsync(() => Apply(AppTheme.System));
        }
    }
}
