# PageForge

Open-source (AGPLv3) PDF viewer, editor, and document platform for Windows.

PageForge renders and edits PDFs through a real MuPDF engine, with offline
local OCR (Tesseract), form filling, redaction, encryption, page organization, and
a set of hosted services (account sync, billing, e-sign, team review, batch OCR and
conversion). Every desktop feature works fully offline — hosted services are
optional and never gate core editing.

## License

PageForge is licensed under the **GNU Affero General Public License v3.0**
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
| `src/PageForge.App` | WinUI 3 shell (builds only in CI's `winui-build` lane) |
| `src/PageForge.App.Wpf` | Runnable desktop proof (net8.0-windows, x64) with a `--smoke` headless mode |
| `services/PageForge.Api` | Hosted API (accounts, sync, billing, e-sign, team review, batch OCR) |
| `tests/` | Core, Fidelity, and Api test suites (`PageForge.*.Tests`) |
| `tools/` | Fidelity corpus/manifest, sample PDFs, verification scripts |
| *repo root* | Source-of-truth docs: `PageForge_Technical_Requirements_Document.md` (what), `PageForge_Technical_Specification_Document.md` (how), `PageForge_Artifact_Verification_Playbook.md` (agent verification) |

## Build & test

Requires the .NET 8 SDK. The native MuPDF build requires Visual Studio Build
Tools 2022 / vcvars64.

```powershell
# Native MuPDF + shim (idempotent; required for engine-backed work)
powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1

# Managed build. NOTE: this machine's SDK is user-scope with -NoPath, so invoke
# it explicitly; the full PageForge.sln includes WinUI and fails without the
# UWP/MSIX MSBuild tasks, so build/test the individual managed projects instead.
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build services/PageForge.Api/PageForge.Api.csproj -c Debug

# Tests (run suites separately; a shared dotnet test invocation errors MSB1008)
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Api.Tests
```

Desktop build/test CI excludes `src/PageForge.App` (WinUI) on this dev machine;
the WinUI shell builds only in the `winui-build` lane. See `AGENTS.md`.

## Hosted source availability

Satisfying AGPL §13, the hosted API exposes `GET /api/v1/source` returning the
license, the public repository URL, and the exact commit SHA in production
(`PAGEFORGE_COMMIT_SHA`). CI verifies the endpoint reports the deployed SHA. See
`CONTRIBUTING.md` for the pre-commit/license checklist.

## Contributing

See `CONTRIBUTING.md` for the developer workflow and `CODE_OF_CONDUCT.md` for
community standards. Report security issues per `SECURITY.md`.

## Disclaimer

The product shell described in this repository is under active development and
may be incomplete. See the requirements/specification documents for authoritative
scope and the exit criteria per phase.