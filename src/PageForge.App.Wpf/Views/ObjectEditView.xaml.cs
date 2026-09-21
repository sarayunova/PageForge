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
/// Interactive object-edit surface (FR-EDIT-04 follow-on): the current page
/// rendered at the viewer DPI with a selection overlay over every image/vector
/// object. Click selects, drag moves, dragging a corner/edge handle resizes, and
/// the Replace… button paints a picked PNG/JPEG inside the selected object's box.
/// Commits go through the Core command stack via <see cref="DocumentTabViewModel"/>
/// so undo/redo stays on the shared FR-EDIT-05 history.
/// </summary>
public partial class ObjectEditView : UserControl
{
    // WCAG 1.4.11: handles render at >=12px and every selection stroke is >=3:1 on
    // white (selected #2b8cff ≈3.3:1, unselected #1f74c6 ≈4.8:1).
    private const double HandleSize = 12;
    private const double HandleHit = 15;

    private DocumentTabViewModel? _vm;
    private PageImageViewModel? _page;
    private double _scale = 1.0;
    private double _pixelW;
    private double _pixelH;
    private ObjectBoxViewModel? _selected;
    private bool _busy;

    /// <summary>The objects on the page, bound by the overlay. Replaces the
    /// parallel list of (object, screen rect, Rectangle) triples the view used to
    /// keep, where the rect and the Rectangle were updated by hand at four call
    /// sites and could disagree with each other.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ObjectBoxViewModel> Boxes { get; } = new();

    /// <summary>Which part of the selection is being dragged this gesture.</summary>
    private enum DragMode { None, Move, Nw, N, Ne, E, Se, S, Sw, W }

    private DragMode _dragMode = DragMode.None;
    private System.Windows.Point _dragStartScreen;
    private PdfRect _dragStartBounds;
    private bool _dragging;

    /// <summary>Keyboard-driven bounds while no Enter has committed them yet.</summary>
    private PdfRect? _pendingBounds;

    public ObjectEditView()
    {
        InitializeComponent();
        Overlay.MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
        Overlay.MouseMove += Overlay_MouseMove;
        Overlay.MouseLeftButtonUp += Overlay_MouseLeftButtonUp;
        PreviewKeyDown += ObjectEditView_PreviewKeyDown;
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
            AutomationProperties.SetName(PageImage, $"Object edit page {pageIndex + 1}");

            PdfPageRegion region = _vm.Core.PageSizes[Math.Min(pageIndex, _vm.Core.PageCount - 1)];
            _pixelW = region.WidthPt * _scale;
            _pixelH = region.HeightPt * _scale;

            PageHost.Width = _pixelW;
            PageHost.Height = _pixelH;
            Overlay.Width = _pixelW;
            Overlay.Height = _pixelH;

            // Assigned AFTER the render. It used to be assigned before, when Bitmap
            // is still null, and nothing reassigned it - so this surface showed a
            // blank sheet and objects were selected and dragged over white. The
            // redact and form surfaces had the identical bug; all three were blank
            // for the same reason, and none of them threw or logged.
            _page = new PageImageViewModel(_vm.Core, pageIndex, _vm.RenderDpi);
            await _page.RenderAsync().ConfigureAwait(true);
            PageImage.Source = _page.Bitmap;

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
        // What was selected has to survive the rebuild. Refresh() runs on any
        // view-model state change - a status line, a zoom, an autosave tick -
        // and dropping the selection each time meant an object stayed selected
        // only until something unrelated happened, which for a click was about
        // two hundred milliseconds. Replace… would then be disabled again with
        // the box still highlighted on screen.
        string? previouslySelected = _selected?.Id;

        Overlay.Children.Clear();
        Boxes.Clear();
        _selected = null;
        ReplaceButton.IsEnabled = false;
        Hint(objects.Count == 0
            ? "No image/vector objects on this page to edit."
            : "Click an object to select; drag to move; drag a handle to resize.");

        foreach (PdfPageObject obj in objects)
        {
            Boxes.Add(new ObjectBoxViewModel(obj, _vm!.RenderDpi, _pixelH));
        }

        if (previouslySelected is not null)
        {
            ObjectBoxViewModel? again = Boxes.FirstOrDefault(
                b => string.Equals(b.Id, previouslySelected, StringComparison.Ordinal));
            if (again is not null)
            {
                // Restored without Select(), which takes focus: a refresh can
                // happen while the user is somewhere else entirely, and pulling
                // focus back to the page under them would be worse than the
                // lost selection this fixes.
                _selected = again;
                again.IsSelected = true;
                ReplaceButton.IsEnabled = true;
                RedrawHandles();
            }
        }
    }

