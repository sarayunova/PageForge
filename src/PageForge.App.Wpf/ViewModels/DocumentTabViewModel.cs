// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Collections.ObjectModel;
using System.IO;
using PageForge.Core.Editing;
using PageForge.Core.Pdf;
using PageForge.Core.View;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>
/// One row in the viewer's page list or thumbnail strip. Carries the lazy full
/// image and the lazy thumbnail, plus the page region for sizing, and its
/// 1-based display number.
/// </summary>
public sealed class PageSlotViewModel
{
    public required PageImageViewModel Image { get; init; }

    public required PageImageViewModel Thumbnail { get; init; }

    public required PageForge.Core.Pdf.PdfPageRegion Region { get; init; }

    public required int DisplayNumber { get; init; }

    public int PageIndex => DisplayNumber - 1;
}

/// <summary>
/// One row in the document outline panel.
/// </summary>
public sealed class OutlineEntryViewModel
{
    public required PageForge.Core.Pdf.OutlineItem Item { get; init; }

    public string Title => Item.Title;

    public string PageLabel => Item.PageNumber > 0 ? $"p{Item.PageNumber}" : string.Empty;

    public int Indent => Math.Max(0, Item.Depth - 1);

    public System.Windows.FontWeight FontWeightPx => Item.Depth == 1 ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
}

/// <summary>
/// One search result row in the viewer's Search panel. Wraps a Core
/// <see cref="SearchHit"/> with the page label the view binds to.
/// </summary>
public sealed class SearchResultViewModel
{
    public required SearchHit Hit { get; init; }

    public int PageIndex => Hit.PageIndex;

    public string Snippet => Hit.Snippet;

    public string PageLabel => $"Page {Hit.PageIndex + 1}";
}

/// <summary>
/// View-model behind one open document tab. Wraps the Core
/// <see cref="DocumentViewModel"/> and exposes the collections and commands the
/// WPF viewer binds to. All engine work runs through the Core VM (off the UI
/// thread, serialized by the engine); this type is a thin presentation seam.
/// </summary>
public sealed class DocumentTabViewModel : ObservableObject
{
    private const int ThumbTargetWidthPx = 140;

    private readonly DocumentViewModel _doc;
    private readonly List<PageSlotViewModel> _pages = new();
    private IReadOnlyList<OutlineEntryViewModel> _outline = Array.Empty<OutlineEntryViewModel>();
    private IReadOnlyList<SearchResultViewModel> _searchHits = Array.Empty<SearchResultViewModel>();
    private IReadOnlyList<AnnotationRowViewModel> _currentPageAnnotations = Array.Empty<AnnotationRowViewModel>();
    private string _status = string.Empty;
    private string _searchQuery = string.Empty;
    private bool _isContinuous;
    private bool _isBusy;
    private bool _isReorderMode;

    /// <summary>The FR-EDIT-05 undo/redo stack for this document session.</summary>
    private readonly EditCommandStack _editStack = new();

    /// <summary>Serializes edit-stack operations (push/undo/redo), since the stack
    /// (like the engine) is not thread-safe and must be driven on one lane at a time.</summary>
    private readonly SemaphoreSlim _editGate = new(1, 1);

