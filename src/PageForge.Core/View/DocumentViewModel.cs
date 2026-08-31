// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.Core.View;

/// <summary>
/// One search hit: the page that matched and a short text snippet around the
/// first match. Backs the full-text search results list (FR-VIEW-03).
/// </summary>
public sealed record SearchHit(int PageIndex, string Snippet);

/// <summary>
/// Hosts a single open document for the viewer. Owns the <see cref="IPdfEngine"/>
/// instance and exposes viewer-facing state and operations: lazy page layout,
/// navigation, zoom/rotation state, the loaded outline, and full-text search.
///
/// Threading: engine calls are serialized by the engine; this class is typically
/// driven from an async context (never the UI thread). The calculator-delivered
/// page sizes and previews are stored here for the view to render.
/// </summary>
public sealed class DocumentViewModel : IAsyncDisposable
{
    private readonly IPdfEngine _engine;
    private readonly List<PdfPageRegion> _pageSizes = new();
    private int _currentPage;

    public DocumentViewModel(IPdfEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>0-based index of the page currently displayed.</summary>
    public int CurrentPage => _currentPage;

    public PdfDocumentInfo? Info { get; private set; }

    /// <summary>The engine backing this document. Exposed so page-organization
    /// operations (FR-PAGE) can run through the same serialized access lane that
    /// the viewer uses, and can reference source files by path in a build job.</summary>
    public IPdfEngine Engine => _engine;

    /// <summary>Fully-qualified path of the document on disk.</summary>
    public string? SourcePath { get; private set; }

    public int PageCount => Info?.PageCount ?? 0;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Scale factor applied to the 72-DPI base render (1.0 = 100%).</summary>
    public double Zoom { get; private set; } = 1.0;

    /// <summary>Clockwise rotation in degrees: 0, 90, 180, 270.</summary>
    public int Rotation { get; private set; }

    public PdfOutline Outline { get; private set; } = PdfOutline.Empty;

    /// <summary>Cumulative top offset (in points) of each page in a continuous
    /// vertical strip. Filled by <see cref="LoadLayoutAsync"/>.</summary>
    public IReadOnlyList<double> PageTopOffsetPt => _pageTopOffset;

    public IReadOnlyList<PdfPageRegion> PageSizes => _pageSizes;

    private readonly List<double> _pageTopOffset = new();

    public event EventHandler? StateChanged;

    public async Task InitializeAsync(string path, CancellationToken ct = default)
    {
        Info = await _engine.OpenAsync(path, ct).ConfigureAwait(false);
        DisplayName = Info.DisplayName;
        SourcePath = Path.GetFullPath(path);
        await LoadLayoutAsync(ct).ConfigureAwait(false);
        await LoadOutlineAsync(ct).ConfigureAwait(false);
    }

    public async Task LoadLayoutAsync(CancellationToken ct = default)
    {
        if (Info is null)
        {
            return;
        }

        _pageSizes.Clear();
        _pageTopOffset.Clear();
        double offset = 0.0;
        for (int i = 0; i < Info.PageCount; i++)
        {
            PdfPageRegion region = await _engine.GetPageSizeAsync(i, ct).ConfigureAwait(false);
            _pageSizes.Add(region);
            _pageTopOffset.Add(offset);
            offset += effectiveHeight(region);
        }

        RaiseStateChanged();
    }

    private static double effectiveHeight(PdfPageRegion region)
    {
        // Reserve a small visual gap between pages in continuous mode.
        return region.HeightPt + 12.0;
    }

    public async Task LoadOutlineAsync(CancellationToken ct = default)
    {
        Outline = await _engine.GetOutlineAsync(ct).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>Renders the given page (0-based) at the requested DPI.</summary>
    public ValueTask<RenderedPdfPage> RenderAsync(int pageIndex, float dpi, CancellationToken ct = default)
        => _engine.RenderPageToPngAsync(pageIndex, dpi, ct);

    /// <summary>Extracts the plain text of a page for search and inspection.</summary>
    public ValueTask<PageText> GetPageTextAsync(int pageIndex, CancellationToken ct = default)
        => _engine.GetPageTextAsync(pageIndex, ct);

    public void GoToPage(int page)
    {
        int clamped = Math.Clamp(page, 0, Math.Max(0, PageCount - 1));
        if (_currentPage == clamped)
        {
            return;
        }

        _currentPage = clamped;
        RaiseStateChanged();
    }

    public void NextPage() => GoToPage(_currentPage + 1);

    public void PreviousPage() => GoToPage(_currentPage - 1);

    public void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.1, 8.0);
        if (double.IsNaN(zoom))
        {
            zoom = 1.0;
        }

        if (Math.Abs(Zoom - zoom) < 1e-9)
        {
            return;
        }

        Zoom = zoom;
        RaiseStateChanged();
    }

    public void AddRotation(int quarterTurnsClockwise)
    {
        Rotation = ((Rotation + (quarterTurnsClockwise * 90)) % 360 + 360) % 360;
        RaiseStateChanged();
    }

    /// <summary>
    /// Full-text search across every page. Returns hits in page order with a
    /// per-page snippet centred on the first match. Case-insensitive.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchHit>();
        }

        var hits = new List<SearchHit>();
        for (int i = 0; i < PageCount; i++)
        {
            PageText pageText = await _engine.GetPageTextAsync(i, ct).ConfigureAwait(false);
            int idx = pageText.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                hits.Add(new SearchHit(i, MakeSnippet(pageText.Text, idx, query.Length)));
            }
        }

        return hits;
    }

    private static string MakeSnippet(string text, int matchStart, int matchLength)
    {
        const int window = 60;
        string normalized = text.Replace("\r", " ").Replace("\n", " ");
        int start = Math.Max(0, matchStart - window);
        int length = Math.Min(normalized.Length - start, window * 2 + matchLength);
        string snippet = normalized.Substring(start, length).Trim();
        if (start > 0)
        {
            snippet = "… " + snippet;
        }
        if (start + length < normalized.Length)
        {
            snippet += " …";
        }
        return snippet;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync().ConfigureAwait(false);
    }
}
