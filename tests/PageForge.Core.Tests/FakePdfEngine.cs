// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.Core.Tests;

/// <summary>
/// In-memory IPdfEngine for unit tests. The engine seam exists precisely so
/// that Core-domain logic (command stack, collision detection, font-fidelity)
/// is testable without a native dependency.
/// </summary>
internal sealed class FakePdfEngine : IPdfEngine
{
    private readonly int _pageCount;
    private readonly int _maxCalls;
    private int _renderCalls;
    private bool _disposed;

    public FakePdfEngine(int pageCount, int maxRenderCalls = -1)
    {
        _pageCount = pageCount;
        _maxCalls = maxRenderCalls;
    }

    public Func<int, float>? OnDpi { get; set; }

    public PdfOutline Outline { get; set; } = PdfOutline.Empty;

    public Func<int, string>? OnPageText { get; set; }

    /// <summary>The most recent build job (page refs, output path) handed to
    /// <see cref="BuildPdfAsync"/>, for FR-PAGE unit assertions, or null if none.</summary>
    public (string OutputPath, IReadOnlyList<PageBuildRef> Pages)? LastBuild { get; private set; }

    /// <summary>Optional hook to simulate a native build failure; throw to fail.</summary>
    public Action? OnBuild { get; set; }

    /// <summary>Per-page list of added annotations, keyed by 0-based page index.</summary>
    private readonly Dictionary<int, List<PdfAnnotation>> _annotations = new();

    /// <summary>Pages on which <see cref="FlattenAnnotationsAsync"/> was called.</summary>
    public List<int> FlattenedPages { get; } = new();

    /// <summary>The most recent output path passed to <see cref="SaveAsAsync"/>.</summary>
    public string? LastSavePath { get; private set; }

    /// <summary>Per-page editable text runs; centers the do/undo/redo assertions.</summary>
    private readonly Dictionary<int, List<PdfTextRun>> _runs = new();

    /// <summary>Runs actually edited on each page (rewrite or revert), oldest first.</summary>
    public Dictionary<int, List<string>> EditedTextByPage { get; } = new();

    /// <summary>Per-page image/vector objects for FR-EDIT-04 tests.</summary>
    private readonly Dictionary<int, List<PdfPageObject>> _objects = new();

    /// <summary>Object geometry edits keyed by the released receipt, for undo/redo routing.</summary>
    private readonly Dictionary<PdfTextEditReceipt, (int PageIndex, string ObjectId, PdfPageObject Before, PdfPageObject After)> _objectEdits = new();

    /// <summary>Seeds a page's image/vector objects.</summary>
    public void AddStoredObject(int pageIndex, PdfPageObject obj)
    {
        if (!_objects.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfPageObject>();
            _objects[pageIndex] = list;
        }

        list.Add(obj);
    }

    /// <summary>The current objects of a page (bounds reflect applied/undone edits).</summary>
    public IReadOnlyList<PdfPageObject> StoredObjects(int pageIndex)
        => _objects.TryGetValue(pageIndex, out var list) ? list.ToArray() : Array.Empty<PdfPageObject>();

    /// <summary>Seeds the page's text runs for text-edit tests.</summary>
    public void AddStoredRun(int pageIndex, PdfTextRun run)
    {
        if (!_runs.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfTextRun>();
            _runs[pageIndex] = list;
        }

        list.Add(run);
    }

    /// <summary>When set, ListTextRunsAsync returns this instead of the stored runs.</summary>
    public IReadOnlyList<PdfTextRun>? StubbedRuns { get; set; }

