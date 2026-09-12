# PageForge fidelity corpus

This document is the canonical note referenced by the fidelity project's
csproj comment ("Multi-row hack-free glob; see docs/fidelity-corpus.md"). It
explains what the corpus is, where the bytes live, and the invariants that
must never be broken. The authoritative *how-to* for judging visual output is
the `fidelity-regression` skill (`.opencode/skills/fidelity-regression/`).

## Layout

| Path | What it holds |
|---|---|
| `tools/sample-pdf/corpus/` | The four pinned Phase-1 corpus PDFs: `contract-multipage.pdf`, `form-application.pdf`, `scan-letters.pdf`, `unicode-multilingual.pdf` |
| `tools/sample-pdf/golden/` | The byte-pinned page-1 renders (`contract-multipage.p1.png`, …, `unicode-multilingual.p1.png`) recorded by `mutool draw -r 96` |
| `tools/sample-pdf/sample-pages3.pdf`, `sample-phase0.pdf` | Deterministic FR-PAGE fixture + Phase 0 render-gate sample (not part of the fidelity corpus) |
| `tests/PageForge.Fidelity.Tests/corpus/manifest.psd1` | The byte-pin manifest (sha256/bytes/pages/page0 per file) that the fidelity harness and CI consume |
| `tools/generate-corpus.ps1` | Deterministic generator. Defaults `-OutDir` to `tools/sample-pdf`; run with `-OutDir <throwaway>` to regenerate against a scratch dir |

## What the corpus proves (Phase 1 exit criterion)

The four real-document models exercise page fidelity, form fidelity, raster
OCR-and-edit fidelity, and Unicode reflow: `contract-multipage.pdf` (4-page
LETTER, two fonts), `form-application.pdf` (AcroForm text field + checkbox,
hand-rolled writer), `scan-letters.pdf` (2-page image-only raster proxy with
full-page images), `unicode-multilingual.pdf` (2-page Latin-1 accented text).
The suite (`tests/PageForge.Fidelity.Tests`, 48 tests) is a **corpus
regression**: a drift in any pinned hash or golden render blocks the merge
(see AGENTS.md build/test rules). The suite copies the corpus PDFs and goldens
into its own output via multi-row globs (`tools/sample-pdf/corpus/*.pdf`,
`tools/sample-pdf/golden/*.png`) and reads `corpus/manifest.psd1`.

## Invariants

1. **Byte-pinned PDFs.** Every corpus PDF's `sha256` is pinned in
   `tests/PageForge.Fidelity.Tests/corpus/manifest.psd1`. The generator must
   reproduce the exact committed bytes; regenerating else-wise is a regression.
2. **Byte-pinned golden renders.** `tools/sample-pdf/golden/*.png` are the
   recorded outputs; the diff harness compares new renders against them.
   `form-application.pdf` and `scan-letters.pdf` include images — their golden
   byte-checks guard the compressed image streams.
3. **Render-gate equality (Phase 0 / goldendiff).** For `sample-phase0.pdf`,
   the spike, WPF-proof and `mutool` renders must stay byte-identical (same
   `sha256` across all three PNGs). Verify with `/goldendiff` and `/rendercheck`;
   the `--smoke` headless mode produces the WPF proof. On this machine the
   pinned value is `5D2501313A03F2BA7B99154185C8E7141F04D89D9CA2266FA2C77627E828345C`.
4. **No line-ending corruption.** `.gitattributes` marks every `.pdf`/`.png`/
   image as `binary` so `core.autocrlf` can never rewrite bytes on checkout.
   Adding corpus files must follow the same rule. The `form-application.pdf`
   writer emits LF throughout — an LF-only xref/trailer is the only form that
   reproduces the pinned blob.

## Regenerating (correct way)

```powershell
# 1. Regenerate against a THROWAWAY dir first (never the committed default):
powershell -ExecutionPolicy Bypass -File tools/generate-corpus.ps1 -OutDir "$env:TEMP\pf-corpus-check"
# 2. Every mutool invocation is Assert-Mutool-guarded: a failing native command
#    aborts loudly (exit 1) instead of silently pinning a broken corpus.
# 3. If the hashes match the manifest, copy the PDFs/goldens into
#    tools/sample-pdf/{corpus,golden} and commit the (unchanged) pins.
```

Verification rule (from the silent-failure sweep): a regenerated corpus must be
**byte-identical** to the manifest pins — verified in both directions, that it
reproduces the pins (exit 0) *and* still fails when handed a broken mutation.