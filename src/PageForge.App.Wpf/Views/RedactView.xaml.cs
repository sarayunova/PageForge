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
/// Interactive redaction surface (FR-SEC-02): the current page rendered at the
/// viewer DPI with a drag-to-draw overlay. Each dragged box becomes a /Redact
/// annotation through
/// <see cref="DocumentTabViewModel.MarkRedactionRegionAsync"/> (non-destructive);
/// "Apply redactions…" commits them through the FR-EDIT-05 command stack (an
/// undoable snapshot-based apply), after which the covered text is permanently
/// removed and the box is painted black. "Save redacted…" writes a new PDF with
/// the redactions baked in and opens it in a fresh tab.
/// </summary>
public partial class RedactView : UserControl
{
    private DocumentTabViewModel? _vm;
    private double _scale = 1.0;
    private double _pixelW;
    private double _pixelH;
    private bool _busy;
    private bool _justApplied;
    private Point _dragStart;
    private Rectangle? _preview;
    private readonly List<PdfRect> _regions = new();

    public RedactView()
    {
        InitializeComponent();
    }

    /// <summary>Binds this surface to a document tab and (re)loads the current page.</summary>
    public void SetContext(DocumentTabViewModel vm)
    {
        _vm = vm;
        _justApplied = false;
        _regions.Clear();
        Refresh();
    }

    /// <summary>Re-renders the current page and re-draws the marked regions. Call
    /// after navigation, zoom, or an apply/undo while this surface is active.</summary>
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

            var page = new PageImageViewModel(_vm.Core, pageIndex, _vm.RenderDpi);
            PageImage.Source = page.Bitmap;
            await page.RenderAsync().ConfigureAwait(true);

            Rebuild(_regions);
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

    private void Rebuild(IReadOnlyList<PdfRect> regions)
    {
        Overlay.Children.Clear();
        RegionsPanel.Children.Clear();

        foreach (PdfRect r in regions)
        {
            var box = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(40, 0xd0, 0x10, 0x10)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xcc, 0xd0, 0x10, 0x10)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Width = (r.X1 - r.X0) * _scale,
                Height = (r.Y1 - r.Y0) * _scale,
            };
            Canvas.SetLeft(box, r.X0 * _scale);
            Canvas.SetTop(box, _pixelH - r.Y1 * _scale);
            Overlay.Children.Add(box);
        }

        if (regions.Count == 0)
        {
            RegionsPanel.Children.Add(new TextBlock
            {
                Text = _justApplied
                    ? "Redactions applied and painted black. ↩ Undo restores the covered content; Save redacted… keeps it removed."
                    : "No regions on this page yet — drag a box over the content to redact.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (PdfRect r in regions)
            {
                RegionsPanel.Children.Add(new TextBlock
                {
                    Text = $"({r.X0:F0}, {r.Y0:F0}) → ({r.X1:F0}, {r.Y1:F0}) pt",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
                    Margin = new Thickness(8, 6, 8, 6),
                });
            }
        }

        ApplyButton.IsEnabled = regions.Count > 0;
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || _justApplied)
        {
            return;
        }

        _dragStart = e.GetPosition(Overlay);
        _preview = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xff, 0xd0, 0x10, 0x10)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0xd0, 0x10, 0x10)),
        };
        Canvas.SetLeft(_preview, _dragStart.X);
        Canvas.SetTop(_preview, _dragStart.Y);
        _preview.Width = 0;
        _preview.Height = 0;
        Overlay.Children.Add(_preview);
        Overlay.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_preview is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point end = e.GetPosition(Overlay);
        double left = Math.Min(_dragStart.X, end.X);
        double top = Math.Min(_dragStart.Y, end.Y);
        double right = Math.Max(_dragStart.X, end.X);
        double bottom = Math.Max(_dragStart.Y, end.Y);
        Canvas.SetLeft(_preview, left);
        Canvas.SetTop(_preview, top);
        _preview.Width = right - left;
        _preview.Height = bottom - top;
    }

    private async void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_preview is null)
        {
            return;
        }

        Point end = e.GetPosition(Overlay);
        Overlay.ReleaseMouseCapture();
        Overlay.Children.Remove(_preview);
        _preview = null;

        double w = Math.Abs(end.X - _dragStart.X);
        double h = Math.Abs(end.Y - _dragStart.Y);
        if (w < 3 || h < 3)
        {
            return;
        }

        // Map the screen-space box to PDF points (bottom-left origin).
        double leftPt = Math.Min(_dragStart.X, end.X) / _scale;
        double rightPt = Math.Max(_dragStart.X, end.X) / _scale;
        double topPt = (_pixelH - Math.Min(_dragStart.Y, end.Y)) / _scale;
        double bottomPt = (_pixelH - Math.Max(_dragStart.Y, end.Y)) / _scale;
        var rect = new PdfRect(leftPt, bottomPt, rightPt, topPt);

        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.MarkRedactionRegionAsync(rect).ConfigureAwait(true);
            _regions.Add(rect);
            _justApplied = false;
            Rebuild(_regions);
            Hint($"Marked a region; drag more boxes or apply. Region: ({leftPt:F0}, {bottomPt:F0}) → ({rightPt:F0}, {topPt:F0}) pt.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not mark the region:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _regions.Count == 0)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Apply {_regions.Count} redaction region(s)? The covered text (if any) is permanently deleted and the box is painted black. You can undo this once by using ↩ Undo.",
            "PageForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            int applied = await _vm.ApplyRedactionsCurrentPageAsync().ConfigureAwait(true);
            _regions.Clear();
            _justApplied = true;
            Hint($"Removed {applied} redaction region(s) — the covered content is gone.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Apply failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
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
            _justApplied = false;
            Hint("Undid the last redaction apply — the covered content is back (re-apply to remove again).");
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

    private async void SaveRedacted_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            FileName = "Redacted.pdf",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _vm.SaveRedactedAsync(dialog.FileName).ConfigureAwait(true);
            Hint($"Saved redacted copy to {System.IO.Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Hint(string text) => HintText.Text = text;
}