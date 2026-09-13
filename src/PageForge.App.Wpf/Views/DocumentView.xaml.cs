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

    /// <summary>Editable words of the current page for the keyboard word-selection
    /// path (WCAG 2.1.1/2.4.3); valid only while edit mode is on.</summary>
    private IReadOnlyList<PdfTextRun> _wordRuns = Array.Empty<PdfTextRun>();
    private int _wordIndex = -1;
    private PageSlotViewModel? _wordPage;

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
        DataContext = vm;

        ThumbList.ItemsSource = vm.IsReorderMode ? vm.ReorderItems : vm.Pages;
        ReorderToggle.IsChecked = vm.IsReorderMode;
        OutlineTreeView.ItemsSource = vm.OutlineTree;
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
        RenderRealizedPages();

        if (ObjectView.Visibility == Visibility.Visible)
        {
            ObjectView.Refresh();
        }

        if (FormView.Visibility == Visibility.Visible)
        {
            FormView.Refresh();
        }

        if (RedactViewHost.Visibility == Visibility.Visible)
        {
            RedactViewHost.Refresh();
        }

        ClearWordCycle();

        RefreshAnnotationsIfNeeded();
    }

    /// <summary>
    /// Re-renders the page slots that currently have a realized container. A zoom
    /// change only retargets the slots' DPI; containers that are already realized
    /// never raise Loaded again, so without this the pages would keep showing their
    /// stale bitmap (and, before the DPI cache tracked staleness, went blank) until
    /// virtualization happened to recycle them.
    /// </summary>
    private void RenderRealizedPages()
    {
        if (_vm is null || PageList.Items.Count == 0)
        {
            return;
        }

        var realized = new List<PageSlotViewModel>();
        foreach (object item in PageList.Items)
        {
            if (item is PageSlotViewModel slot
                && PageList.ItemContainerGenerator.ContainerFromItem(slot) is not null)
            {
                realized.Add(slot);
            }
        }

        if (realized.Count == 0)
        {
            // Nothing on screen yet (first layout pass): the RenderOnLoad behavior
            // will render each slot as its container is realized.
            return;
        }

        _ = _vm.RenderSlotsAsync(realized);
    }

    /// <summary>
    /// Shows the selected tool group's commands and hides the rest.
    ///
    /// This governs only what is on screen. The editing modes themselves are still
    /// owned by the toggles inside each group (EditModeToggle, ObjectModeToggle,
    /// FormModeToggle, RedactModeToggle) and their existing handlers, so selecting
    /// a group never silently enters or leaves an editing mode — switching away
    /// from Redact while redaction is armed would otherwise strand the user in a
    /// mode whose control they can no longer see.
    /// </summary>
    private void ToolGroupTab_Checked(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent for the default-checked tab, before the
        // named group panels exist.
        if (OrganizeGroup is null)
        {
            return;
        }

        (RadioButton Tab, UIElement Group)[] groups =
        {
            (OrganizeModeTab, OrganizeGroup),
            (AnnotateModeTab, AnnotateGroup),
            (EditModeTab, EditGroup),
            (FormsModeTab, FormsGroup),
            (RedactModeTab, RedactGroup),
            (DocumentModeTab, DocumentGroup),
        };

        foreach ((RadioButton tab, UIElement group) in groups)
        {
            group.Visibility = tab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
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

        if (FormView.Visibility == Visibility.Visible)
        {
            FormView.Refresh();
        }

        if (RedactViewHost.Visibility == Visibility.Visible)
        {
            RedactViewHost.Refresh();
        }

        ClearWordCycle();
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

    /// <summary>Retries the render behind a visible page error panel. The button
    /// lives in the page template, so the failed page is the one whose DataContext
    /// the button carries.</summary>
    private async void PageRetryRender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageSlotViewModel slot })
        {
            await slot.Image.RetryAsync();
        }
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
            StatusText.Text = "Reorder mode: drag thumbnails, or Ctrl+Up/Ctrl+Down to move the selected page, then Save order…";
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

    private void OutlineTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineTreeNodeViewModel node)
        {
            if (node.PageNumber > 0)
            {
                _vm?.GoToPage(node.PageNumber - 1);
            }

            RefreshAndScroll();
            if (OutlineTreeView.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem item)
            {
                item.IsSelected = false;
            }
        }
    }

    /// <summary>Keyboard path for page reorder (WCAG 2.1.1): in reorder mode,
    /// Ctrl+Up/Ctrl+Down moves the selected thumbnail within the staging list.</summary>
    private void ThumbList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm?.IsReorderMode != true)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        int index = ThumbList.SelectedIndex;
        int target = e.Key == Key.Up ? index - 1 : index + 1;
        if (index < 0 || target < 0 || target >= ThumbList.Items.Count)
        {
            return;
        }

        _vm.MoveReorderItem(index, target);
        ThumbList.SelectedIndex = target;
        StatusText.Text = "Reorder mode: drag thumbnails, or Ctrl+Up/Ctrl+Down to move the selected page, then Save order…";
        e.Handled = true;
    }

    /// <summary>WCAG 2.4.1 bypass: jumps keyboard focus over the toolbar straight
    /// to the visible document surface (page list, or the object/form/redact
    /// overlay when one of those modes is active).</summary>
    private void SkipToPage_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectView.Visibility == Visibility.Visible)
        {
            ObjectView.FocusSurface();
            return;
        }

        if (FormView.Visibility == Visibility.Visible)
        {
            FormView.FocusSurface();
            return;
        }

        if (RedactViewHost.Visibility == Visibility.Visible)
        {
            RedactViewHost.FocusSurface();
            return;
        }

        FocusCurrentPageItem();
    }

    private void FocusCurrentPageItem()
    {
        if (_vm is null)
        {
            return;
        }

        PageSlotViewModel? slot = _vm.CurrentPageSlot
            ?? (_vm.Pages.Count > 0 ? _vm.Pages[0] : null);
        if (slot is null)
        {
            return;
        }

        PageList.ScrollIntoView(slot);
        _ = FocusPageItemWhenReadyAsync(slot);
    }

    private async Task FocusPageItemWhenReadyAsync(PageSlotViewModel slot)
    {
        await System.Windows.Application.Current.Dispatcher
            .InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        if (PageList.ItemContainerGenerator.ContainerFromItem(slot) is ListBoxItem item)
        {
            Keyboard.Focus(item);
        }
        else
        {
            PageList.Focus();
        }
    }

    /// <summary>Keyboard navigation of the page surface (WCAG 2.1.1/2.4.3): the
    /// main page list items are focusable, so Tab reaches the document and
    /// Up/Down/Left/Right/PageUp/PageDown flip pages. While edit mode is on,
    /// Tab/Shift+Tab instead cycles the page's editable words (Enter edits, Esc
    /// cancels). Focus-scoped so the toolbar's own Tab order is untouched.</summary>
    private void PageList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled || !PageList.IsKeyboardFocusWithin)
        {
            return;
        }

        if (EditModeToggle?.IsChecked == true && HandleWordCycleKey(e))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                _vm.PreviousPage();
                RefreshAndScroll();
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
                _vm.NextPage();
                RefreshAndScroll();
                e.Handled = true;
                break;
        }
    }

    private bool HandleWordCycleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            _ = HandleWordTabAsync();
            e.Handled = true;
            return true;
        }

        if (_wordIndex < 0)
        {
            return false;
        }

        if (e.Key == Key.Enter)
        {
            CommitSelectedWordAsync();
            e.Handled = true;
            return true;
        }

        if (e.Key == Key.Escape)
        {
            ClearWordCycle();
            e.Handled = true;
            return true;
        }

        return false;
    }

    private async Task HandleWordTabAsync()
    {
        if (_vm is null)
        {
            return;
        }

        if (_wordRuns.Count == 0 || _wordPage is null)
        {
            await LoadWordsAsync();
            return;
        }

        bool forward = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        int next = _wordIndex < 0
            ? (forward ? 0 : _wordRuns.Count - 1)
            : (_wordIndex + (forward ? 1 : -1) + _wordRuns.Count) % _wordRuns.Count;
        await PresentWordAsync(next);
    }

    /// <summary>Loads the current page's editable runs for the keyboard
    /// word-selection path and puts the highlight on the first word.</summary>
    private async Task LoadWordsAsync()
    {
        if (_vm is null)
        {
            return;
        }

        var slot = _vm.CurrentPageSlot;
        if (slot is null)
        {
            _wordRuns = Array.Empty<PdfTextRun>();
            _wordIndex = -1;
            _wordPage = null;
            return;
        }

        try
        {
            IReadOnlyList<PdfTextRun> runs = await _vm.ListPageRunsAsync();
            _wordRuns = runs;
            _wordPage = slot;
            _wordIndex = -1;
            if (_wordRuns.Count == 0)
            {
                StatusText.Text = "Edit mode: this page has no editable words.";
                return;
            }

            await PresentWordAsync(0);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Edit mode: could not load words ({ex.Message}).";
        }
    }

    private async Task PresentWordAsync(int index)
    {
        if (_vm is null || _wordPage is null || index < 0 || index >= _wordRuns.Count)
        {
            return;
        }

        _wordIndex = index;
        PdfTextRun run = _wordRuns[index];
        PageSlotViewModel slot = _wordPage;

        PageList.ScrollIntoView(slot);
        await System.Windows.Application.Current.Dispatcher
            .InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        if (PageList.ItemContainerGenerator.ContainerFromItem(slot) is ListBoxItem lvi)
        {
            Border? highlight = FindVisualChildByName<Border>(lvi, "WordHighlight");
            if (highlight is not null && slot.Image.Bitmap is { PixelHeight: > 0 } bitmap)
            {
                double scale = _vm!.RenderDpi / 72.0;
                double pixelH = bitmap.PixelHeight;
                highlight.Visibility = Visibility.Visible;
                highlight.Margin = new Thickness(run.X0 * scale, pixelH - run.Y1 * scale, 0, 0);
                highlight.Width = Math.Max(2.0, (run.X1 - run.X0) * scale);
                highlight.Height = Math.Max(2.0, (run.Y1 - run.Y0) * scale);
            }
        }

        StatusText.Text = $"Edit mode: word {index + 1} of {_wordRuns.Count}: “{run.Text}” — Enter to edit, Tab for the next word, Esc to stop.";
    }

    private async void CommitSelectedWordAsync()
    {
        if (_wordIndex < 0 || _wordIndex >= _wordRuns.Count)
        {
            return;
        }

        PdfTextRun run = _wordRuns[_wordIndex];
        ClearWordCycle();
        await EditRunAsync(run);
        if (_vm is not null)
        {
            Refresh();
        }
    }

    private void ClearWordCycle()
    {
        if (_wordIndex < 0 && _wordRuns.Count == 0)
        {
            return;
        }

        _wordRuns = Array.Empty<PdfTextRun>();
        _wordIndex = -1;
        HideWordHighlight(_wordPage);
        _wordPage = null;
    }

    private void HideWordHighlight(PageSlotViewModel? slot)
    {
        if (slot is null || PageList.ItemContainerGenerator.ContainerFromItem(slot) is not ListBoxItem lvi)
        {
            return;
        }

        if (FindVisualChildByName<Border>(lvi, "WordHighlight") is { } highlight)
        {
            highlight.Visibility = Visibility.Collapsed;
        }
    }

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (FindVisualChildByName<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
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

        if (EditModeToggle?.IsChecked == true && FormModeToggle?.IsChecked == true)
        {
            FormModeToggle.IsChecked = false;
        }

        if (EditModeToggle?.IsChecked == true && RedactModeToggle?.IsChecked == true)
        {
            RedactModeToggle.IsChecked = false;
        }

        StatusText.Text = (EditModeToggle?.IsChecked == true)
            ? "Edit mode: click a word, or Tab / Shift+Tab to pick one and Enter to edit it, Esc to stop"
            : _vm?.Status ?? string.Empty;
        ClearWordCycle();
    }

    private void ObjectModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ObjectModeToggle?.IsChecked == true && EditModeToggle?.IsChecked == true)
        {
            EditModeToggle.IsChecked = false;
        }

        if (ObjectModeToggle?.IsChecked == true && FormModeToggle?.IsChecked == true)
        {
            FormModeToggle.IsChecked = false;
        }

        if (ObjectModeToggle?.IsChecked == true && RedactModeToggle?.IsChecked == true)
        {
            RedactModeToggle.IsChecked = false;
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

    private void FormModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (FormModeToggle?.IsChecked == true && EditModeToggle?.IsChecked == true)
        {
            EditModeToggle.IsChecked = false;
        }

        if (FormModeToggle?.IsChecked == true && ObjectModeToggle?.IsChecked == true)
        {
            ObjectModeToggle.IsChecked = false;
        }

        if (FormModeToggle?.IsChecked == true && RedactModeToggle?.IsChecked == true)
        {
            RedactModeToggle.IsChecked = false;
        }

        bool on = FormModeToggle?.IsChecked == true;
        if (on && _vm is not null)
        {
            FormView.SetContext(_vm);
        }

        FormView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ObjectView.Visibility = on ? Visibility.Collapsed : ObjectView.Visibility;
        RedactViewHost.Visibility = on ? Visibility.Collapsed : RedactViewHost.Visibility;
        PageList.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = on
            ? "Fill form mode: set field values, then flatten the form to static content"
            : _vm?.Status ?? string.Empty;
    }

    private void RedactModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (RedactModeToggle?.IsChecked == true && EditModeToggle?.IsChecked == true)
        {
            EditModeToggle.IsChecked = false;
        }

        if (RedactModeToggle?.IsChecked == true && ObjectModeToggle?.IsChecked == true)
        {
            ObjectModeToggle.IsChecked = false;
        }

        if (RedactModeToggle?.IsChecked == true && FormModeToggle?.IsChecked == true)
        {
            FormModeToggle.IsChecked = false;
        }

        bool on = RedactModeToggle?.IsChecked == true;
        if (on && _vm is not null)
        {
            RedactViewHost.SetContext(_vm);
        }

        RedactViewHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ObjectView.Visibility = on ? Visibility.Collapsed : ObjectView.Visibility;
        FormView.Visibility = on ? Visibility.Collapsed : FormView.Visibility;
        PageList.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = on
            ? "Redact mode: drag boxes over sensitive content, then Apply redactions… to remove it"
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

            await EditRunAsync(run);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Edit failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Prompts for replacement text and commits it through the
    /// FR-EDIT-02/03 gates and the FR-EDIT-05 command stack. Shared by the
    /// mouse hit-test path and the keyboard word-selection path (WCAG 2.1.1).</summary>
    private async Task EditRunAsync(PdfTextRun run)
    {
        string? newText = AskEditText(run.Text);
        if (newText is null)
        {
            return;
        }

        TextEditOutcome outcome = await _vm!.EditTextRunAsync(run.Index, newText, allowCollision: false).ConfigureAwait(true);
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

    /// <summary>Opens a small prompt for replacement text. Returns the text, or
    /// null when the user cancels.</summary>
    private static string? AskEditText(string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        System.Windows.Automation.AutomationProperties.SetName(box, "Replacement text");
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

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? path = AskSavePath("Searchable.pdf");
        if (path is null)
        {
            return;
        }

        try
        {
            await _vm.RunOcrAsync(path);
            StatusText.Text = _vm.Status;
            OpenDocumentRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"OCR failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Protect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? path = AskSavePath("Protected.pdf");
        if (path is null)
        {
            return;
        }

        var dialog = new ProtectDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Options is null)
        {
            return;
        }

        try
        {
            await _vm.RunProtectAsync(path, dialog.Options);
            StatusText.Text = _vm.Status;

            // Deliberately NOT opening the protected file here: the viewer has no
            // password prompt yet, so opening it would render nothing useful.
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Protect failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
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
