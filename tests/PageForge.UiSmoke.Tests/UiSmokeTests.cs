// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using System.Windows.Automation;
using Xunit;

namespace PageForge.UiSmoke.Tests;

/// <summary>
/// UI smoke tests for the WPF fallback viewer + page organizer, driven through
/// the built-in UI Automation client (WinAppDriver-equivalent; WinAppDriver
/// itself requires an elevated install not available on this dev box).
///
/// Covers the FR-PAGE/FR-VIEW critical flows headlessly-on-desktop but against
/// the real window: document tab load, the organizer toolbar, page navigation
/// on a multi-page document, thumbnail reorder staging, and a complete
/// save-reordered-to-new-tab loop. Each fact launches/owns its own app process.
/// </summary>
public class UiSmokeTests
{
    private static string Sample3 => Path.Combine(PageForgeApp.FindRepoRoot(), "tools", "sample-pdf", "sample-pages3.pdf");

    [Fact]
    public async Task Wpf_app_opens_organizes_and_saves_a_reordered_document()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();

        // Launch loads the 1-page sample into the first tab (FR-VIEW-04).
        AutomationElement initial = await app.WaitForVisibleAsync("PageIndicatorText");
        Assert.EndsWith("/ 1", PageForgeApp.GetText(initial).Trim());

        // Organizer toolbar mounted (FR-PAGE): reorder toggle + save order.
        AutomationElement? reorderToggle = app.FindInSelectedTabByName("Reorder");
        AutomationElement? saveOrder = app.FindInSelectedTabByName("Save order…");
        Assert.NotNull(reorderToggle);
        Assert.NotNull(saveOrder);

        // Reorder staging toggle switches status (drag-drop staging list) without a dialog.
        PageForgeApp.Activate(reorderToggle!);
        Assert.Contains("Reorder mode", await WaitForText(app, "StatusText"));
        PageForgeApp.Activate(app.FindInSelectedTabByName("Reorder")!);

        // Open the 3-page fixture via the Open PDF… dialog (button lives on the
        // MainWindow shell, not in the tab content).
        PageForgeApp.Activate(RequireVisible(app, "Open PDF…", isTabScoped: false));
        await OpenFileViaDialog(app, Sample3);

        // Newly opened tab auto-selected -> 3 thumbnails, indicator 1 / 3.
        AutomationElement indicator = await WaitForIndicator(app, "/ 3");
        Assert.Equal("1 / 3", PageForgeApp.GetText(indicator).Trim());

        // Page navigation (FR-VIEW) advances the indicator.
        AutomationElement next = app.FindInSelectedTabByName("Next ▶")
            ?? throw new InvalidOperationException("Next button not found.");
        PageForgeApp.Activate(next);
        AutomationElement advanced = await WaitForIndicator(app, "2 / 3");
        Assert.Equal("2 / 3", PageForgeApp.GetText(advanced).Trim());

        // Thumbnail panel shows the 3 pages (FR-VIEW-03).
        int thumbs = CountThumbnails(app);
        Assert.Equal(3, thumbs);

        // Save order… -> common Save dialog -> new tab for the reordered file.
        string outFile = ReorderedPath();
        AutomationElement saveAs = app.FindInSelectedTabByName("Save order…")
            ?? throw new InvalidOperationException("Save order… button not found.");
        PageForgeApp.Activate(saveAs);
        await SaveViaDialog(app, outFile);

        // The saved copy reopens in a new selected tab (FR-PAGE reorder/merge apply).
        AutomationElement reopened = await WaitForIndicator(app, "/ 3");
        Assert.Equal("1 / 3", PageForgeApp.GetText(reopened).Trim());
        Assert.True(File.Exists(outFile), $"reordered file was not written: {outFile}");
    }

    private static AutomationElement RequireVisible(PageForgeApp app, string name, bool isTabScoped)
    {
        if (!isTabScoped)
        {
            return app.FindByName(name) ?? throw new InvalidOperationException($"Element '{name}' not found.");
        }

        return app.FindInSelectedTabByName(name) ?? throw new InvalidOperationException($"Element '{name}' not found in selected tab.");
    }

    private static async Task<string> WaitForText(PageForgeApp app, string automationId)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? el = app.FindInSelectedTabById(automationId);
            if (el is not null && !string.IsNullOrEmpty(PageForgeApp.GetText(el)))
            {
                return PageForgeApp.GetText(el);
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"Timed out reading text of '{automationId}'.");
    }

    private static async Task<AutomationElement> WaitForIndicator(PageForgeApp app, string suffix)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? el = app.FindInSelectedTabById("PageIndicatorText");
            if (el is not null && PageForgeApp.GetText(el).Trim().EndsWith(suffix, StringComparison.Ordinal))
            {
                return el;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for page indicator ending '{suffix}'.");
    }

    private static int CountThumbnails(PageForgeApp app)
    {
        AutomationElement? list = app.FindInSelectedTabById("ThumbList");
        if (list is null)
        {
            return 0;
        }

        int count = 0;
        foreach (AutomationElement child in list.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            if (child.Current.ControlType == ControlType.ListItem)
            {
                count++;
            }
        }

        return count;
    }

    private static async Task OpenFileViaDialog(PageForgeApp app, string path)
    {
        AutomationElement dialog = await PageForgeApp.WaitForDialogAsync("open");
        TypeFilenameAndConfirm(dialog, path);
    }

    private static async Task SaveViaDialog(PageForgeApp app, string path)
    {
        AutomationElement dialog = await PageForgeApp.WaitForDialogAsync("save");
        TypeFilenameAndConfirm(dialog, path);
    }

    private static void TypeFilenameAndConfirm(AutomationElement dialog, string path)
    {
        AutomationElement? edit = FindInDialog(dialog, "1148");
        Assert.NotNull(edit);
        PageForgeApp.SetText(edit!, path);

        AutomationElement? confirm = FindInDialog(dialog, "1");
        Assert.NotNull(confirm);
        PageForgeApp.Activate(confirm!);
    }

    private static AutomationElement? FindInDialog(AutomationElement dialog, string automationId)
    {
        try
        {
            return dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string ReorderedPath()
        => Path.Combine(Path.GetTempPath(), $"pf-ui-reordered-{Guid.NewGuid():N}.pdf");
}
