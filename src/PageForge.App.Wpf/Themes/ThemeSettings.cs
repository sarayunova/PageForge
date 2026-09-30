// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PageForge.App.Wpf.Themes;

/// <summary>
/// Remembers the theme the user picked, in <c>settings.json</c> beside the log and
/// the recovery folder. A settings file that is missing, unreadable or garbled means
/// "follow Windows": the app must always start, and a bad preference is never worth
/// a crash.
///
/// <c>PAGEFORGE_SETTINGS_DIR</c> moves the folder, which lets the UI tests exercise
/// the real save path without touching the person's own preference.
/// </summary>
public static class ThemeSettings
{
    public const string DirectoryVariable = "PAGEFORGE_SETTINGS_DIR";

    private const string FileName = "settings.json";

    private sealed record Payload(string? Theme);

    /// <summary>The settings file, honouring <see cref="DirectoryVariable"/>.</summary>
    public static string DefaultPath()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(DirectoryVariable);
        string dir = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PageForge");
        return Path.Combine(dir, FileName);
    }

    /// <summary>The saved theme, or <see cref="AppTheme.System"/> when there is none or it cannot be read.</summary>
    public static AppTheme Load(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            if (!File.Exists(path))
            {
                return AppTheme.System;
            }

            Payload? payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
            return Parse(payload?.Theme);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostics.AppLog.For(typeof(ThemeSettings)).LogWarning(
                exception, "Could not read the saved theme from {Path}; following Windows.", path);
            return AppTheme.System;
        }
    }

    /// <summary>Saves the theme. Failure is logged, not thrown: the choice still applies this session.</summary>
    public static void Save(AppTheme theme, string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path, JsonSerializer.Serialize(new Payload(theme.ToString().ToLowerInvariant())));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.For(typeof(ThemeSettings)).LogWarning(
                exception, "Could not save the theme choice to {Path}.", path);
        }
    }

    /// <summary>Parses "light", "dark" or "system" (any case); anything else is <see cref="AppTheme.System"/>.</summary>
    public static AppTheme Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };
}
