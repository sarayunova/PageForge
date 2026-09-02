// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

public sealed record PdfDocumentInfo(int PageCount, string DisplayName);

public sealed record PdfPageRegion(double WidthPt, double HeightPt);

/// <summary>
/// One page reference in a FR-PAGE build job: a page from a named source PDF,
/// optionally rotated clockwise. Composition of these references (with the
/// ability to repeat, omit, reorder, and combine sources) expresses every page
/// organization operation: merge, split, insert, delete, rotate, reorder, and
/// extract.
/// </summary>
public sealed record PageBuildRef(string SourcePath, int PageIndex, int RotationQuarterTurns = 0);

/// <summary>
/// The single engine seam of PageForge. The desktop app depends only on this
/// contract; MuPDF is one implementation (PageForge.MuPdfInterop) and unit
/// tests substitute fakes.
///
/// Threading contract: implementations are NOT thread-safe. All calls for a
/// given instance must be serialized (DocumentViewModel hosts the engine on a
/// dedicated render worker). Never call an engine method on the UI thread.
/// </summary>
public interface IPdfEngine : IAsyncDisposable
{
    /// <summary>Opens a document from disk. Returns the page count.</summary>
    ValueTask<PdfDocumentInfo> OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Returns the 0-based page's size in PDF points (72 per inch).</summary>
    ValueTask<PdfPageRegion> GetPageSizeAsync(int pageIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a single page to an in-memory PNG bitmap at the given DPI
    /// (72 = 1:1 PDF points, 300 = print-quality). Futures phases will tile
    /// this for FR-VIEW-01's 2,000-page lazy rendering.
    /// </summary>
    ValueTask<RenderedPdfPage> RenderPageToPngAsync(int pageIndex, float dpi, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the document outline (bookmarks) as a flattened pre-order item
    /// list. Thumbnails and navigation use it; the caller renders page 1-based
    /// numbers as displayed. Returns an empty outline when none is defined.
    /// </summary>
    ValueTask<PdfOutline> GetOutlineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the plain text of one page (reading order) for full-text search.
    /// </summary>
    ValueTask<PageText> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a new PDF at <paramref name="outputPath"/> whose pages are the
    /// assembled sequence described by <paramref name="pages"/> (a FR-PAGE build
    /// job). Each <see cref="PageBuildRef"/> names a source file, a 0-based page
    /// index within it, and an optional clockwise rotation in quarter turns
    /// (0/1/2/3). The same source file may appear many times (and multiple source
    /// files may be combined), giving merge, split, insert, delete, rotate,
    /// reorder, and extract in one primitive.
    ///
    /// Returns the number of pages written (always <c>pages.Count</c>).
    /// <paramref name="pages"/> must contain at least one entry, and every source
    /// path must already exist and be a readable PDF.
    /// </summary>
    ValueTask<int> BuildPdfAsync(
        string outputPath,
        IReadOnlyList<PageBuildRef> pages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the annotations on one 0-based page, in document order. The
    /// returned records carry the type, its rectangle in PDF points, and, when
    /// present, the free-text contents.
    /// </summary>
    ValueTask<IReadOnlyList<PdfAnnotation>> ListAnnotationsAsync(
        int pageIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a single annotation on one 0-based page, as fully described by
    /// <paramref name="annotation"/> (FR-ANNOT-01). Type-specific data (quad runs
    /// for highlight/underline/strikethrough, vertices for ink, contents for text
    /// notes) must be supplied where the type demands it; the engine validates
    /// that requirement and throws otherwise.
    /// </summary>
    ValueTask AddAnnotationAsync(
        int pageIndex,
        AnnotBuildSpec annotation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flattens (bakes into the page's content streams) every annotation of the
    /// given <paramref name="typesToFlatten"/> on one 0-based page so those are no
    /// longer live annotation objects (FR-ANNOT-02). Each selected annotation's
    /// appearance stream is embedded under the same transform MuPDF uses to paint
    /// it, so the page renders identically, while annotations of other types are
    /// left untouched. Pass an empty set to flatten nothing (a no-op).
    /// </summary>
    ValueTask FlattenAnnotationsAsync(
        int pageIndex,
        IReadOnlySet<AnnotationType> typesToFlatten,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the currently open, possibly edited document to
    /// <paramref name="outputPath"/> as a fresh PDF file. The open document is
    /// left open and unmodified; this is the persistence hook used by the
    /// flatten-on-export flow.
    /// </summary>
    ValueTask SaveAsAsync(string outputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the editable text runs of one 0-based page in a deterministic
    /// document order (FR-EDIT-01). A run is a span of consecutive characters
    /// sharing a font and size on one line; its bounding box and font metadata
    /// let the caller hit-test clicks and drive the edit overlay. Returns an
    /// empty list when the page has no text.
    /// </summary>
    ValueTask<IReadOnlyList<PdfTextRun>> ListTextRunsAsync(
        int pageIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites the text of one run (as listed by <see cref="ListTextRunsAsync"/>)
    /// on a 0-based page to <paramref name="newText"/>, in place (FR-EDIT-06):
    /// only the run's pixels change, its bounding box is recalculated from the
    /// new glyph advance, and every other operator, layout and structure is kept.
    ///
    /// Returns a <see cref="PdfTextEditReceipt"/> that pins the stream/offset and
    /// the old/new operator bytes for undo/redo; hand it back to
    /// <see cref="RevertTextEditAsync"/> to swap the edit. The open document is
    /// mutated in memory — persist it with <see cref="SaveAsAsync"/>.
    ///
    /// Throws when the run index is out of range, no content operator paints the
    /// run (a 2B-unsupported content-stream layout), or the run's font cannot
    /// encode a character of <paramref name="newText"/> (FR-EDIT-03 depth gate).
    /// </summary>
    ValueTask<PdfTextEditReceipt> RewriteTextRunAsync(
        int pageIndex,
        int runIndex,
        string newText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Splices an earlier rewrite's operators back into the page content stream:
    /// undo (<paramref name="redo"/>=false) restores the old operator bytes,
    /// redo (<paramref name="redo"/>=true) re-applies the new ones. The document
    /// must be in the state the matching rewrite produced. Used by the editing
    /// command layer (FR-EDIT-05); ordinary callers should prefer the
    /// <see cref="IPdfEngine"/>'s command stack instead of calling this directly.
    /// </summary>
    ValueTask RevertTextEditAsync(
        int pageIndex,
        PdfTextEditReceipt receipt,
        bool redo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the image and vector objects of one 0-based page, each with its
    /// bounding box in PDF points and a stable id (FR-EDIT-04). The id is opaque
    /// to Core: it names the object's content-stream invocation so
    /// <see cref="MoveResizeObjectAsync"/> can find and rewrite it. Returns an
    /// empty list when the page has no image/vector objects.
    /// </summary>
    ValueTask<IReadOnlyList<PdfPageObject>> ListObjectsAsync(
        int pageIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves and/or resizes the image/vector object identified by
    /// <paramref name="objectId"/> (as returned by <see cref="ListObjectsAsync"/>)
    /// on a 0-based page so its bounding box becomes <paramref name="bounds"/>
    /// (FR-EDIT-04). The transform is applied in place: the object's painting
    /// operator/matrix is updated, every other operator and structure is kept.
    ///
    /// Returns a <see cref="PdfTextEditReceipt"/>-like undo receipt; hand it back
    /// to <see cref="RevertTextEditAsync"/> to swap the geometry for undo/redo.
    /// The open document is mutated in memory — persist it with
    /// <see cref="SaveAsAsync"/>.
    ///
    /// Throws when the page/object id is unknown or the object cannot be
    /// transformed (the native transform is a later slice, so engine
    /// implementations that do not yet support it throw
    /// <see cref="NotSupportedException"/>).
    /// </summary>
    ValueTask<PdfTextEditReceipt> MoveResizeObjectAsync(
        int pageIndex,
        string objectId,
        PdfRect bounds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the interior of the object identified by <paramref name="objectId"/>
    /// (as returned by <see cref="ListObjectsAsync"/>) on a 0-based page with the
    /// new content described by <paramref name="replacement"/> (FR-EDIT-04). The
    /// object's bounding box and transform are preserved — only its painted
    /// interior is swapped for the new embedded content.
    ///
    /// Returns a <see cref="PdfTextEditReceipt"/>-like undo receipt; hand it back
    /// to <see cref="RevertTextEditAsync"/> to swap the content back for undo/redo.
    ///
    /// Throws when the page/object id is unknown or the replacement cannot be
    /// embedded (the native replace is a later slice, so engine implementations
    /// that do not yet support it throw <see cref="NotSupportedException"/>).
    /// </summary>
    ValueTask<PdfTextEditReceipt> ReplaceObjectAsync(
        int pageIndex,
        string objectId,
        PdfObjectReplacement replacement,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the fillable AcroForm fields on a 0-based page (FR-FORM-01). Returns
    /// an empty list when the page has no widget fields. Each result carries a
    /// stable <see cref="PdfFormField.Id"/> (the zero-based widget index on the
    /// page) that is handed back verbatim to <see cref="SetFormFieldValueAsync"/>.
    /// </summary>
    ValueTask<IReadOnlyList<PdfFormField>> ListFormFieldsAsync(
        int pageIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the value of the AcroForm field identified by <paramref name="fieldId"/>
    /// (as returned by <see cref="ListFormFieldsAsync"/>) on a 0-based page to
    /// <paramref name="value"/> (FR-FORM-01). For text fields the value becomes the
    /// field's text; for combo/list boxes it selects the matching option; for
    /// checkbox/radio buttons "Yes"/"On" checks and "Off" unchecks. The field's on-page
    /// appearance is regenerated. The open document is mutated in memory — persist it
    /// with <see cref="SaveAsAsync"/>.
    ///
    /// Throws when the page/field id is unknown, the field is a signature or other
    /// non-fillable type, or the value cannot be applied.
    /// </summary>
    ValueTask SetFormFieldValueAsync(
        int pageIndex,
        string fieldId,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new AcroForm text field on a 0-based page from <paramref name="spec"/>
    /// (FR-FORM-02). Only <see cref="FormFieldKind.Text"/> is supported in this slice;
    /// any other kind throws <see cref="NotSupportedException"/>. The widget is registered
    /// on the page and in the document's AcroForm /Fields, with the default appearance set
    /// and the blank field made visible, then is immediately fillable via
    /// <see cref="ListFormFieldsAsync"/>/<see cref="SetFormFieldValueAsync"/>. Basic validation
    /// (required, read-only, max length, comb/multi-line rendering) is driven by
    /// <see cref="FormFieldSpec.Flags"/>, <see cref="FormFieldSpec.MaxLength"/> and
    /// <see cref="FormFieldSpec.Quadding"/>. The document is mutated in memory — persist it
    /// with <see cref="SaveAsAsync"/>.
    /// </summary>
    ValueTask CreateFormFieldAsync(
        int pageIndex,
        FormFieldSpec spec,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flattens every AcroForm field in the open document into static page content
    /// (FR-FORM-01): after this the fields are no longer interactive, and their
    /// current values render as ordinary page content. The document is mutated in
    /// memory — persist it with <see cref="SaveAsAsync"/>.
    /// </summary>
    ValueTask FlattenFormAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the region <paramref name="bounds"/> (PDF points, normalized) on a
    /// 0-based page as a redaction to be applied later (FR-SEC-02 mark step). This
    /// is NON-destructive: it adds a /Redact annotation with a visible red/pink
    /// appearance so the user sees, across a re-render, exactly what applying will
    /// remove, but no content is deleted yet. Raise <see cref="SaveAsAsync"/> to
    /// persist only the marks. Call <see cref="ApplyRedactionsAsync"/> to delete
    /// the content inside every marked region.
    /// </summary>
    ValueTask AddRedactionAsync(
        int pageIndex,
        PdfRect bounds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies (FR-SEC-02 apply step) every <c>/Redact</c> region already marked on a
    /// 0-based page via <see cref="AddRedactionAsync"/>. The content that intrudes into
    /// each marked region — text runs, vector paths and (per the chosen
    /// <paramref name="options"/>) images — is REMOVED from the page's content stream,
    /// not painted over, overlapping annotations and links are pruned, and the
    /// redaction marks themselves are deleted. A page with no redaction marks is a
    /// no-op that returns 0.
    ///
    /// Pass <c>null</c> for <paramref name="options"/> to use the secure defaults
    /// (see <see cref="RedactionOptions"/>). The document is mutated in memory and
    /// should be persisted with <see cref="SaveAsAsync"/>.
    ///
    /// The operation is destructive; an <see cref="PageForge.Core.Editing.IEditCommand"/> that
    /// snapshots the pre-apply document first (via <see cref="SaveAsAsync"/> to a
    /// scratch file) provides undo by restoring that snapshot with
    /// <see cref="RestoreSnapshotAsync"/>.
    ///
    /// Returns the number of redaction regions that were applied (0 when the page
    /// had no marks).
    /// </summary>
    ValueTask<int> ApplyRedactionsAsync(
        int pageIndex,
        RedactionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the open in-memory document with a fresh copy opened from
    /// <paramref name="snapshotPath"/>, closing the current one (FR-SEC-02 undo
    /// plumbing). The engine's open-path/display identity is unchanged — this only
    /// swaps the in-memory state, so a subsequent <see cref="SaveAsAsync"/> still
    /// writes the document under the name the user chose. Used by the redaction
    /// apply command to restore a pre-apply snapshot saved to a scratch file.
    /// The caller remains responsible for deleting the snapshot file afterwards.
    /// </summary>
    ValueTask RestoreSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs local OCR over the whole open document and writes a new searchable
    /// PDF to <paramref name="outputPath"/> (FR-OCR-01): every page's pixels are
    /// recognized on this machine and the recognized text is embedded as an
    /// invisible layer behind the page image, so the output can be searched and
    /// text-selected while still rendering identically. The open document is
    /// left open and unmodified.
    ///
    /// Pass <c>null</c> for <paramref name="options"/> to use the defaults (see
    /// <see cref="OcrOptions"/>). The output file must not already exist.
    ///
    /// Throws when the engine has no open document, the output path is invalid,
    /// or recognition fails (for example when the bundled trained data is not
    /// reachable). Returns the number of pages written and the effective
    /// language/model on success.
    /// </summary>
    ValueTask<OcrResult> OcrToPdfAsync(
        string outputPath,
        OcrOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies PDF standard security (FR-SEC-01): writes a fresh encrypted copy
    /// of the open document to <paramref name="outputPath"/>. The open document
    /// is left open and unmodified; the copy is what carries the security
    /// handler. Opening the copy without a correct password fails to render.
    ///
    /// Pass <c>null</c> for <paramref name="options"/> to use the defaults (see
    /// <see cref="PdfProtectionOptions"/>): AES-256 with all permissions allowed.
    /// The output file must not already exist and must not be the engine's own
    /// open path. Throws when the engine has no open document or the save fails.
    /// </summary>
    ValueTask SaveEncryptedAsync(
        string outputPath,
        PdfProtectionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether <paramref name="password"/> opens the currently open
    /// document (FR-SEC-01): <c>true</c> when the document needs no password or
    /// the password authenticates (user or owner). Non-mutating; lets the shell
    /// confirm a just-written encrypted copy or verify a password before
    /// offering an unprotect action. Throws when the engine has no open document.
    /// </summary>
    ValueTask<bool> AuthenticateAsync(
        string password,
        CancellationToken cancellationToken = default);
}