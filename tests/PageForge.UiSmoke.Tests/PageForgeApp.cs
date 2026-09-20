// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    public const string WindowTitle = "PageForge";

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

    /// <summary>
    /// Locates the built WPF app, preferring the build configuration this test
    /// assembly was itself built as and falling back to the other.
    ///
    /// This used to look only in <c>bin/Debug</c>. Nobody noticed, because the
    /// suite had never run anywhere but a developer's machine in Debug: it is
    /// absent from <c>PageForge.sln</c> and from every CI lane, despite this
    /// project's own header claiming it "runs fully offline in CI". Wiring it
    /// into CI, which builds Release, is what surfaced the assumption.
    /// </summary>
    public static string FindAppExe()
    {
        string root = FindRepoRoot();

        // The test binary lives in tests/<project>/bin/<Config>/<tfm>/. Trimming
        // the trailing separator makes GetDirectoryName step up from <tfm> to
        // <Config>, which is the name wanted - one step, not two.
        string? configurationDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string ownConfiguration = Path.GetFileName(configurationDir ?? string.Empty);
        string[] configurations = ownConfiguration is "Debug" or "Release"
            ? [ownConfiguration, ownConfiguration == "Debug" ? "Release" : "Debug"]
            : ["Debug", "Release"];

        var probed = new List<string>();
        foreach (string configuration in configurations)
        {
            string exe = Path.Combine(
                root, "src", "PageForge.App.Wpf", "bin", configuration,
                "net8.0-windows", "PageForge.App.Wpf.exe");
            if (File.Exists(exe))
            {
                return exe;
            }

            probed.Add(exe);
        }

        // Name every path tried. A bare "not built" naming one configuration is
        // what would make a wrong guess look like a build problem.
        throw new FileNotFoundException(
            "WPF app not built. Build src/PageForge.App.Wpf first. Looked in:" +
            Environment.NewLine + string.Join(Environment.NewLine, probed),
            probed[0]);
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

        _current = new PageForgeApp(process, window);
        return _current;
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

    /// <summary>
    /// Finds a caption emitted by <c>HeadingTextBlock</c> (class name "Heading").
    ///
    /// Matching on the class as well as the name is what makes this unambiguous:
    /// a plain by-name search can land on a different control that happens to
    /// share the caption's accessible name, and then assert against the wrong
    /// element's class.
    /// </summary>
    public AutomationElement? FindHeadingInSelectedTab(string name)
    {
        AutomationElement? tab = FindSelectedTabItem();
        if (tab is null)
        {
            return null;
        }

        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ClassNameProperty, "Heading"));

        return FindOne(() => tab.FindFirst(TreeScope.Descendants, condition));
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

    /// <summary>
    /// Resizes the main window and waits for the layout to settle.
    ///
    /// Toolbar overflow only shows up at a width, so a test that only ever runs at
    /// the default 1200x820 proves nothing about the sizes users actually pick.
    /// MainWindow declares MinWidth="900", so that is the narrowest width the app
    /// claims to support and the one worth holding it to.
    /// </summary>
    public async Task ResizeAsync(double width, double height)
    {
        var transform = (TransformPattern)Window.GetCurrentPattern(TransformPattern.Pattern);
        transform.Resize(width, height);

        // WPF lays out on the dispatcher after the resize call returns.
        await Task.Delay(600);

        // A resize that silently did nothing would make every width-dependent
        // assertion vacuous - the test would keep passing at the default size while
        // claiming to have checked a narrow one. The tolerance covers window chrome
        // and DPI scaling, not a no-op.
        double actual = Window.Current.BoundingRectangle.Width;
        if (Math.Abs(actual - width) > 40)
        {
            throw new InvalidOperationException(
                $"Resize to {width:F0} did not take effect; the window is {actual:F0} wide.");
        }
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

    public async static Task<IntPtr> WaitForDialogAsync(string kind, TimeSpan timeout = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(10) : timeout;
        int processId = _current?._process?.Id ?? -1;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            IntPtr hwnd = FindDialog(kind, processId);
            if (hwnd != IntPtr.Zero)
            {
                return hwnd;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for the {kind} dialog.");
    }

    private static PageForgeApp? _current;

    /// <summary>Locates the common file dialog (#32770) owned by the app under test.
    /// The dialog is not exposed through UI Automation on this machine (see
    /// UiSmokeTests notes), so it is found via Win32 window enumeration and driven
    /// with SendMessage, which is the same underlying window the dialog shows.</summary>
    private static IntPtr FindDialog(string kind, int processId)
    {
        string match = kind.ToLowerInvariant() switch
        {
            "open" => "open",
            "save" => "save",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hWnd, lParam) =>
        {
            if (!Native.IsWindowVisible(hWnd))
            {
                return true;
            }

            Native.GetWindowThreadProcessId(hWnd, out uint pid);
            if (processId > 0 && pid != (uint)processId)
            {
                return true;
            }

            var cls = new StringBuilder(256);
            Native.GetClassName(hWnd, cls, cls.Capacity);
            if (cls.ToString() != "#32770")
            {
                return true;
            }

            var title = new StringBuilder(256);
            Native.GetWindowText(hWnd, title, title.Capacity);
            if (title.ToString().ToLowerInvariant().StartsWith(match, StringComparison.Ordinal))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Types a full path into the common dialog's file-name field and presses
    /// OK. The modern common dialog varies its layout by dialog kind (open uses the
    /// classic file-name combo 0x047C; save uses a plain Edit 0x3E9), so the target
    /// is located by control id first, then by the single visible Edit control.
    /// Confirmation simulates a real click on the OK/Save button (id 1), which the
    /// newer dialog implementations require; the WM_COMMAND shortcut is ignored.</summary>
    public static void TypeFilenameAndConfirm(IntPtr dialog, string path)
    {
        const int fileNameCombo = 0x047C;
        const int idOk = 1; // button control id in both open and save dialogs
        IntPtr target = Native.GetDlgItem(dialog, fileNameCombo);
        if (target == IntPtr.Zero)
        {
            target = Native.FindChildWithId(dialog, fileNameCombo);
        }

        if (target == IntPtr.Zero)
        {
            target = Native.FindVisibleEdit(dialog);
        }

        if (target == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "File-name field not found in the dialog. Children were:" +
                Environment.NewLine + Native.DescribeChildren(dialog));
        }

        Native.SendMessage(target, Native.WmSetText, IntPtr.Zero, path);

        // Check the text actually took, rather than assuming SendMessage did what
        // was asked. It did not on the CI image: the helper found a control, set
        // nothing anyone could see, clicked OK, and the dialog quietly stayed put
        // with no file written and no error anywhere - the failure that made
        // reorder-and-save untestable in CI (issue #6). Judging the postcondition
        // instead of the call is the same correction the MinIO bucket race needed.
        string readBack = Native.GetControlText(target);
        if (readBack != path)
        {
            throw new InvalidOperationException(
                $"Setting the file name did not take. Wanted '{path}', the field reads " +
                $"'{readBack}'. The dialog's children were:" +
                Environment.NewLine + Native.DescribeChildren(dialog));
        }

        IntPtr okButton = Native.GetDlgItem(dialog, idOk);
        if (okButton == IntPtr.Zero)
        {
            okButton = Native.FindChildWithId(dialog, idOk);
        }

        if (okButton == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "OK button not found in the dialog. Children were:" +
                Environment.NewLine + Native.DescribeChildren(dialog));
        }

        // Two ways of pressing it, because they fail on different dialogs. A
        // WM_COMMAND to the DIALOG is how a real button press reaches it, and the
        // modern IFileDialog acts on that; BM_CLICK to the button is what the
        // classic dialog responds to. Sending both is harmless - the first to
        // work closes the dialog and the second lands on a dead handle.
        Native.SendMessage(
            dialog,
            Native.WmCommand,
            new IntPtr((Native.BnClicked << 16) | (idOk & 0xFFFF)),
            okButton);

        if (Native.IsWindow(dialog))
        {
            Native.SendMessage(okButton, Native.BmClick, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Waits for a dialog to close, which is the only evidence that confirming it
    /// did anything.
    ///
    /// Without this the caller carries on against a dialog still sitting on
    /// screen, and every later assertion describes a state the app never left.
    /// </summary>
    public static async Task WaitForDialogToCloseAsync(IntPtr dialog, TimeSpan timeout = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(10) : timeout;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Native.IsWindow(dialog) || !Native.IsWindowVisible(dialog))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            "The file dialog did not close after confirming it, so the file name was " +
            "never accepted. The dialog's children were:" +
            Environment.NewLine + Native.DescribeChildren(dialog));
    }

    private static class Native
    {
        public const int WmSetText = 0x000C;
        public const int WmGetText = 0x000D;
        public const int WmGetTextLength = 0x000E;
        public const int WmCommand = 0x0111;
        public const int BmClick = 0x00F5;
        public const int BnClicked = 0;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam);

        /// <summary>Reads a control's text, for checking that setting it worked.</summary>
        public static string GetControlText(IntPtr control)
        {
            int length = (int)SendMessage(control, WmGetTextLength, IntPtr.Zero, IntPtr.Zero);
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(length + 1);
            _ = SendMessage(control, WmGetText, new IntPtr(buffer.Capacity), buffer);
            return buffer.ToString();
        }

        /// <summary>Every child of the dialog with its class, control id and text.
        /// This is what a failure needs to be diagnosable on a machine nobody can
        /// open: the classic control ids are an assumption about the dialog's
        /// shape, and an image presenting a different one should say so rather
        /// than silently do nothing.</summary>
        public static string DescribeChildren(IntPtr parent)
        {
            var description = new StringBuilder();
            EnumChildWindows(parent, (hWnd, lParam) =>
            {
                var className = new StringBuilder(128);
                _ = GetClassName(hWnd, className, className.Capacity);
                var text = new StringBuilder(128);
                _ = GetWindowText(hWnd, text, text.Capacity);
                description.AppendLine(
                    $"  id={GetDlgCtrlID(hWnd)} class={className} " +
                    $"visible={IsWindowVisible(hWnd)} text='{text}'");
                return true;
            }, IntPtr.Zero);
            return description.ToString();
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc enumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Finds a direct child control by its control id (fallback when
        /// the layout places the target inside nested wrapper controls).</summary>
        public static IntPtr FindChildWithId(IntPtr parent, int controlId)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (hWnd, lParam) =>
            {
                if (GetDlgCtrlID(hWnd) == controlId)
                {
                    found = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Finds the file-name Edit: the only visible Edit child of the
        /// dialog (the address-bar Edit is always hidden).</summary>
        public static IntPtr FindVisibleEdit(IntPtr parent)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "Edit")
                {
                    found = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }
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
