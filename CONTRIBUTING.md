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

Signing — the production path is **Azure Artifact Signing** (formerly Trusted
Signing). The signing key never leaves Azure, so nothing secret is stored in
GitHub: CI authenticates with OIDC and the signing service does the work.

Public CAs no longer issue exportable `.pfx` files (since June 2023 OV
certificates ship on a FIPS token or HSM), so the local `PAGEFORGE_CERT_PFX` /
`PAGEFORGE_CERT_PASSWORD` path is for self-signed dry runs and internal-CA
certificates only — useful to rehearse the pipeline, but not a publishable
release.

### One-time Azure setup

1. Create an **Azure Artifact Signing account** and complete identity
   validation (an individual takes roughly three business days; a business
   needs 3+ years of verifiable history). Then create a **certificate
   profile** under that account.
2. Create an **app registration** (Microsoft Entra ID) for CI, and add a
   **federated credential** for this repository — entity type "Environment" or
   "Branch"/tag as appropriate. No client secret is needed with OIDC.
3. Grant that app registration the **Trusted Signing Certificate Profile
   Signer** role on the signing account (Access control (IAM) → Add role
   assignment).
4. In the repository, add:
   - secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
   - variables `AZURE_SIGNING_ENDPOINT` (e.g. `https://eus.codesigning.azure.net/`),
     `AZURE_SIGNING_ACCOUNT`, `AZURE_CERT_PROFILE`

### How a release runs

`release.yml`, on a `v*` tag push:

1. builds the native shim, then stages the desktop payload **unzipped**
   (`publish-release.ps1 -NoZip`);
2. logs in to Azure with OIDC and signs the staged executables;
3. re-runs `publish-release.ps1 -ZipOnly -RequireSignature`, which verifies
   every executable with `signtool verify /pa` **before** building the zip, then
   packages it;
4. attaches the zip to a **draft** GitHub Release for manual review.

Ordering matters: signtool rewrites the `.exe` in place, so a zip built before
signing would ship an unsigned binary inside a nominally signed release. The
`-RequireSignature` verification is what makes a silently skipped signing step
fail the build instead of shipping.

If `AZURE_CLIENT_ID` is unset the workflow still builds and uploads an
**unsigned preview artifact**, but creates no release — an unsigned build is
never published.

Note that a newly issued certificate has no SmartScreen reputation; early
downloads may still warn until reputation accrues.

Prereqs to make `release.yml` fully live (Phase 7 exit criterion):

- An Azure Artifact Signing account with a validated identity and a certificate
  profile, plus the app registration and role assignment above.
- The three repository secrets and three variables listed above.
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
