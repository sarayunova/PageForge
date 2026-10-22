# LiVi's PageForge

Open-source (AGPLv3) PDF viewer, editor, and document platform for Windows.

LiVi's PageForge renders and edits PDFs through a real MuPDF engine, with offline
local OCR (Tesseract), form filling, redaction, encryption, page organization, and
a set of hosted services (account sync, billing, e-sign, team review, batch OCR and
conversion). Every desktop feature works fully offline — hosted services are
optional and never gate core editing.

## License

LiVi's PageForge is licensed under the **GNU Affero General Public License v3.0**
(`LICENSE`). Because the hosted services are network-accessible AGPLv3 code,
AGPL §13 applies to them; the `/source` endpoint on the hosted API and the
in-app "View source" link in the desktop app both point to this repository and
the exact commit running in production.

Third-party components (MuPDF 1.28.3, Tesseract trained data, and others) and
their licenses are documented in `THIRD-PARTY-NOTICES.md`.

## Features

- **Viewer & organizer** — multi-document tabs, page navigation and thumbnails.
- **Content editing** — insert/edit text, images and shapes with box-bounded
  reflow and a font-fidelity system, protected by a render-equality regression
  proof (`tools/` + `tests/PageForge.Fidelity.Tests`).
- **Forms & local OCR** — PDF form fill/create, offline OCR to searchable PDF /
  DOCX / per-page PNG (via bundled Tesseract), redaction, and password-protection
  encryption.
- **Hosted services** — account creation + JWT auth, Stripe billing, cross-device
  sync with conflict handling, send-for-signature, shared team review, and batch
  OCR/convert jobs. All REST/OpenAPI (see the hosted module conventions).
- **Accessibility** — the desktop shell targets WCAG 2.1 AA.

## Repository layout

