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

    /// <summary>Per-page AcroForm fields for FR-FORM-01 tests.</summary>
    private readonly Dictionary<int, List<PdfFormField>> _formFields = new();

    /// <summary>Whether <see cref="FlattenFormAsync"/> was called.</summary>
    public bool FormFlattened { get; private set; }

    /// <summary>Records each SetFormFieldValueAsync action as "page:idx:value", oldest first.</summary>
    public List<string> FormValueSet { get; } = new();

    /// <summary>Records each CreateFormFieldAsync action as "page:kind:name", oldest first.</summary>
    public List<string> FormFieldsCreated { get; } = new();

    /// <summary>Seeds a page's AcroForm fields.</summary>
    public void AddStoredFormField(int pageIndex, PdfFormField field)
    {
        if (!_formFields.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfFormField>();
            _formFields[pageIndex] = list;
        }

        list.Add(field);
    }

    /// <summary>The current form fields of a page (values reflect applied edits).</summary>
    public IReadOnlyList<PdfFormField> StoredFormFields(int pageIndex)
        => _formFields.TryGetValue(pageIndex, out var list) ? list.ToArray() : Array.Empty<PdfFormField>();

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

    public ValueTask<IReadOnlyList<PdfFormField>> ListFormFieldsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _formFields.TryGetValue(pageIndex, out var list);
        return ValueTask.FromResult<IReadOnlyList<PdfFormField>>(list?.ToArray() ?? Array.Empty<PdfFormField>());
    }

    public async ValueTask SetFormFieldValueAsync(
        int pageIndex, string fieldId, string value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();

        if (!_formFields.TryGetValue(pageIndex, out var list))
        {
            throw new ArgumentOutOfRangeException(nameof(fieldId));
        }

        int idx = list.FindIndex(f => f.Id == fieldId);
        if (idx < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldId));
        }

        if (list[idx].Kind == FormFieldKind.Signature || list[idx].Kind == FormFieldKind.Button)
        {
            throw new InvalidOperationException($"Cannot fill a {list[idx].Kind} field.");
        }

        list[idx] = list[idx] with { Value = value };
        FormValueSet.Add($"{pageIndex}:{fieldId}:{value}");
    }

    public async ValueTask CreateFormFieldAsync(
        int pageIndex, FormFieldSpec spec, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();

        if (spec.Kind != FormFieldKind.Text)
        {
            throw new NotSupportedException(
                $"Creating a {spec.Kind} form field is not supported; only {FormFieldKind.Text} fields can be created.");
        }

        if (!_formFields.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfFormField>();
            _formFields[pageIndex] = list;
        }

        string id = list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        list.Add(new PdfFormField(
            FormFieldKind.Text,
            id,
            spec.Name,
            spec.Bounds,
            string.Empty));

        FormFieldsCreated.Add($"{pageIndex}:{spec.Kind}:{spec.Name}");
    }

    public async ValueTask FlattenFormAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        FormFlattened = true;
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

    /// <summary>Per-page list of redaction marks, seats for the apply/undo model.</summary>
    private readonly Dictionary<int, List<PdfRect>> _redactions = new();

    /// <summary>Per-page snapshot of the marks present at apply time, for restore.</summary>
    private readonly Dictionary<int, PdfRect[]> _restoredAnchor = new();

    /// <summary>Records each AddRedactionAsync action as "page:x0:y0:x1:y1", oldest first.</summary>
    public List<string> RedactionsMarked { get; } = new();

    /// <summary>Pages on which <see cref="ApplyRedactionsAsync"/> was called (in call order).</summary>
    public List<int> RedactedPages { get; } = new();

    /// <summary>The last options handed to <see cref="ApplyRedactionsAsync"/>, or null if default.</summary>
    public RedactionOptions? LastRedactionOptions { get; private set; }

    /// <summary>Snapshot paths passed to <see cref="RestoreSnapshotAsync"/>, in call order.</summary>
    public List<string> RestoredSnapshots { get; } = new();

    /// <summary>Seeds a redaction mark on a page (as if pre-applied), for undo tests.</summary>
    public void AddStoredRedaction(int pageIndex, PdfRect bounds)
    {
        if (!_redactions.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfRect>();
            _redactions[pageIndex] = list;
        }

        list.Add(bounds);
    }

    /// <summary>The current redaction marks of a page (applied marks are removed).</summary>
    public IReadOnlyList<PdfRect> StoredRedactions(int pageIndex)
        => _redactions.TryGetValue(pageIndex, out var list) ? list.ToArray() : Array.Empty<PdfRect>();

    public async ValueTask AddRedactionAsync(
        int pageIndex, PdfRect bounds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        if (!_redactions.TryGetValue(pageIndex, out var list))
        {
            list = new List<PdfRect>();
            _redactions[pageIndex] = list;
        }

        list.Add(bounds);
        RedactionsMarked.Add($"{pageIndex}:{bounds.X0}:{bounds.Y0}:{bounds.X1}:{bounds.Y1}");
    }

    public async ValueTask<int> ApplyRedactionsAsync(
        int pageIndex, RedactionOptions? options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        LastRedactionOptions = options;
        if (_redactions.TryGetValue(pageIndex, out var list))
        {
            // Stash the marks so a snapshot restore can put the page back to its
            // pre-apply state (the real engine reopens the snapshot, which still
            // carries the /Redact annotations).
            _redactions.Remove(pageIndex);
            _restoredAnchor[pageIndex] = list.ToArray();
            RedactedPages.Add(pageIndex);
            return list.Count;
        }

        RedactedPages.Add(pageIndex);
        return 0;
    }

    public async ValueTask RestoreSnapshotAsync(
        string snapshotPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        RestoredSnapshots.Add(snapshotPath);

        // Emulate swapping to the pre-apply document: pages apply had redacted
        // come back with their marks intact (and their content restored).
        foreach ((int page, PdfRect[] marks) in _restoredAnchor)
        {
            _redactions[page] = new List<PdfRect>(marks);
        }
    }

    /// <summary>The receipts of each <see cref="OcrToPdfAsync"/> call, oldest first.</summary>
    public List<OcrResult> OcrOutputs { get; } = new();

    /// <summary>The most recent OCR job (output path, options) handed to
    /// <see cref="OcrToPdfAsync"/>, for FR-OCR-01 assertions, or null if none.</summary>
    public (string OutputPath, OcrOptions? Options)? LastOcr { get; private set; }

    /// <summary>Optional hook to simulate a native OCR failure; throw to fail.</summary>
    public Action<(string OutputPath, OcrOptions? Options)>? OnOcr { get; set; }

    /// <summary>Writes a fake searchable-PDF artifact and records the job (FR-OCR-01).</summary>
    public async ValueTask<OcrResult> OcrToPdfAsync(
        string outputPath, OcrOptions? options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        (string OutputPath, OcrOptions? Options) job = (outputPath, options);
        OnOcr?.Invoke(job);
        await File.WriteAllTextAsync(outputPath, "searchable", cancellationToken);
        LastOcr = job;
        var result = new OcrResult(
            _pageCount,
            outputPath,
            string.IsNullOrWhiteSpace(options?.Language) ? "eng" : options.Language!,
            options?.DataDirectory ?? string.Empty);
        OcrOutputs.Add(result);
        return result;
    }

    /// <summary>Writes a fake DOCX artifact (ZIP magic) and records the job (FR-OCR-03).</summary>
    public async ValueTask<OcrResult> OcrToDocxAsync(
        string outputPath, OcrOptions? options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        byte[] zipMagic = { 0x50, 0x4B, 0x03, 0x04 }; // PK\x03\x04
        await File.WriteAllBytesAsync(outputPath, zipMagic, cancellationToken);
        LastOcr = (outputPath, options);
        var result = new OcrResult(
            _pageCount,
            outputPath,
            string.IsNullOrWhiteSpace(options?.Language) ? "eng" : options.Language!,
            options?.DataDirectory ?? string.Empty);
        OcrOutputs.Add(result);
        return result;
    }

    /// <summary>Paths of every <see cref="SaveEncryptedAsync"/> artifact, oldest first (FR-SEC-01).</summary>
    public List<string> EncryptedOutputs { get; } = new();

    /// <summary>The most recent protect job (output path, options) handed to
    /// <see cref="SaveEncryptedAsync"/>, for FR-SEC-01 assertions, or null if none.</summary>
    public (string OutputPath, PdfProtectionOptions? Options)? LastEncrypt { get; private set; }

    /// <summary>Optional hook to simulate a native protect failure; throw to fail.</summary>
    public Action<(string OutputPath, PdfProtectionOptions? Options)>? OnEncrypt { get; set; }

    /// <summary>Optional hook for <see cref="AuthenticateAsync"/> results; defaults to true.</summary>
    public Func<bool>? OnAuthenticate { get; set; }

    /// <summary>Writes a fake encrypted-PDF artifact and records the job (FR-SEC-01).</summary>
    public async ValueTask SaveEncryptedAsync(
        string outputPath, PdfProtectionOptions? options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        (string OutputPath, PdfProtectionOptions? Options) job = (outputPath, options);
        OnEncrypt?.Invoke(job);
        await File.WriteAllTextAsync(outputPath, "encrypted", cancellationToken);
        LastEncrypt = job;
        EncryptedOutputs.Add(outputPath);
    }

    /// <summary>Answers <see cref="AuthenticateAsync"/> via <see cref="OnAuthenticate"/> (default true).</summary>
    public async ValueTask<bool> AuthenticateAsync(
        string password, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        return OnAuthenticate?.Invoke() ?? true;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}