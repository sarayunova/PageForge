# Session: Phase 1 FR-ANNOT (annotation editing)

Status: **DONE** — native shim + Core seam + interop + tests + WPF UI + smoke proof all green.

## What was built
- Native shim (`native/PageForge.MuPdfShim/mupdf_shim.[ch]`):
  - `pf_list_annotations(context, doc, page_index, out_path_utf8)` — TSV list of
    typeNum/typeName/bounds/contents per annotation; annot iteration wrapped in
    `fz_try/fz_catch` so escaped native throws become clean PF_ERR.
  - `pf_add_annotation(context, doc, page_index, spec_path_utf8)` — T/R/C/Q/I/O/P
    spec grammar; sets quads/ink/color/opacity/contents; `pdf_update_annot`.
  - `pf_flatten_annotations(context, doc, page_index, types_utf8)` — embeds each
    selected annotation's `/N` appearance (deep-copied) as a page XObject invoked
    under `pdf_annot_transform` matrix, appends a content stream, then
    `pdf_delete_annot`. Type list is comma-separated MuPDF names; empty = all
    non-Link. **Selectable per type (FR-ANNOT-02)**.
  - `pf_save_document(context, doc, out_path_utf8)` — `pdf_save_document`.
- Core (`src/PageForge.Core/Pdf/`): `AnnotationModels.cs` (AnnotationType,
  PdfPoint, PdfQuad, PdfAnnotation, AnnotBuildSpec), `IPdfEngine` extended,
  `AnnotationService.cs` (ListAsync/AddAsync/AddHighlight/AddTextNote/AddInk/
  FlattenForExportAsync + ValidateSpec).
- Interop (`src/PageForge.MuPdfInterop/`): `MuPdfShimBindings.cs` P/Invoke; a
  `MuPdfEngine` implements List/Add/Flatten/Save on the engine lane; a
  `AnnotationListParser.cs` parses the TSV. `FlattenAnnotationsAsync(page,
  IReadOnlySet<AnnotationType>, ct)` builds the comma-separated type list.
- Tests: `tests/PageForge.Core.Tests/AnnotationServiceTests.cs` (9 tests, fake
  engine), `tests/PageForge.Fidelity.Tests/AnnotationFidelityTests.cs` (1 real
  round-trip test).
- WPF UI (`src/PageForge.App.Wpf`): Annotate toolbar (highlight/note/ink/
  flatten…) + Annotations sidebar tab listing current-page annotations;
  `DocumentTabViewModel` gained RefreshAnnotationsAsync/AddHighlight/AddTextNote/
  AddInk/FlattenExportAsync; `App.xaml.cs` gained `RunHeadlessAnnotationProofAsync`.

## Key facts / fixes (painful lessons)
- No public MuPDF flatten API — implemented by embedding synthesized appearance.
- Native `pdf_annot_type` enum has extra SQUIGGLY/REDACT entries. Correct values:
  Text=0, Square=4, Circle=5, Highlight=8, Underline=9, **StrikeOut=11,
  Stamp=13, Ink=15**. Earlier assumption Ink=14 was wrong and broke the parser.
- `pdf_set_annot_rect`/`pdf_annot_rect` throw for Highlight/Underline/StrikeOut/
  Ink (rect_subtypes). Skip set; use `pdf_bound_annot` when listing.
- `pdf_new_dict(ctx, doc, 2)` — never NULL doc for a dict; errors otherwise.
- Crosses passed as NUL-terminated UTF-8 arrays, never BSTR. Engine single-
  threaded via SemaphoreSlim. No engine calls on UI thread.
- Flatten semantics corrected during debug: whole-page flatten would remove every
  type, violating selectable flatten. Pushed the type filter INTO the native
  primitive (`types_utf8`) and threaded `IReadOnlySet<AnnotationType>` through
  the engine seam + `FlattenForExportAsync`.
- WPF `RunBuildAsync` expects `ValueTask<int>`; FlattenForExportAsync returns
  `ValueTask`, so the VM's FlattenExportAsync manages busy/status itself.

## Verification
- Core tests: 34/34 pass.
- Fidelity tests: 6/6 pass (organizer corpus byte-identity intact).
- WPF `--smoke`: exit 0. Annotation proof:
  `annotated(p1)=Highlight,Text,Ink` → flatten Highlight →
  `remaining(p1)=Text,Ink`; `highlight_flattened=True ink_preserved=True
  note_preserved=True`; rendered page written to artifacts/annot-phase1-p1.png.
- Shim exported symbols verified via dumpbin earlier in the slice.

## Notes for the future
- Debug block in `pf_list_annotations` was removed; no leftover temp files.
- `ScratchAnnotTests.cs` (temporary) deleted once the round-trip passed.
