# Contributing to PageForge

Thanks for your interest. PageForge is open source under the GNU Affero General
Public License v3.0 (`LICENSE`). By contributing you agree that your
contributions are licensed that way.

## Before you start

Read the three source-of-truth documents first — the technical requirements
and specification take precedence over this file wherever they conflict:

- `PageForge_Technical_Requirements_Document.md` — what we are building.
- `PageForge_Technical_Specification_Document.md` — how we build it.
- `PageForge_Artifact_Verification_Playbook.md` — how agents verify work.

## Repository conventions

- Cross-cutting conventions live in `AGENTS.md` (build, test, and the 
  buildable-project caveats on the current dev machine).
- Follow the matching skill under `.opencode/skills/` for the topic you touch:
  WinUI/.NET conventions, MuPDF interop, fidelity regression, AGPL compliance,
  and API design.
- Desktop features must never gate on hosted services; everything desktop works
  fully offline.

## Semantic requirements

PageForge is built against a phased roadmap described in the specification. Keep
feature work tied to the relevant FR-/TSD identifier (e.g. FR-VIEW, FR-OCR,
FR-BATCH) so reviewers can reason about scope.

## Build, test, lint

- Managed build: `.NET 8`, invoke via `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"`
  (this machine's SDK is user-scope with `-NoPath`).
- The full `PageForge.sln` includes the WinUI app (`src/PageForge.App`), which
  fails without UWP/MSIX MSBuild tasks — build/test the individual managed
  projects instead. `TreatWarningsAsErrors` and `nullable` warnings are
  enforced via `Directory.Build.props`; emit no warnings.
- Test suites must be run **separately** (SDK 8.0.424 errors MSB1008 if several
  are bundled into one `dotnet test`):
  - `dotnet test tests/PageForge.Core.Tests`
  - `dotnet test tests/PageForge.Fidelity.Tests` — must pass before merge; a
    corpus regression blocks a merge (render-equality = byte-identical PNGs).
  - `dotnet test tests/PageForge.Api.Tests`
- Native: `powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1`.

## Pre-commit / pre-PR AGPL compliance checklist

- [ ] AGPLv3 license header present on every new or edited source file
      (`// Copyright (c) 2026 LiVi Software Company` /
      `// SPDX-License-Identifier: AGPL-3.0-only`).
- [ ] MuPDF attribution retained per Artifex terms; never under a
      GPL-incompatible dependency.
- [ ] Tesseract (Apache 2.0) and other bundled libs — license + notices present
      (`THIRD-PARTY-NOTICES.md`).
- [ ] No proprietary/closed-source edition artifacts committed (explicitly
      rejected, TRD §9).
- [ ] If the hosted API/deploy changed, the `/source` endpoint still reports the
      deployed commit SHA — a desync is a release-blocker, not cosmetic.

## Commit style

- Imperative subject line, matching the phase: `Phase N <SLICE>: <summary>`.
- Reference the relevant FR-/TSD identifier in the body (e.g. `FR-BATCH`,
  `FR-SEC-01`).
- Commit only intended, verified changes. Never commit secrets, keys, or large
  native build outputs.

## Pull requests

1. Branch from `main` for the change; keep it focused and reviewable.
2. Ensure the `managed-build` CI lane passes (Core, Fidelity, and Api test
   suites + fidelity render proof).
3. Request review. The maintainers apply the AGPL checklist above.

## Code of conduct

By participating you agree to abide by `CODE_OF_CONDUCT.md`.

## Security

Found a vulnerability? Do **not** open a public issue. Report per `SECURITY.md`.