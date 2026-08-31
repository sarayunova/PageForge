# PageForge — Artifact-Layer Verification Playbook

**Purpose:** How the coding agent (opencode, "big-pickle") verifies PageForge
visually without being able to operate the WinUI 3 window directly.

**Status:** Reference for agent use during development. Companion to the TRD
and TSD; does not alter them.

---

## 1. Core principle

The agent cannot *look at the running app window*, but it CAN read artifact
files (PNG, PDF, text dumps) with its `read` tool. Therefore every visual
assertion is converted into a **deterministic artifact** that any script,
test, or CI job can produce — and the agent inspects the artifact.

Move the verification target onto the artifact layer. Never rely on a human
screenshot of a running app as the primary feedback loop.

## 2. The verification loop

```
write/edit code
      |
      v
add or update an artifact-producing script/test
      |
      v
run it (headless)
      |
      v
read the artifact (PNG/PDF/diff) with the Read tool
      |
      v
fix defects -> repeat
```

Every page-rendering or UI-surfacing change must produce an artifact that the
agent reads before the change is considered done.

## 3. Verification layers (in priority order)

| Layer | Tooling | What it catches | Notes |
|---|---|---|---|
| 1. Engine rendering | MuPDF `mutool draw` / IPdfEngine bitmap path | Pixel-level rendering fidelity, text clipping, overlap | Runs headless; the rendering truth lives in the engine, not WinUI |
| 2. Golden-image suite | Pixel-diff of canonical renders against committed PNGs | Regressions on the fidelity corpus | The TSD's "single highest-priority test asset"; Phase 1 corpus is manifest-driven (byte-pinned sha256 per PDF + golden p1 render) — see §6 |
| 3. UI automation | WinAppDriver smoke tests + window screenshots (PNG) | Real-app flows: open, edit, save, annotate | Agent reads captured screenshots directly |
| 4. Structural validation | `qpdf --check`, `mutool clean`, `pdftotext`, tag-tree/PDF-UA inspection | Corruption, broken structure, missing tags/bookmarks/fields | Deterministic; runs before any human looks |
| 5. Human sign-off | Manual review | Aesthetic taste, design polish, spacing judgment | The only layer a human must own; intentional, not a gap |

## 4. Artifact conventions

- Artifacts live under `artifacts/` next to the repo root (gitignored except
  the committed golden corpus).
- Name by intent, not timestamp: `page-12-golden.png`, `diff-redaction-3.png`,
  `smoke-edit-save.png`.
- Scripts that produce artifacts live in `tests/` beside the harness that
  consumes them.
- Every artifact-producing test accepts an `--dump` flag so the agent can ask
  for a specific page/layer render on demand.

## 5. How the agent asks for a visual check

- `/rendercheck path\to.pdf --page 12 --dump` — engine-render a page to PNG.
- `/goldendiff path\to.pdf` — diff against the committed golden render.
- `/smoke edit-save` — run the WinAppDriver flow, capture screenshots.
- `/structcheck path\to.pdf` — run qpdf/mutool/tag-tree validation.

These are opencode **commands** (`.opencode/command/*.md`) that wrap the
underlying scripts so the loop is one keystroke.

## 6. Phase mapping

| Phase | Primary verification weapon |
|---|---|
| 0 — Foundation | `mutool draw` page render (proves the binding renders on x64/ARM64); agent reads the PNG |
| 1 — Viewer & organizer | WinAppDriver screenshots + golden thumbnail renders; **corpus smoke + dogfood gates** (TSD §12) |
| 2 — Content editing | Golden-image diffs per edit op; structural checks on edited output; every edit is an `IEditCommand` on the `EditCommandStack` with a crash-recovery `EditJournal` (FR-EDIT-05) |
| 3 — Forms & OCR | Structural validation (form fields re-open correctly) + text-extraction diff |
| 4+ — Hosted | HTTP-level contract tests; web app artifacts are out of agent scope where browsable, covered by API tests |

### 6a. Phase 1 real-document corpus + dogfood gate (TSD §12)

The fidelity corpus is the Phase 1 exit gate. It must be exercised on the **real
MuPDF shim**, not a fake.

- **Artifacts:** `tools/sample-pdf/corpus/*.pdf` (4 deterministic docs: contract,
  form, scan, Unicode) + `tools/sample-pdf/golden/*.p1.png` + byte-pinned
  `tests/PageForge.Fidelity.Tests/corpus/manifest.psd1` (sha256, page count,
  page-0 size per file). Regenerate with `powershell -ExecutionPolicy Bypass
  -File tools/generate-corpus.ps1` (deterministic; hashes change on regen).
- **Smoke gate** (`tests/PageForge.Fidelity.Tests/CorpusSmokeTests.cs`): every
  PDF opens, every page renders, render is byte-deterministic, and the committed
  file still matches its manifest sha256 (a drift blocks the merge).
- **Dogfood gate** (`CorpusDogfoodTests.cs` + WPF `--smoke`
  `RunHeadlessCorpusDogfoodProofAsync`): for each corpus doc, open → render
  every page → organizer build (reorder+rotate) and reopen → add highlight/ink/
  text-note, flatten on save and reopen. Any shim crash → exit 1. The WPF proof
  writes `artifacts/corpus-dogfood-proof.txt` (`open+render+organize+annotate=ok`)
  and the test suite asserts the same pipeline under xunit.
- **`--smoke` exit 0 is the CI gate**; the dogfood stage runs last so a corpus
  crash dominates the process exit code.

## 7. Definition of done (visual)

A change is not mergeable until:

1. It produced an artifact (render, screenshot, or structural dump).
2. The agent read the artifact and asserted intent (correct output, no
   overlap/clipping/corruption).
3. A human signed off on aesthetic-only layers where taste matters.

---

*Created for agent reference. Not part of the TRD/TSD acceptance criteria.*