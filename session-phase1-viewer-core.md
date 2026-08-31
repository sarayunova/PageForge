# Phase 1 — FR-VIEW viewer core (WPF fallback)

**Scope:** Phase 1 slice: FR-VIEW viewer core in the WPF fallback app, with Core/Interop engine growth and tests. FR-PAGE and FR-ANNOT are deferred to later Phase 1 slices.

**Status date:** 29/08/2026

---

## Objective

Move PageForge from Phase 0 (done) into Phase 1 (MVP viewer & organizer, TSD §8 row 1). This session delivers **FR-VIEW viewer core first**, landing in the runnable WPF app (`src/PageForge.App.Wpf`) while the WinUI 3 app (`src/PageForge.App`) remains the product target that cannot build on this dev box (documented machine limitation — no VS UWP/MSIX build tasks). Per the locked UI-shell decision, `App.Wpf` is the runnable desktop proof and is kept in sync conceptually; it must never be deleted until WinUI builds locally.

FR-VIEW requirements targeted:

- **FR-VIEW-01** — lazy rendering, ≤2,000 pages, never whole doc in memory.
- **FR-VIEW-02** — continuous-scroll and single-page modes, zoom, view rotation.
- **FR-VIEW-03** — thumbnail panel, outline/bookmark panel, full-text search with result snippets.
- **FR-VIEW-04** — multiple documents open in tabs in one window.

---

## What changed in this session

### 1. Core + Interop (engine growth)

