// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace PageForge.UiSmoke.Tests;

/// <summary>
/// Launches the runnable WPF fallback app in place from its build output and
/// drives it through the built-in UI Automation client (the same accessibility
/// surface WinAppDriver wraps). All waits are bounded so failures surface as
/// timeouts rather than hangs. Owns the child process; call <see cref="DisposeAsync"/>
/// to terminate it.
/// </summary>
internal sealed class PageForgeApp : IAsyncDisposable
{
    public const string WindowTitle = "PageForge — Phase 1 Viewer (WPF fallback)";

    private readonly Process? _process;
    private bool _disposed;

    private PageForgeApp(Process? process, AutomationElement window)
    {
        _process = process;
        Window = window;
    }

    public AutomationElement Window { get; }

    public static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PageForge.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? Directory.GetCurrentDirectory();
    }

    public static string FindAppExe()
    {
        string exe = Path.Combine(
            FindRepoRoot(),
            "src", "PageForge.App.Wpf", "bin", "Debug", "net8.0-windows", "PageForge.App.Wpf.exe");
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("WPF app not built. Build src/PageForge.App.Wpf first.", exe);
        }

        return exe;
    }

    public static async Task<PageForgeApp> LaunchAsync(TimeSpan timeout = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(30) : timeout;
        string exe = FindAppExe();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe),
        };

        Process? process = Process.Start(psi);
        AutomationElement? window = null;
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (process is not null && process.HasExited)
            {
                throw new InvalidOperationException($"App exited unexpectedly (code {process.ExitCode}).");
            }

            window = FindWindow();
            if (window is not null)
            {
                break;
            }

            await Task.Delay(250);
        }

        if (window is null)
        {
            process?.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out waiting for the PageForge main window.");
        }

        return new PageForgeApp(process, window);
    }

    private static AutomationElement? FindWindow()
    {
        try
        {
            return AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, WindowTitle));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    public AutomationElement? FindById(string automationId, TreeScope scope = TreeScope.Descendants)
        => FindOne(() => Window.FindFirst(scope, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)));

    public AutomationElement? FindByName(string name, TreeScope scope = TreeScope.Descendants)
        => FindOne(() => Window.FindFirst(scope, new PropertyCondition(AutomationElement.NameProperty, name)));

    private static AutomationElement? FindOne(Func<AutomationElement?> search)
    {
        try
        {
            return search();
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    public async Task<AutomationElement> WaitForVisibleAsync(string automationId, TimeSpan timeout = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(10) : timeout;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? el = FindById(automationId);
            if (el is not null && !el.Current.IsOffscreen)
            {
                return el;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for element by id '{automationId}'.");
    }

    /// <summary>The automation element for the currently selected (visible) tab.
    /// Each open document creates a DocumentView with identical AutomationIds, so
    /// automation queries must be scoped to this subtree to avoid cross-tab
    /// ambiguity.</summary>
    public AutomationElement? FindSelectedTabItem()
    {
        AutomationElement? tabs = FindById("DocTabs");
        if (tabs is null)
        {
            return null;
        }

        foreach (AutomationElement item in tabs.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                if (item.Current.ControlType == ControlType.TabItem &&
                    item.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern sip &&
                    sip.Current.IsSelected)
                {
                    return item;
                }
            }
            catch (ElementNotAvailableException)
            {
                // tab closed mid-enumeration
            }
        }

        return null;
    }

    public AutomationElement? FindInSelectedTabById(string automationId)
    {
        AutomationElement? tab = FindSelectedTabItem();
        if (tab is null)
        {
            return null;
        }

        return FindOne(() => tab.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)));
    }

    public AutomationElement? FindInSelectedTabByName(string name)
    {
        AutomationElement? tab = FindSelectedTabItem();
        if (tab is null)
        {
            return null;
        }

        return FindOne(() => tab.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)));
    }

    /// <summary>Reads an element's text via Value/Text patterns, falling back to
    /// its accessible Name.</summary>
    public static string GetText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value) &&
            ((ValuePattern)value).Current.IsReadOnly == false && !string.IsNullOrEmpty((value as ValuePattern)!.Current.Value))
        {
            return ((ValuePattern)value).Current.Value;
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? text))
        {
            try
            {
                string t = ((TextPattern)text).DocumentRange.GetText(-1);
                if (!string.IsNullOrEmpty(t))
                {
                    return t;
                }
            }
            catch (InvalidOperationException)
            {
                // TextPattern raised while text empty
            }
        }

        return element.Current.Name;
    }

    /// <summary>Sets the text of an edit control (used for the common file
    /// dialog's file-name box).</summary>
    public static void SetText(AutomationElement element, string text)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value))
        {
            ((ValuePattern)value).SetValue(text);
            return;
        }

        throw new InvalidOperationException("Element supports no value pattern.");
    }

    /// <summary>Invokes any element that supports Invoke, Toggle, or SelectionItem
    /// (WPF buttons/toggle-buttons/list items).</summary>
    public static void Activate(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object? invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selection))
        {
            ((SelectionItemPattern)selection).Select();
            return;
        }

        throw new InvalidOperationException("Element supports no actionable pattern.");
    }

    public async static Task<AutomationElement> WaitForDialogAsync(string kind, TimeSpan timeout = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(10) : timeout;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? dialog = FindDialog(kind);
            if (dialog is not null)
            {
                return dialog;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for the {kind} dialog.");
    }

    private static AutomationElement? FindDialog(string kind)
    {
        string match = kind.ToLowerInvariant() switch
        {
            "open" => "open",
            "save" => "save",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        foreach (AutomationElement child in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                if (child.Current.ClassName == "#32770" && child.Current.Name.ToLowerInvariant().StartsWith(match, StringComparison.Ordinal))
                {
                    return child;
                }
            }
            catch (ElementNotAvailableException)
            {
                // dialog closed mid-enumeration
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
        }
    }
}
