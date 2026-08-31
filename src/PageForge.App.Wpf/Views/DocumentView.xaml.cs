// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PageForge.App.Wpf.ViewModels;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Per-document viewer: pages (single/continuous), thumbnails, outline, and
/// full-text search. Deliberately thin — all document/engine logic lives in
/// <see cref="DocumentTabViewModel"/> (and Core); this code-behind only wires
/// the toolbar events to the VM and refreshes bindings.
/// </summary>
public partial class DocumentView : UserControl
{
    private DocumentTabViewModel? _vm;
    private int _dragStartIndex = -1;
    private System.Windows.Point _mouseDownPoint;
    private int _lastAnnotatedPage = -1;

    public DocumentView()
    {
        InitializeComponent();

        CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Undo,
            (_, _) => EditUndo_Click(this, new RoutedEventArgs()),
            (_, e) => e.CanExecute = _vm?.CanUndo == true));
        CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Redo,
            (_, _) => EditRedo_Click(this, new RoutedEventArgs()),
            (_, e) => e.CanExecute = _vm?.CanRedo == true));
    }

    /// <summary>Raised when the organizer writes a new PDF the host should open
    /// (MainWindow subscribes and opens it in a fresh tab).</summary>
    public event Action<string>? OpenDocumentRequested;

    public void SetTab(DocumentTabViewModel vm)
    {
        _vm = vm;

        ThumbList.ItemsSource = vm.IsReorderMode ? vm.ReorderItems : vm.Pages;
        ReorderToggle.IsChecked = vm.IsReorderMode;
        OutlineList.ItemsSource = vm.Outline;
        SearchList.ItemsSource = vm.SearchHits;
        AnnotationList.ItemsSource = vm.Annotations;
        ContinuousToggle.IsChecked = vm.IsContinuous;
        StatusText.Text = vm.Status;

        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null)
        {
            return;
        }

        PageList.ItemsSource = _vm.VisiblePages;
        PageIndicatorText.Text = _vm.PageIndicator;
        ZoomText.Text = $"{_vm.Zoom * 100.0:0}%";
        StatusText.Text = _vm.Status;

        _vm.ApplyZoomToPages();

        if (ObjectView.Visibility == Visibility.Visible)
        {
            ObjectView.Refresh();
        }

        RefreshAnnotationsIfNeeded();
    }

    private void RefreshAnnotationsIfNeeded()
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.Core.CurrentPage == _lastAnnotatedPage)
        {
            return;
        }

        _lastAnnotatedPage = _vm.Core.CurrentPage;
        _ = _vm.RefreshAnnotationsAsync();
    }

    private void RefreshAndScroll()
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.IsContinuous)
        {
            if (PageList.Items.IsNullOrEmpty() || !ReferenceEquals(PageList.Items[0], _vm.Pages[0]))
            {
                PageList.ItemsSource = _vm.Pages;
            }

            if (_vm.CurrentPageSlot is { } current)
            {
                PageList.ScrollIntoView(current);
            }
        }
        else
        {
            PageList.ItemsSource = _vm.VisiblePages;
        }

        PageIndicatorText.Text = _vm.PageIndicator;
        ZoomText.Text = $"{_vm.Zoom * 100.0:0}%";
        StatusText.Text = _vm.Status;

        if (ObjectView.Visibility == Visibility.Visible)
        {
            ObjectView.Refresh();
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _vm?.PreviousPage();
        RefreshAndScroll();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _vm?.NextPage();
        RefreshAndScroll();
    }

    private void ContinuousToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.IsContinuous = ContinuousToggle.IsChecked == true;
        }

        RefreshAndScroll();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ZoomIn();
        Refresh();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ZoomOut();
        Refresh();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ZoomReset();
        Refresh();
    }

    private void RotateCW_Click(object sender, RoutedEventArgs e)
    {
        _vm?.RotateClockwise();
        Refresh();
    }

    private void RotateCCW_Click(object sender, RoutedEventArgs e)
    {
        _vm?.RotateCounterClockwise();
        Refresh();
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            _ = RunSearchAsync();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _ = RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.SearchQuery = SearchBox.Text;
        await _vm.RunSearchAsync();
        StatusText.Text = _vm.Status;
    }

    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm?.IsReorderMode == true)
        {
            return;
        }

        if (ThumbList.SelectedItem is PageSlotViewModel slot)
        {
            _vm?.GoToPage(slot.PageIndex);
            ThumbList.SelectedIndex = -1;
            RefreshAndScroll();
        }
    }

    private void ReorderToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (ReorderToggle.IsChecked == true)
        {
            _vm.EnterReorderMode();
            ThumbList.ItemsSource = _vm.ReorderItems;
            StatusText.Text = "Reorder mode: drag thumbnails to arrange, then Save order…";
        }
        else
        {
            _vm.ExitReorderMode();
            ThumbList.ItemsSource = _vm.Pages;
            StatusText.Text = _vm.Status;
        }

        _dragStartIndex = -1;
    }

    private void ThumbList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPoint = e.GetPosition(ThumbList);
        _dragStartIndex = _vm?.IsReorderMode == true ? GetIndexUnderPoint(_mouseDownPoint) : -1;
    }

    private void ThumbList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.IsReorderMode != true || _dragStartIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = e.GetPosition(ThumbList);
        double dx = Math.Abs(current.X - _mouseDownPoint.X);
        double dy = Math.Abs(current.Y - _mouseDownPoint.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance && dy < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ThumbList.SelectedIndex = _dragStartIndex;
        try
        {
            DragDrop.DoDragDrop(ThumbList, ThumbList.SelectedItem, DragDropEffects.Move);
        }
        finally
        {
            _dragStartIndex = -1;
        }
    }

    private void ThumbList_Drop(object sender, DragEventArgs e)
    {
        if (_vm?.IsReorderMode != true || _dragStartIndex < 0)
        {
            _dragStartIndex = -1;
            return;
        }

        int target = GetIndexUnderPoint(e.GetPosition(ThumbList));
        if (target >= 0)
        {
            _vm.MoveReorderItem(_dragStartIndex, target);
        }

        _dragStartIndex = -1;
    }

    private int GetIndexUnderPoint(Point point)
    {
        DependencyObject? element = ThumbList.InputHitTest(point) as DependencyObject;
        while (element is not null and not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element is ListBoxItem lbi
            ? ThumbList.ItemContainerGenerator.IndexFromContainer(lbi)
            : -1;
    }

    private async void SaveOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? path = AskSavePath("Reordered.pdf");
        if (path is null)
        {
            return;
        }

        try
        {
            int[] order = _vm.BuildOrder();
            await _vm.ReorderAsync(order, path);
            StatusText.Text = _vm.Status;
            OpenDocumentRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Reorder failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is OutlineEntryViewModel entry)
        {
            _vm?.NavigateToOutline(entry);
            OutlineList.SelectedIndex = -1;
            RefreshAndScroll();
        }
    }

    private void SearchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchList.SelectedItem is SearchResultViewModel hit)
        {
            _vm?.NavigateToSearchHit(hit);
            SearchList.SelectedIndex = -1;
            RefreshAndScroll();
        }
    }

    private async void AnnotateHighlight_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.AddHighlightAsync();
            StatusText.Text = _vm.Status;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Add highlight failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AnnotateText_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.AddTextNoteAsync();
            StatusText.Text = _vm.Status;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Add note failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AnnotateInk_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.AddInkAsync();
            StatusText.Text = _vm.Status;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Add ink failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AnnotateFlatten_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? path = AskSavePath("Flattened.pdf");
        if (path is null)
        {
            return;
        }

        try
        {
            var types = new HashSet<AnnotationType> { AnnotationType.Highlight };
            await _vm.FlattenExportAsync(types, path);
            StatusText.Text = _vm.Status;
            OpenDocumentRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Flatten failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (EditModeToggle?.IsChecked == true && ObjectModeToggle?.IsChecked == true)
        {
            ObjectModeToggle.IsChecked = false;
        }

        StatusText.Text = (EditModeToggle?.IsChecked == true)
            ? "Edit mode: click a word on the page to replace its text"
            : _vm?.Status ?? string.Empty;
    }

    private void ObjectModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ObjectModeToggle?.IsChecked == true && EditModeToggle?.IsChecked == true)
        {
            EditModeToggle.IsChecked = false;
        }

        bool on = ObjectModeToggle?.IsChecked == true;
        if (on && _vm is not null)
        {
            ObjectView.SetContext(_vm);
        }

        ObjectView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PageList.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = on
            ? "Object edit mode: select, move, resize, or replace image/vector objects"
            : _vm?.Status ?? string.Empty;
    }

    private async void EditUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.UndoEditAsync();
            StatusText.Text = _vm.Status;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Undo failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EditRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.RedoEditAsync();
            StatusText.Text = _vm.Status;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Redo failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Edit-mode click handler on a page image: maps the click to PDF
    /// points, hit-tests the run, prompts for replacement text, and commits it
    /// through the FR-EDIT-02/03 gates and the undo/redo command stack.</summary>
    private async void PageImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || EditModeToggle?.IsChecked != true)
        {
            return;
        }

        if (sender is not Image image || image.DataContext is not PageSlotViewModel slot)
        {
            return;
        }

        try
        {
            System.Windows.Point pos = e.GetPosition(image);
            double dpi = slot.Image.RenderDpi;
            double xPt = pos.X * 72.0 / dpi;
            double yPt = (image.ActualHeight - pos.Y) * 72.0 / dpi;

            PdfTextRun? run = await _vm.HitTestAsync(xPt, yPt).ConfigureAwait(true);
            if (run is null)
            {
                StatusText.Text = "No editable text at that point (edit mode).";
                return;
            }

            string? newText = AskEditText(run.Text);
            if (newText is null)
            {
                return;
            }

            TextEditOutcome outcome = await _vm.EditTextRunAsync(run.Index, newText, allowCollision: false).ConfigureAwait(true);
            if (outcome.Succeeded)
            {
                StatusText.Text = _vm.Status;
                return;
            }

            // FR-EDIT-02 collision confirmation gateway: surface the warning and
            // require explicit confirmation before committing.
            if (outcome.Kind == TextEditOutcomeKind.NeedsConfirmation)
            {
                var confirm = MessageBox.Show(
                    $"{outcome.Message}\n\nApply it anyway?",
                    "Overflow / collision",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm == MessageBoxResult.Yes)
                {
                    TextEditOutcome forced = await _vm.EditTextRunAsync(run.Index, newText, allowCollision: true).ConfigureAwait(true);
                    StatusText.Text = forced.Succeeded ? _vm.Status : forced.Message ?? "Edit not applied.";
                }
                else
                {
                    StatusText.Text = "Edit cancelled.";
                }

                return;
            }

            // FR-EDIT-03 font fidelity: the text can't be painted faithfully.
            StatusText.Text = outcome.Message ?? "Edit not applied.";
            MessageBox.Show(outcome.Message ?? "The new text cannot be rendered by the run's font.", "Font fidelity", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Edit failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Opens a small prompt for replacement text. Returns the text, or
    /// null when the user cancels.</summary>
    private static string? AskEditText(string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Replacement text:", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        string? result = null;
        var dlg = new Window
        {
            Title = "Edit text",
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize,
        };
        dlg.PreviewKeyDown += (_, arg) =>
        {
            if (arg.Key == Key.Escape)
            {
                dlg.DialogResult = false;
            }
        };

        ok.Click += (_, _) => { result = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text; dlg.DialogResult = true; };
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        dlg.ShowDialog();
        return result;
    }

    private async void OrganizeRotatePage_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? path = AskSavePath("Rotated.pdf");
        if (path is null)
        {
            return;
        }

        try
        {
            await _vm.RotateCurrentPageAsync(1, path);
            StatusText.Text = _vm.Status;
            OpenDocumentRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Rotate failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OrganizeDeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? path = AskSavePath("Deleted.pdf");
        if (path is null)
        {
            return;
        }

        try
        {
            await _vm.DeleteCurrentPageAsync(path);
            StatusText.Text = _vm.Status;
            OpenDocumentRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Delete failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OrganizeExtractPage_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? path = AskSavePath("Extracted.pdf");
        if (path is null)
        {
            return;
        }

        try
        {
            await _vm.ExtractCurrentPageAsync(path);
            StatusText.Text = _vm.Status;
            OpenDocumentRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Extract failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OrganizeInsert_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var open = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            Multiselect = false,
        };
        if (open.ShowDialog() != true)
        {
            return;
        }

        string? outPath = AskSavePath("Inserted.pdf");
        if (outPath is null)
        {
            return;
        }

        try
        {
            int otherCount = await CountPagesAsync(open.FileName);
            int insertAt = _vm.Core.CurrentPage;
            await _vm.InsertFileAtAsync(open.FileName, otherCount, insertAt, outPath);
            StatusText.Text = _vm.Status;
            OpenDocumentRequested?.Invoke(outPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Insert failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? AskSavePath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            FileName = suggestedName,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static async Task<int> CountPagesAsync(string path)
    {
        await using var engine = MuPdfEngine.Create();
        PageForge.Core.Pdf.PdfDocumentInfo info = await engine.OpenAsync(path);
        return info.PageCount;
    }
}

internal static class ListExtensions
{
    public static bool IsNullOrEmpty(this System.Collections.IList? list)
        => list is null || list.Count == 0;
}