| Path | Contents |
|---|---|
| `src/PageForge.Core` | Portable engine-agnostic domain model and interfaces |
| `src/PageForge.MuPdfInterop` | Managed bindings over the native MuPDF shim |
| `native/PageForge.MuPdfShim` | C shim (`pageforge_mupdf.dll`) over MuPDF 1.28.3 |
| `src/PageForge.App` | WinUI 3 spike, page-one render only (builds only in CI's `winui-build` lane). The shipping shell for the v0.1 beta is `src/PageForge.App.Wpf` — see TSD §12.1 |
| `src/PageForge.App.Wpf` | Runnable desktop proof (net10.0-windows, x64) with a `--smoke` headless mode |
| `services/PageForge.Api` | Hosted API (accounts, sync, billing, e-sign, team review, batch OCR) |
| `tests/` | Core, Fidelity, Api, and UiSmoke suites (`PageForge.*.Tests`) |
| `docs/sessions/` | Curated agent session notes and handoffs (moved off the repo root) |
| `tools/` | Fidelity corpus/manifest, sample PDFs, verification scripts, release publish and installer build |
| `installer/` | Inno Setup script for the per-user Windows installer |
| *repo root* | Source-of-truth docs: `PageForge_Technical_Requirements_Document.md` (what), `PageForge_Technical_Specification_Document.md` (how), `PageForge_Artifact_Verification_Playbook.md` (agent verification) |

## Prerequisites

- **Windows 10/11 x64.** The shipping shell is `net10.0-windows`, x64.
- **.NET 10 SDK.** `global.json` pins `10.0.401` with `rollForward: latestMinor`.
  Use the machine-wide install, which is on PATH (`C:\Program Files\dotnet`):
  `dotnet`, or `& "C:\Program Files\dotnet\dotnet.exe"`. CI installs `10.0.x`
  the same way (`actions/setup-dotnet`). The user-scope SDK under
  `%LOCALAPPDATA%\Microsoft\dotnet` is not the SDK this repo builds with.
- **Visual Studio Build Tools 2022** with the "Desktop development with C++"
  workload (v143), for the native MuPDF build. MSVC is located via
  `vcvars64.bat`. `curl.exe` and `tar` ship with Windows.
- **Docker**, only for a local hosted API (Postgres 16, MinIO, Mailpit, Redis
  via `docker-compose.yml`). Desktop features do not need it.

## Build

```powershell
# Native MuPDF 1.28.3 + pageforge_mupdf.dll. Idempotent; ends with a mutool info smoke check.
powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1

# Every managed project. Do not build PageForge.sln directly: it includes the
# WinUI 3 spike (src/PageForge.App), which needs the Visual Studio UWP/MSIX
# MSBuild tasks and fails MSB4062 on a machine that only has the .NET SDK.
# The script owns that project list and fails if the solution gains a project
# it does not know about. Add -Configuration Release -WarnAsError to match CI.
powershell -ExecutionPolicy Bypass -File tools/build-managed.ps1
```

`src/PageForge.App.Wpf` is the shipping shell. The WinUI spike builds only in
CI's enforcing `winui-build` lane. See `AGENTS.md`.

## Test

Run each suite in its own `dotnet test`. Passing several projects to one
invocation errors MSB1008, and a script that lists them in sequence only
reports the last exit code unless it checks each one. CI's `managed-build`
lane runs the first three in Release; `ui-smoke` is its own lane.

```powershell
dotnet test tests/PageForge.Core.Tests
dotnet test tests/PageForge.Fidelity.Tests
dotnet test tests/PageForge.Api.Tests
dotnet test tests/PageForge.UiSmoke.Tests
```

The fidelity suite must pass before merge; a corpus regression blocks the
merge. Render equality requires the spike, WPF, and mutool PNGs to stay
byte-identical. UiSmoke launches the real WPF window and drives it through UI
Automation, so it needs an interactive desktop and `src/PageForge.App.Wpf`
built in the same configuration.

## Run

```powershell
# Shipping desktop shell (build the native shim first for engine-backed viewing).
dotnet run --project src/PageForge.App.Wpf

# Headless render used by CI, after the shell is built:
# src/PageForge.App.Wpf/bin/<Configuration>/net10.0-windows/PageForge.App.Wpf.exe --smoke

# Hosted API. appsettings.json already matches the compose Postgres and MinIO
# defaults, so no .env file is required for a local stack. The http launch
# profile listens on http://localhost:5230.
docker compose up -d
dotnet run --project services/PageForge.Api
```

Optional API overrides (Mailpit SMTP, OAuth, Stripe) are listed as
placeholders in `services/PageForge.Api/.env.example`. Copy that file to
`.env` and export the variables before `dotnet run`; the process does not
load a `.env` file on its own. Keep `.env` out of source control.

## Hosted source availability

Satisfying AGPL §13, the hosted API exposes `GET /api/v1/source` returning the
license, the public repository URL, and the exact commit SHA in production
(`PAGEFORGE_COMMIT_SHA`). CI verifies the endpoint reports the deployed SHA. See
`CONTRIBUTING.md` for the pre-commit/license checklist.

## Downloads & beta releases

Beta builds are published as draft GitHub Releases from `v*` tags. Each release
carries two desktop assets. Both are self-contained, so no .NET runtime install
is required:

- `pageforge-<version>-win-x64-setup.exe` — per-user installer (no administrator
  rights). Adds a Start menu shortcut, an optional desktop shortcut, and
  registers LiVi's PageForge as a program that can open `.pdf` files without
  changing the current default. See `docs/INSTALL.md`.
- `pageforge-<version>-win-x64.zip` — the same build as a portable folder.
  Unpack it and run `PageForge.App.Wpf.exe`.

Until code signing is configured they ship **unsigned** (decision recorded in
TSD §12.1), so Windows SmartScreen may show "Windows protected your PC /
Unknown publisher", Microsoft Defender may quarantine the binary until you
choose "More info → Run anyway", and Edge/Chrome may warn on download — this
is expected for an unsigned open-source beta and does not mean the file is
malicious. For extra assurance, cross-check the file's SHA-256 against the
release run's build artifact.

SignPath signing, once configured, covers the application binaries and the
setup executable (see `CONTRIBUTING.md`). A manual run of the `release`
workflow builds both artifacts and does not create a release. Pull requests
compile and install-test the installer in the `installer` CI lane.

Release payloads deliberately exclude the hosted API (`-SkipApi`): hosted
services deploy separately from desktop releases, so the zip and the installer
never bundle a dead API server and nothing is omitted by accident.

## Contributing

See `CONTRIBUTING.md` for the developer workflow and `CODE_OF_CONDUCT.md` for
community standards. Report security issues per `SECURITY.md`.

## Disclaimer

The product shell described in this repository is under active development and
may be incomplete. See the requirements/specification documents for authoritative
scope and the exit criteria per phase.