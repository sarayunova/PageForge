---
name: fidelity-regression
description: Use when adding a PDF to the fidelity regression corpus, extending the diff harness, or judging whether edited output passes the fidelity suite. Source of truth for what "looks right" means.
---

# Fidelity regression corpus (PageForge)

The TSD's single highest-priority test asset. Every CI run edits the corpus programmatically and diffs against expected output.

## Location
`tests/PageForge.Fidelity.Tests/` — corpus PDFs under `corpus/`, harness alongside, golden renders under `golden/`. Scripts that dump artifacts live in `tests/`.

## Adding a new PDF
1. Drop the PDF into `corpus/` (contracts, forms, scans, multi-column layouts — see TSD §8).
2. Add a manifest line: source, what edits to perform, expected result.
3. Produce and commit a baseline: structural dump (`qpdf`, `mutool clean`) + golden render (PNG) + text extraction.
4. The agent must read the golden render with the Read tool and assert intent before committing it.

## Defining "pass"
- Reopens in Acrobat Reader with no visible corruption (TRD acceptance §10).
- No silent overlap after growth: box-bounded reflow grows cleanly **or** produces the collision warning (FR-EDIT-02).
- Structure not touched by an edit is preserved: tags, bookmarks, form fields, /UA metadata (FR-EDIT-06).
- Font substitution is always surfaced (FR-EDIT-03).

## Diff layers (in order)
1. Structural: `qpdf --check`, `mutool clean`, tag-tree inspection
2. Pixel: golden-image diff
3. Text: `pdftotext` extraction diff

A regression in any layer blocks the merge. Refresh `docs`/`playbook` pointers if this corpus grows.