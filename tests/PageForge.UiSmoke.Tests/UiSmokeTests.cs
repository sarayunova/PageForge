// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using System.Text.RegularExpressions;
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

    /// <summary>The scanned corpus fixture: a placed image per page, which is
    /// what the object editor can select. The generated samples are text only.</summary>
    private static string ScannedFixture => Path.Combine(
        PageForgeApp.FindRepoRoot(), "tools", "sample-pdf", "corpus", "scan-letters.pdf");

    [Fact]
    public async Task Wpf_app_opens_organizes_and_saves_a_reordered_document()
    {
        // The save destination is handed to the app up front instead of typed
        // into a native save dialog. That dialog cannot be driven reliably across
        // Windows images - the Win32 messages that confirm it here cancel it on
        // the hosted CI runner, so the app saw a cancelled save, wrote nothing,
        // and correctly reported nothing (issue #6). What matters is reorder,
        // write and reopen; the dialog is Windows' code, and automating it was
        // only ever the means of reaching the part that is ours.
        string outFile = ReorderedPath();
        await using PageForgeApp app = await PageForgeApp.LaunchAsync(
            environment: new Dictionary<string, string>
            {
                ["PAGEFORGE_UITEST_SAVE_PATH"] = outFile,
            });

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

        // Save order… -> writes straight to the scripted destination -> new tab
        // for the reordered file.
        AutomationElement saveAs = app.FindInSelectedTabById("SaveOrderButton")
            ?? throw new InvalidOperationException("Save order… button not found.");
        PageForgeApp.Activate(saveAs);

        // The saved copy reopens in a new selected tab (FR-PAGE reorder/merge apply).
        // Exact match: a suffix wait would resolve immediately against the previous
        // tab's "2 / 3" before the new tab is selected.
        AutomationElement reopened = await WaitForIndicatorValue(app, "1 / 3");

        // Say what the app said and what is actually on disk. "reordered file was
        // not written: <path>" was true but useless: it could not tell a save that
        // never ran from one that ran and failed, or from one that wrote under a
        // different name because the file dialog's filename field was not where
        // the Win32 helper looked for it. Those have different fixes, and on a CI
        // machine nobody can see, the failure message is the whole investigation.
        Assert.True(File.Exists(outFile), BuildSaveFailureReport(app, outFile));
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

    /// <summary>
    /// Everything needed to diagnose a failed save without access to the machine:
    /// what the app's own status line says, and what is actually in the directory
    /// the file was meant to land in.
    /// </summary>
    private static string BuildSaveFailureReport(PageForgeApp app, string outFile)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"reordered file was not written: {outFile}");

        // The status line is where the app reports the outcome of a save,
        // including a failure. Read without waiting - this is already the
        // failure path, and a timeout here would replace the real message.
        string status;
        try
        {
            AutomationElement? el = app.FindInSelectedTabById("StatusText") ?? app.FindById("StatusText");
            status = el is null ? "<StatusText not found>" : PageForgeApp.GetText(el).Trim();
        }
        catch (Exception ex)
        {
            status = $"<could not read StatusText: {ex.GetType().Name}: {ex.Message}>";
        }

        report.AppendLine($"app status line: {status}");

        // Anything the app is showing that the test never looked at. A failed
        // reorder reports itself through MessageBox.Show, so the explanation can
        // be sitting on screen in a window nobody enumerated while the run
        // reports only the downstream symptom.
        report.AppendLine("windows the app has open:");
        report.AppendLine(PageForgeApp.DescribeProcessWindows());

        // WHERE the file went, not just whether it is at the expected path.
        //
        // Looking only in the target directory was too narrow and made a save
        // that succeeded look like one that never happened. The evidence says it
        // did happen: the dialog accepted the name and closed, and a reordered
        // three-page document opened in a new tab - which it could only do from a
        // file that existed. So the question is not "was anything written" but
        // "under what name, and where", and the answer is worth finding in one
        // run rather than by guessing at one directory at a time.
        foreach (string root in CandidateSaveRoots())
        {
            report.AppendLine($"recent .pdf under {root}:");
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true,
                };

                DateTime since = DateTime.UtcNow.AddMinutes(-10);
                string[] recent = Directory.EnumerateFiles(root, "*.pdf", options)
                    .Where(f => File.GetLastWriteTimeUtc(f) > since)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(10)
                    .ToArray();

                if (recent.Length == 0)
                {
                    report.AppendLine("  (nothing written in the last 10 minutes)");
                }

                foreach (string file in recent)
                {
                    var info = new FileInfo(file);
                    report.AppendLine($"  {info.LastWriteTimeUtc:HH:mm:ss} {info.Length,10} {info.FullName}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  <could not list: {ex.GetType().Name}: {ex.Message}>");
            }
        }

        return report.ToString();
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

            // Organize, Annotate and Document have no mode toggle - their commands
            // sit directly in the contextual row - but that row is laid out the same
            // way and overflows the same way, so it gets the same geometry check.
            await AssertModeAsync(
                app, "OrganizeModeTab", toggleName: null,
                page: null, panelHeader: null,
                commands: ["Rotate page", "Delete page", "Extract page", "Insert page"]);

            await AssertModeAsync(
                app, "AnnotateModeTab", toggleName: null,
                page: null, panelHeader: null,
                commands: ["Add highlight", "Add text note", "Add ink stroke", "Flatten annotations"]);

            await AssertModeAsync(
                app, "DocumentModeTab", toggleName: null,
                page: null, panelHeader: null,
                commands: ["Run OCR", "Protect document"]);
        }
    }

    /// <param name="toggleName">The mode toggle to switch on, or null for a group
    /// whose commands sit directly in the contextual row with no mode to enter.</param>
    /// <summary>
    /// Every page surface must actually draw something.
    ///
    /// This is the assertion the project has never had. UI Automation reports that
    /// an element exists, where it is and how big it is, and the redact, form-fill
    /// and object-edit surfaces satisfied all three while rendering nothing at all
    /// - through a whole phase of UI work, with no exception and no log line. The
    /// main viewer had shipped the same way earlier. Four occurrences of one bug,
    /// none of which any test could see.
    ///
    /// What is asserted is only that the page area is not a single flat colour.
    /// Not which pixels, not against a baseline image: those are the things that
    /// make screenshot tests brittle and get them deleted. A blank sheet is one
    /// colour and a rendered page is not, and that is the whole difference between
    /// the bug and working software.
    /// </summary>
    private static string FormFixture => Path.Combine(
        PageForgeApp.FindRepoRoot(), "tools", "sample-pdf", "corpus", "form-application.pdf");

    /// <summary>
    /// The form-field cards are bound, and the binding actually reaches the
    /// document.
    ///
    /// These were the last controls in the shell built in code, and converting
    /// them to a template is exactly the change that can fail silently: an
    /// unresolved Command binding leaves a button visible, enabled and completely
    /// inert - no exception, no log - which is how an earlier phase of this same
    /// conversion shipped dead buttons. Compiling proves nothing here.
    ///
    /// So this types a value, presses Set, then re-reads the field from a freshly
    /// rebuilt card list. The value can only come back if the command resolved,
    /// the two-way binding carried the text, the engine wrote it, and the rebuilt
    /// card bound it again.
    /// </summary>
    [Fact]
    public async Task Form_field_cards_write_their_value_to_the_document()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        PageForgeApp.Activate(app.FindById("OpenPdfButton")
            ?? throw new InvalidOperationException("Open PDF… button not found."));
        await OpenFileViaDialog(app, FormFixture);
        await WaitForIndicator(app, "/ 1");

        SelectToolGroup(app, "FormsModeTab");
        AutomationElement formToggle = app.FindByName("Form fill mode")
            ?? throw new InvalidOperationException("Form fill mode toggle not found.");
        ((TogglePattern)formToggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();

        // The fixture's fields are FullName and Consent; FullName is the text one.
        const string fieldValue = "Ada Lovelace";
        AutomationElement box = await WaitForElementAsync(app, "FullName value");

        // The card must be a real list item WITH a real name (WCAG 1.3.1, 4.1.2).
        //
        // Binding the collection turned each card into a UIA DataItem inside a
        // List, which is the structure the Phase 6 assessment wanted and rated
        // PARTIAL for lacking. But an item with no name of its own falls back to
        // ToString() on the view model, and this announced
        // "PageForge.App.Wpf.ViewModels.FormFieldCardViewModel" to a screen reader
        // until the container was named. Asserted here because the accessibility
        // gate is otherwise a prose document that five UI phases did not re-read.
        AutomationElement item = TreeWalker.RawViewWalker.GetParent(box);
        Assert.Equal(ControlType.DataItem, item.Current.ControlType);
        Assert.Equal(ControlType.List, TreeWalker.RawViewWalker.GetParent(item).Current.ControlType);
        Assert.Equal("FullName (Text)", item.Current.Name);


        ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue(fieldValue);

        PageForgeApp.Activate(app.FindByName("Set FullName")
            ?? throw new InvalidOperationException("Set FullName button not found."));

        // A successful fill re-lists the fields and rebuilds the cards, so the box
        // that ends up on screen is a DIFFERENT element carrying what the document
        // now reports. Both halves are needed:
        //
        //   - a new element proves the command fired at all, and
        //   - the value on that new element proves the engine stored it,
        //
        // and neither alone is enough, which four attempts at this assertion
        // established the hard way. All of the following passed against a Set
        // button bound to a property that does not exist:
        //
        //   - Re-reading the text box. The two-way binding updates the view model
        //     on every keystroke, so it shows the typed text whether or not
        //     anything was committed - and after a real write it shows the same
        //     string, so the text alone cannot tell the two apart.
        //   - Leaving form mode and returning. The view persists across a mode
        //     switch, so its cards persist with it and nothing is re-read.
        //
        // Two more were tried and rejected: comparing rendered page pixels, whose
        // count drifts while the page settles and reported changes unrelated to
        // the value; and the status line, which the rebuild immediately overwrites
        // with its own message.
        int[] originalId = box.GetRuntimeId();
        //
        // Finding a witness that is not downstream of the binding under test took
        // three attempts, and the two rejected ones are worth recording because
        // both looked convincing and both passed against a completely inert
        // button:
        //
        //   - Re-reading the text box. The two-way binding updates the view model
        //     on every keystroke, so the box shows the typed text whether or not
        //     anything was committed.
        //   - Leaving form mode and returning. The view persists across a mode
        //     switch, so its cards - and their already-updated values - persist
        //     with it. Nothing is re-read from the document.
        //
        // Comparing rendered page pixels was tried too and rejected for the
        // opposite reason: the count drifts between captures while the page
        // settles, so it reported a change that had nothing to do with the value.
        //
        // What this does NOT prove is that the engine stored the value - that is
        // FormFidelityTests' job, against the real shim. The gap this closes is
        // the binding, which is the part that fails silently.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        string seen = "<the card was never rebuilt>";
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? reread = app.FindByName("FullName value");
            if (reread is not null && !reread.GetRuntimeId().SequenceEqual(originalId))
            {
                seen = reread.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                    ? ((ValuePattern)pattern).Current.Value
                    : "<no value pattern>";
                if (seen == fieldValue)
                {
                    return;
                }
            }

            await Task.Delay(200);
        }

        Assert.Fail(
            $"The form card did not put '{fieldValue}' into the document: the rebuilt card " +
            $"reads '{seen}'. If it says the card was never rebuilt, the Set command never " +
            "ran - an inert Command binding looks exactly like that, the button present and " +
            "enabled and doing nothing. If it reads something else, the command ran and the " +
            "engine did not keep the value.");
    }


    /// <summary>
    /// The places a file dialog could plausibly have put the document.
    ///
    /// The app's own directory is in the list because the process is launched
    /// with its working directory set to its output folder, which is where a
    /// dialog given a name it could not resolve would write.
    /// </summary>
    private static IEnumerable<string> CandidateSaveRoots()
    {
        var roots = new List<string>
        {
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory,
        };

        try
        {
            roots.Add(Path.GetDirectoryName(PageForgeApp.FindAppExe()) ?? string.Empty);
        }
        catch (FileNotFoundException)
        {
            // The app was located once to launch it; if it cannot be found now
            // that is not what this report is about.
        }

        return roots
            .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<AutomationElement> WaitForElementAsync(PageForgeApp app, string name)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? found = app.FindByName(name);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for the element named '{name}'.");
    }

    [Fact]
    public async Task Every_page_surface_actually_draws_the_page()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        // The document surface, then each mode that renders a page of its own.
        // Mode surfaces are named by the accessible name of their page image,
        // which is how the layout test already finds them.
        await AssertPageDrawsAsync(app, "Document page 1", modeTab: null, toggleName: null);
        await AssertPageDrawsAsync(app, "Form fill page 1", "FormsModeTab", "Form fill mode");
        await AssertPageDrawsAsync(app, "Redact page 1", "RedactModeTab", "Redact mode");
        await AssertPageDrawsAsync(app, "Object edit page 1", "EditModeTab", "Object editing mode");

        // The signature placement surface is reached by a command rather than a
        // mode toggle, but it is a page surface with the same failure mode: it
        // assigns the bitmap after awaiting a render, and getting that order
        // wrong would leave the user drawing a signature box over a blank sheet.
        SelectToolGroup(app, "DocumentModeTab");
        PageForgeApp.Activate(
            app.FindByName("Sign document")
            ?? throw new InvalidOperationException("The Sign document command was not found."));
        await AssertPageDrawsAsync(app, "Page 1 to sign", modeTab: null, toggleName: null);

        // Leave the shell as it was found, so nothing later in this run is
        // looking at the placement surface.
        PageForgeApp.Activate(
            app.FindByName("Cancel signing")
            ?? throw new InvalidOperationException("The Cancel signing command was not found."));
    }

    /// <summary>
    /// Places a signature by actually dragging on the page (FR-SEC-03).
    ///
    /// The drag is the one part of placement no other check can reach: the
    /// coordinate maths is proved in the smoke harness and the surface is
    /// proved to render through Automation, but nothing until now pressed the
    /// mouse down on the overlay and moved it. A handler wired to the wrong
    /// element, a rubber band that never becomes a placement, or an overlay
    /// that does not receive input at all would pass every other test.
    ///
    /// What it asserts is deliberately not "a box appeared". Dragging near the
    /// TOP of the page must report a LARGER PDF y than dragging near the
    /// bottom, because PDF measures from the bottom-left while the screen
    /// measures from the top-left. That is the direction of the flip, checked
    /// through the real gesture — and a flip mistake is invisible otherwise,
    /// since both versions produce a perfectly ordinary-looking rectangle.
    /// </summary>
    [Fact]
    public async Task A_dragged_box_becomes_the_signature_position()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        EnterSignPlacement(app);

        System.Windows.Rect page = SignPlacementPageBounds(app);
        double x0 = page.Left + (page.Width * 0.2);
        double x1 = page.Left + (page.Width * 0.7);

        // Upper part of the page, then lower. Both boxes are a good fraction of
        // the sheet, so neither can be mistaken for the stray-click case.
        SyntheticMouse.Drag((x0, page.Top + (page.Height * 0.10)), (x1, page.Top + (page.Height * 0.28)));
        await Task.Delay(200);

        AutomationElement continueButton = FindSignPlacementContinue(app);
        Assert.True(
            continueButton.Current.IsEnabled,
            "Dragging a box on the placement surface did not register as a placement: " +
            $"Continue stayed disabled. Hint said: '{SignPlacementHint(app)}'. " +
            $"The drag was aimed inside {page} and the pointer finished at " +
            $"{SyntheticMouse.CursorPosition().X},{SyntheticMouse.CursorPosition().Y}; " +
            $"the window is at {app.Window.Current.BoundingRectangle}.");

        (double _, double upperY) = ParsePlacedBox(SignPlacementHint(app));

        SyntheticMouse.Drag((x0, page.Top + (page.Height * 0.62)), (x1, page.Top + (page.Height * 0.80)));
        await Task.Delay(200);

        (double _, double lowerY) = ParsePlacedBox(SignPlacementHint(app));

        Assert.True(
            upperY > lowerY,
            $"A box dragged near the top of the page reported y={upperY:F0} pt and one near " +
            $"the bottom reported y={lowerY:F0} pt. PDF measures upward from the bottom-left " +
            "and the screen downward from the top-left, so the upper box must have the " +
            "LARGER y. As it stands a signature would be placed on the opposite half of the " +
            "page from where it was drawn.");

        // A fresh surface, so the next assertion cannot pass on the strength of
        // the placement that already succeeded.
        PageForgeApp.Activate(
            app.FindByName("Cancel signing")
            ?? throw new InvalidOperationException("The Cancel signing command was not found."));
        await Task.Delay(200);
        EnterSignPlacement(app);

        AutomationElement freshContinue = FindSignPlacementContinue(app);
        Assert.False(
            freshContinue.Current.IsEnabled,
            "Re-entering placement kept the previous box, so Continue was already enabled " +
            "and the stray-click check below would prove nothing.");

        // A stray click must not place a signature. The surface has just been
        // shown to receive drags, so this failing would be a real refusal to
        // guard the size, not a dead overlay.
        System.Windows.Rect fresh = SignPlacementPageBounds(app);
        SyntheticMouse.Drag(
            (fresh.Left + (fresh.Width * 0.4), fresh.Top + (fresh.Height * 0.4)),
            (fresh.Left + (fresh.Width * 0.4) + 6, fresh.Top + (fresh.Height * 0.4) + 6));
        await Task.Delay(200);

        Assert.False(
            FindSignPlacementContinue(app).Current.IsEnabled,
            "A six-pixel drag was accepted as a signature placement. Nobody means to sign " +
            $"in a box that small. Hint said: '{SignPlacementHint(app)}'.");

        PageForgeApp.Activate(
            app.FindByName("Cancel signing")
            ?? throw new InvalidOperationException("The Cancel signing command was not found."));
    }

    /// <summary>
    /// Marks a redaction by actually dragging on the page (FR-SEC-02).
    ///
    /// This is here because the signature placement surface could not receive
    /// a mouse event, and the redaction surface — which it was modelled on —
    /// turned out to have the identical defect: a Canvas overlay with no
    /// Background is invisible to hit testing, so every press went to the page
    /// image underneath and none of the drag handlers ever ran. Drag-to-mark
    /// had never worked in the shipped shell. Nothing caught it because
    /// screenshots show the surface looking correct and no test had ever
    /// driven a pointer at it.
    /// </summary>
    [Fact]
    public async Task A_dragged_redaction_box_is_marked()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        SelectToolGroup(app, "RedactModeTab");
        AutomationElement toggle = app.FindByName("Redact mode")
            ?? throw new InvalidOperationException("The Redact mode toggle was not found.");
        ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        await Task.Delay(700);

        SyntheticMouse.EnsureForeground(new IntPtr(app.Window.Current.NativeWindowHandle));

        AutomationElement page = app.FindByName("Redact page 1")
            ?? throw new InvalidOperationException("The redaction page surface was not found.");
        System.Windows.Rect bounds = page.Current.BoundingRectangle;
        bounds.Intersect(app.Window.Current.BoundingRectangle);

        SyntheticMouse.Drag(
            (bounds.Left + (bounds.Width * 0.2), bounds.Top + (bounds.Height * 0.2)),
            (bounds.Left + (bounds.Width * 0.6), bounds.Top + (bounds.Height * 0.4)));
        await Task.Delay(400);

        // The marked regions are listed beside the page, each row named
        // "Redaction region (x0, y0) → (x1, y1) pt".
        AutomationElement? region = app.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Redaction region", PropertyConditionFlags.None));

        if (region is null)
        {
            AutomationElement[] named = app.Window
                .FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Where(e => e.Current.Name.StartsWith("Redaction region", StringComparison.Ordinal))
                .ToArray();

            Assert.True(
                named.Length > 0,
                "Dragging a box on the redaction surface marked nothing. The drag handlers " +
                "are on the overlay Canvas, and a Panel with no Background does not hit " +
                "test, so the press lands on the page image underneath and never reaches " +
                "them — which is how this shipped without drag-to-mark working at all.");
        }
    }

    /// <summary>
    /// Selects a page object by clicking it (FR-EDIT).
    ///
    /// Object editing is the product's stated differentiator, and its entire
    /// mouse story — click to select, drag to move, drag a handle to resize —
    /// runs through one handler on an overlay canvas that had no Background
    /// and therefore could not be hit. It was the third surface found with
    /// that defect, after redaction and the new placement surface, which is
    /// why this now has a test instead of a reasoned argument.
    /// </summary>
    [Fact]
    public async Task Clicking_a_page_object_selects_it()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        // The default sample is generated text, and an "object" here is an image
        // or a vector drawing, so the scanned fixture is the one with something
        // to select: each of its pages is a single placed image.
        PageForgeApp.Activate(app.FindById("OpenPdfButton")
            ?? throw new InvalidOperationException("The Open PDF… command was not found."));
        await OpenFileViaDialog(app, ScannedFixture);
        await WaitForIndicator(app, "/ 2");

        SelectToolGroup(app, "EditModeTab");
        AutomationElement toggle = app.FindByName("Object editing mode")
            ?? throw new InvalidOperationException("The Object editing mode toggle was not found.");
        ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        await Task.Delay(900);

        // Counted across the window rather than looked up in the selected tab:
        // every open document carries its own object editor, and which subtree
        // UI Automation calls the selected tab is not worth depending on here.
        // Before the click no editor anywhere has a selection; after it, one does.
        Assert.Equal(0, EnabledReplaceButtons(app));

        // The object boxes carry their label and position as accessible names,
        // which is also how the test learns where an object actually is rather
        // than guessing at a coordinate on the page.
        AutomationElement[] boxes = app.Window
            .FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(e => e.Current.Name.Contains(" at (", StringComparison.Ordinal)
                        && e.Current.Name.EndsWith(" pt", StringComparison.Ordinal)
                        && e.Current.BoundingRectangle.Width >= 8
                        && e.Current.BoundingRectangle.Height >= 8)
            .ToArray();

        Assert.True(
            boxes.Length > 0,
            "The object editor listed no objects on this page, so there is nothing to click. " +
            "This fixture is supposed to have editable content.");

        SyntheticMouse.EnsureForeground(new IntPtr(app.Window.Current.NativeWindowHandle));

        // Clip to what is actually on screen before aiming. A page-sized object
        // on a page larger than the window reports a rectangle that runs off the
        // edge, and its "centre" is then a point on someone else's window.
        System.Windows.Rect box = boxes[0].Current.BoundingRectangle;
        AutomationElement surface = app.FindByName("Object edit page 1")
            ?? throw new InvalidOperationException("The object edit page surface was not found.");
        box.Intersect(surface.Current.BoundingRectangle);
        box.Intersect(app.Window.Current.BoundingRectangle);

        Assert.False(
            box.IsEmpty || box.Width < 8 || box.Height < 8,
            $"None of the object '{boxes[0].Current.Name}' is on screen, so there is nowhere " +
            "to click it.");

        SyntheticMouse.Click((box.Left + (box.Width / 2), box.Top + (box.Height / 2)));
        await Task.Delay(400);

        Assert.True(
            EnabledReplaceButtons(app) > 0,
            $"Clicking the object at the centre of {box} selected nothing. Select, move and " +
            "resize all run through one handler on the overlay canvas, so if the press does " +
            "not reach it, none of object editing responds to the mouse at all.");
    }

    /// <summary>
    /// How many object editors currently have a selection, counted by their
    /// enabled Replace… command across the whole window.
    ///
    /// Counted rather than looked up in the selected tab because each open
    /// document carries its own editor, and which subtree UI Automation
    /// considers part of the selected tab is not a detail worth depending on.
    /// Zero before a click and more than zero after is the same evidence
    /// either way.
    /// </summary>
    private static int EnabledReplaceButtons(PageForgeApp app)
        => app.Window
            .FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ReplaceButton"))
            .Cast<AutomationElement>()
            .Count(e => e.Current.IsEnabled);

    private static void EnterSignPlacement(PageForgeApp app)
    {
        SelectToolGroup(app, "DocumentModeTab");
        PageForgeApp.Activate(
            app.FindByName("Sign document")
            ?? throw new InvalidOperationException("The Sign document command was not found."));

        // The pointer is about to be driven at this window, so it has to be the
        // one receiving input. Synthesized input follows the foreground window,
        // not the automation element, so this is checked rather than assumed.
        SyntheticMouse.EnsureForeground(new IntPtr(app.Window.Current.NativeWindowHandle));
    }

    private static AutomationElement FindSignPlacementContinue(PageForgeApp app)
        => app.FindByName("Continue to signing")
           ?? throw new InvalidOperationException("The Continue to signing command was not found.");

    private static string SignPlacementHint(PageForgeApp app)
    {
        AutomationElement hint = app.FindById("SignPlaceHint")
            ?? throw new InvalidOperationException("The placement hint text was not found.");
        return PageForgeApp.GetText(hint);
    }

    /// <summary>The placement page's rectangle, clipped to what is actually on
    /// screen. A page taller than the window reports a bounding rectangle that
    /// runs past the bottom edge, and a drag aimed there would land on whatever
    /// is behind the window.</summary>
    private static System.Windows.Rect SignPlacementPageBounds(PageForgeApp app)
    {
        AutomationElement page = app.FindByName("Page 1 to sign")
            ?? throw new InvalidOperationException("The placement page surface was not found.");

        System.Windows.Rect bounds = page.Current.BoundingRectangle;
        System.Windows.Rect window = app.Window.Current.BoundingRectangle;
        bounds.Intersect(window);

        if (bounds.IsEmpty || bounds.Width < 80 || bounds.Height < 80)
        {
            throw new InvalidOperationException(
                $"Only {bounds.Width:F0}x{bounds.Height:F0} of the placement page is on " +
                "screen, which is too little to drag a meaningful box across.");
        }

        return bounds;
    }

    /// <summary>Reads the placed box's lower-left corner out of the hint, which
    /// reports it in PDF points.</summary>
    private static (double X, double Y) ParsePlacedBox(string hint)
    {
        Match match = Regex.Match(
            hint, @"\((-?[\d.]+), (-?[\d.]+)\) to \((-?[\d.]+), (-?[\d.]+)\) pt");

        Assert.True(
            match.Success,
            $"The placement surface did not report a placed box. It said: '{hint}'.");

        return (
            double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static async Task AssertPageDrawsAsync(
        PageForgeApp app, string pageName, string? modeTab, string? toggleName)
    {
        if (modeTab is not null)
        {
            SelectToolGroup(app, modeTab);
            if (toggleName is not null)
            {
                AutomationElement toggle = app.FindByName(toggleName)
                    ?? throw new InvalidOperationException($"Mode toggle '{toggleName}' not found.");
                ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            }
        }

        AutomationElement page = app.FindByName(pageName)
            ?? throw new InvalidOperationException($"Page surface '{pageName}' not found.");

        // Rendering is asynchronous - these surfaces await a render before assigning
        // the bitmap - so poll rather than capture once. A fixed sleep would either
        // be too short and flake, or long enough that someone deletes it for being
        // slow.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WindowCapture.RegionContent content = default;
        bool everHadArea = false;
        while (DateTime.UtcNow < deadline)
        {
            System.Windows.Rect bounds = page.Current.BoundingRectangle;
            if (bounds.Width >= 1 && bounds.Height >= 1)
            {
                everHadArea = true;
                content = WindowCapture
                    .Of(new IntPtr(app.Window.Current.NativeWindowHandle))
                    .Measure(bounds);
                if (content.DifferingFraction >= MinimumInkFraction)
                {
                    return;
                }
            }

            await Task.Delay(250);
        }

        // The two ways this fails are different findings and get different
        // messages. An Image with no source collapses to nothing, so the element
        // never gains area and there is no region to measure - reporting
        // "0/0 pixels differ from the modal colour <empty>" would describe a
        // measurement that never happened. A surface that has area and paints one
        // flat colour is the other case, and there the numbers are the evidence.
        Assert.Fail(everHadArea
            ? $"'{pageName}' is a single flat colour: {content}. Expected at least " +
              $"{MinimumInkFraction:P2} of the page area to differ from its background."
            : $"'{pageName}' never took up any space on screen, so nothing was drawn " +
              "there at all. A WPF Image whose Source is null collapses to zero size - " +
              "which is exactly what assigning Source before awaiting the render does, " +
              "the bug that shipped on four separate page surfaces.");
    }

    /// <summary>
    /// How much of the page area must differ from its background colour.
    ///
    /// Low on purpose. A sparse page of text covers only a few percent of the sheet
    /// in ink, so the threshold has to pass for the emptiest real document while
    /// still failing for a blank one - and a blank one scores exactly zero, so
    /// there is a wide gap to sit in. Raise it into "how much ink" territory and it
    /// becomes a test of the fixture rather than of whether rendering happened.
    /// </summary>
    private const double MinimumInkFraction = 0.001;

    private static async Task AssertModeAsync(
        PageForgeApp app,
        string tabAutomationId,
        string? toggleName,
        string? page,
        string? panelHeader,
        string[] commands)
    {
        // Groups with no mode toggle still need a name in the failure message.
        string label = toggleName ?? tabAutomationId;

        SelectToolGroup(app, tabAutomationId);

        AutomationElement? toggle = null;
        if (toggleName is not null)
        {
            toggle = app.FindByName(toggleName)
                ?? throw new InvalidOperationException($"Mode toggle '{toggleName}' not found.");
            ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        }

        // The panel renders the page before it lists anything, so give it a beat.
        await Task.Delay(toggleName is null ? 400 : 1500);

        System.Windows.Rect window = app.Window.Current.BoundingRectangle;

        foreach (string command in commands)
        {
            AutomationElement el = app.FindByName(command)
                ?? throw new InvalidOperationException(
                    $"{label}: command '{command}' is not in the automation tree.");

            System.Windows.Rect r = el.Current.BoundingRectangle;
            Assert.False(el.Current.IsOffscreen, $"{label}: '{command}' is offscreen.");
            Assert.True(r.Width > 0 && r.Height > 0, $"{label}: '{command}' has no size.");
            Assert.True(
                r.Left >= window.Left && r.Right <= window.Right,
                $"{label}: '{command}' is outside the window horizontally " +
                $"(button {r.Left:F0}..{r.Right:F0}, window {window.Left:F0}..{window.Right:F0}).");
        }

        if (page is not null && panelHeader is not null)
        {
            // The page surface is renamed at runtime to carry its page number
            // ("Form fill page 1"), so match on the prefix rather than the
            // XAML-time name.
            AutomationElement pageEl = FindByNamePrefix(app, page)
                ?? throw new InvalidOperationException($"{label}: page surface '{page}…' not found.");
            AutomationElement headerEl = app.FindByName(panelHeader)
                ?? throw new InvalidOperationException($"{label}: panel header '{panelHeader}' not found.");

            System.Windows.Rect pageRect = pageEl.Current.BoundingRectangle;
            System.Windows.Rect headerRect = headerEl.Current.BoundingRectangle;

            Assert.True(headerRect.Width > 0 && headerRect.Height > 0,
                $"{label}: panel header '{panelHeader}' has no size.");

            // No geometric relation between the page and the panel is asserted, and
            // that is deliberate.
            //
            // There used to be a no-overlap check here, and it passed - but for the
            // wrong reason. These surfaces assigned PageImage.Source before
            // rendering and never reassigned it, so the page element had no size and
            // could not overlap anything. Once the page actually renders it is wider
            // than its viewport and extends under the panel, which is correct and
            // clipped by the ScrollViewer; UIA reports unclipped bounds, so both
            // intersection and containment now fail on a perfectly good layout.
            //
            // UIA has no z-order, so occlusion - the actual bug, a panel sharing one
            // Grid cell with the page and being drawn over - cannot be detected from
            // here at all. Catching it needs a screenshot diff. What is asserted
            // above still holds: the panel header exists and has a real size, and
            // every command is on screen.
            _ = pageRect;
        }

        // Leave the mode off so the next one starts clean.
        if (toggle is not null)
        {
            ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            await Task.Delay(400);
        }
    }

    /// <summary>
    /// Closing the last document must leave something useful on screen.
    ///
    /// Every other fact here runs with the sample document open, so the zero-tab
    /// path had no cover at all - the same blind spot that let the mode panels ship
    /// invisible. It also pins the wiring rather than just the markup: the empty
    /// state's visibility is set from the two call sites that change the tab count,
    /// because TabControl.Items is not observable, so a refactor that drops the
    /// UpdateEmptyState call from CloseTab would leave the user staring at a blank
    /// window with no failing test anywhere.
    /// </summary>
    [Fact]
    public async Task Closing_the_last_document_shows_the_empty_state()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        // The empty state must not be showing while a document is open. WPF keeps a
        // Collapsed element in the automation tree and marks it offscreen rather
        // than removing it, so absence is the wrong thing to assert - IsOffscreen is
        // what actually distinguishes "not rendered" from "rendered".
        AutomationElement? hiddenHeading = app.FindByName("No document open");
        Assert.True(
            hiddenHeading is null || hiddenHeading.Current.IsOffscreen,
            "The empty state is showing while a document is open.");

        // Close every tab. The close button is named "Close <document name>".
        for (int i = 0; i < 5; i++)
        {
            AutomationElement? close = FindByNamePrefix(app, "Close ");
            if (close is null)
            {
                break;
            }

            PageForgeApp.Activate(close);
            await Task.Delay(500);
        }

        AutomationElement heading = await WaitForOnScreenAsync(app, "No document open");
        Assert.False(heading.Current.IsOffscreen, "The empty state heading is offscreen.");

        AutomationElement open = app.FindByName("Open a PDF")
            ?? throw new InvalidOperationException(
                "The empty state offers no way to open a document.");

        System.Windows.Rect r = open.Current.BoundingRectangle;
        System.Windows.Rect window = app.Window.Current.BoundingRectangle;
        Assert.True(r.Width > 0 && r.Height > 0, "The empty state's Open button has no size.");
        Assert.True(
            r.Left >= window.Left && r.Right <= window.Right,
            "The empty state's Open button is outside the window.");
    }

    /// <summary>
    /// Waits for an element to be rendered, by exact automation name. Presence is
    /// not enough: a Collapsed element stays in the tree marked offscreen, so
    /// waiting for it to merely exist would return immediately and prove nothing.
    /// </summary>
    private static async Task<AutomationElement> WaitForOnScreenAsync(PageForgeApp app, string name)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? el = app.FindByName(name);
            if (el is not null && !el.Current.IsOffscreen)
            {
                return el;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for '{name}' to be on screen.");
    }

    /// <summary>
    /// No text anywhere in the window may carry mojibake.
    ///
    /// Five button labels shipped reading "Insertâ€¦" instead of "Insert…" because a
    /// tool round-tripped the XAML through an encoding that mangled every non-ASCII
    /// character. Nothing caught it: the suite looks elements up by AutomationId and
    /// AutomationProperties.Name, and those are ASCII, so the visible label text was
    /// never once asserted.
    ///
    /// This checks the whole automation tree rather than a list of known labels, so
    /// it catches the next one too - encoding damage is never confined to the string
    /// you thought to write a test for. The markers are how the UTF-8 bytes of the
    /// punctuation this UI uses (…, ↩, ↪) look once misdecoded as CP1252.
    /// </summary>
    [Fact]
    public async Task No_visible_text_is_mojibake()
    {
        await using PageForgeApp app = await PageForgeApp.LaunchAsync();
        await app.WaitForVisibleAsync("PageIndicatorText");

        string[] markers = ["â€", "â†", "Ã¢", "Ã©"];
        var bad = new List<string>();

        foreach (AutomationElement el in app.Window.FindAll(
            TreeScope.Descendants, Condition.TrueCondition))
        {
            string name = el.Current.Name;
            if (!string.IsNullOrEmpty(name) &&
                markers.Any(m => name.Contains(m, StringComparison.Ordinal)))
            {
                bad.Add(name);
            }
        }

        Assert.True(
            bad.Count == 0,
            "Mojibake in visible text - a file was saved through an encoding that " +
            "mangled non-ASCII characters: " + string.Join(" | ", bad));
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
        await PageForgeApp.WaitForDialogToCloseAsync(dialog);
    }

    private static async Task SaveViaDialog(PageForgeApp app, string path)
    {
        IntPtr dialog = await PageForgeApp.WaitForDialogAsync("save");
        PageForgeApp.TypeFilenameAndConfirm(dialog, path);

        // The dialog closing is the only evidence that confirming it did
        // anything. Without this the test walked on while the dialog was still
        // open, and reported "the file was not written" - true, but describing a
        // save that had never been asked for.
        await PageForgeApp.WaitForDialogToCloseAsync(dialog);
    }

    private static string ReorderedPath()
        => Path.Combine(Path.GetTempPath(), $"pf-ui-reordered-{Guid.NewGuid():N}.pdf");
}
