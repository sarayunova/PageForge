// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop.Native;

namespace PageForge.MuPdfInterop;

/// <summary>
/// IPdfEngine implemented over MuPDF (AGPLv3, Artifex Software) through the
/// PageForge shim ABI. One instance == one pf_context == one serialized access
/// lane; the caller or a wrapping view-model must not share it across threads.
/// </summary>
public sealed class MuPdfEngine : IPdfEngine
{
    private readonly nint _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private nint _document;
    private string? _displayName;
    private string? _openPath;

    private static readonly Encoding JobUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private MuPdfEngine(nint context) => _context = context;

    public static MuPdfEngine Create()
    {
        if (MuPdfShimBindings.pf_create_context(out nint context, out nint error) != MuPdfShimBindings.PfOk)
        {
            throw new InvalidOperationException(
                $"Failed to create the MuPDF context: {Marshal.PtrToStringUTF8(error) ?? "unknown error"}");
        }

        return new MuPdfEngine(context);
    }

    public ValueTask<PdfDocumentInfo> OpenAsync(string path, CancellationToken cancellationToken = default)
        => OpenCoreAsync(path, cancellationToken);

    private async ValueTask<PdfDocumentInfo> OpenCoreAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("A document path is required.", nameof(path));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_document != nint.Zero)
            {
                throw new InvalidOperationException("This engine already has a document open.");
            }

            byte[] pathUtf8 = Utf8Z(path);
            if (MuPdfShimBindings.pf_open_document(_context, pathUtf8, out nint document) != MuPdfShimBindings.PfOk)
            {
                throw new InvalidOperationException($"Failed to open '{path}': {LastError()}");
            }

            if (MuPdfShimBindings.pf_page_count(_context, document, out int count) != MuPdfShimBindings.PfOk)
            {
                MuPdfShimBindings.pf_close_document(_context, document);
                throw new InvalidOperationException($"Failed to read page count for '{path}': {LastError()}");
            }

            _document = document;
            _displayName = Path.GetFileName(path);
            _openPath = Path.GetFullPath(path);
            return new PdfDocumentInfo(count, _displayName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PdfPageRegion> GetPageSizeAsync(int pageIndex, CancellationToken cancellationToken = default)
        => GetPageSizeCoreAsync(pageIndex, cancellationToken);

    private async ValueTask<PdfPageRegion> GetPageSizeCoreAsync(int pageIndex, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();
            return GetPageSizeLocked(pageIndex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PdfPageRegion GetPageSizeLocked(int pageIndex)
    {
        if (MuPdfShimBindings.pf_page_size(_context, _document, pageIndex, out float w, out float h)
            != MuPdfShimBindings.PfOk)
        {
            throw new InvalidOperationException($"Failed to read the size of page {pageIndex}: {LastError()}");
        }

        return new PdfPageRegion(w, h);
    }

    public ValueTask<RenderedPdfPage> RenderPageToPngAsync(int pageIndex, float dpi, CancellationToken cancellationToken = default)
        => RenderCoreAsync(pageIndex, dpi, cancellationToken);

    private async ValueTask<RenderedPdfPage> RenderCoreAsync(int pageIndex, float dpi, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (dpi < 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), "dpi must be >= 1.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string outputPath = Path.Combine(Path.GetTempPath(), $"pageforge-render-{Guid.NewGuid():N}.png");
            try
            {
                byte[] outPathUtf8 = Utf8Z(outputPath);
                if (MuPdfShimBindings.pf_render_page_to_png(_context, _document, pageIndex, dpi, outPathUtf8)
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to render page {pageIndex} at {dpi} DPI: {LastError()}");
                }

                byte[] pngBytes = await File.ReadAllBytesAsync(outputPath, ct).ConfigureAwait(false);
                PdfPageRegion region = GetPageSizeLocked(pageIndex);

                int widthPx = (int)Math.Round(region.WidthPt * dpi / 72.0);
                int heightPx = (int)Math.Round(region.HeightPt * dpi / 72.0);

                return new RenderedPdfPage { PngBytes = pngBytes, WidthPixels = widthPx, HeightPixels = heightPx };
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PageText> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default)
        => GetPageTextCoreAsync(pageIndex, cancellationToken);

    private async ValueTask<PageText> GetPageTextCoreAsync(int pageIndex, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string outputPath = Path.Combine(Path.GetTempPath(), $"pageforge-text-{Guid.NewGuid():N}.txt");
            try
            {
                byte[] outPathUtf8 = Utf8Z(outputPath);
                if (MuPdfShimBindings.pf_page_text(_context, _document, pageIndex, outPathUtf8)
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to extract text from page {pageIndex}: {LastError()}");
                }

                string text = await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false);
                return new PageText(pageIndex, text);
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PdfOutline> GetOutlineAsync(CancellationToken cancellationToken = default)
        => GetOutlineCoreAsync(cancellationToken);

    private async ValueTask<PdfOutline> GetOutlineCoreAsync(CancellationToken ct)
    {
        IReadOnlyList<OutlineItem> items = Array.Empty<OutlineItem>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string outputPath = Path.Combine(Path.GetTempPath(), $"pageforge-outline-{Guid.NewGuid():N}.txt");
            try
            {
                byte[] outPathUtf8 = Utf8Z(outputPath);
                if (MuPdfShimBindings.pf_load_outline(_context, _document, outPathUtf8)
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException($"Failed to load the document outline: {LastError()}");
                }

                if (File.Exists(outputPath))
                {
                    string[] lines = await File.ReadAllLinesAsync(outputPath, ct).ConfigureAwait(false);
                    items = PdfOutlineParser.Parse(lines).Items;
                }
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        }
        finally
        {
            _gate.Release();
        }

        return new PdfOutline(items);
    }

    public ValueTask<int> BuildPdfAsync(
        string outputPath,
        IReadOnlyList<PageBuildRef> pages,
        CancellationToken cancellationToken = default)
        => BuildPdfCoreAsync(outputPath, pages, cancellationToken);

    private async ValueTask<int> BuildPdfCoreAsync(string outputPath, IReadOnlyList<PageBuildRef> pages, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        }

        if (pages is null || pages.Count == 0)
        {
            throw new ArgumentException("At least one page must be selected.", nameof(pages));
        }

        // Normalize each distinct source path and assign it a sequential id.
        var idByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PageBuildRef r in pages)
        {
            RequireValidSource(r, pages);
            string fullPath = Path.GetFullPath(r.SourcePath);
            if (!idByPath.ContainsKey(fullPath))
            {
                idByPath[fullPath] = idByPath.Count;
            }
        }

        var job = new StringBuilder();
        job.Append("V\t1").Append('\n');
        foreach ((string path, int id) in idByPath)
        {
            job.Append("S\t").Append(id).Append('\t').Append(path).Append('\n');
        }

        foreach (PageBuildRef r in pages)
        {
            RequireValidSource(r, pages);
            string fullPath = Path.GetFullPath(r.SourcePath);
            int rot = ((r.RotationQuarterTurns % 4) + 4) % 4;
            job.Append("P\t").Append(idByPath[fullPath]).Append('\t')
               .Append(r.PageIndex).Append('\t').Append(rot).Append('\n');
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string outputFull = Path.GetFullPath(outputPath);
            string jobPath = Path.Combine(Path.GetTempPath(), $"pageforge-build-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(jobPath, job.ToString(), JobUtf8, ct).ConfigureAwait(false);

                if (MuPdfShimBindings.pf_build_pdf(_context, Utf8Z(jobPath), Utf8Z(outputFull))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to build '{Path.GetFileName(outputPath)}': {LastError()}");
                }

                if (!File.Exists(outputFull))
                {
                    throw new InvalidOperationException(
                        $"The page organizer produced no output file at '{outputFull}'.");
                }
            }
            finally
            {
                TryDeleteFile(jobPath);
            }
        }
        finally
        {
            _gate.Release();
        }

        return pages.Count;
    }

    public ValueTask<IReadOnlyList<PdfAnnotation>> ListAnnotationsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
        => ListAnnotationsCoreAsync(pageIndex, cancellationToken);

    private async ValueTask<IReadOnlyList<PdfAnnotation>> ListAnnotationsCoreAsync(int pageIndex, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string outputPath = Path.Combine(Path.GetTempPath(), $"pageforge-annot-{Guid.NewGuid():N}.txt");
            try
            {
                byte[] outPathUtf8 = Utf8Z(outputPath);
                if (MuPdfShimBindings.pf_list_annotations(_context, _document, pageIndex, outPathUtf8)
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to list annotations on page {pageIndex}: {LastError()}");
                }

                string[] lines = await File.ReadAllLinesAsync(outputPath, ct).ConfigureAwait(false);
                return AnnotationListParser.Parse(lines);
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask AddAnnotationAsync(
        int pageIndex, AnnotBuildSpec annotation, CancellationToken cancellationToken = default)
        => AddAnnotationCoreAsync(pageIndex, annotation, cancellationToken);

    private async ValueTask AddAnnotationCoreAsync(int pageIndex, AnnotBuildSpec annotation, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentNullException.ThrowIfNull(annotation);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string specPath = Path.Combine(Path.GetTempPath(), $"pageforge-annot-spec-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(specPath, BuildSpec(annotation), JobUtf8, ct).ConfigureAwait(false);
                if (MuPdfShimBindings.pf_add_annotation(_context, _document, pageIndex, Utf8Z(specPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to add a {annotation.Type} annotation on page {pageIndex}: {LastError()}");
                }
            }
            finally
            {
                TryDeleteFile(specPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask FlattenAnnotationsAsync(
        int pageIndex, IReadOnlySet<AnnotationType> typesToFlatten, CancellationToken cancellationToken = default)
        => FlattenAnnotationsCoreAsync(pageIndex, typesToFlatten, cancellationToken);

    private async ValueTask FlattenAnnotationsCoreAsync(int pageIndex, IReadOnlySet<AnnotationType> typesToFlatten, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentNullException.ThrowIfNull(typesToFlatten);

        if (typesToFlatten.Count == 0)
        {
            return; // Nothing selected: nothing to flatten (a no-op).
        }

        string typeList = string.Join(",", typesToFlatten.OrderBy(TypeName));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();
            if (MuPdfShimBindings.pf_flatten_annotations(_context, _document, pageIndex, Utf8Z(typeList))
                != MuPdfShimBindings.PfOk)
            {
                throw new InvalidOperationException(
                    $"Failed to flatten annotations on page {pageIndex}: {LastError()}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask SaveAsAsync(string outputPath, CancellationToken cancellationToken = default)
        => SaveAsCoreAsync(outputPath, cancellationToken);

    private async ValueTask SaveAsCoreAsync(string outputPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();
            if (MuPdfShimBindings.pf_save_document(_context, _document, Utf8Z(Path.GetFullPath(outputPath)))
                != MuPdfShimBindings.PfOk)
            {
                throw new InvalidOperationException($"Failed to save '{Path.GetFileName(outputPath)}': {LastError()}");
            }

            if (!File.Exists(Path.GetFullPath(outputPath)))
            {
                throw new InvalidOperationException($"Save produced no file at '{Path.GetFullPath(outputPath)}'.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<IReadOnlyList<PdfTextRun>> ListTextRunsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
        => ListTextRunsCoreAsync(pageIndex, cancellationToken);

    private async ValueTask<IReadOnlyList<PdfTextRun>> ListTextRunsCoreAsync(int pageIndex, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string outputPath = Path.Combine(Path.GetTempPath(), $"pageforge-runs-{Guid.NewGuid():N}.txt");
            try
            {
                if (MuPdfShimBindings.pf_list_text_runs(_context, _document, pageIndex, Utf8Z(outputPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to list the text runs of page {pageIndex}: {LastError()}");
                }

                string[] lines = await File.ReadAllLinesAsync(outputPath, ct).ConfigureAwait(false);
                return TextRunListParser.Parse(lines);
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PdfTextEditReceipt> RewriteTextRunAsync(
        int pageIndex, int runIndex, string newText, CancellationToken cancellationToken = default)
        => RewriteTextRunCoreAsync(pageIndex, runIndex, newText, cancellationToken);

    private async ValueTask<PdfTextEditReceipt> RewriteTextRunCoreAsync(
        int pageIndex, int runIndex, string newText, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (runIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(newText);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string newTextPath = Path.Combine(Path.GetTempPath(), $"pageforge-newtext-{Guid.NewGuid():N}.txt");
            string receiptPath = Path.Combine(Path.GetTempPath(), $"pageforge-receipt-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(newTextPath, newText, JobUtf8, ct).ConfigureAwait(false);
                if (MuPdfShimBindings.pf_rewrite_text_run(
                        _context, _document, pageIndex, runIndex, Utf8Z(newTextPath), Utf8Z(receiptPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to rewrite the text of run {runIndex} on page {pageIndex}: {LastError()}");
                }

                if (!File.Exists(receiptPath))
                {
                    throw new InvalidOperationException(
                        $"Rewriting run {runIndex} produced no undo receipt for page {pageIndex}.");
                }

                string[] lines = await File.ReadAllLinesAsync(receiptPath, ct).ConfigureAwait(false);
                return TextEditReceiptSerializer.Parse(lines);
            }
            finally
            {
                TryDeleteFile(newTextPath);
                TryDeleteFile(receiptPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask RevertTextEditAsync(
        int pageIndex, PdfTextEditReceipt receipt, bool redo, CancellationToken cancellationToken = default)
        => RevertTextEditCoreAsync(pageIndex, receipt, redo, cancellationToken);

    private async ValueTask RevertTextEditCoreAsync(
        int pageIndex, PdfTextEditReceipt receipt, bool redo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string receiptPath = Path.Combine(Path.GetTempPath(), $"pageforge-revert-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(receiptPath, TextEditReceiptSerializer.ToTsv(receipt), JobUtf8, ct)
                    .ConfigureAwait(false);

                if (MuPdfShimBindings.pf_revert_text_rewrite(
                        _context, _document, pageIndex, Utf8Z(receiptPath), redo ? 1 : 0)
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to {(redo ? "redo" : "undo")} the text edit on page {pageIndex}: {LastError()}");
                }
            }
            finally
            {
                TryDeleteFile(receiptPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<IReadOnlyList<PdfPageObject>> ListObjectsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
        => ListObjectsCoreAsync(pageIndex, cancellationToken);

    private async ValueTask<IReadOnlyList<PdfPageObject>> ListObjectsCoreAsync(int pageIndex, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string outputPath = Path.Combine(Path.GetTempPath(), $"pageforge-objects-{Guid.NewGuid():N}.txt");
            try
            {
                if (MuPdfShimBindings.pf_list_objects(_context, _document, pageIndex, Utf8Z(outputPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to list the objects of page {pageIndex}: {LastError()}");
                }

                string[] lines = await File.ReadAllLinesAsync(outputPath, ct).ConfigureAwait(false);
                return ObjectListParser.Parse(lines);
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PdfTextEditReceipt> MoveResizeObjectAsync(
        int pageIndex, string objectId, PdfRect bounds, CancellationToken cancellationToken = default)
        => MoveResizeObjectCoreAsync(pageIndex, objectId, bounds, cancellationToken);

    private async ValueTask<PdfTextEditReceipt> MoveResizeObjectCoreAsync(
        int pageIndex, string objectId, PdfRect bounds, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(objectId);
        if (!int.TryParse(objectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int objectIndex)
            || objectIndex < 0)
        {
            throw new ArgumentException($"The object id '{objectId}' does not name a known object.", nameof(objectId));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string receiptPath = Path.Combine(Path.GetTempPath(), $"pageforge-objreceipt-{Guid.NewGuid():N}.txt");
            try
            {
                if (MuPdfShimBindings.pf_move_resize_object(
                        _context, _document, pageIndex, objectIndex,
                        bounds.X0, bounds.Y0, bounds.X1, bounds.Y1, Utf8Z(receiptPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to move/resize object {objectId} on page {pageIndex}: {LastError()}");
                }

                if (!File.Exists(receiptPath))
                {
                    throw new InvalidOperationException(
                        $"Moving/resizing object {objectId} produced no undo receipt for page {pageIndex}.");
                }

                string[] lines = await File.ReadAllLinesAsync(receiptPath, ct).ConfigureAwait(false);
                return TextEditReceiptSerializer.Parse(lines);
            }
            finally
            {
                TryDeleteFile(receiptPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PdfTextEditReceipt> ReplaceObjectAsync(
        int pageIndex, string objectId, PdfObjectReplacement replacement, CancellationToken cancellationToken = default)
        => ReplaceObjectCoreAsync(pageIndex, objectId, replacement, cancellationToken);

    private async ValueTask<PdfTextEditReceipt> ReplaceObjectCoreAsync(
        int pageIndex, string objectId, PdfObjectReplacement replacement, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentException.ThrowIfNullOrEmpty(replacement.SourcePath);
        ArgumentException.ThrowIfNullOrEmpty(replacement.Format);

        if (!int.TryParse(objectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int objectIndex)
            || objectIndex < 0)
        {
            throw new ArgumentException($"The object id '{objectId}' does not name a known object.", nameof(objectId));
        }

        if (!File.Exists(replacement.SourcePath))
        {
            throw new FileNotFoundException("The replacement image does not exist.", replacement.SourcePath);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string receiptPath = Path.Combine(Path.GetTempPath(), $"pageforge-replreceipt-{Guid.NewGuid():N}.txt");
            try
            {
                if (MuPdfShimBindings.pf_replace_object(
                        _context, _document, pageIndex, objectIndex,
                        Utf8Z(replacement.SourcePath), Utf8Z(receiptPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to replace object {objectId} on page {pageIndex}: {LastError()}");
                }

                if (!File.Exists(receiptPath))
                {
                    throw new InvalidOperationException(
                        $"Replacing object {objectId} produced no undo receipt for page {pageIndex}.");
                }

                string[] lines = await File.ReadAllLinesAsync(receiptPath, ct).ConfigureAwait(false);
                return TextEditReceiptSerializer.Parse(lines);
            }
            finally
            {
                TryDeleteFile(receiptPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<IReadOnlyList<PdfFormField>> ListFormFieldsAsync(
        int pageIndex, CancellationToken cancellationToken = default)
        => ListFormFieldsCoreAsync(pageIndex, cancellationToken);

    private async ValueTask<IReadOnlyList<PdfFormField>> ListFormFieldsCoreAsync(int pageIndex, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string outputPath = Path.Combine(Path.GetTempPath(), $"pageforge-widgets-{Guid.NewGuid():N}.txt");
            try
            {
                if (MuPdfShimBindings.pf_list_widgets(_context, _document, pageIndex, Utf8Z(outputPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to list the form fields of page {pageIndex}: {LastError()}");
                }

                string[] lines = await File.ReadAllLinesAsync(outputPath, ct).ConfigureAwait(false);
                return WidgetListParser.Parse(lines);
            }
            finally
            {
                TryDeleteFile(outputPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask SetFormFieldValueAsync(
        int pageIndex, string fieldId, string value, CancellationToken cancellationToken = default)
        => SetFormFieldValueCoreAsync(pageIndex, fieldId, value, cancellationToken);

    private async ValueTask SetFormFieldValueCoreAsync(
        int pageIndex, string fieldId, string value, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(fieldId);
        ArgumentNullException.ThrowIfNull(value);

        if (!int.TryParse(fieldId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int widgetIndex)
            || widgetIndex < 0)
        {
            throw new ArgumentException($"The form field id '{fieldId}' does not name a known field.", nameof(fieldId));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string valuePath = Path.Combine(Path.GetTempPath(), $"pageforge-widgetval-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(valuePath, value, ct).ConfigureAwait(false);

                if (MuPdfShimBindings.pf_set_widget_value(
                        _context, _document, pageIndex, widgetIndex, Utf8Z(valuePath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to set form field {fieldId} on page {pageIndex}: {LastError()}");
                }
            }
            finally
            {
                TryDeleteFile(valuePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask CreateFormFieldAsync(
        int pageIndex, FormFieldSpec spec, CancellationToken cancellationToken = default)
        => CreateFormFieldCoreAsync(pageIndex, spec, cancellationToken);

    private async ValueTask CreateFormFieldCoreAsync(
        int pageIndex, FormFieldSpec spec, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(spec.Name);

        if (spec.Kind != FormFieldKind.Text)
        {
            throw new NotSupportedException(
                $"Creating a {spec.Kind} form field is not supported; only {FormFieldKind.Text} fields can be created.");
        }

        if (spec.MaxLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "MaxLength cannot be negative.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string specPath = Path.Combine(Path.GetTempPath(), $"pageforge-field-spec-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(specPath, BuildFormFieldSpec(spec), JobUtf8, ct).ConfigureAwait(false);

                if (MuPdfShimBindings.pf_create_field(_context, _document, pageIndex, Utf8Z(specPath))
                    != MuPdfShimBindings.PfOk)
                {
                    throw new InvalidOperationException(
                        $"Failed to create a form field on page {pageIndex}: {LastError()}");
                }
            }
            finally
            {
                TryDeleteFile(specPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildFormFieldSpec(FormFieldSpec s)
    {
        // Matches the mupdf_shim.c pf_create_field spec format.
        var sb = new StringBuilder();
        sb.Append("K\tTXT\n");
        sb.Append("N\t").Append(s.Name).Append('\n');
        sb.Append("R\t").Append(s.Bounds.X0).Append('\t').Append(s.Bounds.Y0)
          .Append('\t').Append(s.Bounds.X1).Append('\t').Append(s.Bounds.Y1).Append('\n');
        if (s.Flags != 0)
        {
            sb.Append("F\t").Append(s.Flags).Append('\n');
        }
        if (s.MaxLength > 0)
        {
            sb.Append("M\t").Append(s.MaxLength).Append('\n');
        }
        if (s.Quadding != FormFieldJustification.Left)
        {
            sb.Append("Q\t").Append((int)s.Quadding).Append('\n');
        }
        if (s.BorderWidth != 1 && s.BorderWidth > 0)
        {
            sb.Append("W\t").Append(s.BorderWidth).Append('\n');
        }
        return sb.ToString();
    }

    public ValueTask FlattenFormAsync(CancellationToken cancellationToken = default)
        => FlattenFormCoreAsync(cancellationToken);

    private async ValueTask FlattenFormCoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            if (MuPdfShimBindings.pf_bake_widgets(_context, _document)
                != MuPdfShimBindings.PfOk)
            {
                throw new InvalidOperationException($"Failed to flatten the form: {LastError()}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask AddRedactionAsync(
        int pageIndex, PdfRect bounds, CancellationToken cancellationToken = default)
        => AddRedactionCoreAsync(pageIndex, bounds, cancellationToken);

    private async ValueTask AddRedactionCoreAsync(int pageIndex, PdfRect bounds, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();
            if (MuPdfShimBindings.pf_add_redact(
                    _context, _document, pageIndex,
                    bounds.X0, bounds.Y0, bounds.X1, bounds.Y1)
                != MuPdfShimBindings.PfOk)
            {
                throw new InvalidOperationException(
                    $"Failed to mark a redaction region on page {pageIndex}: {LastError()}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<int> ApplyRedactionsAsync(
        int pageIndex, RedactionOptions? options, CancellationToken cancellationToken = default)
        => ApplyRedactionsCoreAsync(pageIndex, options, cancellationToken);

    private async ValueTask<int> ApplyRedactionsCoreAsync(
        int pageIndex, RedactionOptions? options, CancellationToken ct)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? optsPath = null;
        try
        {
            RequireDocument();

            // Only write an options file when non-defaults are requested; the
            // native side uses secure defaults when the path is NULL.
            if (options is not null && !IsDefault(options))
            {
                optsPath = Path.Combine(Path.GetTempPath(), $"pageforge-redact-opts-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(optsPath, BuildRedactionOpts(options), JobUtf8, ct).ConfigureAwait(false);
            }

            if (MuPdfShimBindings.pf_apply_redactions(
                    _context, _document, pageIndex,
                    optsPath is null ? null : Utf8Z(optsPath),
                    out int appliedCount)
                != MuPdfShimBindings.PfOk)
            {
                throw new InvalidOperationException(
                    $"Failed to apply redactions on page {pageIndex}: {LastError()}");
            }

            return appliedCount;
        }
        finally
        {
            if (optsPath is not null)
            {
                TryDeleteFile(optsPath);
            }

            _gate.Release();
        }
    }

    private static bool IsDefault(RedactionOptions options)
        => options.BlackBox && options.ImageMethod == RedactionImageMethod.Remove
           && options.LineArtMethod == RedactionLineArtMethod.RemoveIfCovered
           && options.TextMethod == RedactionTextMethod.Remove;

    private static string BuildRedactionOpts(RedactionOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("B\t").Append(options.BlackBox ? 1 : 0).Append('\n');
        sb.Append("I\t").Append((int)options.ImageMethod).Append('\n');
        sb.Append("L\t").Append((int)options.LineArtMethod).Append('\n');
        sb.Append("T\t").Append((int)options.TextMethod).Append('\n');
        return sb.ToString();
    }

    public ValueTask RestoreSnapshotAsync(
        string snapshotPath, CancellationToken cancellationToken = default)
        => RestoreSnapshotCoreAsync(snapshotPath, cancellationToken);

    private async ValueTask RestoreSnapshotCoreAsync(string snapshotPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        if (!File.Exists(snapshotPath))
        {
            throw new FileNotFoundException("The snapshot document does not exist.", snapshotPath);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            // Swap the in-memory document: close the current one (the redacted
            // state) and reopen the snapshot (the pre-redaction state). The
            // engine identity/paths stay, so a later SaveAs still targets the
            // user's chosen file.
            nint replacement = nint.Zero;
            if (MuPdfShimBindings.pf_open_document(_context, Utf8Z(Path.GetFullPath(snapshotPath)), out replacement)
                != MuPdfShimBindings.PfOk)
            {
                throw new InvalidOperationException(
                    $"Failed to restore the snapshot '{Path.GetFileName(snapshotPath)}': {LastError()}");
            }

            MuPdfShimBindings.pf_close_document(_context, _document);
            _document = replacement;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<OcrResult> OcrToPdfAsync(
        string outputPath, OcrOptions? options, CancellationToken cancellationToken = default)
        => OcrToPdfCoreAsync(outputPath, options, cancellationToken);

    private async ValueTask<OcrResult> OcrToPdfCoreAsync(
        string outputPath, OcrOptions? options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDocument();

            string language = string.IsNullOrWhiteSpace(options?.Language) ? "eng" : options.Language!;
            string? dataDirectory = ResolveOcrDataDirectory(options?.DataDirectory);

            if (MuPdfShimBindings.pf_ocr_pdf(
                    _context, _document,
                    Utf8Z(Path.GetFullPath(outputPath)),
                    Utf8Z(language),
                    dataDirectory is null ? null : Utf8Z(dataDirectory),
                    out int pageCount)
                != MuPdfShimBindings.PfOk)
            {
                TryDeleteFile(Path.GetFullPath(outputPath));
                throw new InvalidOperationException($"Failed to run OCR: {LastError()}");
            }

            return new OcrResult(pageCount, Path.GetFullPath(outputPath), language, dataDirectory ?? string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the first existing directory able to supply
    /// "&lt;language&gt;.traineddata", or null so the native side falls back to
    /// its own TESSDATA_PREFIX/cwd search. Candidate order: explicit option,
    /// PF_TESSDATA_DIR, app-local tessdata, native build output of a dev
    /// checkout.
    /// </summary>
    private static string? ResolveOcrDataDirectory(string? configured)
    {
        string?[] candidates = configured is null
            ? new[]
            {
                Environment.GetEnvironmentVariable("PF_TESSDATA_DIR"),
                Path.Combine(AppContext.BaseDirectory, "tessdata"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "out", "tessdata"),
            }
            : new[] { configured };

        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string BuildSpec(AnnotBuildSpec a)
    {
        var spec = new StringBuilder();
        spec.Append("T\t").Append(TypeName(a.Type)).Append('\n');
        spec.Append("R\t").Append(a.X0).Append('\t').Append(a.Y0).Append('\t').Append(a.X1).Append('\t').Append(a.Y1).Append('\n');

        if (!string.IsNullOrEmpty(a.Contents))
        {
            spec.Append("C\t").Append(a.Contents).Append('\n');
        }

        if (a.Color is { } c)
        {
            spec.Append("O\t").Append(c.R).Append('\t').Append(c.G).Append('\t').Append(c.B).Append('\n');
        }

        if (a.Opacity is { } o)
        {
            spec.Append("P\t").Append(o).Append('\n');
        }

        if (a.Quads is not null)
        {
            foreach (PdfQuad q in a.Quads)
            {
                spec.Append("Q\t").Append(q.LowerLeft.X).Append('\t').Append(q.LowerLeft.Y).Append('\t')
                    .Append(q.UpperRight.X).Append('\t').Append(q.UpperRight.Y).Append('\n');
            }
        }

        if (a.InkPoints is not null)
        {
            foreach (PdfPoint p in a.InkPoints)
            {
                spec.Append("I\t").Append(p.X).Append('\t').Append(p.Y).Append('\n');
            }
        }

        return spec.ToString();
    }

    private static string TypeName(AnnotationType type) => type switch
    {
        AnnotationType.Highlight => "Highlight",
        AnnotationType.Underline => "Underline",
        AnnotationType.StrikeOut => "StrikeOut",
        AnnotationType.Ink => "Ink",
        AnnotationType.Text => "Text",
        AnnotationType.Square => "Square",
        AnnotationType.Circle => "Circle",
        AnnotationType.Stamp => "Stamp",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static void RequireValidSource(PageBuildRef r, IReadOnlyList<PageBuildRef> pages)
    {
        if (r is null)
        {
            throw new ArgumentException("A page entry is null.", nameof(pages));
        }

        if (string.IsNullOrEmpty(r.SourcePath))
        {
            throw new ArgumentException("A page source path is required.", nameof(pages));
        }

        if (r.PageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pages), "Page indexes must be non-negative.");
        }
    }

    private void RequireDocument()
    {
        if (_document == nint.Zero)
        {
            throw new InvalidOperationException("No document is open. Call OpenAsync first.");
        }
    }

    private static string LastError()
    {
        IntPtr ptr = MuPdfShimBindings.pf_last_error();
        return ptr == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(ptr) ?? "unknown error";
    }

    private static byte[] Utf8Z(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var terminated = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, terminated, 0, bytes.Length);
        return terminated;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp render; never fail the render for it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_document != nint.Zero)
            {
                MuPdfShimBindings.pf_close_document(_context, _document);
                _document = nint.Zero;
            }

            MuPdfShimBindings.pf_destroy_context(_context);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}