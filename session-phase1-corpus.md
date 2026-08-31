# Phase 1 — FR-CORPUS real-document fidelity corpus + dogfood exit gate

**Scope:** Lands the Phase 1 real-document fidelity corpus (TSD §12 exit gate):
four deterministic realistic PDFs, byte-pinned manifest, golden page-1 renders,
per-document smoke tests, and a full-pipeline dogfood gate driven on the real
MuPDF shim from both the xunit suite and the WPF `--smoke` run.

**Status:** **DONE** — corpus generated + pinned; smoke 20/20, Core 34/34,
`--smoke` dogfood gate EXIT=0.

---

## What was built

- **`tools/generate-corpus.ps1`** — deterministic, idempotent corpus generator
  using `mutool create -O reproducible` (no timestamps → stable bytes):
  - `contract-multipage.pdf` — 4-page LETTER (title, 2 body, signature page),
    Helvetica + Times-Roman.
  - `form-application.pdf` — 1-page LETTER AcroForm; hand-rolled PDF writer
    (mutool create has no widget DSL) with `FullName` text field + `Consent`
    checkbox.
  - `scan-letters.pdf` — 2-page raster/scan proxy (image-only pages; embedded
    PNGs compressed with `mutool clean -z -i` for determinism).
  - `unicode-multilingual.pdf` — 2-page LETTER with Latin-1 accented text
    (French, Spanish, German; WinAnsi bytes written directly).
  - Draws `golden/*.p1.png` (96 dpi mutool) per doc and prints a sha256/pages/
    page0 manifest. Corpus at `tools/sample-pdf/corpus/`, goldens at
    `tools/sample-pdf/golden/`.
- **`tests/PageForge.Fidelity.Tests/corpus/manifest.psd1`** — machine-readable
  pin: sha256, source, page count, page-0 size, golden path, expected for each
  of the 4 docs. Keyed by file name so the harness resolves per-file metadata.
- **`tests/PageForge.Fidelity.Tests/CorpusSmokeTests.cs`** — 3 theories × 4
  docs + 1 fact (13 cases): opens and renders through the real shim with
  correct page count / page-0 dims / PNG signature; every page renders safely;
  render is byte-deterministic; and **committed corpus files still match their
  pinned manifest sha256** (drift blocks the merge).
- **`tests/PageForge.Fidelity.Tests/CorpusDogfoodTests.cs`** — 1 theory × 4
  docs (4 cases): full pipeline on the real shim — open, walk/render every
  page, organizer build (reorder + rotate page 90°) and reopen, add highlight
  + ink + text-note, flatten on save and reopen. Zero crashes = pass.
- **WPF dogfood stage** (`src/PageForge.App.Wpf/App.xaml.cs`
  `RunHeadlessCorpusDogfoodProofAsync`) — mirrors the test pipeline headlessly
  and writes `artifacts/corpus-dogfood-proof.txt` per doc as
  `open+render+organize+annotate=ok`. Runs **last** in `--smoke` so a corpus
  crash dominates the process exit code; any failure → exit 1.
- **`tests/PageForge.Fidelity.Tests/PageForge.Fidelity.Tests.csproj`** —
  links `tools/sample-pdf/corpus/*.pdf` + `golden/*.png` into the test output
  `corpus/` and `golden/` (PreserveNewest); `manifest.psd1` copied alongside.
- **Playbook** — `PageForge_Artifact_Verification_Playbook.md` §6a documents
  the corpus + dogfood gate; phase-1 row and golden-suite row updated.

## Key facts / painful lessons

- `dotnet test` here accepts only **one** project per invocation (MSB1008 with
  two); the AGENTS.md test line was run as two separate `test` calls.
- Corpus lives in `tools/sample-pdf/` (repo source of truth) and is **Linked**
  into the test output directory — never copy-paste the PDFs into the test
  project folder, or the manifest-`corpus` vs `fixtures` split breaks.
- `mutool create -O reproducible` alone is not enough for the scan doc — the
  raw embedded PNGs blow the file up, so it is post-processed with
  `mutool clean -z -i` (deterministic) before pinning.
- The dogfood gate deliberately uses the **real engine** (no fakes) so a shim
  crash — the failure class that matters at the Phase 1 exit gate — is caught.

## Verification (all green)

- **Core tests: 34/34** (`dotnet test tests/PageForge.Core.Tests`).
- **Fidelity tests: 20/20** (13 smoke + 4 dogfood + 1 annotation + 2 organizer)
  — real shim, renders stay byte-identical, manifest hashes match.
- **WPF build clean** (0 warnings / 0 errors); **`--smoke` EXIT=0** covering
  Phase 0 render proof, viewer proof, organizer proof, annotation proof, and the
  corpus dogfood proof.
- `artifacts/corpus-dogfood-proof.txt` → `dogfood_corpus_count=4`, each doc
  `open+render+organize+annotate=ok`; per-doc page counts 4/1/2/2 match the
  manifest.

## How to verify

```
# Regenerate corpus (deterministic; hashes change) then re-pin manifest on purpose:
powershell -ExecutionPolicy Bypass -File tools/generate-corpus.ps1

# Build + tests (per-project; WinUI app excluded on this box)
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests

# Dogfood exit gate (native DLL must be in App.Wpf output) — exit 0
& src/PageForge.App.Wpf/bin/Debug/net8.0-windows/PageForge.App.Wpf.exe --smoke
# -> artifacts/corpus-dogfood-proof.txt
```

## Outstanding / notes

- WinUI `src/PageForge.App` remains non-buildable on this box; the dogfood
  `--smoke` stage lives in `App.Wpf` and must be kept conceptually in sync.
- `docs/fidelity-corpus.md` referenced by the fidelity csproj comment does not
  exist yet; the playbook §6a is the current pointer.