    /// <summary>Screen rectangle (overlay coordinates) of a box, for hit-testing
    /// and handle placement.</summary>
    private static Rect ScreenRect(ObjectBoxViewModel box) =>
        new(box.Left, box.Top, box.Width, box.Height);

    // The PDF<->screen conversions this file used to carry are gone: the forward
    // one is PageBoxGeometry, reached through ObjectBoxViewModel and shared with
    // the redaction overlay, and the reverse one had no callers left once drags
    // began working in PDF points throughout. A local copy of the Y flip is
    // exactly what let the form overlay be mirrored for as long as it was.

    private void Select(ObjectBoxViewModel? box)
    {
        if (!ReferenceEquals(box, _selected))
        {
            _pendingBounds = null;
            if (_selected is not null)
            {
                _selected.IsSelected = false;
            }

            _selected = box;
            ReplaceButton.IsEnabled = box is not null;
            if (box is not null)
            {
                box.IsSelected = true;
                Overlay.Focus();
            }
        }

        RedrawHandles();
    }

    /// <summary>
    /// Rebuilds the eight selection handles over the current selection.
    ///
    /// It clears the Canvas first, which the old version did not: it appended
    /// eight fresh Rectangles on every call and removed none, and it is called on
    /// every mouse-move of a drag. A single drag across the page therefore left
    /// hundreds of stale handles behind at the positions the pointer had passed
    /// through, and deselecting left the last eight on screen for good.
    ///
    /// Only the handles live in this Canvas now - the boxes themselves are bound -
    /// so clearing it is exactly the right scope.
    /// </summary>
    private void RedrawHandles()
    {
        Overlay.Children.Clear();

        if (_selected is null)
        {
            return;
        }

        foreach (System.Windows.Point p in HandlePoints(ScreenRect(_selected)))
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromArgb(0xff, 0x2b, 0x8c, 0xff)),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(handle, p.X - (HandleSize / 2));
            Canvas.SetTop(handle, p.Y - (HandleSize / 2));
            Overlay.Children.Add(handle);
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
            DragMode mode = HitTestHandles(ScreenRect(sel), pos);
            if (mode != DragMode.None)
            {
                BeginDrag(mode, sel);
                e.Handled = true;
                return;
            }

