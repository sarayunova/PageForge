// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.
//
// PageForge.MuPdfShim — minimal C ABI over the MuPDF static core.
//
// This is the deliberate interface between the managed PageForge.MuPdfInterop
// layer and MuPDF (AGPLv3, Artifex). Keeping the ABI tiny means the P/Invoke
// surface is hand-curated: every crossing point is reviewed for marshaling,
// lifetime and threading correctness (see .opencode/skills/mupdf-interop).
//
// All MuPDF calls must be invoked on exactly one thread per pf_context, and
// never on the UI thread of the host application. The managed layer is
// responsible for that serialization (render worker queue).
//
// Strings (paths) cross as UTF-8 byte arrays, per the MuPDF C API.
// Contexts are created with a fresh fz_context; the managed layer must use
// pf_destroy_context before process exit and must not share contexts.

#ifndef PAGE_FORGE_MUPDF_SHIM_H
#define PAGE_FORGE_MUPDF_SHIM_H

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#define PF_EXPORT __declspec(dllexport)
#else
#define PF_EXPORT
#endif

#define PF_OK 0
#define PF_ERR 1

// Returns the error message recorded by the most recent failed call on the
// calling thread, or an empty string. The buffer remains valid until the next
// shim call from the same thread.
PF_EXPORT const char *pf_last_error(void);

typedef void *pf_context;
typedef void *pf_document;

// Creates a new MuPDF context. Returns PF_OK (0) or PF_ERR (1). On failure
// *out_error receives a static UTF-8 message valid until the next shim call
// from the same thread.
PF_EXPORT int pf_create_context(pf_context *out_context, const char **out_error);

// Destroys a context created by pf_create_context. NULL is a no-op.
PF_EXPORT void pf_destroy_context(pf_context context);

// Opens a document from a filesystem path (UTF-8). Returns PF_OK/PF_ERR.
PF_EXPORT int pf_open_document(pf_context context, const char *path_utf8, pf_document *out_document);

// Closes a document opened by pf_open_document. NULL is a no-op.
PF_EXPORT void pf_close_document(pf_context context, pf_document document);

// Writes the page count into *out_count. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_page_count(pf_context context, pf_document document, int *out_count);

// Writes the page dimensions (points, 0-based page_index) into the outs.
PF_EXPORT int pf_page_size(pf_context context, pf_document document, int page_index,
                 float *out_width_pt, float *out_height_pt);

// Renders page `page_index` (0-based) to a PNG file at dpi DPI (typical 72..300).
// Writes the file at out_path_utf8. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_render_page_to_png(pf_context context, pf_document document, int page_index,
                          float dpi, const char *out_path_utf8);

// Extracts the plain text of page `page_index` (0-based) as UTF-8 and writes it
// to the file at out_path_utf8. Returns PF_OK/PF_ERR. The output approximates the
// page's reading order (paragraph blocks joined by newlines); used by search.
PF_EXPORT int pf_page_text(pf_context context, pf_document document, int page_index,
                  const char *out_path_utf8);

// Writes the document outline (bookmarks) to the file at out_path_utf8 as UTF-8
// text, one line per item, Tab-separated fields, in pre-order:
//     depth<TAB>page_1based<TAB>x_pt<TAB>y_pt<TAB>title
//   - depth  : 0 = top-level, increments per nesting level.
//   - page   : 1-based page number the item resolves to, or 0 if it has no
//              internal destination.
//   - x, y   : destination coordinates in points (0 when no destination).
//   - title  : the outline label; Tab/CR/LF are replaced with spaces.
// Writes nothing and returns PF_OK if the document has no outline. Returns
// PF_ERR on failure.
PF_EXPORT int pf_load_outline(pf_context context, pf_document document,
                     const char *out_path_utf8);

// Builds a new PDF at out_path_utf8 from the page-assembly job described by the
// UTF-8 text file at job_path_utf8. This is the single FR-PAGE primitive; it
// implements merge, split, insert, delete, rotate, reorder, and extract by
// assembling an output page list from one or more source PDFs, optionally
// rotating each emitted page, then saving.
//
// Job file format: newline-separated records, Tab-separated fields. Windows
// file paths never contain Tab or newline, so those are safe separators.
//     V<TAB>1                                  version marker (required, first line)
//     S<TAB><srcId><TAB><pathUtf8>             register a source PDF with integer id
//     P<TAB><srcId><TAB><page0based><TAB><rot> emit that source page into the
//                                              output, in the order written;
//                                              rot = 0/1/2/3 = 0/90/180/270 CW
// Every P record produces exactly one output page. Returns PF_OK/PF_ERR; on
// failure pf_last_error carries a UTF-8 message. Sources are opened with the
// raw PDF document API (not the smart parry), so rotation is applied per page.
PF_EXPORT int pf_build_pdf(pf_context context, const char *job_path_utf8,
                           const char *out_path_utf8);

