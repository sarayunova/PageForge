# Session: cover-and-replace editing for scanned text

**Date:** 2026-09-29

## Why
A user OCR'd a scanned book, clicked a word with the Text tool, and got "Failed to
rewrite the text of run 86 ... no content operator paints the run". OCR text is a
hidden layer (`3 Tr`, a two-byte `GlyphLessFont`, a new `Tz` per word): the in-place
rewriter cannot match it, and rewriting it would not change what the page shows
anyway. Commit `1764654` first made the failure explain itself (`PdfTextRun.IsHiddenOcrText`);
the user then asked for real editing.

## What was built
- **Native** `pf_cover_replace_text` (`native/PageForge.MuPdfShim`): appends ONE new content
  stream that fills a rectangle in the paper colour and shows the text in Helvetica.
  Append-only, so text-edit receipts (which pin stream indices) stay valid. Latin-1 only;
  anything else is refused, never substituted. Grows the cover to the right for longer text.
- **Core** `ScanTextModels.cs` (`ScanTextReplacement`, `ScanTextPlanner`, `ScanColorSampler`),
  `IPdfEngine.CoverAndReplaceTextAsync`, `ScanTextEditCommand` (snapshot, remove the old OCR
  words with a text-only redaction, cover, undo by restoring the snapshot; rolls back on failure).
- **App** `DocumentTabViewModel.ReplaceScanTextAsync` samples paper/ink and the baseline from a
  150 DPI render; `DocumentView.EditRunAsync` routes hidden-OCR runs there, with a one-time
  per-document notice that the original is hidden, not erased (points to Redact).
- OCR runs are ONE WORD each, so editing a line means editing its words. A longer word grows
  over its neighbours and triggers the existing collision confirmation; the neighbours' hidden
  words are cleared too (`ClearRegion`), so search never finds words the page no longer shows.

## Bugs found on the way (both fixed)
1. `pf_apply_redactions` parsed its options file on tabs only. Every option but "black box"
   was ignored, so "keep images" removed them. Symptom that exposed it: the visible words next
   to the edit vanished. Regression test `Redaction_options_reach_the_engine`.
2. `build-mupdf.ps1` could not run here: SDK 10.0.26100.0 has no `ucrt.lib`, the Build Tools
   have no x86 CRT libs (`OLDNAMES.lib`). The script now picks the newest SDK that has
   `ucrt.lib` and builds `bin2coff` as x64. Render output stayed byte-identical (62 Fidelity pass).

## Verification
Core 196, Fidelity 62, Api 47, UiSmoke 21 all pass. The headless `--smoke` run has a new
`scan text proof` that goes through the real view-model. Fidelity test
`Scanned_text_is_replaced_by_covering_it_and_undo_restores_it` writes `scan-edit-page1.png`
into the test output for inspection by eye.

## Known limits
- Replacement font is Helvetica, whatever the scan uses.
- The cover blends into flat paper; on a textured or photographic background it will show.
- Rotated pages fall back to white paper / black ink for colour sampling.
- Not clicked through in the live app by the user yet.
