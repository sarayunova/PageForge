# Phase 3 — FR-FORM slice 3A: AcroForm fill + flatten (FR-FORM-01)

**Scope:** Phase 3 first slice. Implements FR-FORM-01 — fill and flatten AcroForm
fields (text, checkbox, radio, combo/list, button) — end to end through the real
MuPDF shim, plus an interactive WPF fill/flatten UI. DEFERRED to the next slice:
FR-FORM-02 field *creation* (MuPDF has no high-level create helper — needs low-level
AcroForm object construction).

**Status:** **DONE** — Fidelity 39/39, Core 121/121, WPF build 0/0, `--smoke` EXIT=0,
goldendiff invariant intact (spike==mutool==wpfproof, single SHA `5d250131…`), corpus
manifest pins unchanged, fill+flatten verified with `mutool` (field values become
static content; `/AcroForm` removed after flatten; `mutool clean` passes).

---

## Native shim (`native/PageForge.MuPdfShim/mupdf_shim.c/.h`)

Three new `PF_EXPORT` primitives (verified exported via dumpbin):

- **`pf_list_widgets(ctx, doc, page, out_path)`** — TSV per widget:
  `widget_index \t type_num \t field_name \t x0 \t y0 \t x1 \t y1 \t value`.
  Walks `pdf_first_widget`/`pdf_next_widget`; type via `pdf_widget_type`; name via
  `pdf_annot_obj`+`pdf_dict_get(T)`+`pdf_to_text_string`; value via `pdf_annot_field_value`.
- **`pf_set_widget_value(ctx, doc, page, widget_index, value_path)`** — reads the value
  file (`pf_read_file`), strips one trailing CR/LF, iterates to the widget index, routes
  every fillable type through `pdf_set_annot_field_value(..., ignore_trigger_events=1)`
  (checkbox/radio "Yes"/"On" checks, "Off" unchecks), then `pdf_update_widget`. Signature
  fields throw. **Gotcha:** `pdf_set_annot_field_value` returns a *validation-accepted
  tri-state* (text fields return 1 on success), NOT a success/failure boolean — so the
  shim must NOT gate on the return value; real failures surface as exceptions caught by
  `fz_catch`. The first attempt used `pdf_set_text_field_value`, which runs the widget's
  keystroke JavaScript trigger and rejected the value — hence the switch to the trigger-free
  canonical setter.
- **`pf_bake_widgets(ctx, doc)`** — `pdf_bake_document(ctx, pdf, 0 /*annots*/, 1 /*widgets*/)`
  flattens every widget document-wide in memory; the caller persists via `pf_save_document`.

## Managed layer

- **`src/PageForge.MuPdfInterop/Native/MuPdfShimBindings.cs`** — P/Invoke for the three.
- **`src/PageForge.MuPdfInterop/WidgetListParser.cs`** — decodes the TSV; maps `pdf_widget_type`
  ints to `FormFieldKind`.
- **`src/PageForge.MuPdfInterop/MuPdfEngine.cs`** — `ListFormFieldsAsync`,
  `SetFormFieldValueAsync`, `FlattenFormAsync` (gate + temp-file + status, mirroring the
  object-edit methods).
- **`src/PageForge.Core/Pdf/FormFieldModels.cs`** — `FormFieldKind` enum + `PdfFormField`
  record (`Kind`, `Id` = zero-based widget index, `Name`, `Bounds`, `Value`, `Label`).
- **`src/PageForge.Core/Pdf/IPdfEngine.cs`** — the three methods on the seam.

## Core test double

`tests/PageForge.Core.Tests/FakePdfEngine.cs` gained per-page stored `PdfFormField`s + the
three methods (fill check/radio/toggle, reject Signature/Button fill, unknown id),
`FormFlattened`, `FormValueSet`. New `tests/PageForge.Core.Tests/FormFillTests.cs` locks in
list/fill/reject/flatten contract (7 tests → Core 114→121).

## WPF UI

- **`src/PageForge.App.Wpf/Views/FormFillView.xaml(.cs)`** — page render at viewer DPI with a
  dashed box over every field + right-hand fields panel. Text/combo/list → TextBox+Set,
  checkbox/radio → CheckBox (checked=Yes, unchecked=Off); commits via
  `DocumentTabViewModel.SetFormFieldValueAsync` and re-renders. "Flatten form…" →
  `FlattenFormAsync` with a confirm. Set via `DocumentView`'s new mutual-exclusive `☑ form`
  ToggleButton (excludes ✎ text / ⬒ object modes).

## Verification

- `mutool draw -F text` on `form-application.flattened.pdf` shows **"Grace Hopper"** as static
  content; `trailer/Root/AcroForm` is **null** after flatten; `mutool clean` validates cleanly.
- Fidelity 39/39 (incl. `FormFidelityTests` — fills every corpus PDF's fields, proves value
  round-trip, flattens, verifies no widgets remain on reopen, renders; formless corpus docs
  validate the empty-list path).
- All other gates green (see Status header).

## Non-goals / next

- Tesseract OCR / FR-OCR and signature/redaction FR-SEC are later Phase 3 slices.

