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

        // Phase U4: the command bar drives the view model through ICommand rather
        // than Click handlers. A Command binding that does not resolve leaves the
        // button visible, enabled and completely inert - the same silent-failure
        // shape as the original blank-viewer bug - so invoke one and assert the
        // view model actually moved.
        Assert.Equal("100%", (await WaitForText(app, "ZoomText")).Trim());
        PageForgeApp.Activate(app.FindByName("Zoom in")
            ?? throw new InvalidOperationException("Zoom in button not found."));
        Assert.Equal("125%", await WaitForTextToBe(app, "ZoomText", "125%"));
        PageForgeApp.Activate(app.FindByName("Reset zoom to 100%")
            ?? throw new InvalidOperationException("Reset zoom button not found."));
        Assert.Equal("100%", await WaitForTextToBe(app, "ZoomText", "100%"));

        // WCAG 2.4.6/1.3.1: toolbar captions are real "Heading" control elements
        // in the automation tree, and the 2.4.1 content bypass exists.
        // The toolbar is now a command bar plus a tool-group selector, so only the
        // selected group's commands are mounted. Each group is selected in turn and
        // its heading asserted, which also proves the selector actually swaps the
        // contextual row rather than merely highlighting a tab.
        AssertToolGroupHeading(app, "OrganizeModeTab", "Organize tools");
        AssertToolGroupHeading(app, "AnnotateModeTab", "Annotate tools");
        AssertToolGroupHeading(app, "EditModeTab", "Edit tools");
        AssertToolGroupHeading(app, "FormsModeTab", "Form tools");
        AssertToolGroupHeading(app, "RedactModeTab", "Redact tools");
        AssertToolGroupHeading(app, "DocumentModeTab", "Document tools");

        // Leave Organize selected: the reorder/save-order flow below lives there.
        SelectToolGroup(app, "OrganizeModeTab");
        AutomationElement skip = app.FindInSelectedTabById("SkipToPageButton")
            ?? throw new InvalidOperationException("Skip-to-document button not found.");
        Assert.Equal("Skip to the document", skip.Current.Name);

        // Organizer toolbar mounted (FR-PAGE): reorder toggle + save order.
        // Lookups are by AutomationId: exact-name matching is ambiguous because
        // button content text is exposed as its own inner text element with the
        // same visible string (e.g. "Reorder"/"Save order…").
        AutomationElement? reorderToggle = app.FindInSelectedTabById("ReorderToggle");
        AutomationElement? saveOrder = app.FindInSelectedTabById("SaveOrderButton");
        Assert.NotNull(reorderToggle);
        Assert.NotNull(saveOrder);

        // Open the 3-page fixture via the Open PDF… dialog (button lives on the
        // MainWindow shell, not in the tab content).
        PageForgeApp.Activate(app.FindById("OpenPdfButton") ?? throw new InvalidOperationException("Open PDF… button not found."));
        await OpenFileViaDialog(app, Sample3);

        // Newly opened tab auto-selected -> 3 thumbnails, indicator 1 / 3.
        AutomationElement indicator = await WaitForIndicator(app, "/ 3");
        Assert.Equal("1 / 3", PageForgeApp.GetText(indicator).Trim());

        // Page navigation (FR-VIEW) advances the indicator.
        AutomationElement next = app.FindInSelectedTabById("NextButton")
            ?? throw new InvalidOperationException("Next button not found.");
        PageForgeApp.Activate(next);
        AutomationElement advanced = await WaitForIndicator(app, "2 / 3");
        Assert.Equal("2 / 3", PageForgeApp.GetText(advanced).Trim());

        // Thumbnail panel shows the 3 pages (FR-VIEW-03).
        int thumbs = CountThumbnails(app);
        Assert.Equal(3, thumbs);

        // Reorder staging toggle switches status without a dialog; staging must be
        // entered on the tab that will be saved (BuildOrder validates a permutation).
        PageForgeApp.Activate(app.FindInSelectedTabById("ReorderToggle")!);
        Assert.Contains("Reorder mode", await WaitForText(app, "StatusText"));

        // Save order… -> common Save dialog -> new tab for the reordered file.
        string outFile = ReorderedPath();
        AutomationElement saveAs = app.FindInSelectedTabById("SaveOrderButton")
            ?? throw new InvalidOperationException("Save order… button not found.");
        PageForgeApp.Activate(saveAs);
        await SaveViaDialog(app, outFile);

        // The saved copy reopens in a new selected tab (FR-PAGE reorder/merge apply).
        // Exact match: a suffix wait would resolve immediately against the previous
        // tab's "2 / 3" before the new tab is selected.
        AutomationElement reopened = await WaitForIndicatorValue(app, "1 / 3");
        Assert.True(File.Exists(outFile), $"reordered file was not written: {outFile}");
    }

    private static void AssertHeading(PageForgeApp app, string captionName)
    {
        AutomationElement? caption = app.FindHeadingInSelectedTab(captionName)
            ?? throw new InvalidOperationException($"Heading caption '{captionName}' not found.");
        Assert.Equal("Heading", caption.Current.ClassName);
    }

    /// <summary>
    /// Selects one tool group in the toolbar's group selector.
    ///
    /// By AutomationId, for the same reason the rest of this suite does it: a
    /// button's content text is exposed as its own inner text element with the
    /// same string, so an exact-name lookup is ambiguous between the control and
    /// its label.
    /// </summary>
    private static void SelectToolGroup(PageForgeApp app, string tabAutomationId)
    {
        AutomationElement tab = app.FindInSelectedTabById(tabAutomationId)
            ?? throw new InvalidOperationException($"Tool group tab '{tabAutomationId}' not found.");

        // The tabs are RadioButtons, so they expose SelectionItem rather than Invoke.
        var pattern = (SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern);
        pattern.Select();
    }

    /// <summary>
    /// Selects a tool group and asserts its contextual row carries the expected
    /// heading (WCAG 1.3.1). The tab's own accessible name is deliberately distinct
    /// from the heading's ("Organize tool group" vs "Organize tools") so the
    /// heading lookup cannot accidentally match the tab.
    /// </summary>
    private static void AssertToolGroupHeading(PageForgeApp app, string tabAutomationId, string captionName)
    {
        SelectToolGroup(app, tabAutomationId);
        AssertHeading(app, captionName);
    }

    /// <summary>Waits for a bound text element to settle on an expected value.
    /// Commands run through the dispatcher, so the binding updates a beat after
    /// the Invoke returns.</summary>
    private static async Task<string> WaitForTextToBe(PageForgeApp app, string automationId, string expected)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            text = (await WaitForText(app, automationId)).Trim();
            if (string.Equals(text, expected, StringComparison.Ordinal))
            {
                return text;
            }

            await Task.Delay(100);
        }

        return text;
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

    private static async Task<AutomationElement> WaitForIndicatorValue(PageForgeApp app, string expected)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? el = app.FindInSelectedTabById("PageIndicatorText");
            if (el is not null && PageForgeApp.GetText(el).Trim() == expected)
            {
                return el;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for page indicator '{expected}'.");
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

    /// <summary>
    /// Every tool mode must actually show its panel and keep its commands on
    /// screen.
    ///
    /// UiSmoke only ever drove the Organize group, so Forms, Redact and Object
    /// edit had no automated exercise at all - and three layout bugs shipped in
    /// them. The side panel was laid out underneath the page and could not be seen
    /// (DockPanel.Dock on a Grid child, which is silently ignored); the redaction
    /// Apply bar was pushed off its own panel; and the mode toolbars laid a long
    /// hint and their buttons in a StackPanel, so at the default window width the
    /// last command sat past the right edge.
    ///
    /// A test that only asked "does this element exist?" would have passed through
    /// all three: every one of those elements was in the automation tree the whole
    /// time. So this asserts geometry - commands inside the window, panels beside
    /// the page rather than under it - because geometry is what was broken.
    /// </summary>
    [Fact]
    public async Task Every_tool_mode_shows_its_panel_and_keeps_its_commands_on_screen()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        // Overflow is a function of width, so the default size proves nothing about
        // the sizes users actually pick. 900 is MainWindow's declared MinWidth -
        // the narrowest the app says it supports, so the narrowest it must hold up
        // at. The toolbar overflow this test exists for was invisible at 1200 and
        // obvious at 900.
        foreach ((double width, double height) in new[] { (1200.0, 820.0), (900.0, 600.0) })
        {
            await app.ResizeAsync(width, height);

            await AssertModeAsync(
                app, "FormsModeTab", "Form fill mode",
                page: "Form fill page",
                panelHeader: "Fields on this page",
                commands: ["New text field…", "Flatten form…"]);

            await AssertModeAsync(
                app, "RedactModeTab", "Redact mode",
                page: "Redact page",
                panelHeader: "Regions marked on this page",
                commands: ["Undo redaction", "Save redacted…", "Apply redactions…"]);

            // Object edit has no side panel; only its command row is at issue.
            await AssertModeAsync(
                app, "EditModeTab", "Object editing mode",
                page: null,
                panelHeader: null,
                commands: ["Replace…"]);
        }
    }

    private static async Task AssertModeAsync(
        PageForgeApp app,
        string tabAutomationId,
        string toggleName,
        string? page,
        string? panelHeader,
        string[] commands)
    {
        SelectToolGroup(app, tabAutomationId);

        AutomationElement toggle = app.FindByName(toggleName)
            ?? throw new InvalidOperationException($"Mode toggle '{toggleName}' not found.");
        ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();

        // The panel renders the page before it lists anything, so give it a beat.
        await Task.Delay(1500);

        System.Windows.Rect window = app.Window.Current.BoundingRectangle;

        foreach (string command in commands)
        {
            AutomationElement el = app.FindByName(command)
                ?? throw new InvalidOperationException(
                    $"{toggleName}: command '{command}' is not in the automation tree.");

            System.Windows.Rect r = el.Current.BoundingRectangle;
            Assert.False(el.Current.IsOffscreen, $"{toggleName}: '{command}' is offscreen.");
            Assert.True(r.Width > 0 && r.Height > 0, $"{toggleName}: '{command}' has no size.");
            Assert.True(
                r.Left >= window.Left && r.Right <= window.Right,
                $"{toggleName}: '{command}' is outside the window horizontally " +
                $"(button {r.Left:F0}..{r.Right:F0}, window {window.Left:F0}..{window.Right:F0}).");
        }

        if (page is not null && panelHeader is not null)
        {
            // The page surface is renamed at runtime to carry its page number
            // ("Form fill page 1"), so match on the prefix rather than the
            // XAML-time name.
            AutomationElement pageEl = FindByNamePrefix(app, page)
                ?? throw new InvalidOperationException($"{toggleName}: page surface '{page}…' not found.");
            AutomationElement headerEl = app.FindByName(panelHeader)
                ?? throw new InvalidOperationException($"{toggleName}: panel header '{panelHeader}' not found.");

            System.Windows.Rect pageRect = pageEl.Current.BoundingRectangle;
            System.Windows.Rect headerRect = headerEl.Current.BoundingRectangle;

            Assert.True(headerRect.Width > 0 && headerRect.Height > 0,
                $"{toggleName}: panel header '{panelHeader}' has no size.");

            System.Windows.Rect overlap = System.Windows.Rect.Intersect(pageRect, headerRect);
            Assert.True(
                overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1,
                $"{toggleName}: the panel is laid out on top of the page instead of beside it " +
                $"(page {pageRect}, panel header {headerRect}).");
        }

        // Leave the mode off so the next one starts clean.
        ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        await Task.Delay(400);
    }

    /// <summary>First descendant whose automation name starts with the prefix.
    /// UIA property conditions are exact-match, and some names carry a page
    /// number appended at runtime.</summary>
    private static AutomationElement? FindByNamePrefix(PageForgeApp app, string prefix)
    {
        foreach (AutomationElement el in app.Window.FindAll(
            TreeScope.Descendants, Condition.TrueCondition))
        {
            if (el.Current.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return el;
            }
        }

        return null;
    }

    private static async Task OpenFileViaDialog(PageForgeApp app, string path)
    {
        IntPtr dialog = await PageForgeApp.WaitForDialogAsync("open");
        PageForgeApp.TypeFilenameAndConfirm(dialog, path);
    }

    private static async Task SaveViaDialog(PageForgeApp app, string path)
    {
        IntPtr dialog = await PageForgeApp.WaitForDialogAsync("save");
        PageForgeApp.TypeFilenameAndConfirm(dialog, path);
    }

    private static string ReorderedPath()
        => Path.Combine(Path.GetTempPath(), $"pf-ui-reordered-{Guid.NewGuid():N}.pdf");
}
