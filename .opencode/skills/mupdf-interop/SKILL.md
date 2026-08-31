---
name: mupdf-interop
description: Use when working on the MuPDF binding layer (PageForge.MuPdfInterop) — IPdfEngine surface, P/Invoke signatures, marshaling gotchas, threading rules, and content-stream editing flows.
---

# MuPDF interop patterns (PageForge)

Project-specific binding knowledge unlikely to be in general training data.

## Interface surface (`IPdfEngine` in Core)
- Render page → bitmap tiles (lazy; supports FR-VIEW-01, 2000+ pages).
- Text extraction + hit-testing to a content-stream text run (FR-EDIT-01).
- Content-stream rewriting for edits; return old/new operator lists for undo (see command layer).
- Embedded-image / vector object move-resize-replace (FR-EDIT-04).
- OCR hook (Tesseract) and form-field read/write (FR-FORM).

## Marshaling gotchas
- All `fz_*` objects are `IntPtr`; hold JavaDoc-style refs only as long as needed; deterministic dispose (never rely on finalizers).
- Strings cross the boundary as UTF-8 byte arrays, not Unicode BSTRs.
- Document/page/device lifetimes: render contexts must stay alive until the bitmap is fully copied out.
- Treat native structs as opaque; read fields through getters, not P/Invoke layout guesses.

## Threading
- Never call MuPDF on the Windows UI thread.
- Serialize engine calls (lock or single worker queue) — MuPDF contexts are not thread-safe by default.

## Text edit flow (TSD §6)
hit-test run → extract run + font ref → editable overlay → on commit rewrite run operators and recalc bounding box → record old/new operators for undo.

## Font fidelity (FR-EDIT-03)
Check embedded subset for required glyph before committing an inserted character; on miss, resolve from the bundled font-fallback table and flag the run.

## Licensing
MuPDF is AGPLv3 — keep its attribution/license header on packaging and never combine with GPL-incompatible deps.