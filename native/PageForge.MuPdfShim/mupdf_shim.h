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

// FR-EDIT-04: replaces the interior of the object `object_index` (as listed by
// pf_list_objects) with the raster image at source_path_utf8. The object's
// bounding box is preserved exactly: the replacement image is embedded as a new
// XObject under a unique name, added to the page's /Resources /XObject dict, and
// only the name token before the `Do` operator is spliced in the content stream.
// Writes a PF-TRW-format receipt (stream, offset, old/new name-token bytes) so
// pf_revert_text_rewrite can undo/redo it. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_replace_object(pf_context context, pf_document document,
                                int page_index, int object_index,
                                const char *source_path_utf8,
                                const char *receipt_path_utf8);

// ---------------------------------------------------------------------------
// FR-FORM AcroForm primitives (slice 3A). These give the managed layer what it
// needs to implement form fill (FR-FORM-01): a per-page listing of the widgets
// (fields), a primitive that sets one widget's value with its appearance
// regenerated, and a primitive that flattens every widget into static page
// content. Apart from listing, they mutate the open document in memory; the
// caller persists with pf_save_document.
// ---------------------------------------------------------------------------

// Writes the AcroForm widgets on page `page_index` (0-based) to out_path_utf8
// as UTF-8 TSV, one line per widget, in deterministic page order:
//     widget_index<TAB>type_num<TAB>field_name<TAB>x0<TAB>y0<TAB>x1<TAB>y1<TAB>value
//   - widget_index : zero-based index within the page (the value handed back to
//               pf_set_widget_value); stable within one document state.
//   - type_num    : enum pdf_widget_type integer (see mupdf/pdf/form.h):
//               0=Unknown 1=Button 2=Checkbox 3=Combobox 4=Listbox
//               5=Radiobutton 6=Signature 7=Text.
//   - field_name  : the field's /T name (Tab/CR/LF replaced with spaces).
//   - x0,y0,x1,y1 : the widget Rect in PDF points.
//   - value       : the field's current value (Tab/CR/LF replaced with spaces);
//               empty when the field has no value.
// Writes nothing and returns PF_OK when the page has no widgets. Returns
// PF_ERR on failure.
PF_EXPORT int pf_list_widgets(pf_context context, pf_document document,
                              int page_index, const char *out_path_utf8);

// Sets the value of widget `widget_index` (as listed by pf_list_widgets) on
// page `page_index` (0-based) of the open document to the UTF-8 text read from
// the file at value_path_utf8 (a single trailing CR/LF is stripped). Every
// fillable widget type (Text, Combobox/Listbox, Checkbox/Radiobutton/Button) is
// set through pdf_set_annot_field_value with trigger events ignored — the
// canonical direct setter that bypasses keystroke/format JavaScript. For
// checkbox/radio "Yes"/"On" checks, "Off" unchecks. The widget appearance is then
// regenerated with pdf_update_widget. The document is mutated in memory; call
// pf_save_document to persist. Signature widgets are not fillable here.
// Returns PF_OK/PF_ERR.
PF_EXPORT int pf_set_widget_value(pf_context context, pf_document document,
                                  int page_index, int widget_index,
                                  const char *value_path_utf8);

// Flattens every AcroForm widget in the open document into static page content
// (pdf_bake_document with widgets baked) in memory, so fields are no longer
// interactive. Call pf_save_document to persist. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_bake_widgets(pf_context context, pf_document document);

// Creates a new AcroForm text field on `page_index` (0-based) of the open
// document, reading a UTF-8 spec file at spec_path_utf8 (see mupdf_shim.c
// pf_create_field for the spec format: K kind, N name, R rect, F /Ff flags,
// M /MaxLen, Q quadding, W border width). The widget is registered on the page
// and appended to the AcroForm /Fields array; /DA is "/Helv 12 Tf 0 g" and the
// appearance is generated so the blank field is visible. Call pf_save_document
// to persist; then list/fill it with pf_list_widgets/pf_set_widget_value.
// Returns PF_OK/PF_ERR.
PF_EXPORT int pf_create_field(pf_context context, pf_document document,
                              int page_index, const char *spec_path_utf8);

// ---------------------------------------------------------------------------
// FR-SEC-02 true redaction primitives. Redaction DELETES the marked content
// from the page's content streams (it is not painted over), so it is
// destructive once applied: the surviving file is genuinely free of the
// redacted data, which is the product requirement here. Marking (adding the
// /Redact annotation) is non-destructive and gives the UI a visible preview
// of what apply will remove; applying removes the content AND the marks.
// These functions mutate the open document in memory; the caller persists
// with pf_save_document.
// ---------------------------------------------------------------------------