            if (sel.Contains(pos.X, pos.Y))
            {
                BeginDrag(DragMode.Move, sel);
                e.Handled = true;
                return;
            }
        }

        // Not on the current selection: pick the topmost object under the cursor.
        ObjectBoxViewModel? hit = null;
        for (int i = Boxes.Count - 1; i >= 0; i--)
        {
            if (Boxes[i].Contains(pos.X, pos.Y))
            {
                hit = Boxes[i];
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

    private void BeginDrag(DragMode mode, ObjectBoxViewModel box)
    {
        _dragMode = mode;
        _dragStartScreen = Mouse.GetPosition(Overlay);

        // The bounds the gesture starts from are the ones on screen now, not the
        // object's last committed ones: after an uncommitted keyboard nudge those
        // differ, and starting from the committed pair would snap the box back
        // before the drag began.
        _dragStartBounds = box.Bounds;
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

        // Negated, because screen Y grows down and PDF Y grows up. The old line
        // carried exactly that sentence as a comment and then did not negate, so
        // every drag moved the object the opposite way vertically, and the north
        // and south resize handles each grew the box where they should shrink it.
        // The keyboard path next door has always had the sign right (Up is +step),
        // which is what makes the disagreement findable by reading.
        double dyPdf = -(cur.Y - _dragStartScreen.Y) / _scale;

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

        // One assignment: the view model converts and the binding redraws. This
        // used to be five statements repeated at two call sites, which is how the
        // screen rect and the Rectangle could end up describing different boxes.
        _selected.Bounds = nb;
        RedrawHandles();
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

        // A click is not an edit. Selecting an object is a press and a release
        // with nothing in between, and committing that wrote a no-op move into
        // the document: it went on the undo stack, marked the document dirty
        // and was copied aside by the autosave buffer. The refresh that follows
        // a commit then rebuilt the overlay and dropped the selection, so the
        // click the user made to select something left them with nothing
        // selected and an edited document.
        if (_selected.Bounds == _dragStartBounds)
        {
            return;
        }

        await CommitMoveResizeAsync(_selected.Id, _selected.Bounds);
    }

    /// <summary>Commits the selection's current bounds to the engine through the
    /// FR-EDIT-05 command stack (shared by the mouse-drag and keyboard paths).
    ///
    /// Takes PDF points rather than a screen rectangle: the caller already holds
    /// the bounds in the units the engine wants, and converting them to screen
    /// coordinates only to convert them straight back added a rounding trip and a
    /// second copy of the Y flip for nothing.</summary>
    private async Task CommitMoveResizeAsync(string id, PdfRect target)
    {
        if (_vm is null || _selected is null)
        {
            return;
        }

        try
        {
            await _vm.MoveResizeObjectAsync(id, target).ConfigureAwait(true);
            ReplaceButton.IsEnabled = true;
            Status("Moved/resized the selected object (undo available).");
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

    /// <summary>Keyboard path for object selection/move/resize (WCAG 2.1.1):
    /// Tab cycles the objects, arrows nudge the selection (Shift ⇒ 8 pt steps),
    /// Ctrl+arrows resize from the bottom-right corner, Enter commits, Esc cancels.</summary>
    private void ObjectEditView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || _busy)
        {
            return;
        }

        if (!Overlay.IsKeyboardFocusWithin)
        {
            return;
        }

        if (e.Key == Key.Tab)
        {
            if (Boxes.Count == 0)
            {
                return;
            }

            bool forward = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
            int index = _selected is null
                ? (forward ? Boxes.Count - 1 : 0)
                : (Boxes.IndexOf(_selected) + (forward ? 1 : -1) + Boxes.Count) % Boxes.Count;
            Select(Boxes[index]);
            e.Handled = true;
            return;
        }

        if (_selected is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelKeyboardEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitKeyboardEdit();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return;
        }

        bool resize = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 8.0 : 1.0;
        double dx = e.Key is Key.Left ? -step : e.Key is Key.Right ? step : 0;
        double dy = e.Key is Key.Up ? step : e.Key is Key.Down ? -step : 0;
        ApplyKeyboardNudge(resize, dx, dy);
        e.Handled = true;
    }

    private void ApplyKeyboardNudge(bool resize, double dxPdf, double dyPdf)
    {
        PdfRect source = _pendingBounds ?? _selected!.Bounds;
        PdfRect next = resize
            ? new PdfRect(source.X0, source.Y0, source.X1 + dxPdf, source.Y1 + dyPdf)
            : new PdfRect(source.X0 + dxPdf, source.Y0 + dyPdf, source.X1 + dxPdf, source.Y1 + dyPdf);

        if (next.X1 <= next.X0 || next.Y1 <= next.Y0)
        {
            return;
        }

        _pendingBounds = next;
        ApplyBoundsToScreen(next);
    }

    private void ApplyBoundsToScreen(PdfRect pdf)
    {
        _selected!.Bounds = pdf;
        RedrawHandles();
    }

    private async void CommitKeyboardEdit()
    {
        if (_selected is null || _vm is null || _pendingBounds is null)
        {
            return;
        }

        _pendingBounds = null;
        await CommitMoveResizeAsync(_selected.Id, _selected.Bounds);
    }

    private void CancelKeyboardEdit()
    {
        _pendingBounds = null;
        if (_selected is not null)
        {
            ApplyBoundsToScreen(_selected.Object.Bounds);
        }

        Select(null);
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

        string id = _selected.Id;
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

    /// <summary>Moves keyboard focus onto the interactive surface so the keyboard
    /// selection/move/resize path is reachable with one action (WCAG 2.4.1).</summary>
    public void FocusSurface() => Keyboard.Focus(Overlay);
}
