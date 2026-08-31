// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PageForge.App.Wpf.ViewModels;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Interactive object-edit surface (FR-EDIT-04 follow-on): the current page
/// rendered at the viewer DPI with a selection overlay over every image/vector
/// object. Click selects, drag moves, dragging a corner/edge handle resizes, and
/// the Replace… button paints a picked PNG/JPEG inside the selected object's box.
/// Commits go through the Core command stack via <see cref="DocumentTabViewModel"/>
/// so undo/redo stays on the shared FR-EDIT-05 history.
/// </summary>
public partial class ObjectEditView : UserControl
{
    private const double HandleSize = 8;
    private const double HandleHit = 11;

    private sealed class ObjBox
    {
        public required PdfPageObject Obj { get; init; }
        public required Rect Screen { get; set; }
        public required Rectangle Visual { get; init; }
    }

    private DocumentTabViewModel? _vm;
    private PageImageViewModel? _page;
    private double _scale = 1.0;
    private double _pixelW;
    private double _pixelH;
    private readonly List<ObjBox> _boxes = new();
    private ObjBox? _selected;
    private bool _busy;

    /// <summary>Which part of the selection is being dragged this gesture.</summary>
    private enum DragMode { None, Move, Nw, N, Ne, E, Se, S, Sw, W }

    private DragMode _dragMode = DragMode.None;
    private System.Windows.Point _dragStartScreen;
    private PdfRect _dragStartBounds;
    private bool _dragging;

    public ObjectEditView()
    {
        InitializeComponent();
        Overlay.MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
        Overlay.MouseMove += Overlay_MouseMove;
        Overlay.MouseLeftButtonUp += Overlay_MouseLeftButtonUp;
    }

    /// <summary>Binds this surface to a document tab and (re)loads the current page.</summary>
    public void SetContext(DocumentTabViewModel vm)
    {
        _vm = vm;
        Refresh();
    }