// Mark region (x0,y0)-(x1,y1) (PDF points, bottom-left origin; must be
// normalized x1>=x0 and y1>=y0) on page `page_index` (0-based) of the open
// document as a redaction to be applied later. Adds a /Subtype /Redact
// annotation with a visible red-stroked, pink-filled appearance and updates
// it, so a re-render shows exactly the region that apply will remove.
// Non-destructive: nothing is removed until pf_apply_redactions runs.
// Returns PF_OK/PF_ERR.
PF_EXPORT int pf_add_redact(pf_context context, pf_document document,
                            int page_index, double x0, double y0,
                            double x1, double y1);

// Applies every /Redact annotation on page `page_index` (0-based) of the open
// document. The content that intrudes into each marked region — text runs,
// vector paths, and image objects according to the option values below — is
// REMOVED from the page's content stream (not covered), overlapping links and
// annotations are pruned, and each /Redact annotation itself is deleted. When
// black_boxes is nonzero a solid black box is painted over the emptied region
// so the viewer sees a classic redaction bar. Secure defaults are used when
// opts_path_utf8 is NULL: text=remove-overlapping, images=remove (even if
// clipped), line-art=remove-if-covered, black boxes on — the choices that do
// not leak content (FR-SEC-02).
//
// opts_path_utf8 names an optional UTF-8 TSV file, one option per line
// (missing options keep the secure defaults):
//     B<TAB><0|1>       black box over each region (default 1)
//     I<TAB><int>       image method: 0=none 1=remove 2=black-out-pixels
//                       3=remove-unless-invisible (default 1)
//     L<TAB><int>       line-art method: 0=none 1=remove-if-covered
//                       2=remove-if-touched (default 1)
//     T<TAB><int>       text method: 0=remove 1=none 2=remove-invisible-only
//                       (default 0; 1 leaks on purpose and is NOT recommended)
//
// Writes the number of marked regions that were applied into
// *out_count (may be NULL; 0 when the page had no /Redact annotations, which
// is also a no-op). Returns PF_OK/PF_ERR; call pf_save_document to persist the
// redacted document.
PF_EXPORT int pf_apply_redactions(pf_context context, pf_document document,
                                  int page_index, const char *opts_path_utf8,
                                  int *out_count);

// ---------------------------------------------------------------------------
// FR-OCR-01 local OCR primitives. Recognition runs entirely on this machine
// through the MuPDF-bundled Tesseract (Apache-2.0); nothing is sent to a
// hosted service. The pdfocr band writer emits a searchable PDF whose text
// layer is positioned from the recognised glyph boxes so it overlays the
// raster image of each page.
// ---------------------------------------------------------------------------

// Converts every page (0-based count via pf_page_count) of the open document
// into a searchable PDF at out_path_utf8: each page is rendered to a raster
// and OCR'd by Tesseract, which writes the raster plus a transparent text
// layer into the output. The open document is NOT modified; the caller opens
// the finished file normally afterwards.
//
// language_utf8: Tesseract OCR language code (e.g. "eng"); may be NULL or
// "" for the "eng" default.
//
// datadir_utf8: path to the directory containing the language's *.traineddata
// file (e.g. ".../tessdata"); may be NULL, in which case Tesseract falls back
// to its TESSDATA_PREFIX environment variable. The managed layer resolves a
// bundled tessdata dir when present and passes it here, so OCR works offline
// with no environment configuration.
//
// After this call the summary line "N pages OCR'd from <count>
// input pages" is written to stderr; OCR failures on a page abort the whole
// run with PF_ERR and a message in pf_last_error. Returns PF_OK/PF_ERR.
PF_EXPORT int pf_ocr_pdf(pf_context context, pf_document document,
                         const char *out_path_utf8,
                         const char *language_utf8,
                         const char *datadir_utf8,
                         int *out_page_count);

// ---------------------------------------------------------------------------
// FR-SEC-01 password protection primitives. protect writes a fresh encrypted
// copy; authenticate answers "does this password open the (encrypted) open
// document?" so the managed layer can verify a save and later power an unprotect
// UI. Protecting never mutates the open document.
// ---------------------------------------------------------------------------

