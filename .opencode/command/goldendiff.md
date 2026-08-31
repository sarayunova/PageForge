---
description: Phase 0 golden diff — byte-compare engine, mutool, and WPF-proof renders.
agent: build
---

Phase 0 has no committed `golden/` corpus yet (that arrives with real-world PDFs, see the
`fidelity-regression` skill). Today this command verifies the golden invariant we do have:
the same MuPDF build must produce byte-identical output across all render paths.

1. Ensure `artifacts/sample-phase0-p1-*.png` exist (run `/rendercheck` then WPF smoke:
   `src/PageForge.App.Wpf/bin/Debug/net8.0-windows/PageForge.App.Wpf.exe --smoke`).
2. Compute the SHA-256 of every PNG under `artifacts/`; all must be equal (spike == mutool ==
   wpfproof). Report the shared hash, or the per-file hashes plus a first-diverging-byte offset
   if any differ, and the likely cause (engine change, corpus change, toolchain change).
3. `tools/sample-pdf/sample-phase0.pdf` must still hash to the `sha256` pinned in
   `tests/PageForge.Fidelity.Tests/corpus/manifest.psd1`; regenerating commits a regression.