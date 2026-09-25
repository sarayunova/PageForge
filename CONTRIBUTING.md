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

## Releases & code signing

A release is a signed, self-contained desktop payload staged and zipped by
`tools/publish-release.ps1` (net8.0-windows, win-x64; folder layout so the
native `pageforge_mupdf.dll` and `tessdata` stay on disk exactly as CI's `--smoke`
runs them — offline OCR must not rely on single-file extraction).

Run it locally:

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1
```

Output lands in `artifacts/release/`. The script signs **only if a certificate is
provided**; otherwise it warns loudly and produces an **unsigned** payload, which is
not a release.

Signing — the production path is **SignPath** (SignPath Foundation's free code
signing for qualifying open-source projects). The signing key never leaves
SignPath's infrastructure: CI uploads the unsigned zip as a build artifact,
SignPath signs it server-side, and its action downloads the signed zip back.

Azure Artifact Signing (the originally planned backend) is not used: its
identity-validation step is not available for an India-based maintainer.
SignPath was chosen instead because it accepts a project application rather
than requiring account-holder identity validation in a specific country list.

Public CAs no longer issue exportable `.pfx` files (since June 2023 OV
certificates ship on a FIPS token or HSM), so the local `PAGEFORGE_CERT_PFX` /
`PAGEFORGE_CERT_PASSWORD` path is for self-signed dry runs and internal-CA
certificates only — useful to rehearse the pipeline, but not a publishable
release.

### One-time SignPath setup

1. Apply at <https://signpath.io/solutions/open-source-community>. Eligibility:
   a public repository, an OSI-recognized license (AGPL-3.0 qualifies), visible
   active development, and 2FA enabled on the GitHub account holding the repo.
   Review is manual, typically 1-2 weeks, and may include follow-up questions.
2. Once approved, in the SignPath dashboard create (or note) the **project**
   and a **signing policy** (a "release-signing" or "public trust" policy,
   named per your SignPath setup), and generate an **API token** for a
   submitter-role CI user.
3. In the repository, add:
   - secret `SIGNPATH_API_TOKEN`
   - variables `SIGNPATH_ORG_ID`, `SIGNPATH_PROJECT_SLUG`,
     `SIGNPATH_SIGNING_POLICY_SLUG`, `SIGNPATH_CONNECTOR_URL` (the GitHub
     Actions endpoint of your SignPath Pipeline Connector, from the dashboard)

### How a release runs

`release.yml`, on a `v*` tag push:

1. builds the native shim, then publishes and zips the desktop payload
   **unsigned** (`publish-release.ps1`) — SignPath signs a zip as a whole
   rather than rewriting an `.exe` in a staged folder, so there is no unzipped
   staging step here;
2. **if signing is configured** (secret `SIGNPATH_API_TOKEN` is set), uploads
   the unsigned zip as a build artifact, submits it to SignPath via
   `SignPath/github-action-submit-signing-request`, waits for the signed zip,
   then extracts and verifies the executable with `signtool verify /pa`
   **before** it replaces the release asset;
3. **if signing is not configured**, ships the unsigned zip as-is — an
   unsigned beta ships rather than a signing-pipeline hold-up;
4. attaches the zip to a **draft** GitHub Release for manual review, with release
   notes that state the signing status truthfully (they say "Not code-signed in
   this build" for unsigned payloads) and document the SmartScreen / Defender /
   browser "unknown publisher" warnings, so a released beta is never
   misrepresented as signed.

The distribution policy is recorded in TSD §12.1: the v0.1 beta ships unsigned
until signing is configured; signed installers take over as the distribution
path once the SignPath setup below exists.

Ordering matters for the signed path: SignPath returns a new zip rather than
modifying files in place, and the workflow verifies the executable inside that
returned zip with `signtool verify /pa` before it overwrites the release
asset — a signing step that silently no-ops or fails can never produce a
release claiming to be signed. Note also that a newly issued certificate has
no SmartScreen reputation; early signed downloads may still warn until
reputation accrues.

Prereqs to enable the **signed** release path (the SignPath distribution
path):

- An approved SignPath Foundation project, with a signing policy and a
  submitter API token.
- The one repository secret and four variables listed above.
- ~~A public hosted repository (the `/source` endpoint's `PAGEFORGE_REPO_URL`).~~
  Done — the repository is live at <https://github.com/sarayunova/PageForge>. CI
  sets `PAGEFORGE_REPO_URL` from `github.repository`, and the same value is the
  built-in fallback for the `/source` endpoint and the desktop "View source"
  link. Deployments outside CI must set the variable explicitly.

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
