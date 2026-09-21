// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Runtime.InteropServices;

namespace PageForge.UiSmoke.Tests;

/// <summary>
/// A real mouse drag, synthesized with SendInput.
///
/// UI Automation can invoke, toggle and select, but it cannot drag — and a
/// drag is the whole interaction for placing a signature. Everything else
/// about placement can be asserted without one (the coordinate maths in the
/// smoke proof, the surface rendering through Automation), which is precisely
/// what makes the gesture worth driving: it is the only part where a mistake
/// would be invisible to every other check.
/// </summary>
/// <remarks>
/// This is not the file-dialog trap recorded in AGENTS.md (issue #6). That
/// failed because the Win32 messages that confirm a NATIVE common dialog
/// behave differently on a hosted runner. Here the target is our own WPF
/// canvas in our own process: the input goes through the ordinary mouse
/// pipeline and the window handles it like any other click, with no foreign
/// modal involved.
///
/// SendInput is session-wide — it moves the actual pointer — so this needs the
/// interactive desktop session the suite already requires.
/// </remarks>
internal static class SyntheticMouse
{
    /// <summary>
    /// Makes this process DPI-aware before anything reads a coordinate.
    ///
    /// A DPI-unaware process is lied to by Windows: it is told the screen is
    /// smaller than it is and its coordinates are scaled on the way out. UI
    /// Automation then reports one coordinate space while SendInput's absolute
    /// input uses another, so on a scaled display every drag lands a scale
    /// factor away from where it was aimed — quietly, because nothing errors.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void MakeProcessDpiAware()
    {
        const int PerMonitorAwareV2 = -4;
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(PerMonitorAwareV2));
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows; the suite's other assertions do not depend on it.
        }
    }

    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>
    /// Brings a window to the foreground and confirms it got there.
    ///
    /// Synthesized input goes wherever the pointer is, to whatever window is
    /// active — so without this the drag can land on another application
    /// entirely and the test reports that the feature did nothing. Confirming
    /// rather than assuming matters more than usual here: a silently
    /// unfocused run would make every assertion below it vacuous, which is the
    /// failure shape this suite exists to prevent.
    /// </summary>
    public static void EnsureForeground(IntPtr window)
    {
        const int SwRestore = 9;

        ShowWindow(window, SwRestore);
        BringWindowToTop(window);
        SetForegroundWindow(window);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (GetForegroundWindow() == window)
            {
                // The window is up but WPF still has activation work queued;
                // input delivered in that gap is dropped.
                Thread.Sleep(300);
                return;
            }

            SetForegroundWindow(window);
            Thread.Sleep(150);
        }

        throw new InvalidOperationException(
            "The PageForge window could not be brought to the foreground, so a synthesized " +
            "drag would be delivered to whatever window is. This needs an interactive, " +
            "unlocked desktop session with no other window holding foreground lock.");
    }

    /// <summary>
    /// Presses at <paramref name="from"/>, moves to <paramref name="to"/> in
    /// steps, and releases — all in physical screen pixels, which is what UI
    /// Automation's BoundingRectangle reports.
    /// </summary>
    /// <remarks>
    /// The intermediate moves are not decoration. A press followed by a single
    /// jump to the release point produces no MouseMove in between, so a surface
    /// that tracks the rubber band during the drag would look like it worked
    /// while never having been told the box grew.
    /// </remarks>
    public static void Drag((double X, double Y) from, (double X, double Y) to, int steps = 12)
    {
        MoveTo(from.X, from.Y);
        Thread.Sleep(60);
        Send(MouseEventLeftDown);
        Thread.Sleep(60);

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            MoveTo(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));
            Thread.Sleep(15);
        }

        Thread.Sleep(60);
        Send(MouseEventLeftUp);
        Thread.Sleep(120);
    }

    private static void MoveTo(double x, double y)
    {
        // Absolute mouse input is normalized to 0..65535 across the whole
        // virtual desktop, so the conversion has to account for a monitor left
        // of or above the primary one, which has negative coordinates —
        // otherwise every drag lands on the primary screen wherever the window
        // actually is.
        int originX = GetSystemMetrics(SmXVirtualScreen);
        int originY = GetSystemMetrics(SmYVirtualScreen);
        int width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen) - 1);
        int height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen) - 1);

        int nx = (int)Math.Round((x - originX) * 65535.0 / width);
        int ny = (int)Math.Round((y - originY) * 65535.0 / height);

        Send(MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk, nx, ny);
    }

    private static void Send(uint flags, int x = 0, int y = 0)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = x,
                    Dy = y,
                    MouseData = 0,
                    DwFlags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            throw new InvalidOperationException(
                $"SendInput did not deliver the mouse event (flags 0x{flags:x}); " +
                $"last error {Marshal.GetLastWin32Error()}. This needs an interactive " +
                "desktop session that is not locked.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointStruct
    {
        public int X;
        public int Y;
    }

    /// <summary>Where the pointer actually ended up, for diagnosing a drag that
    /// was aimed at one place and delivered somewhere else.</summary>
    public static PointStruct CursorPosition()
    {
        GetCursorPos(out PointStruct point);
        return point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);
}
