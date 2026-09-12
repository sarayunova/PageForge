# PageForge — Project Analysis Report

**Date:** 2026-09-12
**Scope:** Repository analysis of `./pageforge` at commit `1aa5fb6` (master == origin/main).

---

## 1. Executive summary

PageForge is an open-source (AGPLv3) **PDF viewer, editor, and document platform for Windows** by LiVi Software Company — marketed as an Adobe Acrobat alternative. It is a genuine "two halves" product: a **fully-offline desktop app** (view, annotate, edit text/objects, fill forms, redact, encrypt, OCR, sign) plus an **optional hosted ASP.NET Core API** (accounts/billing, sync, e-sign, team review, batch OCR). The v0.1 beta repository is substantially built: all three CI lanes were made green for the first time in the project's history on 2026-09-04, and the repo is live and public at `github.com/sarayunova/PageForge`, in sync with `origin/main` (working tree clean).

The project is unusually disciplined for an agent-built codebase: a TRD/TSD/verification-playbook trio of "source-of-truth" documents, an artifact-layer verification workflow, a byte-pinned fidelity corpus, and a ruthless post-hoc "reports success while doing nothing" sweep that found nine silent-failure defects and was completed on 2026-09-12. The sign-or-not decision was resolved on 2026-09-12 in favour of shipping the beta unsigned until signing is configured; Phase 6 evidence was produced on the same date (recorded load-test runs + a WCAG 2.1 AA self-assessment, both in `docs/phase6-evidence/`). Remaining open items are the WCAG remediation worklist the self-assessment exposes and the post-beta WinUI 3 / ARM64 track.

## 2. Codebase at a glance