---

# Phase 3 — FR-FORM slice 3B: create form fields (FR-FORM-02)

**Scope:** Implements FR-FORM-02 — create a new AcroForm field end to end through the real
MuPDF shim, plus an interactive WPF "New text field…" UI. **Text fields only** (`/FT /Tx`):
checkbox/radio need manual `/AP` appearance streams, which MuPDF's `pdf_write_widget_appearance`
refuses for `/Btn` (throws) — explicitly out of scope this slice.

**Status:** **DONE** — Core 124/124, Fidelity 40/40 (incl. new
`Create_field_fills_then_flattens_to_static_content`), WPF build 0/0, `--smoke` EXIT=0
(full dogfood incl. form-application.pdf), goldendiff invariant intact (single SHA
`5d250131…`), corpus manifest pins unchanged.

## Native shim (`native/PageForge.MuPdfShim/mupdf_shim.c/.h`)

- **`pf_create_field(ctx, doc, page, spec_path)`** (exported, verified via dumpbin). Reads a
  TSV/line spec: `K\tTXT` (kind), `N\t<name>` (/T), `R\tx0\ty0\x1\ty1` (rect), `F\t<flags>`
  (`/Ff`), `M\t<maxlen>` (`/MaxLen`), `Q\t<quadding>` (`/Q`), `W\t<border>` (`/BS`).
  Builds the field + widget annotation by hand (no `pdf_create_field` in MuPDF 1.28.3) by
  adapting `pdf_create_signature_widget`:
  `pdf_create_annot_raw(..., PDF_ANNOT_WIDGET)`, mutates via `pdf_annot_obj`, then registers
  the object in the AcroForm `/Fields` array (creating `Root/Fields`+`/AcroForm` if absent).
  Sets `/FT /Tx`, `/DA "/Helv 12 Tf 0 g"`, `/F PDF_ANNOT_IS_PRINT`, then `pdf_update_widget`
  to generate `/AP`. Wrapped in `pdf_begin_operation`/`pdf_end_operation` (abandon on error,
  `pdf_delete_annot` cleanup).
- **Gotcha (already known):** `pdf_annot` is opaque — `annot->obj` is illegal out-of-package
  (`error C2037`); must use the public `pdf_annot_obj(ctx, annot)` accessor.
- **Fonts:** text fields auto-acquire the base-14 `/Helv` Helvetica via `add_required_fonts`
  (pdf-appearance.c); verified in the produced PDF (`mutool info` shows `18 0 R Helvetica`).

## Managed layer

- `MuPdfShimBindings.cs` — `pf_create_field` P/Invoke.
- `IPdfEngine.CreateFormFieldAsync(FormFieldSpec, ct)` — rejects non-text kinds with
  `NotSupportedException` (until a later slice adds `/Btn`).
- `MuPdfEngine.CreateFormFieldCoreAsync` + `BuildFormFieldSpec` — writes the TSV to a
  `pageforge-field-spec-{Guid}.txt`, calls the shim, refreshes the page-level field list.
- `FormFieldModels.cs` — new `FormFieldSpec` record, `FormFieldFlags` constant class
  (`ReadOnly=1`, `Required=1<<1`, `Multiline=1<<12`, `Comb=1<<24`), `FormFieldJustification`
  enum. Note: the earlier invented `FormFieldFlags.MaxLengthPlaceholderOrNone()` placeholder
  was removed in favor of `Required|Comb` + `MaxLength: 8`.

## Tests

- Core: `FakePdfEngine` gained `FormFieldsCreated` tracker + `CreateFormFieldAsync` (silently
  appends a `PdfFormField`, rejects non-text). `FormFillTests` adds 3 tests (lists back,
  fillable, rejects non-text). Core 114→124.
- Fidelity: `Create_field_fills_then_flattens_to_static_content` — create a required/comb
  text field on page 0 of the preferred corpus PDF, assert field list grows +1 and fill
  round-trips, flatten+save, reopen has no widgets, render PNG. Fixed a doc-comment typo
  (`flatten,  save` → `flatten, save`).

## End-to-end (mutool)

`form-create.created.pdf` (7220 B) + `.p1.png` (14304 B): `mutool info` opens clean (4 pages,
auto-provisioned Helvetica), `mutool draw -F text` shows the filled comb digits
`1 2 3 - 4 5 6 - 7 8 9 0` as static text, `mutool clean` exit 0, reopen lists no fields
(flattened).

## WPF UI

`DocumentTabViewModel.CreateFormFieldAsync(FormFieldSpec, ct)` (after `FlattenFormAsync`);
`FormFillView.xaml` gains a "New text field…" toolbar button wired to `NewField_Click`, which
shows a `PromptFieldName()` modal and creates a default required field on the current page,
then re-renders.

## Non-goals / next

- Checkbox/radio field creation (needs manual `/AP` streams — `/Btn` unsupported for
  auto-appearance) and comb/multiline validation extras are later slices.
- Tesseract OCR / FR-OCR and signature/redaction FR-SEC are later Phase 3 slices.
