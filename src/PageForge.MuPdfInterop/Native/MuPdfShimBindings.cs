// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Runtime.InteropServices;

namespace PageForge.MuPdfInterop.Native;

/// <summary>
/// P/Invoke declarations for the PageForge.MuPdfShim C ABI (pageforge_mupdf.dll).
///
/// Marshaling rules (see .opencode/skills/mupdf-interop):
///  - All fz handles cross as raw IntPtr; lifetime owned by this layer.
///  - Paths cross as NUL-terminated UTF-8 byte arrays, never BSTR.
///  - Every native call runs on a thread that owns the pf_context
///    (see MuPdfEngine's serialization gate).
/// </summary>
internal static class MuPdfShimBindings
{
    private const string DllName = "pageforge_mupdf";

    internal const int PfOk = 0;
    internal const int PfErr = 1;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_create_context(out nint context, out nint errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pf_destroy_context(nint context);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_open_document(nint context, [In] byte[] pathUtf8, out nint document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pf_close_document(nint context, nint document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_page_count(nint context, nint document, out int count);

    /// <summary>Whether the open document has edits not yet written out. Answered
    /// by MuPDF itself rather than tracked per mutating call, so it cannot drift
    /// from what the library actually did.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_has_unsaved_changes(nint context, nint document, out int dirty);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_page_size(nint context, nint document, int pageIndex, out float widthPt, out float heightPt);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_render_page_to_png(nint context, nint document, int pageIndex, float dpi, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_page_text(nint context, nint document, int pageIndex, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_load_outline(nint context, nint document, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_build_pdf(nint context, [In] byte[] jobPathUtf8, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_list_annotations(nint context, nint document, int pageIndex, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_add_annotation(nint context, nint document, int pageIndex, [In] byte[] specPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_flatten_annotations(nint context, nint document, int pageIndex, [In] byte[] typesUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_save_document(nint context, nint document, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_list_text_runs(nint context, nint document, int pageIndex, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_rewrite_text_run(
        nint context, nint document, int pageIndex, int runIndex,
        [In] byte[] newTextPathUtf8, [In] byte[] receiptPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_revert_text_rewrite(
        nint context, nint document, int pageIndex,
        [In] byte[] receiptPathUtf8, int redoFlag);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_list_objects(nint context, nint document, int pageIndex, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_move_resize_object(
        nint context, nint document, int pageIndex, int objectIndex,
        double x0, double y0, double x1, double y1,
        [In] byte[] receiptPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_replace_object(
        nint context, nint document, int pageIndex, int objectIndex,
        [In] byte[] sourcePathUtf8, [In] byte[] receiptPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_list_widgets(nint context, nint document, int pageIndex, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_set_widget_value(
        nint context, nint document, int pageIndex, int widgetIndex,
        [In] byte[] valuePathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_bake_widgets(nint context, nint document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_create_field(
        nint context, nint document, int pageIndex, [In] byte[] specPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_add_redact(
        nint context, nint document, int pageIndex,
        double x0, double y0, double x1, double y1);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_apply_redactions(
        nint context, nint document, int pageIndex, [In] byte[]? optsPathUtf8, out int outCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_ocr_pdf(
        nint context, nint document,
        [In] byte[] outPathUtf8, [In] byte[]? languageUtf8, [In] byte[]? datadirUtf8,
        out int outPageCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_ocr_docx(
        nint context, nint document,
        [In] byte[] outPathUtf8, [In] byte[]? languageUtf8, [In] byte[]? datadirUtf8,
        out int outPageCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_ocr_png(
        nint context, nint document,
        [In] byte[] outPathUtf8, out int outPageCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_save_encrypted(
        nint context, nint document,
        [In] byte[] outPathUtf8, [In] byte[]? opwdUtf8, [In] byte[]? upwdUtf8,
        int method, int permissions);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_auth_password(
        nint context, nint document, [In] byte[]? passwordUtf8, out int outResult);

    /// <summary>FR-SEC-03. Creates a signature widget on the page and signs it with the
    /// PKCS#12 named in the spec file. Mutates the document in memory only; the digest is
    /// completed by a subsequent save, so a sign call without a save leaves nothing behind.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_sign_pdf(
        nint context, nint document, int pageIndex, [In] byte[] specPathUtf8);

    /// <summary>Appends changes to a copy of the original file rather than rewriting it, which
    /// is what keeps any signature already in the document valid — a full rewrite renumbers
    /// objects and invalidates every earlier byte range.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_save_document_incremental(
        nint context, nint document, [In] byte[] outPathUtf8);

    /// <summary>Writes one TSV row per AcroForm signature field, each already verified against
    /// the OS trust stores. Writes an empty file when the document has no signature fields.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pf_list_signatures(
        nint context, nint document, [In] byte[] outPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pf_last_error();
}