| Area | Files | Lines (C#/XAML) |
|---|---|---|
| `src/PageForge.Core` | 34 | 3,207 |
| `src/PageForge.MuPdfInterop` | 7 | 1,683 |
| `src/PageForge.App.Wpf` (shipping shell) | 18 | 3,934 |
| `src/PageForge.App` (WinUI 3 spike) | 4 | 168 |
| `services/PageForge.Api` | 63 | 8,299 |
| `tests/` (5 projects) | 42 | 6,340 |
| Native shim (`mupdf_shim.c/.h`, `pf_sig_crypt32.c`) | 3 | 6,959 |
| CI/CD workflows (2) | 2 | 351 |

All C#/XAML ≈ **24,500 lines**. The tree also contains the full extracted **MuPDF 1.28.3 source (~615k lines of C across 1,513 .h + 407 .cpp)** under `native/out/_work` — that is build output, not repo content (packed repo is only 3.72 MiB).

## 3. Architecture

- **Dependency direction:** `App → MuPdfInterop → Core`. `Core` never references WinUI or MuPDF; all PDF access goes through a single swappable seam, `IPdfEngine` (`src/PageForge.Core/Pdf/IPdfEngine.cs`) with 30+ async methods spanning render, text runs, page build, annotations, form fields, redaction, OCR, and encryption. Tests substitute a 533-line `FakePdfEngine`.
- **Engine threading contract:** `IPdfEngine` implementations are **not thread-safe**; `MuPdfEngine` serializes via a `SemaphoreSlim` gate and temp-file/UTF-8 hand-offs to a tiny C ABI.
- **Native shim** (`native/PageForge.MuPdfShim`, built by `native/build-mupdf.ps1` via vcvars64/MSBuild into `pageforge_mupdf.dll`): a hand-curated `PF_EXPORT` C ABI over MuPDF static libs — ~25 primitives (`pf_open_document`, `pf_render_page_to_png`, `pf_rewrite_text_run`, `pf_build_pdf`, `pf_apply_redactions`, `pf_ocr_*`, `pf_save_encrypted`, `pf_sign_pdf`, `pf_list_signatures`, …). Strings cross as UTF-8, errors via `pf_last_error`. This is the deliberate, reviewed interop boundary (see `mupdf-interop` skill). Digital signatures (`pf_sig_crypt32.c`) use Windows crypt32, fully offline.
- **Editing command layer** (`src/PageForge.Core/Editing/`): `IEditCommand` Do/Undo, `EditCommandStack` (unlimited undo/redo, FR-EDIT-05), `DelegateEditCommand`, `CompositeEditCommand`, and `EditJournal` — an append-only, byte-level crash-recovery journal with torn-tail tolerance (TSD §3.1 reliability requirement).
- **Layered separation is clean:** overflow detection (`TextOverflowDetector`), font-fidelity analysis (`FontFidelityAnalyzer`/`FontFallbackTable`), page organization (`PdfPageOrganizer`), annotation, redaction, security, and OCR services all live in Core as pure logic over the engine seam.

## 4. Feature coverage vs TRD requirements

Applied FR coverage (with verification evidence):

- **FR-VIEW** — lazy page/thumbnail virtualization, tabs (multi-doc), continuous/single scroll, zoom, rotation, outline + full-text search. ✔
- **FR-ANNOT** — highlight/underline/strikethrough/ink/notes/shapes + per-type flatten on export. ✔
- **FR-EDIT** — text-run rewrite with in-place content-stream operator splicing, undo/redo receipts, overflow/collision detection, font-fidelity substitution, object move/resize/replace. ✔
- **FR-FORM** — fill + flatten text/checkbox/radio/combo/list; create new **text-only** fields (checkbox/radio *creation* deferred — `/Btn` appearance streams). ✔ / partial
- **FR-PAGE** — merge/split/insert/delete/rotate/reorder/extract via one `pf_build_pdf` graft primitive, drag-and-drop reorder. ✔
- **FR-OCR (local)** — Tesseract (Apache-2.0, bundled traineddata) → searchable PDF / DOCX (native OOXML writer) / per-page PNG zip. ✔
- **FR-SEC** — encryption (RFC 9506, AES-256), true content-stream redaction, digital signature sign + verify via crypt32. ✔
- **Hosted** — accounts (JWT + Google/MS OAuth), Stripe billing + webhooks, sync/versioning with conflict metadata, e-sign lifecycle, team review comments, batch OCR worker (native MuPDF+Tesseract or a test no-op), plus AGPL `/source` endpoint. ✔ coverage; productionization (Redis/queue/S3 wiring, load targets) still pending — see open items.

## 5. Tests & verification culture

- **Three suites** (must run separately due to an SDK quirk): Core 154, Fidelity 48, Api 46 tests.
- **Fidelity corpus** (`tools/sample-pdf/corpus/`): 4 deterministic real-world-ish PDFs (contract, AcroForm, scan, Unicode) with a **sha256-pinned manifest** (`manifest.psd1`) that *blocks merges* on drift, golden page-1 renders, a smoke suite, and a dogfood gate that runs open→render→organize→annotate on the real shim (both in xunit and the WPF `--smoke` headless mode).
- **Render-equality contract:** the engine render, the WPF proof, and `mutool draw` output must be **byte-identical** (single SHA `5d250131…`); this is checked in CI and via `.opencode/command/goldendiff.md`.
- **CI is now genuinely green** (3 lanes: native MuPDF build → managed build+tests+render proof → enforcing WinUI-build). Only in Phase 7 was it discovered that CI had *never* passed: the "reports success while doing nothing" sweep fixed 9 silent-pass defects (test-failure swallowing, `$LASTEXITCODE` misuse, CRLF corruption of committed PDFs, skipped-with-passed OCR tests, vacuous bytes-compares, etc.).

## 6. CI/CD & release pipeline

- `ci.yml`: native build (~8m30s) → managed build `-warnaserror` → 3 test suites each checked → byte-compare proof → WinUI lane via Visual Studio MSBuild (vswhere-located, never hardcoded VS paths).
- `release.yml`: stage payload (`publish-release.ps1 -NoZip`) → Azure login (OIDC) → **Azure Artifact Signing** → re-stage `-ZipOnly -RequireSignature` (signtool verify *before* zipping) → draft GitHub Release. Decided 2026-09-12: when signing isn't configured, a `v*` tag still creates a draft release with an unsigned payload whose release notes say so and warn about SmartScreen/Defender/browser "unknown publisher" prompts — unsigned beta ships over a withheld one (TSD §12.1; the signed path re-activates automatically once the Azure secrets/variables exist).

## 7. Key open items / risks (from the Phase 7 handoff)

1. **Signing — RESOLVED (2026-09-12):** ship unsigned until Azure Artifact Signing is configured. `release.yml` gate relaxed, release notes made truthful (+ SmartScreen warnings), TSD §12.1 amended, handoff/docs updated. The Azure path still needs the maintainer's account + identity validation (individual ≈3 business days, business 3+ years) before signed installers take over distribution.
2. **Phase 6 exit evidence — recorded (2026-09-12).** `tools/loadtest/` gained a `--warm` phase (JIT cold-start was dominating the tail: first-ever run FAILED with p95=512ms until warm-up was added) and three runs are recorded in `docs/phase6-evidence/load-test-report.md`: 20VU×3it p95=92ms, 40VU×3it p95=170ms, 0 failures — targets met against the hermetic in-memory host (real Postgres/MinIO runs remain future work). The WCAG 2.1 AA **documented self-assessment** (format confirmed with the maintainer) is `docs/phase6-evidence/wcag-2.1-aa-self-assessment.md`: the shell does **not** yet pass — the assessment is an honest FAIL on 1.1.1/2.1.1/1.4.3/4.1.3 etc., with a 7-item prioritized remediation plan that is the concrete remaining work to meet the "AA pass on core screens" exit criterion.
3. **Shell is WPF, not WinUI** — `PageForge.App.Wpf` (~3.9k lines, all product UI) is the shipping shell; `PageForge.App` is a 168-line WinUI spike kept from rotting by the enforcing CI lane. Porting is post-beta (TSD §12.1 amendment). x64 only; ARM64 deferred.
4. **Sweep's last item — DONE (2026-09-12)** — `tools/generate-corpus.ps1` gained an `-OutDir` switch and all nine unchecked `mutool` invocations are now guarded by `Assert-Mutool`. The `-OutDir` verification caught a real drift (form-application's hand-rolled xref writer emitted CRLF against an LF-pinned blob); the writer was normalized to LF, and all four PDFs + four goldens now regenerate byte-identical to the manifest pins. Verified in both directions: success path reproduces pins (exit 0), and a failing `mutool` now aborts loudly (exit 1) instead of silently continuing.
5. Minor: `main` had **no branch protection** — now added (2026-09-12, see §9.4); actions pins were bumped; `.gitattributes` added; `docs/fidelity-corpus.md` referenced but missing; `-SkipApi` README rationale added; WCAG-audit-format question resolved (documented self-assessment → produced).

