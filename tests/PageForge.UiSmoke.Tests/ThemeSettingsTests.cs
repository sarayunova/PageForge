// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using PageForge.App.Wpf.Themes;
using Xunit;

namespace PageForge.UiSmoke.Tests;

/// <summary>
/// The remembered theme: saved by the title-bar menu, read at the next start. Pure
/// file I/O, so it runs without a window.
/// </summary>
public sealed class ThemeSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pf-theme-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.System)]
    public void A_saved_theme_is_read_back(AppTheme theme)
    {
        ThemeSettings.Save(theme, SettingsPath);

        Assert.Equal(theme, ThemeSettings.Load(SettingsPath));
    }

    [Fact]
    public void A_missing_file_means_follow_windows()
    {
        Assert.Equal(AppTheme.System, ThemeSettings.Load(SettingsPath));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"Theme\":\"purple\"}")]
    [InlineData("")]
    public void A_damaged_file_means_follow_windows_and_never_throws(string contents)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, contents);

        Assert.Equal(AppTheme.System, ThemeSettings.Load(SettingsPath));
    }

    [Fact]
    public void A_file_written_by_hand_in_any_case_is_understood()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"Theme\":\"light\"}");

        Assert.Equal(AppTheme.Light, ThemeSettings.Load(SettingsPath));
    }
}