// Applies PDF standard security (RFC 9506 algorithms) to the open document and
// writes an encrypted copy at out_path_utf8. The open document is NOT modified.
//
// method: PDF encryption algorithm, taken from mupdf/pdf/crypt.h:
//     PDF_ENCRYPT_RC4_40=2  PDF_ENCRYPT_RC4_128=3
//     PDF_ENCRYPT_AES_128=4 PDF_ENCRYPT_AES_256=5
// permissions: the document permissions mask bits OR'd together, also from
// crypt.h (print=1<<2, modify=1<<3, copy=1<<4, annotate=1<<5, form=1<<8,
// accessibility=1<<9, assemble=1<<10, print-hq=1<<11; the fixed/masked bits are
// handled by MuPDF). Use the owner password to restrict permissions.
//
// opwd_utf8 / upwd_utf8: open (user) and permissions (owner) passwords as
// UTF-8, each at most 127 bytes (128-byte PDF string limit). Either may be NULL
// or ""; a non-empty owner with an empty user password creates a document that
// opens freely but whose permissions the owner password alone can change, and a
// non-empty user with an empty owner makes the owner and user passwords equal.
//
// The output path must not already exist and must not equal the open document's
// own path. Returns PF_OK/PF_ERR with the reason in pf_last_error.
PF_EXPORT int pf_save_encrypted(pf_context context, pf_document document,
                                const char *out_path_utf8,
                                const char *opwd_utf8, const char *upwd_utf8,
                                int method, int permissions);

// Writes the authentication status of `password_utf8` against the open
// (encrypted) document into *out_result: 0 = the password did not match,
// 1 = matched (the doc is either unencrypted, in which case any password is
// accepted, or the password opened it). Returns PF_OK/PF_ERR with the reason
// in pf_last_error.
PF_EXPORT int pf_auth_password(pf_context context, pf_document document,
                               const char *password_utf8, int *out_result);

// ---------------------------------------------------------------------------
// FR-SEC-03 digital signature primitives. Signing produces a standard PDF
// signature (/SubFilter adbe.pkcs7.detached); the PKCS#7/CMS blob is built by
// the Windows crypto backend (crypt32, fully offline) and the digest is
// computed by MuPDF over the saved /ByteRange exactly as the PDF spec and
// Acrobat expect. Verification (digest + certificate chain) runs against the
// OS trust stores, so no certificate bundle is shipped with the app.
// ---------------------------------------------------------------------------

// Signs the open document on `page_index` (0-based), creating a fresh
// signature widget. Reads a UTF-8 spec file at spec_path_utf8 (see
// mupdf_shim.c pf_sign_pdf for the format: N name, R rect, E reason,
// L location, P12 PKCS#12 path, PW password). The PKCS#12 must contain a
// certificate with a private key. The document is mutated in memory; call
// pf_save_document or pf_save_document_incremental to persist (the save
// completes the signature digest over the file byte range). Returns
// PF_OK/PF_ERR with the reason in pf_last_error.
PF_EXPORT int pf_sign_pdf(pf_context context, pf_document document,
                          int page_index, const char *spec_path_utf8);

// Saves the open document as an incremental update at out_path_utf8 — the
// canonical save for a just-signed document. Original file bytes are preserved
// verbatim and changes are appended, so prior signatures stay valid.
// Returns PF_OK/PF_ERR with the reason in pf_last_error.
PF_EXPORT int pf_save_document_incremental(pf_context context,
                                           pf_document document,
                                           const char *out_path_utf8);

// Lists every AcroForm signature field in the open document to out_path_utf8,
// verifying each signed field's digest and certificate chain with the OS
// certificate engine (one TSV row per field; see mupdf_shim.c
// pf_list_signatures for the column layout). Returns PF_OK/PF_ERR with the
// reason in pf_last_error.
PF_EXPORT int pf_list_signatures(pf_context context, pf_document document,
                                 const char *out_path_utf8);

// ---------------------------------------------------------------------------
// Internal to the shim DLL (not exported): Windows-crypto backend constructors
// implemented in pf_sig_crypt32.c. Both return objects the caller owns (drop
// with pdf_drop_signer/pdf_drop_verifier) and fz_throw on failure.
// ---------------------------------------------------------------------------
typedef struct pdf_pkcs7_signer pdf_pkcs7_signer;
typedef struct pdf_pkcs7_verifier pdf_pkcs7_verifier;
pdf_pkcs7_signer *pf_capi_signer_new(fz_context *ctx, const unsigned char *pfx,
                                     size_t pfx_len, const char *password_utf8);
pdf_pkcs7_verifier *pf_capi_verifier_new(fz_context *ctx);

#ifdef __cplusplus
}
#endif

#endif // PAGE_FORGE_MUPDF_SHIM_H