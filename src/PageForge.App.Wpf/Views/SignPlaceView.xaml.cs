// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PageForge.App.Wpf.ViewModels;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Where a signature goes (FR-SEC-03): the chosen page rendered at the viewer
/// DPI, with a drag-to-draw box that becomes the signature widget's rectangle.
///
/// This exists because the alternative is asking for four numbers. A signature
/// has to sit somewhere specific — clear of the text, beside the line naming
/// the signatory — and that is a judgement about the page in front of the
/// user, not a coordinate they can be expected to compute.
/// </summary>
/// <remarks>
/// The keyboard path is not a courtesy: putting the only interaction behind a
/// mouse drag would make signing unreachable for keyboard and assistive-tech
/// users (WCAG 2.1.1), and signing is precisely the operation nobody should
/// have to ask someone else to perform for them. Enter begins a box at the
/// page centre, arrows size it, Ctrl+arrows move it, Enter places it and Esc
/// cancels — the grammar the redaction surface already uses, so it is learned
/// once. "Use the default box" is the one-press escape for anyone who does not
/// care where it lands.
/// </remarks>
public partial class SignPlaceView : UserControl
{
    /// <summary>A box smaller than this in either direction is treated as a stray
    /// click rather than a placement; a 2pt-wide signature is never intended.</summary>
    private const double MinimumSizePt = 20;

    private DocumentTabViewModel? _vm;
    private double _scale = 1.0;
    private double _pixelW;
    private double _pixelH;
    private bool _busy;
    private int _pageIndex;

    private Point _dragStart;
    private Rectangle? _preview;

    /// <summary>The placed box, in PDF points, or null until one is placed.</summary>
    private PdfRect? _placed;
    private Rectangle? _placedShape;

    private bool _kbdActive;
    private Point _kbdAnchor;
    private Point _kbdCorner;

    public SignPlaceView()
    {
        InitializeComponent();
        PreviewKeyDown += SignPlaceView_PreviewKeyDown;
    }

    /// <summary>Raised when the user settles on a page and a box.</summary>
    public event Action<int, PdfRect>? Placed;

    /// <summary>Raised when the user backs out; the caller puts the shell back.</summary>
    public event Action? Cancelled;

    /// <summary>Binds the surface to a tab and starts on the page the viewer is on.</summary>
    public void SetContext(DocumentTabViewModel vm)
    {
        _vm = vm;
        _pageIndex = Math.Clamp(vm.CurrentPageIndex, 0, Math.Max(0, vm.PageCount - 1));
        ClearPlacement();
        Refresh();
    }

    /// <summary>Re-renders the current page and re-projects the placed box for the
    /// current zoom. Call after navigation or a zoom change while this is active.</summary>
    public async void Refresh()
    {
        if (_vm is null || _busy)
        {
            return;
        }

        _busy = true;
        try
        {
            _scale = _vm.RenderDpi / 72.0;
            PageText.Text = $"Page {_pageIndex + 1} of {_vm.PageCount}";
            PrevPageButton.IsEnabled = _pageIndex > 0;
            NextPageButton.IsEnabled = _pageIndex < _vm.PageCount - 1;
            AutomationProperties.SetName(PageImage, $"Page {_pageIndex + 1} to sign");

            PdfPageRegion region = _vm.Core.PageSizes[Math.Min(_pageIndex, _vm.Core.PageCount - 1)];
            _pixelW = region.WidthPt * _scale;
            _pixelH = region.HeightPt * _scale;

            PageHost.Width = _pixelW;
            PageHost.Height = _pixelH;
            Overlay.Width = _pixelW;
            Overlay.Height = _pixelH;

            // Assigned AFTER the render: the bitmap is null until then, and a
            // blank sheet would have the user placing a signature over nothing —
            // the same mistake the redaction surface shipped with once.
            var page = new PageImageViewModel(_vm.Core, _pageIndex, _vm.RenderDpi);
            await page.RenderAsync().ConfigureAwait(true);
            PageImage.Source = page.Bitmap;

            RedrawPlacement();
        }
        catch (Exception ex)
        {
            Hint(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(_pageIndex - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_pageIndex + 1);

    private void GoToPage(int index)
    {
        if (_vm is null || index < 0 || index >= _vm.PageCount)
        {
            return;
        }

        // A box placed on page 2 means nothing on page 3, and carrying it over
        // would sign a different part of the document than the one the user was
        // looking at when they drew it.
        _pageIndex = index;
        ClearPlacement();
        Refresh();
    }

    private void UseDefault_Click(object sender, RoutedEventArgs e)
        => Placed?.Invoke(_pageIndex, PdfSigningService.DefaultBounds);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_placed is { } rect)
        {
            Placed?.Invoke(_pageIndex, rect);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || _busy)
        {
            return;
        }

        if (_kbdActive)
        {
            CancelKeyboardBox();
        }

        _dragStart = e.GetPosition(Overlay);
        _preview = NewBoxShape(dashed: true);
        Canvas.SetLeft(_preview, _dragStart.X);
        Canvas.SetTop(_preview, _dragStart.Y);
        Overlay.Children.Add(_preview);
        Overlay.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_preview is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SizeShapeTo(_preview, _dragStart, e.GetPosition(Overlay));
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_preview is null)
        {
            return;
        }

        Point end = e.GetPosition(Overlay);
        Overlay.ReleaseMouseCapture();
        Overlay.Children.Remove(_preview);
        _preview = null;

        Place(_dragStart, end);
    }