    public DocumentTabViewModel(DocumentViewModel doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _doc.StateChanged += (_, _) => RaiseStateChanged();
        _editStack.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        };
    }

    /// <summary>True when the FR-EDIT-05 stack has an edit that can be undone.</summary>
    public bool CanUndo => _editStack.CanUndo;

    /// <summary>True when the FR-EDIT-05 stack has an undone edit that can be redone.</summary>
    public bool CanRedo => _editStack.CanRedo;

    public DocumentViewModel Core => _doc;

    public string DisplayName => _doc.DisplayName;

    public int CurrentDisplayPage => _doc.CurrentPage + 1;

    public int PageCount => _doc.PageCount;

    public string PageIndicator => $"{_doc.CurrentPage + 1} / {Math.Max(1, _doc.PageCount)}";

    public double Zoom => _doc.Zoom;

    public int Rotation => _doc.Rotation;

    /// <summary>1.0 = 100% zoom on a 96-DPI screen.</summary>
    public double RenderDpi => 96.0 * _doc.Zoom;

    public ObservableCollection<PageSlotViewModel> Pages { get; } = new();

    /// <summary>A reorder staging list of the same page slots, in user-editable
    /// display order. Used only while reorder mode is active so the live viewer
    /// (which maps navigation through <see cref="Pages"/> and the document) stays
    /// stable until the user saves the reordered copy.</summary>
    public ObservableCollection<PageSlotViewModel> ReorderItems { get; } = new();

    /// <summary>The pages the main surface should show: all of them in
    /// continuous mode, or only the single current page in single mode.</summary>
    public IReadOnlyList<PageSlotViewModel> VisiblePages =>
        _isContinuous ? _pages : new[] { CurrentSlot }.OfType<PageSlotViewModel>().ToArray();

    private PageSlotViewModel? CurrentSlot =>
        _doc.CurrentPage >= 0 && _doc.CurrentPage < _pages.Count ? _pages[_doc.CurrentPage] : null;

    /// <summary>The slot for the page currently displayed, or null when empty.</summary>
    public PageSlotViewModel? CurrentPageSlot => CurrentSlot;

    public IReadOnlyList<OutlineEntryViewModel> Outline
    {
        get => _outline;
        private set => SetProperty(ref _outline, value);
    }

    public IReadOnlyList<SearchResultViewModel> SearchHits
    {
        get => _searchHits;
        private set => SetProperty(ref _searchHits, value);
    }

    /// <summary>The annotations bound to the currently displayed page (FR-ANNOT).
    /// Refreshed by <see cref="RefreshAnnotationsAsync"/>.</summary>
    public IReadOnlyList<AnnotationRowViewModel> Annotations
    {
        get => _currentPageAnnotations;
        private set => SetProperty(ref _currentPageAnnotations, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public bool IsContinuous
    {
        get => _isContinuous;
        set
        {
            if (SetProperty(ref _isContinuous, value))
            {
                RaiseStateChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public event EventHandler? StateChanged;

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(CurrentDisplayPage));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(Rotation));
        OnPropertyChanged(nameof(RenderDpi));
        OnPropertyChanged(nameof(VisiblePages));
    }

    /// <summary>Loads the document, layout, outline, and page slots.</summary>
    public async Task InitializeAsync(string path, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await _doc.InitializeAsync(path, ct).ConfigureAwait(false);

            for (int i = 0; i < _doc.PageCount; i++)
            {
                PdfPageRegion region = _doc.PageSizes[i];
                double thumbDpi = region.WidthPt > 0
                    ? ThumbTargetWidthPx * 72.0 / region.WidthPt
                    : 72.0;
                _pages.Add(new PageSlotViewModel
                {
                    Image = new PageImageViewModel(_doc, i, RenderDpi),
                    Thumbnail = new PageImageViewModel(_doc, i, thumbDpi),
                    Region = region,
                    DisplayNumber = i + 1,
                });
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Pages.Clear();
                foreach (PageSlotViewModel p in _pages)
                {
                    Pages.Add(p);
                }
            });

            _outline = _doc.Outline.Items
                .Select(item => new OutlineEntryViewModel { Item = item })
                .ToArray();

            Status = _doc.Outline.HasItems
                ? $"{_doc.PageCount} pages · {_doc.Outline.Items.Count} bookmarks"
                : $"{_doc.PageCount} pages";
        }
        finally
        {
            IsBusy = false;
        }

        OnPropertyChanged(nameof(Outline));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(DisplayName));
        RaiseStateChanged();
    }

    /// <summary>Sets every full page render DPI to the current zoom-based DPI
    /// (or to <paramref name="overrideDpi"/> when provided) and drops caches so
    /// visible pages re-render at the new resolution.</summary>
    public void ApplyZoomToPages(double overrideDpi = 0)
    {
        double dpi = overrideDpi > 0 ? overrideDpi : RenderDpi;
        foreach (PageSlotViewModel page in _pages)
        {
            page.Image.RenderDpi = dpi;
        }
    }

    public void GoToPage(int page)
    {
        _doc.GoToPage(page);
        RaiseStateChanged();
    }

    public void NextPage() => GoToPage(_doc.CurrentPage + 1);

    public void PreviousPage() => GoToPage(_doc.CurrentPage - 1);

    public void ZoomIn()
    {
        _doc.SetZoom(_doc.Zoom + 0.25);
        ApplyZoomToPages();
        RaiseStateChanged();
    }

    public void ZoomOut()
    {
        _doc.SetZoom(_doc.Zoom - 0.25);
        ApplyZoomToPages();
        RaiseStateChanged();
    }

    public void ZoomReset()
    {
        _doc.SetZoom(1.0);
        ApplyZoomToPages();
        RaiseStateChanged();
    }

    public void RotateClockwise()
    {
        _doc.AddRotation(1);
        RaiseStateChanged();
    }

    public void RotateCounterClockwise()
    {
        _doc.AddRotation(-1);
        RaiseStateChanged();
    }

    public async Task RunSearchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchHits = Array.Empty<SearchResultViewModel>();
            Status = string.Empty;
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<SearchHit> hits = await _doc.SearchAsync(SearchQuery, ct).ConfigureAwait(false);
            SearchHits = hits.Select(h => new SearchResultViewModel { Hit = h }).ToArray();
            Status = $"{hits.Count} match(es)";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void NavigateToSearchHit(SearchResultViewModel hit) => GoToPage(hit.PageIndex);

    public void NavigateToOutline(OutlineEntryViewModel entry)
    {
        if (entry.Item.PageNumber > 0)
        {
            GoToPage(entry.Item.PageNumber - 1);
        }
    }

    /// <summary>Rotates the current page clockwise by <paramref name="quarterTurns"/>
    /// and writes the result to a new file (FR-PAGE rotate).</summary>
    public async Task<int> RotateCurrentPageAsync(int quarterTurns, string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        int page = Math.Min(_doc.CurrentPage, _doc.PageCount - 1);
        return await RunBuildAsync(
            "rotate",
            PdfPageOrganizer.RotateAsync(_doc.Engine, SourcePathForBuild(), _doc.PageCount, new Dictionary<int, int> { [page] = quarterTurns }, outputPath, ct),
            outputPath);
    }

    /// <summary>Deletes the current page and writes the result to a new file
    /// (FR-PAGE delete).</summary>
    public async Task<int> DeleteCurrentPageAsync(string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        int page = Math.Min(_doc.CurrentPage, _doc.PageCount - 1);
        return await RunBuildAsync(
            "delete",
            PdfPageOrganizer.DeleteAsync(_doc.Engine, SourcePathForBuild(), _doc.PageCount, new HashSet<int> { page }, outputPath, ct),
            outputPath);
    }

    /// <summary>Extracts the current page and writes it to a new file
    /// (FR-PAGE extract / split).</summary>
    public async Task<int> ExtractCurrentPageAsync(string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        int page = Math.Min(_doc.CurrentPage, _doc.PageCount - 1);
        return await RunBuildAsync(
            "extract",
            PdfPageOrganizer.ExtractAsync(_doc.Engine, SourcePathForBuild(), new[] { page }, outputPath, ct),
            outputPath);
    }

    /// <summary>Merges this document with <paramref name="otherPath"/>
    /// (whose page count is <paramref name="otherPageCount"/>) and writes the
    /// combined result to a new file (FR-PAGE merge).</summary>
    public async Task<int> MergeWithAsync(string otherPath, int otherPageCount, string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        var sources = new List<SourceWithCount>
        {
            new(SourcePathForBuild(), _doc.PageCount),
            new(otherPath, otherPageCount),
        };
        return await RunBuildAsync(
            "merge",
            PdfPageOrganizer.MergeAsync(_doc.Engine, sources, outputPath, ct),
            outputPath);
    }

    /// <summary>Inserts every page of <paramref name="otherPath"/> before the given
    /// position and writes the result to a new file (FR-PAGE insert).</summary>
    public async Task<int> InsertFileAtAsync(string otherPath, int otherPageCount, int insertAt, string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        var insert = Enumerable.Range(0, otherPageCount).ToArray();
        return await RunBuildAsync(
            "insert",
            PdfPageOrganizer.InsertAsync(_doc.Engine, SourcePathForBuild(), _doc.PageCount, insertAt, otherPath, insert, outputPath, ct),
            outputPath);
    }

    /// <summary>Writes the pages of this document in <paramref name="newOrder"/>
    /// (a permutation of 0..pageCount-1) to a new file (FR-PAGE reorder).</summary>
    public async Task<int> ReorderAsync(IReadOnlyList<int> newOrder, string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        if (newOrder.Count != _doc.PageCount)
        {
            throw new ArgumentException("Reorder must reference every page exactly once.", nameof(newOrder));
        }

        return await RunBuildAsync(
            "reorder",
            PdfPageOrganizer.ReorderAsync(_doc.Engine, SourcePathForBuild(), newOrder, outputPath, ct),
            outputPath);
    }

    /// <summary>Writes a plain copy of the document (all pages, unmodified) to a
    /// new file (FR-PAGE save-as / baseline).</summary>
    public async Task<int> SaveCopyAsync(string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        var all = Enumerable.Range(0, _doc.PageCount).ToArray();
        return await RunBuildAsync(
            "copy",
            PdfPageOrganizer.ExtractAsync(_doc.Engine, SourcePathForBuild(), all, outputPath, ct),
            outputPath);
    }

    /// <summary>Reloads the annotations bound to the current page into
    /// <see cref="Annotations"/> (FR-ANNOT-01 list).</summary>
    public async Task RefreshAnnotationsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PdfAnnotation> list = await AnnotationService
            .ListAsync(_doc.Engine, _doc.CurrentPage, ct).ConfigureAwait(false);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Annotations = list
                .Select(a => new AnnotationRowViewModel(a))
                .ToArray();
        });
    }

    /// <summary>Adds a text highlight across a quad laid over the top of the
    /// current page (FR-ANNOT-01) and refreshes the list.</summary>
    public async Task AddHighlightAsync(CancellationToken ct = default)
    {
        PdfPageRegion region = CurrentRegion();
        double left = region.WidthPt * 0.08, right = region.WidthPt * 0.92;
        double baseline = region.HeightPt * 0.85, lineHeight = region.HeightPt * 0.05;
        var quad = new PdfQuad(
            new PdfPoint(left, baseline),
            new PdfPoint(right, baseline),
            new PdfPoint(right, baseline + lineHeight),
            new PdfPoint(left, baseline + lineHeight));
        await AnnotationService.AddHighlightAsync(_doc.Engine, _doc.CurrentPage, new[] { quad }, (0.96, 0.98, 0.30), ct).ConfigureAwait(false);
        await RefreshAnnotationsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Adds a text note pinned near the top-right of the current page
    /// (FR-ANNOT-01) and refreshes the list.</summary>
    public async Task AddTextNoteAsync(CancellationToken ct = default)
    {
        PdfPageRegion region = CurrentRegion();
        double size = Math.Min(region.WidthPt, region.HeightPt) * 0.10;
        double x0 = region.WidthPt - size * 1.4, y0 = region.HeightPt - size * 1.4;
        await AnnotationService.AddTextNoteAsync(
            _doc.Engine, _doc.CurrentPage, x0, y0, x0 + size, y0 + size,
            "PageForge annotation note", ct).ConfigureAwait(false);
        await RefreshAnnotationsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Adds an ink squiggle across the middle of the current page
    /// (FR-ANNOT-01) and refreshes the list.</summary>
    public async Task AddInkAsync(CancellationToken ct = default)
    {
        PdfPageRegion region = CurrentRegion();
        double y = region.HeightPt * 0.5;
        double x0 = region.WidthPt * 0.15, x1 = region.WidthPt * 0.85;
        var points = new List<PdfPoint>();
        const int n = 40;
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            double wave = Math.Sin(t * Math.PI * 6) * region.HeightPt * 0.02;
            points.Add(new PdfPoint(x0 + (x1 - x0) * t, y + wave));
        }
        await AnnotationService.AddInkAsync(_doc.Engine, _doc.CurrentPage, points, (0.9, 0.1, 0.1), ct).ConfigureAwait(false);
        await RefreshAnnotationsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes a copy of the document to <paramref name="outputPath"/> with
    /// every annotation of the given types baked into static content (FR-ANNOT-02)
    /// and the others preserved, then refreshes the live list (the in-memory doc is
    /// unchanged). Returns the number of pages written.</summary>
    public async Task<int> FlattenExportAsync(IReadOnlySet<AnnotationType> typesToFlatten, string outputPath, CancellationToken ct = default)
    {
        GuardPageCount();
        IsBusy = true;
        try
        {
            await AnnotationService
                .FlattenForExportAsync(_doc.Engine, _doc.PageCount, typesToFlatten, outputPath, ct)
                .ConfigureAwait(false);
            Status = $"flatten: wrote {_doc.PageCount} pages -> {Path.GetFileName(outputPath)}";
            return _doc.PageCount;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Returns the editable run whose bounding box contains the given
    /// PDF point (bottom-left origin), or null when the click is not on text.</summary>
    public async ValueTask<PdfTextRun?> HitTestAsync(double xPt, double yPt, CancellationToken ct = default)
        => await TextEditService.HitTestAsync(_doc.Engine, _doc.CurrentPage, xPt, yPt, ct).ConfigureAwait(false);

    /// <summary>
    /// Commits a user-facing text edit through the FR-EDIT-02/03 gates and the
    /// FR-EDIT-05 command stack. Runs the overflow/collision analysis and the
    /// font-fidelity check BEFORE committing; if the edit would collide with a
    /// sibling and <paramref name="allowCollision"/> is false, or the new text has
    /// characters the run's font cannot paint, the edit is not applied and a
    /// descriptive <see cref="TextEditOutcome"/> is returned. Returns
    /// <see cref="TextEditOutcome.Success"/> after the command is pushed.
    /// </summary>
    public async Task<TextEditOutcome> EditTextRunAsync(
        int runIndex, string newText, bool allowCollision, CancellationToken ct = default)
    {
        await _editGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GuardPageCount();
            int page = Math.Min(_doc.CurrentPage, _doc.PageCount - 1);

            PreparedTextEdit prepared = await TextEditService
                .PrepareRewriteAsync(_doc.Engine, page, runIndex, newText, options: null, cancellationToken: ct).ConfigureAwait(false);
            FontFidelityResult fidelity = await TextEditService
                .CheckFontFidelityAsync(_doc.Engine, page, runIndex, newText, table: null, cancellationToken: ct).ConfigureAwait(false);

            if (fidelity.HasIssues)
            {
                string count = fidelity.HasSubstitutions
                    ? $"{fidelity.Issues.Count} character(s) substituted; others can't render"
                    : $"{fidelity.Issues.Count} character(s) can't render";
                return TextEditOutcome.Blocked($"font-fidelity: {count}");
            }

            if (prepared.NeedsConfirmation && !allowCollision)
            {
                return TextEditOutcome.Overflow(
                    "edit would grow beyond the original box and collide with another object — confirm to proceed");
            }

            IEditCommand pushed = await _editStack
                .PushAsync(new TextEditCommand(_doc.Engine, page, runIndex, newText), ct).ConfigureAwait(false);
            Status = $"edited text (undo available)";
            await RefreshCurrentPageRenderAsync(ct).ConfigureAwait(false);
            return TextEditOutcome.Success();
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>Lists the image/vector objects of the currently displayed page
    /// (FR-EDIT-04) for interactive selection in the object-edit overlay.</summary>
    public async Task<IReadOnlyList<PdfPageObject>> ListObjectsAsync(CancellationToken ct = default)
    {
        GuardPageCount();
        int page = Math.Min(_doc.CurrentPage, _doc.PageCount - 1);
        return await _doc.Engine.ListObjectsAsync(page, ct).ConfigureAwait(false);
    }

    /// <summary>Moves/resizes the object identified by <paramref name="objectId"/>
    /// to <paramref name="newBounds"/> through the FR-EDIT-05 command stack
    /// (FR-EDIT-04 interactive transform). Undo/redo via the shared stack.</summary>
    public async Task MoveResizeObjectAsync(string objectId, PdfRect newBounds, CancellationToken ct = default)
    {
        await _editGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GuardPageCount();
            int page = Math.Min(_doc.CurrentPage, _doc.PageCount - 1);
            await _editStack
                .PushAsync(new ObjectEditCommand(_doc.Engine, page, objectId, newBounds), ct).ConfigureAwait(false);
            Status = $"moved/resized object {objectId} (undo available)";
            await RefreshCurrentPageRenderAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>Replaces the interior of the object identified by
    /// <paramref name="objectId"/> with <paramref name="replacement"/> through the
    /// FR-EDIT-05 command stack (FR-EDIT-04 interactive replace). The bounding box
    /// is preserved; undo/redo via the shared stack.</summary>
    public async Task ReplaceObjectAsync(string objectId, PdfObjectReplacement replacement, CancellationToken ct = default)
    {
        await _editGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GuardPageCount();
            int page = Math.Min(_doc.CurrentPage, _doc.PageCount - 1);
            await _editStack
                .PushAsync(new ReplaceObjectCommand(_doc.Engine, page, objectId, replacement), ct).ConfigureAwait(false);
            Status = $"replaced object {objectId} (undo available)";
            await RefreshCurrentPageRenderAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>Undoes the most recent edit on the FR-EDIT-05 stack, if any.</summary>
    public async Task UndoEditAsync(CancellationToken ct = default)
    {
        await _editGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IEditCommand? undone = await _editStack.UndoAsync(ct).ConfigureAwait(false);
            if (undone is not null)
            {
                Status = $"undo: {undone.Name}";
                await RefreshCurrentPageRenderAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>Re-applies the most recently undone edit on the stack, if any.</summary>
    public async Task RedoEditAsync(CancellationToken ct = default)
    {
        await _editGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IEditCommand? redone = await _editStack.RedoAsync(ct).ConfigureAwait(false);
            if (redone is not null)
            {
                Status = $"redo: {redone.Name}";
                await RefreshCurrentPageRenderAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>Drops the whole edit history (document close).</summary>
    public void ClearEditHistory() => _editStack.Clear();

    /// <summary>Clears and re-renders the currently displayed page so an applied
    /// text edit (or its undo/redo) shows on screen.</summary>
    private async Task RefreshCurrentPageRenderAsync(CancellationToken ct = default)
    {
        PageSlotViewModel? slot = _pages.FirstOrDefault(p => p.PageIndex == _doc.CurrentPage);
        if (slot is null)
        {
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            slot.Image.Clear();
        });
        await slot.Image.RenderAsync(ct).ConfigureAwait(false);
    }

    private PdfPageRegion CurrentRegion()
    {
        GuardPageCount();
        return _doc.PageSizes[Math.Min(_doc.CurrentPage, _doc.PageCount - 1)];
    }

    /// <summary>True while the thumbnail panel is in reorder-drag mode (selection
    /// navigates normally when false).</summary>
    public bool IsReorderMode
    {
        get => _isReorderMode;
        set => SetProperty(ref _isReorderMode, value);
    }

    /// <summary>Begins reorder staging: snapshots the current page slots into
    /// <see cref="ReorderItems"/> and turns on reorder mode.</summary>
    public void EnterReorderMode()
    {
        ReorderItems.Clear();
        foreach (PageSlotViewModel p in Pages)
        {
            ReorderItems.Add(p);
        }

        IsReorderMode = true;
    }

    /// <summary>Leaves reorder staging without changing the live document. The
    /// user applies a reorder by saving (which opens a new tab); this only exits
    /// the drag mode on the thumbnails.</summary>
    public void ExitReorderMode()
    {
        IsReorderMode = false;
    }

    /// <summary>Moves the slot at <paramref name="fromIndex"/> to
    /// <paramref name="toIndex"/> within the reorder staging list (drag-drop).</summary>
    public void MoveReorderItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= ReorderItems.Count)
        {
            return;
        }

        toIndex = Math.Clamp(toIndex, 0, ReorderItems.Count - 1);
        if (toIndex != fromIndex)
        {
            ReorderItems.Move(fromIndex, toIndex);
        }
    }

    /// <summary>The current staging order as a permutation of 0-based source
    /// indices (each item's <see cref="PageSlotViewModel.PageIndex"/> is its
    /// original position in the source document).</summary>
    public int[] BuildOrder() => ReorderItems.Select(p => p.PageIndex).ToArray();

    private void GuardPageCount()
    {
        if (_doc.PageCount <= 0)
        {
            throw new InvalidOperationException("The document has no pages to organize.");
        }
    }

    private string SourcePathForBuild()
        => _doc.SourcePath ?? throw new InvalidOperationException("The document has no backing file path to build from.");

    private async Task<int> RunBuildAsync(string op, ValueTask<int> build, string outputPath)
    {
        IsBusy = true;
        try
        {
            int count = await build.ConfigureAwait(false);
            Status = $"{op}: wrote {count} pages -> {Path.GetFileName(outputPath)}";
            return count;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// The result of a user-facing text edit attempt in
/// <see cref="DocumentTabViewModel.EditTextRunAsync"/>. <see cref="Succeeded"/>
/// means the edit was committed to the stack; otherwise <see cref="Kind"/>
/// describes why it was not applied (font-fidelity block or FR-EDIT-02 collision
/// confirmation), with a human-readable <see cref="Message"/> to surface to the user.
/// </summary>
public sealed record TextEditOutcome(TextEditOutcomeKind Kind, string? Message = null)
{
    public bool Succeeded => Kind == TextEditOutcomeKind.Applied;

    public static TextEditOutcome Success() => new(TextEditOutcomeKind.Applied);

    public static TextEditOutcome Blocked(string message) => new(TextEditOutcomeKind.FontBlocked, message);

    public static TextEditOutcome Overflow(string message) => new(TextEditOutcomeKind.NeedsConfirmation, message);
}

/// <summary>Classifies a <see cref="TextEditOutcome"/>.</summary>
public enum TextEditOutcomeKind
{
    /// <summary>The edit was applied to the document.</summary>
    Applied,

    /// <summary>The new text has characters the run's font cannot render (FR-EDIT-03).</summary>
    FontBlocked,

    /// <summary>The edit would collide with a sibling object (FR-EDIT-02) and needs confirmation.</summary>
    NeedsConfirmation,
}

/// <summary>
/// One row in a document's Annotations panel (FR-ANNOT): wraps a Core
/// <see cref="PdfAnnotation"/> with the display strings the view binds to.
/// </summary>
public sealed class AnnotationRowViewModel
{
    public AnnotationRowViewModel(PdfAnnotation annotation)
    {
        Annotation = annotation;
    }

    public PdfAnnotation Annotation { get; }

    public string TypeName => Annotation.Type.ToString();

    public string Bounds => $"({Annotation.X0:F0}, {Annotation.Y0:F0}) – ({Annotation.X1:F0}, {Annotation.Y1:F0})";

    public string Description => string.IsNullOrWhiteSpace(Annotation.Contents)
        ? $"{Annotation.Type} on this page"
        : $"{Annotation.Type}: {Annotation.Contents}";
}