// ---------------------------------------------------------------------------
// FR-ANNOT annotation primitives. All of these operate on the document that is
// already open (pf_open_document) and, apart from listing, mutate it in memory.
// The caller persists any edits with pf_save_document. Handle/page lifetimes
// follow the rules in the header preamble (single thread per pf_context).
// ---------------------------------------------------------------------------

// Writes the annotations on page `page_index` (0-based) to out_path_utf8 as
// UTF-8 TSV, one line per annotation (pre-order): 
//     type_num<TAB>type_name<TAB>x0<TAB>y0<TAB>x1<TAB>y1<TAB>contents
//   - type_num : enum pdf_annot_type integer (see mupdf/pdf/annot.h).
//   - type_name: canonical subtype string (e.g. "Highlight").
//   - x0,y0,x1,y1: the annotation Rect in PDF points.
//   - contents: the annotation's text contents with Tab/CR/LF replaced by
//     spaces. An annotation with no contents produces an empty final field.
// Writes nothing and returns PF_OK when the page has no annotations. Returns
// PF_ERR on failure.
PF_EXPORT int pf_list_annotations(pf_context context, pf_document document,
                                  int page_index, const char *out_path_utf8);

// Adds one annotation to page `page_index` (0-based) of the open document,
// described by the UTF-8 TSV spec file at spec_path_utf8. The annotation is
// committed to the in-memory document (appearance synthesized where
// applicable); call pf_save_document to persist. Returns PF_OK/PF_ERR.
//
// Spec file format (newline-separated records, Tab-separated fields):
//     T<TAB><typeName>         annotation type, e.g. Highlight|Underline|
//                              StrikeOut|Ink|Text|Square|Circle|Stamp
//     R<TAB>x0<TAB>y0<TAB>x1<TAB>y1    bounding Rect in PDF points (required)
//     C<TAB><contents>         text contents (required for Text; optional else)
//     Q<TAB>x0<TAB>y0<TAB>x1<TAB>y1   a quad (highlight region) in PDF points;
//                              may be repeated (optional)
//     I<TAB>x<TAB>y            an ink stroke vertex; may be repeated (ink only)
//     O<TAB>r<TAB>g<TAB>b      stroke color, each 0..1 (optional; default black)
//     P<TAB>opacity            fill/pen opacity 0..1 (optional; default 1)
// Annotation types and the fields they honour: Highlight/Underline/StrikeOut
// (T+R+one or more Q), Ink (T+R+I...), Text (T+R+C), Square/Circle (T+R),
// Stamp (T+R+C; a text stamp labelled by contents).
PF_EXPORT int pf_add_annotation(pf_context context, pf_document document,
                                int page_index, const char *spec_path_utf8);

// Flattens annotations of selected types on page `page_index` (0-based) of the
// open document into the page's appearance, removing them as interactive
// annotations. Each selected annotation's synthesized appearance is embedded
// into the page content so the rendered result is unchanged (vector fidelity
// preserved) but the annotation is no longer listable/editable (FR-ANNOT-02,
// selectable per type). types_utf8 is a comma-separated list of MuPDF type
// names (e.g. "Highlight" or "Highlight,Ink" or "Text,Stamp"); an empty string
// flattens every non-link annotation. Annotation types not in the list are left
// untouched. Call pf_save_document to persist. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_flatten_annotations(pf_context context, pf_document document,
                                     int page_index, const char *types_utf8);

// Saves the (possibly edited) open document to out_path_utf8. Returns
// PF_OK/PF_ERR. Used after pf_add_annotation / pf_flatten_annotations to
// persist FR-ANNOT edits. The in-memory document remains open and editable.
PF_EXPORT int pf_save_document(pf_context context, pf_document document,
                               const char *out_path_utf8);

// ---------------------------------------------------------------------------
// FR-EDIT text-run introspection + rewrite primitives (slice 2B). These give
// the managed command layer what it needs to implement click-to-edit of
// existing text (FR-EDIT-01): a stable list of the page's text runs, a
// primitive that rewrites one run's content-stream operators in place, and a
// primitive that splices the old operators back for undo/redo (FR-EDIT-05).
// The receipt written by the rewrite carries opaque old/new operator lists so
// undo/redo can be driven without re-running geometry matching.
// ---------------------------------------------------------------------------