    /// <summary>Turns two screen corners into the placed box, or says why it was
    /// ignored. Shared by the drag and the keyboard path, so the two cannot
    /// disagree about what counts as a placement.</summary>
    private void Place(Point start, Point end)
    {
        PdfRect rect = PageBoxGeometry.ToPdf(
            (start.X, start.Y), (end.X, end.Y), _vm?.RenderDpi ?? 96.0, _pixelH);

        if (rect.X1 - rect.X0 < MinimumSizePt || rect.Y1 - rect.Y0 < MinimumSizePt)
        {
            Hint($"That box is too small to hold a signature (at least {MinimumSizePt:F0} by {MinimumSizePt:F0} points). " +
                 "Drag a larger one, or use the default box.");
            return;
        }

        _placed = rect;
        ContinueButton.IsEnabled = true;
        RedrawPlacement();
        Hint($"Signature box on page {_pageIndex + 1}: ({rect.X0:F0}, {rect.Y0:F0}) to ({rect.X1:F0}, {rect.Y1:F0}) pt. " +
             "Drag again to move it, or Continue to choose a certificate.");
    }

    private void ClearPlacement()
    {
        _placed = null;
        if (ContinueButton is not null)
        {
            ContinueButton.IsEnabled = false;
        }

        RedrawPlacement();
    }

    /// <summary>Re-projects the placed box for the current page and zoom. The box
    /// is kept in PDF points, so a zoom change must redraw it rather than leave
    /// it pinned to the pixels it was drawn at.</summary>
    private void RedrawPlacement()
    {
        if (_placedShape is not null)
        {
            Overlay.Children.Remove(_placedShape);
            _placedShape = null;
        }

        if (_placed is not { } rect)
        {
            return;
        }

        ScreenBox box = PageBoxGeometry.ToScreen(rect, _vm?.RenderDpi ?? 96.0, _pixelH);
        _placedShape = NewBoxShape(dashed: false);
        _placedShape.Width = box.Width;
        _placedShape.Height = box.Height;
        AutomationProperties.SetName(
            _placedShape,
            $"Signature box on page {_pageIndex + 1}, {rect.X1 - rect.X0:F0} by {rect.Y1 - rect.Y0:F0} points");
        Canvas.SetLeft(_placedShape, box.Left);
        Canvas.SetTop(_placedShape, box.Top);
        Overlay.Children.Add(_placedShape);
    }

    private static Rectangle NewBoxShape(bool dashed)
    {
        var shape = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xff, 0x1a, 0x73, 0xe8)),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(38, 0x1a, 0x73, 0xe8)),
            Width = 0,
            Height = 0,
        };

        if (dashed)
        {
            shape.StrokeDashArray = new DoubleCollection { 4, 2 };
        }

        return shape;
    }

    private static void SizeShapeTo(Rectangle shape, Point a, Point b)
    {
        double left = Math.Min(a.X, b.X);
        double top = Math.Min(a.Y, b.Y);
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        shape.Width = Math.Abs(b.X - a.X);
        shape.Height = Math.Abs(b.Y - a.Y);
    }

    /// <summary>Keyboard placement (WCAG 2.1.1), in the same grammar as the
    /// redaction surface: Enter begins a box at the page centre, arrows size the
    /// bottom-right corner, Ctrl+arrows move the whole box, Enter places it, Esc
    /// cancels.</summary>
    private void SignPlaceView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || _busy || !Overlay.IsKeyboardFocusWithin)
        {
            return;
        }

        const double step = 8;

        if (e.Key == Key.Escape)
        {
            if (_kbdActive)
            {
                CancelKeyboardBox();
                Hint("Box cancelled. Drag one, press Enter to begin another, or Cancel to stop signing.");
            }
            else
            {
                Cancelled?.Invoke();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (!_kbdActive)
            {
                _kbdAnchor = new Point((_pixelW / 2) - (100 * _scale), (_pixelH / 2) - (30 * _scale));
                _kbdCorner = new Point(_kbdAnchor.X + (200 * _scale), _kbdAnchor.Y + (60 * _scale));
                _kbdActive = true;
                _preview = NewBoxShape(dashed: true);
                Overlay.Children.Add(_preview);
                SizeShapeTo(_preview, _kbdAnchor, _kbdCorner);
                Hint("Sizing a box: arrows resize, Ctrl+arrows move, Enter places it, Esc cancels.");
            }
            else
            {
                Point anchor = _kbdAnchor;
                Point corner = _kbdCorner;
                CancelKeyboardBox();
                Place(anchor, corner);
            }

            e.Handled = true;
            return;
        }

        if (!_kbdActive || _preview is null)
        {
            return;
        }

        bool move = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        double dx = e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 };
        double dy = e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 };

        if (dx == 0 && dy == 0)
        {
            return;
        }

        if (move)
        {
            _kbdAnchor = new Point(_kbdAnchor.X + dx, _kbdAnchor.Y + dy);
            _kbdCorner = new Point(_kbdCorner.X + dx, _kbdCorner.Y + dy);
        }
        else
        {
            // Never let the far corner cross the anchor: an inverted box reads as
            // degenerate and would be refused as "too small" for no visible reason.
            _kbdCorner = new Point(
                Math.Max(_kbdAnchor.X + 1, _kbdCorner.X + dx),
                Math.Max(_kbdAnchor.Y + 1, _kbdCorner.Y + dy));
        }

        SizeShapeTo(_preview, _kbdAnchor, _kbdCorner);
        e.Handled = true;
    }

    private void CancelKeyboardBox()
    {
        _kbdActive = false;
        if (_preview is not null)
        {
            Overlay.Children.Remove(_preview);
            _preview = null;
        }
    }

    private void Hint(string message) => HintText.Text = message;
}
