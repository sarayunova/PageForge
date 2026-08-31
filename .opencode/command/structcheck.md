---
description: Phase 0 structural validation — qpdf --check and strict mutool clean.
agent: build
---

Validate structure of the PDF in $ARGUMENTS with strict tooling. Requires `qpdf` (download:
`qpdf/qpdf` GitHub latest `*-msvc64` zip) and the built mutool.

1. `qpdf --check <pdf>` — must report "No syntax or stream encoding errors"; a warning that
   Matches a *common error* (e.g. offset 0) is a defect to fix, not accept.
2. `mutool clean <pdf> <tmp>.pdf` — must exit 0 with **no** "repairing PDF document" line
   (that message means the document is malformed even if the output renders).
3. `mutool info <pdf>` — page count and box must match the corpus manifest
   (`tests/PageForge.Fidelity.Tests/corpus/manifest.psd1`).
Report pass/fail for each step and the exact tool output for any failure.