// Writes the text runs of page `page_index` (0-based) to out_path_utf8 as
// UTF-8 TSV, one line per run, in a deterministic document order:
//     run_idx<TAB>x0<TAB>y0<TAB>x1<TAB>y1<TAB>font_size<TAB>font_embedded<TAB>font_name<TAB>text
//   - run_idx : stable within one document state (blocks/then lines/then runs
//               of consecutive chars sharing a font + size); the value handed
//               back to pf_rewrite_text_run.
//   - x0,y0,x1,y1: the run's bounding box in PDF points.
//   - font_size : the run's font size in points.
//   - font_embedded : 1 when the run's font has an in-document (FreeType) face
//               in this context, else 0. A slice-2B depth proxy; FR-EDIT-03's
//               subset check lands in a later slice.
//   - font_name : the resolved font's PostScript name (Tabs/CR/LF are
//               replaced with spaces).
//   - text : the run's text as UTF-8 (Tabs/CR/LF replaced with spaces).
// A hit-test (FR-EDIT-01) is done managed-side by picking the run whose box
// contains the click point. Writes nothing and returns PF_OK when the page
// has no text. Returns PF_ERR on failure.
PF_EXPORT int pf_list_text_runs(pf_context context, pf_document document,
                                int page_index, const char *out_path_utf8);

// Rewrites the text of run `run_index` (as listed by pf_list_text_runs) on
// page `page_index` (0-based) of the open document to the UTF-8 text read from
// the file at new_text_path_utf8. The run's text-showing operator(s) in the
// page's content stream are replaced in place at the run's font/position, so
// only that run's pixels change; the surrounding operators, layout and
// structure are left untouched (FR-EDIT-06). The new run's bounding box is
// recalculated from the new glyph advance.
//
// On success a rewrite receipt is written to receipt_path_utf8 (UTF-8 TSV):
//     PF-TRW<TAB>1
//     R<TAB>stream_index<TAB>offset<TAB>old_len<TAB>new_len
//     O<TAB><base64 of the old operator bytes>
//     N<TAB><base64 of the new operator bytes>
//
// The managed command layer keeps this receipt for undo/redo: it is opaque to
// the journal (its payload is text), and pf_revert_text_rewrite splices the
// stored bytes back. The document is mutated in memory; call pf_save_document
// to persist. Returns PF_OK/PF_ERR; failures include: run out of range, no
// operator matches the run (content stream layout unsupported in 2B), or a new
// character the run's font cannot encode.
PF_EXPORT int pf_rewrite_text_run(pf_context context, pf_document document,
                                  int page_index, int run_index,
                                  const char *new_text_path_utf8,
                                  const char *receipt_path_utf8);

// Splices an earlier rewrite's operators back into the page content stream:
// undo (redo_flag = 0) swaps the new operator bytes back to the old; redo
// (redo_flag != 0) re-applies the new bytes. Confirmed by the stream index,
// byte offset and lengths in the receipt written by pf_rewrite_text_run. The
// document must be in the state the matching rewrite produced. Returns
// PF_OK/PF_ERR.
PF_EXPORT int pf_revert_text_rewrite(pf_context context, pf_document document,
                                     int page_index, const char *receipt_path_utf8,
                                     int redo_flag);

// FR-EDIT-04: lists the image/vector objects on a page to out_path_utf8 as
// tab-separated lines:
// `object_index \t tag(Image=1/Form=2) \t resource_name \t x0 \t y0 \t x1 \t y1
//  \t stream_index \t span_start \t span_end`. The bounds are in PDF points
// (device space); span_start/span_end are decoded byte offsets of the object's
// `cm ... Do` region in its content stream. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_list_objects(pf_context context, pf_document document,
                              int page_index, const char *out_path_utf8);

// FR-EDIT-04: moves and/or resizes the object `object_index` (as listed by
// pf_list_objects) so its device bounds become (x0,y0)-(x1,y1), in PDF points.
// Rewrites the object's content-stream `cm ... Do` region in memory and writes
// a PF-TRW-format receipt (stream, offset, old/new operator bytes) so
// pf_revert_text_rewrite can undo/redo it. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_move_resize_object(pf_context context, pf_document document,
                                    int page_index, int object_index,
                                    double x0, double y0, double x1, double y1,
                                    const char *receipt_path_utf8);

#ifdef __cplusplus
}
#endif

#endif // PAGE_FORGE_MUPDF_SHIM_H