## 8. Observations / assessment

**Strengths:** remarkably coherent architecture with a genuinely testable engine seam; the strongest quality signal is *process* — source-of-truth docs, artifact-first verification, and a documented history of catching its own silent-failure defects and verifying each fix in both directions (passes healthy + still fails broken). The native ABI is deliberately tiny and documented. Offline-first is respected (core features never touch the network; hosted services optional by construction).

**Weaknesses/risks:** the desktop shell is a *proof/toy* relative to a shippable app — UI is thin over the engine, WinAppDriver UI automation isn't run (the `UiSmoke.Tests` project exists; `--smoke` headless is the stand-in), and the "Word-like text editing" depth gate (FR-EDIT-03 font-subset enforcement) is a proxy. The hosted API is a development-stage monolith dependent on Postgres/Redis/MinIO/Stripe config with no production deploy path (releases pass `-SkipApi`). Volume is modest for the claimed feature breadth — much functionality is driven through the native shim, making the hand-rolled content-stream editing the highest-fidelity-risk (and most valuable) part of the code. AGPL compliance is handled unusually well for an AI-built project (license headers enforced, `/source` endpoint + in-app links, notices).

## 9. Recommended next steps (priority order)

1. ~~Resolve the signing decision~~ **DONE (2026-09-12)** — option B (ship unsigned) implemented across `release.yml`, TSD §12.1, README/CONTRIBUTING/CHANGELOG and the handoff; tagging `v0.1.0-beta` now produces an unsigned draft release. Tag once CI stays green.
2. ~~Add an `-OutDir` switch to `tools/generate-corpus.ps1`, then add the exit-code checks~~ **DONE (2026-09-12)** — sweep complete; see §7.4. Generator and release scripts both now check every native invocation.
3. ~~Produce Phase 6 exit evidence~~ **DONE (2026-09-12)** — recorded load-test runs (targets met) and the WCAG 2.1 AA documented self-assessment live in `docs/phase6-evidence/`. **Follow-up:** work the self-assessment's remediation plan (accessible text layer, keyboard paths, contrast, live regions) and re-verify with `UiSmoke.Tests` before claiming the "AA pass on core screens" exit criterion.
4. ~~Add branch protection / rulesets on `main`.~~ **DONE (2026-09-12)** via the classic branch-protection API: block deletions and force pushes, require linear history, enforced on admins. Deliberately **not** enforced: required status checks and PR review — CI runs on `on: push` after the commit lands, so a required-check on the direct-push workflow would deadlock the `master:main` flow; adopt a PR workflow first if you want those gates. Revert at any time: `gh api -X DELETE repos/sarayunova/PageForge/branches/main/protection`.
5. ~~Record the `-SkipApi` rationale in the README~~ **DONE (2026-09-12)** — hosted deploys are intentionally separate from desktop releases; a sentence in the README's downloads section closes the gap.
6. (Post-beta, tracked, not blockers) Port the shell to WinUI 3 and add a native ARM64 build lane, per TSD §12.1.