- **`native/PageForge.MuPdfShim/mupdf_shim.{h,c}`** — added `pf_page_text` (extract a page's plain text) and `pf_load_outline` (flatten the document outline as tab-separated `depth<TAB>page_1based<TAB>x_pt<TAB>y_pt<TAB>title`, with tabs/CR/LF in titles sanitized). Native DLL rebuilt via vcvars64 + msbuild, new exports present.
- **`src/PageForge.Core/Pdf/IPdfEngine.cs`** — extended the seam: `GetOutlineAsync` and `GetPageTextAsync`.
- **`src/PageForge.Core/Pdf/PdfOutline.cs`** — `OutlineItem(Title, PageNumber, X, Y, Depth)` record + `PdfOutline` + `PageText` record.
- **`src/PageForge.Core/Pdf/PdfOutlineParser.cs`** — culture-invariant managed parser for the shim outline format (max-5 split for tab-in-title).
- **`src/PageForge.MuPdfInterop/MuPdfEngine.cs` + `Native/MuPdfShimBindings.cs`** — P/Invoke bindings and serialized implementations of the two new methods.
- **`src/PageForge.Core/View/DocumentViewModel.cs`** — viewer host VM: lazy layout (page sizes + cumulative top offsets), clamped navigation, zoom (0.1–8.0), normalized rotation (∘360), outline load, case-insensitive full-text `SearchAsync` returning `SearchHit(PageIndex, Snippet)`.

### 2. WPF viewer (`src/PageForge.App.Wpf`)

Rebuilt the Phase-0 single-image proof into a multi-doc viewer. Dependency direction follows the skill: App → Interop → Core.

- **`MainWindow`** — `TabControl` of document tabs + "Open PDF…" toolbar (FR-VIEW-04). Each tab owns a private `MuPdfEngine` via its view-model. On launch it opens the sample document into a tab.
- **`ViewModels/`**
  - `ObservableObject` — minimal `INotifyPropertyChanged` base (the WPF proof deliberately avoids the CommunityToolkit.Mvvm package that WinUI uses).
  - `PageImageViewModel` — lazy per-page render to a frozen `BitmapSource`, DPI-tracking re-render, cancellation-safe.
  - `PageImageBehavior` — attached behavior that renders a page when its image is realized, powering the virtualized lazy page/thumbnail lists (FR-VIEW-01 bounded memory).
  - `PageSlotViewModel`, `OutlineEntryViewModel`, `SearchResultViewModel`, `DocumentTabViewModel` — bindable page/thumbnail/outline/search collections, single vs. continuous `VisiblePages`, navigation, zoom, rotation, search.
- **`Views/`**
  - `DocumentView.xaml(.cs)` — per-document surface: toolbar (continuous/single toggle, prev/next, zoom −/+ /fit, rotate ⟲/⟳), left sidebar tabs (Pages/thumbnails, Bookmarks, Search), virtualized page list, page indicator + status bar.
  - `Converters.cs` — rotation→`RotateTransform`, zoom→percent, outline indent.
- **`App.xaml.cs`** — `--smoke` headless mode now also runs a Phase-1 **viewer proof**: renders page 1 through the viewer's `DocumentViewModel` and dumps the outline + a full-text search result into additive artifacts (see below). The pinned Phase-0 `sample-phase0-p1-wpfproof.png` output is unchanged.

### 3. Tests

- **`tests/PageForge.Core.Tests/ViewerCoreTests.cs`** and updated `FakePdfEngine.cs` — 11 new tests (parser incl. tab-in-title, view-model clamp/rotation/zoom/search), alongside the original 5.

---

## Verification (all green)

- **Core tests: 16/16 passed** (`dotnet test tests/PageForge.Core.Tests`).
- **Fidelity tests: 3/3 passed** (`dotnet test tests/PageForge.Fidelity.Tests`) — exercises the real shim engine; renders stay byte-identical.
- **Managed lane builds clean** — Core, MuPdfInterop, App.Wpf all **0 warnings / 0 errors**. (WinUI `src/PageForge.App` still doesn't build on this box — known machine limitation, expected.)
- **`--smoke` EXIT=0** — both proofs pass.

### View artifact verification
```
artifacts/sample-phase0-p1-wpfproof.png   16709 bytes  (pinned Phase-0 proof, unchanged)
artifacts/sample-phase0-p1-spike.png      16709 bytes  (golden — matches mutool)
artifacts/sample-phase0-p1-mutool.png     16709 bytes
artifacts/viewer-phase1-p1.png            16709 bytes  (NEW Phase-1 viewer proof)
artifacts/viewer-phase1-outline.txt             128 bytes  (NEW: pages/outline/search dump)
```
- `viewer-phase1-p1.png` is **byte-identical** (SHA-256 match) to the pinned Phase-0/mutool render → the viewer's render path preserves the **fidelity byte-identity contract**.
- `viewer-phase1-outline.txt` shows the viewer core working end-to-end through `DocumentViewModel`: `pages=1 outline=0`, search `'PageForge'` → 1 hit on page 1 with a snippet.

---

## Outstanding / deferred

- **FR-PAGE** and **FR-ANNOT** slices (later Phase 1 work).
- **WinUI 3 app** — still must be kept in sync conceptually; builds only in the `winui-build` CI lane (continue-on-error) until the machine limitation is resolved.
- **WinAppDriver UI smoke** — TSD §8 marks this Phase 1+; the current `--smoke` headless viewer proof is the Phase 1 stand-in.
- **Session/playbook notes** — this document; consider folding a FR-VIEW checklist into the artifact-verification playbook if desired.

---

## How to verify

```
# Build managed lane (per-project; WinUI app excluded on this box)
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.Core/PageForge.Core.csproj -c Debug
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.MuPdfInterop/PageForge.MuPdfInterop.csproj -c Debug
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug

# Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests/PageForge.Core.Tests.csproj -c Debug
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests/PageForge.Fidelity.Tests.csproj -c Debug

# Headless viewer + render proofs (native DLL must be in App.Wpf output)
& src/PageForge.App.Wpf/bin/Debug/net8.0-windows/PageForge.App.Wpf.exe --smoke   # exit 0
```

---

# Phase 1 — FR-PAGE page organizer slice (30/08/2026)

**Scope:** Extends the engine with a **page-build** primitive and implements the FR-PAGE operations **merge / split / insert / delete / rotate / reorder / extract** (TRD FR-PAGE-01) in Core, with native fidelity verification and a WPF organizer UI + `--smoke` proof.

## What changed

### Native shim — `pf_build_pdf` (graft pipeline)
`native/PageForge.MuPdfShim/mupdf_shim.{h,c}`:
- New `pf_build_pdf(context, job_path_utf8, out_path_utf8)`: job-file driven page assembly.
  - Job format (tab-separated): `V<TAB>1`, `S<TAB><id><TAB><path>`, `P<TAB><srcId><TAB><page0based><TAB><rotationQuarterTurns>`.
  - Opens sources with `pdf_open_document`, creates a fresh `pdf_create_document`, grafts pages with `pdf_graft_mapped_page` (per-source graft maps), applies per-page `/Rotate` via `pdf_dict_put_int(... PDF_NAME(Rotate))`, saves with `pdf_save_document`.
  - Returns `PF_OK`/`PF_ERR`; reports failures via `pf_last_error` (UTF-8, per-thread).
- Rebuilt via vcvars64 + MSBuild; `pf_build_pdf` confirmed exported.

### Core — build seam + organizer (`src/PageForge.Core`)
- `Pdf/IPdfEngine.cs` — `PageBuildRef(string SourcePath, int PageIndex, int RotationQuarterTurns = 0)` record + `BuildPdfAsync(outputPath, pages, ct)`.
- `Pdf/PdfPageOrganizer.cs` — pure helpers mapping each FR-PAGE op to a build job: `Extract/Delete/Rotate/Reorder/Split/Merge/Insert` (all via one `BuildAsync` choke point). `SourceWithCount` record for merge sources.
- `View/DocumentViewModel.cs` — exposed `Engine` and `SourcePath` for build.

### Interop — `MuPdfEngine.BuildPdfAsync`
- `MuPdfInterop/MuPdfEngine.cs` + `Native/MuPdfShimBindings.cs` — `pf_build_pdf` binding; dedups source paths into sequential source ids, writes a UTF-8-no-BOM job temp file, calls with `Utf8Z` paths, checks output existence, cleans up the temp job.

### WPF organizer UI (`src/PageForge.App.Wpf`)
- `ViewModels/DocumentTabViewModel.cs` — organizer actions over `PdfPageOrganizer`: `RotateCurrentPageAsync`, `DeleteCurrentPageAsync`, `ExtractCurrentPageAsync`, `MergeWithAsync`, `InsertFileAtAsync`, `ReorderAsync`, `SaveCopyAsync` (each writes a new file and returns the page count).
- `Views/DocumentView.xaml(.cs)` — "Organize" toolbar group (rotate/delete/extract page, insert…), plus **drag-and-drop thumbnail reorder** ("Reorder" toggle + "Save order…"): each save opens a dialog, builds the new PDF via the engine's page-build, and raises `OpenDocumentRequested` so `MainWindow` opens the result in a fresh tab. Insert counts the picked file's pages with a temp engine; reorder stages the thumbnail list (`DocumentTabViewModel.MoveReorderItem`/`BuildOrder`) and persists it via `ReorderAsync`.
- `MainWindow.xaml.cs` — subscribes `OpenDocumentRequested` → `OpenDocumentAsync`.
- `App.xaml.cs` — `--smoke` now also runs an **organizer proof** (`RunHeadlessOrganizerProofAsync`) writing `artifacts/organizer-rotated-merged.pdf` + `organizer-proof.txt` (6 pages, p1 rotated landscape).

### Tests / fixtures
- `tests/PageForge.Core.Tests/FakePdfEngine.cs` — `OnBuild`/`LastBuild` + `BuildPdfAsync` recording.
- `tests/PageForge.Core.Tests/PdfPageOrganizerTests.cs` — 9 tests: extract/delete/rotate/reorder/split/merge/insert + validation (25 total Core tests now).
- `tests/PageForge.Fidelity.Tests/PageOrganizerFidelityTests.cs` — 2 tests exercising the **real** native build (rotate-then-merge → 6 pages; delete → 2 pages).
- `tools/generate-sample-pages.ps1` + `tools/sample-pdf/sample-pages3.pdf` — deterministic 3-page FR-PAGE fixture (kept out of the single-hash fidelity corpus as a separate `fixtures/`-style glob).

## Verification (all green)
- **Core tests: 25/25** · **Fidelity tests: 5/5** (real shim; renders byte-identical).
- **WPF app builds clean** (0 warnings / 0 errors); `--smoke` EXIT=0.
- **Byte-identity contract**: the built `organizer-rotated-merged.pdf` renders byte-identically to mutool — rotated page 1 = landscape (841.9×595.3), unrotated copy page 4 is SHA-256 identical to its source page render. Proves `pf_build_pdf` output is spec-correct (faithful rotation + content preservation).
- Smoke artifact: `artifacts/organizer-proof.txt` → `built=6 reopen=6`, `p1 841.9x595.3`, `p2..p6 595.3x841.9`.

## How to verify (FR-PAGE)
```
# rebuild native shim after touching mupdf_shim.c (see native/build-mupdf.ps1),
# then build + test the managed lane:
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests/PageForge.Fidelity.Tests.csproj
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests/PageForge.Core.Tests.csproj
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug
& src/PageForge.App.Wpf/bin/Debug/net8.0-windows/PageForge.App.Wpf.exe --smoke   # exit 0
```

## Outstanding / notes
- Drag-and-drop thumbnail reorder is wired (Reorder toggle → drag → Save order… → reopens result; verified reversed-order build renders byte-identical to mutool).
- WinUI `src/PageForge.App` remains non-buildable on this box; `App.Wpf` organizer UI is the runnable proof and must be kept conceptually in sync.
