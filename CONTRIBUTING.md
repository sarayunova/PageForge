# Contributing to LiVi's PageForge

Thanks for your interest. LiVi's PageForge is open source under the GNU Affero General
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

- **.NET 10 SDK.** `global.json` pins `10.0.401` with `rollForward: latestMinor`.
  Use the machine-wide install, which is on PATH (`C:\Program Files\dotnet`):
  `dotnet`, or `& "C:\Program Files\dotnet\dotnet.exe"`. The user-scope SDK
  under `%LOCALAPPDATA%\Microsoft\dotnet` is not the SDK this repo builds with.
- Managed build: `powershell -ExecutionPolicy Bypass -File tools/build-managed.ps1`
  (add `-Configuration Release -WarnAsError` to match CI). Do not build
  `PageForge.sln` directly: it includes the WinUI spike (`src/PageForge.App`),
  which needs the Visual Studio UWP/MSIX MSBuild tasks and fails MSB4062 on a
  machine that only has the .NET SDK. The script owns that project list.
  `TreatWarningsAsErrors` and `nullable` warnings are enforced via
  `Directory.Build.props`; emit no warnings.
- Test suites must be run **separately** (the .NET SDK errors MSB1008 if several
  are bundled into one `dotnet test`):
  - `dotnet test tests/PageForge.Core.Tests`
  - `dotnet test tests/PageForge.Fidelity.Tests` — must pass before merge; a
    corpus regression blocks a merge (render-equality = byte-identical PNGs).
  - `dotnet test tests/PageForge.Api.Tests`
  - `dotnet test tests/PageForge.UiSmoke.Tests` — launches the real WPF window
    through UI Automation, so it needs an interactive desktop and
    `src/PageForge.App.Wpf` built in the same configuration. CI runs it in the
    `ui-smoke` lane.
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
`tools/publish-release.ps1` (net10.0-windows, win-x64; folder layout so the
native `pageforge_mupdf.dll` and `tessdata` stay on disk exactly as CI's `--smoke`
runs them — offline OCR must not rely on single-file extraction), plus a
per-user installer compiled from that folder by `tools/build-installer.ps1`.

Run it locally:

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1 -SkipApi
powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1
```

Output lands in `artifacts/release/`: `pageforge-<version>-win-x64.zip` and
`pageforge-<version>-win-x64-setup.exe`. Use the same `-Version` for both
commands when it is not the default `0.0.0-beta`. The publish script signs
**only if a certificate is provided**; otherwise it warns loudly and produces
an **unsigned** payload. The installer compiler does not sign unless
`-SignToolCommand` is passed. An unsigned payload is not a release.

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
   - optional variable `SIGNPATH_INSTALLER_SIGNING_POLICY_SLUG`, only if the
     setup executable needs a different policy from the application zip.
     When it is unset, the installer request uses `SIGNPATH_SIGNING_POLICY_SLUG`.

The signing policy has to match both shapes, or the two policies do: the
application zip contains `PageForge.App.Wpf.exe` (and the other binaries you
want signed), and the installer request is a zip of
`pageforge-<version>-win-x64-setup.exe`. SignPath never sees the files inside
a compiled setup, so the application must be signed first.

### How a release runs

`release.yml` builds on a `v*` tag push and on a manual `workflow_dispatch`.
Only the tag creates a GitHub Release. The manual run uploads the zip and the
setup executable as workflow artifacts and stops there. Pull requests do not
run this workflow; they compile and install-test the installer in the
`installer` job of `ci.yml`.

On a tag:

1. builds the native shim, then publishes and zips the desktop payload
   **unsigned** (`publish-release.ps1`) — SignPath signs a zip as a whole
   rather than rewriting an `.exe` in a staged folder;
2. **if signing is configured** (secret `SIGNPATH_API_TOKEN` is set), uploads
   the unsigned zip as a build artifact, submits it to SignPath via
   `SignPath/github-action-submit-signing-request`, waits for the signed zip,
   then extracts and verifies `PageForge.App.Wpf.exe` with `signtool verify /pa`
   **before** it replaces the release zip. The verified files also replace the
   payload folder, because the installer is compiled from that folder;
3. compiles `pageforge-<version>-win-x64-setup.exe` and install-tests it on
   the runner (per-user install, upgrade, uninstall that keeps user data,
   uninstall that deletes it when asked);
4. **if signing is configured**, zips the setup executable, submits that zip
   in a second SignPath request, and verifies the returned setup executable
   with `signtool verify /pa` before it replaces the release asset;
5. **if signing is not configured**, ships the unsigned zip and the unsigned
   setup executable — an unsigned beta ships rather than a signing-pipeline
   hold-up;
6. on a `v*` tag only, attaches both files to a **draft** GitHub Release for
   manual review. The notes state the signing status truthfully (they say
   "Not code-signed in this build" for unsigned payloads) and document the
   SmartScreen / Defender / browser "unknown publisher" warnings, so a
   released beta is never misrepresented as signed.

The distribution policy is recorded in TSD §12.1: the v0.1 beta ships unsigned
until signing is configured; signed builds take over as the distribution path
once the SignPath setup below exists. The portable zip stays alongside the
installer either way.

Ordering matters for the signed path: SignPath returns a new zip rather than
modifying files in place, and the workflow verifies the executable inside that
returned zip with `signtool verify /pa` before it overwrites the release
asset — a signing step that silently no-ops or fails can never produce a
release claiming to be signed. The installer is compiled after that
replacement, then signed itself. Note also that a newly issued certificate has
no SmartScreen reputation; early signed downloads may still warn until
reputation accrues.

The uninstaller that Inno Setup embeds is not covered by the post-compile
SignPath request, because its hash is fixed when `ISCC` runs. Pass
`-SignToolCommand` to `build-installer.ps1` once a local sign tool exists
(the command's `$f` placeholder is the file Inno asks to sign). That switch
is the hook; CI does not set it and does not store a signing secret for it.

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
   suites + fidelity render proof). UiSmoke is the separate `ui-smoke` lane.
3. Request review. The maintainers apply the AGPL checklist above.

## Code of conduct

By participating you agree to abide by `CODE_OF_CONDUCT.md`.

## Security

Found a vulnerability? Do **not** open a public issue. Report per `SECURITY.md`.