    /// <summary>Re-renders the current page and re-lists its objects. Call after
    /// navigation, zoom, or undo/redo while this surface is active.</summary>
    public async void Refresh()
    {
        if (_vm is null || _busy)
        {
            return;
        }

        _busy = true;
        try
        {
            int pageIndex = _vm.Core.CurrentPage;
            _scale = _vm.RenderDpi / 72.0;

            PdfPageRegion region = _vm.Core.PageSizes[Math.Min(pageIndex, _vm.Core.PageCount - 1)];
            _pixelW = region.WidthPt * _scale;
            _pixelH = region.HeightPt * _scale;

            PageHost.Width = _pixelW;
            PageHost.Height = _pixelH;
            Overlay.Width = _pixelW;
            Overlay.Height = _pixelH;

            _page = new PageImageViewModel(_vm.Core, pageIndex, _vm.RenderDpi);
            PageImage.Source = _page.Bitmap;
            await _page.RenderAsync().ConfigureAwait(true);

            IReadOnlyList<PdfPageObject> objects = await _vm.ListObjectsAsync().ConfigureAwait(true);
            Rebuild(objects);
        }
        catch (Exception ex)
        {
            Status(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Rebuild(IReadOnlyList<PdfPageObject> objects)
    {
        Overlay.Children.Clear();
        _boxes.Clear();
        _selected = null;
        ReplaceButton.IsEnabled = false;
        Hint(objects.Count == 0
            ? "No image/vector objects on this page to edit."
            : "Click an object to select; drag to move; drag a handle to resize.");

        foreach (PdfPageObject obj in objects)
        {
            var rect = PdfBoxToScreen(obj.Bounds);
            var border = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(16, 0x2b, 0x8c, 0xff)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xcc, 0x77, 0x77, 0x77)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
            };
            Canvas.SetLeft(border, rect.X);
            Canvas.SetTop(border, rect.Y);
            border.Width = rect.Width;
            border.Height = rect.Height;

            var box = new ObjBox { Obj = obj, Screen = rect, Visual = border };
            _boxes.Add(box);
            Overlay.Children.Add(border);
        }
    }

    private Rect PdfBoxToScreen(PdfRect b)
    {
        double x = b.X0 * _scale;
        double y = _pixelH - b.Y1 * _scale;
        double w = (b.X1 - b.X0) * _scale;
        double h = (b.Y1 - b.Y0) * _scale;
        return new Rect(x, y, w, h);
    }

    private PdfRect ScreenBoxToPdf(Rect r)
    {
        double x0 = r.X / _scale;
        double x1 = (r.X + r.Width) / _scale;
        double y0 = (_pixelH - (r.Y + r.Height)) / _scale;
        double y1 = (_pixelH - r.Y) / _scale;
        return new PdfRect(x0, y0, x1, y1);
    }

    private void Select(ObjBox? box)
    {
        if (ReferenceEquals(box, _selected))
        {
            RedrawSelectionVisuals();
            return;
        }

        _selected = box;
        ReplaceButton.IsEnabled = box is not null;
        RedrawSelectionVisuals();
    }

    private void RedrawSelectionVisuals()
    {
        if (_selected is not null)
        {
            _selected.Visual.Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x2b, 0x8c, 0xff));
            _selected.Visual.Stroke = new SolidColorBrush(Color.FromArgb(0xff, 0x2b, 0x8c, 0xff));
            _selected.Visual.StrokeThickness = 2;
            _selected.Visual.StrokeDashArray = null;

            foreach (System.Windows.Point p in HandlePoints(_selected.Screen))
            {
                var h = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xff, 0x2b, 0x8c, 0xff)),
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(h, p.X - HandleSize / 2);
                Canvas.SetTop(h, p.Y - HandleSize / 2);
                Overlay.Children.Add(h);
            }
        }
        else
        {
            foreach (ObjBox box in _boxes)
            {
                box.Visual.Fill = new SolidColorBrush(Color.FromArgb(16, 0x2b, 0x8c, 0xff));
                box.Visual.Stroke = new SolidColorBrush(Color.FromArgb(0xcc, 0x77, 0x77, 0x77));
                box.Visual.StrokeThickness = 1;
                box.Visual.StrokeDashArray = new DoubleCollection { 3, 2 };
            }
        }
    }

    private static System.Windows.Point[] HandlePoints(Rect r) => new[]
    {
        new System.Windows.Point(r.Left, r.Top),      // NW
        new System.Windows.Point(r.Left + r.Width / 2, r.Top),   // N
        new System.Windows.Point(r.Right, r.Top),      // NE
        new System.Windows.Point(r.Right, r.Top + r.Height / 2), // E
        new System.Windows.Point(r.Right, r.Bottom),   // SE
        new System.Windows.Point(r.Left + r.Width / 2, r.Bottom), // S
        new System.Windows.Point(r.Left, r.Bottom),    // SW
        new System.Windows.Point(r.Left, r.Top + r.Height / 2),  // W
    };

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        System.Windows.Point pos = e.GetPosition(Overlay);
        _dragging = false;
        _dragMode = DragMode.None;

        if (_selected is { } sel)
        {
            DragMode mode = HitTestHandles(sel.Screen, pos);
            if (mode != DragMode.None)
            {
                BeginDrag(mode, sel);
                e.Handled = true;
                return;
            }

            if (sel.Screen.Contains(pos))
            {
                BeginDrag(DragMode.Move, sel);
                e.Handled = true;
                return;
            }
        }

        // Not on the current selection: pick the topmost object under the cursor.
        ObjBox? hit = null;
        for (int i = _boxes.Count - 1; i >= 0; i--)
        {
            if (_boxes[i].Screen.Contains(pos))
            {
                hit = _boxes[i];
                break;
            }
        }

        Select(hit);
        if (hit is not null)
        {
            BeginDrag(DragMode.Move, hit);
            e.Handled = true;
        }
    }

    private static DragMode HitTestHandles(Rect r, System.Windows.Point p)
    {
        System.Windows.Point[] pts = HandlePoints(r);
        DragMode[] modes = { DragMode.Nw, DragMode.N, DragMode.Ne, DragMode.E, DragMode.Se, DragMode.S, DragMode.Sw, DragMode.W };
        for (int i = 0; i < pts.Length; i++)
        {
            if (Math.Abs(p.X - pts[i].X) <= HandleHit && Math.Abs(p.Y - pts[i].Y) <= HandleHit)
            {
                return modes[i];
            }
        }

        return DragMode.None;
    }

    private void BeginDrag(DragMode mode, ObjBox box)
    {
        _dragMode = mode;
        _dragStartScreen = Mouse.GetPosition(Overlay);
        _dragStartBounds = box.Obj.Bounds;
        _dragging = true;
        _ = Overlay.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _selected is null || _dragMode == DragMode.None)
        {
            return;
        }

        System.Windows.Point cur = e.GetPosition(Overlay);
        double dxPdf = (cur.X - _dragStartScreen.X) / _scale;
        double dyPdf = (cur.Y - _dragStartScreen.Y) / _scale; // screen Y grows down; PDF Y grows up

        PdfRect nb = _dragMode == DragMode.Move
            ? new PdfRect(
                _dragStartBounds.X0 + dxPdf,
                _dragStartBounds.Y0 + dyPdf,
                _dragStartBounds.X1 + dxPdf,
                _dragStartBounds.Y1 + dyPdf)
            : ResizeBounds(_dragMode, _dragStartBounds, dxPdf, dyPdf);

        if (nb.X1 <= nb.X0 || nb.Y1 <= nb.Y0)
        {
            return;
        }

        Rect screen = PdfBoxToScreen(nb);
        _selected.Screen = screen;
        Canvas.SetLeft(_selected.Visual, screen.X);
        Canvas.SetTop(_selected.Visual, screen.Y);
        _selected.Visual.Width = screen.Width;
        _selected.Visual.Height = screen.Height;
        RedrawSelectionVisuals();
    }

    private static PdfRect ResizeBounds(DragMode mode, PdfRect orig, double dxPdf, double dyPdf)
    {
        double x0 = orig.X0, y0 = orig.Y0, x1 = orig.X1, y1 = orig.Y1;
        if (mode is DragMode.Nw or DragMode.W or DragMode.Sw)
        {
            x0 = orig.X0 + dxPdf;
        }

        if (mode is DragMode.Ne or DragMode.E or DragMode.Se)
        {
            x1 = orig.X1 + dxPdf;
        }

        if (mode is DragMode.Nw or DragMode.N or DragMode.Ne)
        {
            y1 = orig.Y1 + dyPdf;
        }

        if (mode is DragMode.Sw or DragMode.S or DragMode.Se)
        {
            y0 = orig.Y0 + dyPdf;
        }

        return new PdfRect(x0, y0, x1, y1);
    }

    private async void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _dragMode = DragMode.None;
        Overlay.ReleaseMouseCapture();

        if (_vm is null || _selected is null)
        {
            return;
        }

        PdfRect target = ScreenBoxToPdf(_selected.Screen);
        string id = _selected.Obj.Id;
        Select(_selected);
        try
        {
            await _vm.MoveResizeObjectAsync(id, target).ConfigureAwait(true);
            ReplaceButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Status($"Move/resize failed: {ex.Message}");
        }
        finally
        {
            Refresh();
        }
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _selected is null)
        {
            return;
        }

        var open = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
            Multiselect = false,
        };
        if (open.ShowDialog() != true)
        {
            return;
        }

        string ext = System.IO.Path.GetExtension(open.FileName).TrimStart('.').ToLowerInvariant();
        string format = ext switch
        {
            "jpg" or "jpeg" => "jpeg",
            _ => ext,
        };

        if (format is not ("png" or "jpeg" or "bmp" or "gif"))
        {
            MessageBox.Show($"Unsupported image format '.{ext}' for object replace.", "PageForge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string id = _selected.Obj.Id;
        var replacement = new PdfObjectReplacement(open.FileName, format);
        try
        {
            await _vm.ReplaceObjectAsync(id, replacement).ConfigureAwait(true);
            Status($"Replaced object {id}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Replace failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Refresh();
        }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.UndoEditAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Undo failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Refresh();
        }
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.RedoEditAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Redo failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Refresh();
        }
    }

    private void Hint(string text) => HintText.Text = text;

    private void Status(string text) => HintText.Text = text;
}