    public ValueTask<IReadOnlyList<PdfTextRun>> ListTextRunsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<PdfTextRun> runs = StubbedRuns
            ?? (IReadOnlyList<PdfTextRun>?)_runs.GetValueOrDefault(pageIndex)?.ToArray()
            ?? Array.Empty<PdfTextRun>();
        return ValueTask.FromResult(runs);
    }

    public async ValueTask<PdfTextEditReceipt> RewriteTextRunAsync(
        int pageIndex, int runIndex, string newText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();

        if (!_runs.TryGetValue(pageIndex, out var list) || runIndex < 0 || runIndex >= list.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex));
        }

        List<string> edited = Edited(pageIndex);
        string oldText = list[runIndex].Text;
        byte[] oldBytes = System.Text.Encoding.UTF8.GetBytes(oldText);
        byte[] newBytes = System.Text.Encoding.UTF8.GetBytes(newText);
        list[runIndex] = list[runIndex] with { Text = newText };
        edited.Add($"rewrite:{oldText}->{newText}");

        return new PdfTextEditReceipt(1, 0, runIndex, oldBytes.Length, newBytes.Length, oldBytes, newBytes);
    }

    public async ValueTask RevertTextEditAsync(
        int pageIndex, PdfTextEditReceipt receipt, bool redo, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();

        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        if (_objectEdits.TryGetValue(receipt, out var objectEdit))
        {
            PdfPageObject apply = redo ? objectEdit.After : objectEdit.Before;
            var objectList = _objects[objectEdit.PageIndex];
            int idx = objectList.FindIndex(o => o.Id == objectEdit.ObjectId);
            if (idx < 0)
            {
                throw new InvalidOperationException("Object no longer present for undo/redo.");
            }

            objectList[idx] = apply;
            Edited(objectEdit.PageIndex).Add($"object-{(redo ? "redo" : "undo")}:{apply.Bounds.X0}");
            return;
        }

        if (!_runs.TryGetValue(pageIndex, out var list) || receipt.Offset < 0 || receipt.Offset >= list.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(receipt));
        }

        string revertedTo = redo
            ? System.Text.Encoding.UTF8.GetString(receipt.NewOperators)
            : System.Text.Encoding.UTF8.GetString(receipt.OldOperators);
        string appliedText = list[receipt.Offset].Text;
        list[receipt.Offset] = list[receipt.Offset] with { Text = revertedTo };
        Edited(pageIndex).Add($"{(redo ? "redo" : "undo")}:{appliedText}->{revertedTo}");
    }

    public ValueTask<IReadOnlyList<PdfPageObject>> ListObjectsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _objects.TryGetValue(pageIndex, out var list);
        return ValueTask.FromResult<IReadOnlyList<PdfPageObject>>(list?.ToArray() ?? Array.Empty<PdfPageObject>());
    }

    public async ValueTask<PdfTextEditReceipt> MoveResizeObjectAsync(
        int pageIndex, string objectId, PdfRect bounds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();

        if (!_objects.TryGetValue(pageIndex, out var list))
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        int idx = list.FindIndex(o => o.Id == objectId);
        if (idx < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        PdfPageObject before = list[idx];
        PdfPageObject after = before with { Bounds = bounds };

        byte[] desc = System.Text.Encoding.UTF8.GetBytes($"{after.Bounds.X0}\t{after.Bounds.Y0}\t{after.Bounds.X1}\t{after.Bounds.Y1}");
        var receipt = new PdfTextEditReceipt(1, 0, idx, CrawlBytes(before.Bounds).Length, desc.Length, CrawlBytes(before.Bounds), desc);
        list[idx] = after;
        _objectEdits[receipt] = (pageIndex, objectId, before, after);
        return receipt;
    }

    public async ValueTask<PdfTextEditReceipt> ReplaceObjectAsync(
        int pageIndex, string objectId, PdfObjectReplacement replacement, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();

        if (!_objects.TryGetValue(pageIndex, out var list))
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        int idx = list.FindIndex(o => o.Id == objectId);
        if (idx < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        PdfPageObject before = list[idx];
        PdfPageObject after = before with { Name = replacement.SourcePath };

        byte[] mark = System.Text.Encoding.UTF8.GetBytes(replacement.SourcePath);
        byte[] beforeBytes = CrawlBytes(before.Bounds);
        var receipt = new PdfTextEditReceipt(1, 0, idx, beforeBytes.Length, mark.Length, beforeBytes, mark);
        list[idx] = after;
        _objectEdits[receipt] = (pageIndex, objectId, before, after);
        return receipt;
    }

    private static byte[] CrawlBytes(PdfRect r)
        => System.Text.Encoding.UTF8.GetBytes($"{r.X0}\t{r.Y0}\t{r.X1}\t{r.Y1}");

    private List<string> Edited(int pageIndex)
    {
        if (!EditedTextByPage.TryGetValue(pageIndex, out var list))
        {
            list = new List<string>();
            EditedTextByPage[pageIndex] = list;
        }

        return list;
    }

    /// <summary>Seed annotations on a page for list/flatten tests.</summary>
    public void AddStoredAnnotation(int pageIndex, AnnotationType type, string? contents = null)
    {
        if (!_annotations.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfAnnotation>();
            _annotations[pageIndex] = list;
        }

        list.Add(new PdfAnnotation(type, 10, 10, 100, 50, contents));
    }

    public ValueTask<PdfDocumentInfo> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(new PdfDocumentInfo(_pageCount, Path.GetFileName(path)));
    }

    public ValueTask<PdfPageRegion> GetPageSizeAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(new PdfPageRegion(595, 842));
    }

    public async ValueTask<RenderedPdfPage> RenderPageToPngAsync(int pageIndex, float dpi, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        _renderCalls++;

        if (_maxCalls >= 0 && _renderCalls > _maxCalls)
        {
            throw new InvalidOperationException("Engine exhausted for this test.");
        }

        return new RenderedPdfPage
        {
            PngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 },
            WidthPixels = 595,
            HeightPixels = 842,
        };
    }

    public ValueTask<PdfOutline> GetOutlineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(Outline);
    }

    public ValueTask<PageText> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string text = OnPageText?.Invoke(pageIndex) ?? $"page {pageIndex} sample text";
        return ValueTask.FromResult(new PageText(pageIndex, text));
    }

    public async ValueTask<int> BuildPdfAsync(
        string outputPath,
        IReadOnlyList<PageBuildRef> pages,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pages is null || pages.Count == 0)
        {
            throw new ArgumentException("At least one page must be selected.", nameof(pages));
        }

        await Task.Yield();
        LastBuild = (outputPath, pages.ToArray());
        OnBuild?.Invoke();
        return pages.Count;
    }

    public ValueTask<IReadOnlyList<PdfAnnotation>> ListAnnotationsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _annotations.TryGetValue(pageIndex, out var list);
        return ValueTask.FromResult<IReadOnlyList<PdfAnnotation>>(list?.ToArray() ?? Array.Empty<PdfAnnotation>());
    }

    public async ValueTask AddAnnotationAsync(
        int pageIndex, AnnotBuildSpec annotation, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        if (!_annotations.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfAnnotation>();
            _annotations[pageIndex] = list;
        }

        list.Add(new PdfAnnotation(
            annotation.Type, annotation.X0, annotation.Y0, annotation.X1, annotation.Y1, annotation.Contents));
    }

    public async ValueTask FlattenAnnotationsAsync(
        int pageIndex, IReadOnlySet<AnnotationType> typesToFlatten, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        FlattenedPages.Add(pageIndex);
        if (_annotations.TryGetValue(pageIndex, out var list))
        {
            list.RemoveAll(a => typesToFlatten.Contains(a.Type));
        }
    }

    public async ValueTask SaveAsAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        LastSavePath = outputPath;
        await File.WriteAllTextAsync(outputPath, "saved", cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}