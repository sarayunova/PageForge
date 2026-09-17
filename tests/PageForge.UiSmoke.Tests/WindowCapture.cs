// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Runtime.InteropServices;

namespace PageForge.UiSmoke.Tests;

/// <summary>
/// The pixels of the app's window, so a test can ask what is actually drawn.
///
/// UI Automation cannot answer that. It reports that an element exists, where it
/// is and how big it is - and every one of this project's UI defects passed all
/// three of those checks while being visibly wrong: the viewer blank, then the
/// redact, form-fill and object-edit surfaces blank, form outlines mirrored onto
/// the wrong half of the page, side panels invisible behind the page, toolbar
/// commands past the right edge, nine dark-theme colours on a light background.
/// Nothing in this repository has ever looked at a pixel.
///
/// Captured with PrintWindow(PW_RENDERFULLCONTENT) rather than by reading the
/// screen: PrintWindow asks the window to draw itself, so the result does not
/// depend on the window being frontmost or unobscured. That keeps the check free
/// of the flake that makes screenshot tests a maintenance burden. The cost is
/// that occlusion by another window is invisible here - acceptable, because the
/// question asked is "did this draw", not "is something on top of it".
///
/// Pixels are read straight out of a DIB section, so no imaging library is
/// involved: a new dependency would need an AGPL compatibility check and a
/// THIRD-PARTY-NOTICES entry, which is a poor trade for counting colours.
/// </summary>
internal sealed class WindowCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;

    private WindowCapture(int width, int height, byte[] bgra, int originX, int originY)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
        OriginX = originX;
        OriginY = originY;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Top-down BGRA, four bytes per pixel, stride = Width * 4.</summary>
    public byte[] Bgra { get; }

    /// <summary>Screen coordinate of the capture's top-left, so a UI Automation
    /// bounding rectangle (which is in screen coordinates) can be mapped in.</summary>
    public int OriginX { get; }

    public int OriginY { get; }

    public static WindowCapture Of(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("No window handle; the element has no native window.", nameof(hwnd));
        }

        if (!Native.GetWindowRect(hwnd, out Native.Rect rect))
        {
            throw new InvalidOperationException("GetWindowRect failed for the app window.");
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Window has no area: {width}x{height}.");
        }

        IntPtr windowDc = Native.GetWindowDC(hwnd);
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            memoryDc = Native.CreateCompatibleDC(windowDc);

            var info = new Native.BitmapInfo
            {
                Header = new Native.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
                    Width = width,

                    // Negative: a top-down DIB, so row 0 is the top of the window
                    // and the buffer can be indexed without flipping it.
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                },
            };

            dib = Native.CreateDIBSection(memoryDc, ref info, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            previous = Native.SelectObject(memoryDc, dib);
            if (!Native.PrintWindow(hwnd, memoryDc, PwRenderFullContent))
            {
                throw new InvalidOperationException(
                    "PrintWindow failed. If that turns out to be image-specific, capturing the " +
                    "screen is the alternative - but check first that the result is not simply blank.");
            }

            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return new WindowCapture(width, height, buffer, rect.Left, rect.Top);
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                _ = Native.SelectObject(memoryDc, previous);
            }

            if (dib != IntPtr.Zero)
            {
                _ = Native.DeleteObject(dib);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = Native.DeleteDC(memoryDc);
            }

            _ = Native.ReleaseDC(hwnd, windowDc);
        }
    }

    /// <summary>
    /// How varied a rectangle of the capture is, given in screen coordinates.
    ///
    /// The measure is deliberately crude: the most common colour, and how many
    /// pixels differ from it by more than a small tolerance. A blank sheet is one
    /// colour; anything actually drawn - text, a rule, an outline - is not. That
    /// makes it immune to what makes image comparison brittle: font hinting,
    /// anti-aliasing, theme, scaling, and the exact content of the document. It
    /// cannot say the page is CORRECT, only that something was painted, which is
    /// exactly the defect that keeps recurring here.
    /// </summary>
    public RegionContent Measure(System.Windows.Rect screenRegion, int tolerance = 12)
    {
        int left = Math.Max(0, (int)Math.Round(screenRegion.Left) - OriginX);
        int top = Math.Max(0, (int)Math.Round(screenRegion.Top) - OriginY);
        int right = Math.Min(Width, (int)Math.Round(screenRegion.Right) - OriginX);
        int bottom = Math.Min(Height, (int)Math.Round(screenRegion.Bottom) - OriginY);

        if (right <= left || bottom <= top)
        {
            throw new InvalidOperationException(
                $"Region {screenRegion} does not overlap the captured window " +
                $"({Width}x{Height} at {OriginX},{OriginY}).");
        }

        var histogram = new Dictionary<int, int>();
        for (int y = top; y < bottom; y++)
        {
            int row = y * Width * 4;
            for (int x = left; x < right; x++)
            {
                int i = row + (x * 4);
                int packed = (Bgra[i + 2] << 16) | (Bgra[i + 1] << 8) | Bgra[i];
                histogram[packed] = histogram.GetValueOrDefault(packed) + 1;
            }
        }

        int total = (right - left) * (bottom - top);
        KeyValuePair<int, int> modal = histogram.MaxBy(pair => pair.Value);
        int modalR = (modal.Key >> 16) & 0xFF;
        int modalG = (modal.Key >> 8) & 0xFF;
        int modalB = modal.Key & 0xFF;

        int differing = 0;
        foreach ((int packed, int count) in histogram)
        {
            int r = (packed >> 16) & 0xFF;
            int g = (packed >> 8) & 0xFF;
            int b = packed & 0xFF;
            if (Math.Abs(r - modalR) > tolerance ||
                Math.Abs(g - modalG) > tolerance ||
                Math.Abs(b - modalB) > tolerance)
            {
                differing += count;
            }
        }

        return new RegionContent(
            total,
            differing,
            histogram.Count,
            $"#{modalR:X2}{modalG:X2}{modalB:X2}",
            new System.Windows.Int32Rect(left, top, right - left, bottom - top));
    }

    /// <param name="Pixels">Pixels examined.</param>
    /// <param name="Differing">Pixels differing from the most common colour.</param>
    /// <param name="DistinctColours">Distinct colours present.</param>
    /// <param name="ModalColour">The most common colour, as #RRGGBB.</param>
    /// <param name="Examined">The window-relative rectangle actually measured.</param>
    public readonly record struct RegionContent(
        int Pixels,
        int Differing,
        int DistinctColours,
        string ModalColour,
        System.Windows.Int32Rect Examined)
    {
        public double DifferingFraction => Pixels == 0 ? 0 : (double)Differing / Pixels;

        public override string ToString() =>
            $"{Examined.Width}x{Examined.Height} at ({Examined.X},{Examined.Y}): " +
            $"{Differing}/{Pixels} pixels ({DifferingFraction:P2}) differ from the modal " +
            $"colour {ModalColour}; {DistinctColours} distinct colours";
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BitmapInfo pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);
    }
}
