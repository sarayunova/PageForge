# LiVi's PageForge — Installation

> **SPDX-License-Identifier: AGPL-3.0-only**
> LiVi's PageForge is an open-source (AGPLv3) PDF viewer, editor and document platform
> for Windows. This file is install-only guidance; build authority lives in
> `AGENTS.md` and the source-of-truth TRD/TSD artifacts.

## Install a downloaded release

A `v*` tag's draft GitHub Release carries two files. Prefer the installer.

`pageforge-<version>-win-x64-setup.exe` installs **for your Windows account**
and does not ask for administrator rights. The program is placed in
`%LOCALAPPDATA%\Programs\PageForge`. The wizard can switch to a per-machine
install (`%ProgramFiles%\PageForge`); that choice does ask for administrator
rights. A silent per-user install is `setup.exe /VERYSILENT /CURRENTUSER`, and
`/ALLUSERS` selects the per-machine install instead.

The installer creates a Start menu shortcut. A desktop shortcut is offered and
left unticked. PageForge is registered as a program that can open `.pdf` files,
so it appears under **Open with** and under **Settings > Apps > Default apps**.
The current default PDF program is not changed.

Uninstall from **Settings > Apps**. The program, its shortcuts and its file
registration are removed. Settings, logs and crash-recovery files in
`%LOCALAPPDATA%\PageForge` stay unless the uninstaller asks and you say yes
(or you pass `/DELETEUSERDATA=yes` to the uninstaller). An upgrade installs
over the previous copy and leaves that folder alone.

`pageforge-<version>-win-x64.zip` is the same build without an installer.
Unpack it anywhere and run `PageForge.App.Wpf.exe`. It does not register a
file association or add shortcuts.

Until code signing is configured, Windows may warn that the publisher is
unknown. That warning is expected for this unsigned beta. See the README.

Building the installer from source (after `tools/publish-release.ps1` has
published the desktop folder for the same version):

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1 -Version 0.0.0-beta
```

That downloads a pinned Inno Setup 6 compiler if `ISCC.exe` is not already
installed, and writes `artifacts/release/pageforge-<version>-win-x64-setup.exe`.

## 1. What you get

```
PageForge.sln                            — .NET 10 solution (root; do not build it directly)
services/PageForge.Api                   — API service (Npgsql + MinIO, hosted)
src/PageForge.App.Wpf                    — SHIPPING desktop shell, x64, net10.0-windows
src/PageForge.App                        — retained WinUI 3 spike (page-one render only;
                                           NOT buildable on this dev machine)
src/PageForge.Core                       — core library
tests/PageForge.Api.Tests                — hermetic API integration lane (release authority)
tests/PageForge.Core.Tests               — core unit suite
tests/PageForge.Fidelity.Tests           — fidelity regression (render byte-pins)
tests/PageForge.UiSmoke.Tests            — real-window UI Automation suite
tools/build-managed.ps1                  — managed build (every project except the WinUI spike)
tools/publish-release.ps1                — self-contained desktop folder and portable zip
tools/build-installer.ps1                — per-user Windows installer (Inno Setup)
installer/pageforge.iss                  — the installer script
docker-compose.yml                       — hosted lane: Postgres 16, MinIO, Mailpit, Redis
LICENSE, THIRD-PARTY-NOTICES.md          — AGPL + third-party notices
```

## 2. Prerequisites

- **Windows 10/11 x64** — the shipping shell is `net10.0-windows`, x64.
- **.NET 10 SDK.** `global.json` pins `10.0.401` with `rollForward: latestMinor`.
  Use the machine-wide install, which is on PATH (`C:\Program Files\dotnet`):
  `dotnet`, or `& "C:\Program Files\dotnet\dotnet.exe"`. CI installs `10.0.x`.
  The user-scope SDK under `%LOCALAPPDATA%\Microsoft\dotnet` is not the SDK
  this repo builds with. If it is missing, install from
  https://dotnet.microsoft.com/download/dotnet/10.0.
- **`git`** — for building from source.
- **Docker (only if you run the hosted lane):** Postgres 16 + MinIO containers
  for the `api-hosted` CI job and local hosted runs. Not needed to run or test
  hermetically.

## 3. Quick start (hermetic — the release authority)

The hermetic lane runs the API integration suite with **no external services**
(in-memory EF provider, in-memory MinIO/blob fake, in-memory email capture).
That suite is the release gate for the hosted API.

```powershell
# Restore + build every managed project. Do not build PageForge.sln directly:
# it includes the WinUI spike and fails MSB4062 without the Visual Studio
# UWP/MSIX MSBuild tasks. Add -Configuration Release -WarnAsError to match CI.
powershell -ExecutionPolicy Bypass -File tools/build-managed.ps1

# One suite per invocation. Passing several projects to one `dotnet test`
# errors MSB1008.
dotnet test tests/PageForge.Api.Tests
dotnet test tests/PageForge.Core.Tests
dotnet test tests/PageForge.Fidelity.Tests
dotnet test tests/PageForge.UiSmoke.Tests
```

UiSmoke launches the real WPF window through UI Automation, so it needs an
interactive desktop and `src/PageForge.App.Wpf` built in the same configuration.
The fidelity suite must pass before merge. Render equality requires the spike,
WPF, and mutool PNGs to stay byte-identical.

> Hermetic never touches PostgreSQL, MinIO, SMTP or any network. Any suite that
> needs those is a separate hosted lane (below), which is infrastructure-only and
> hermetic/assertion-neutral.

## 4. Run the API locally (hermetic / dev)

```powershell
dotnet run --project services/PageForge.Api
```

The API starts with EF startup migration **off** by default (`Database:AutoMigrate`
unset/false); schema side effects only happen when explicitly requested. For local
dev this needs no live PostgreSQL or MinIO. `appsettings.json` already matches
the compose Postgres and MinIO defaults, so a `.env` file is not required.

To run against the local stack instead:

```powershell
docker compose up -d
dotnet run --project services/PageForge.Api
```

## 5. Hosted lane (real PostgreSQL + MinIO via Docker Compose)

For the `api-hosted` CI job (real Npgsql context + real MinIO-backed blob
storage), provision the real services first:

```powershell
docker compose up -d postgres minio
$env:PAGEFORGE_HOSTED_CI = "1"      # flips the factory into the hosted lane
dotnet test tests/PageForge.Api.Tests -c Release
```

The hosted lane also enables deterministic schema-before-start via
`Database:AutoMigrate` in `Program.cs`, so `OcrJobWorker`'s startup sweep runs
against a provisioned schema rather than an empty real PostgreSQL.

### Known hosted-lane residual (test-infrastructure debt)

The hosted lane can surface a cross-test `WebApplicationFactory`/DI-provider
**teardown-contention echo** (`ObjectDisposedException` from host/DI teardown).
It is:
- **Assertion-neutral** — zero assertion surface; no `[FAIL]`.
- **Hermetic-unreachable** — hermetic never starts/disposes a real host, so this
  log echo can never occur there; the hermetic API suite stays the release
  authority.
- Tracked as **test-infrastructure debt** (per-test DI-provider lifecycle
  contention), not as a product defect.

## 6. MuPDF native engine

The API interop + fidelity lanes use a pinned MuPDF 1.28.3 AGPL build plus a
managed `pageforge_mupdf` shim. Reproducible native builds live in `native/`:

```powershell
powershell -ExecutionPolicy Bypass -File native\build-mupdf.ps1
```

This is idempotent and ends with a `mutool info` smoke check.

## 7. AGPL + source offer

LiVi's PageForge is **AGPLv3**. Running a hosted deployment of the API makes you the
operator of a network service; a public source-offer / AGPL-compliance factsheet
lives with the `agpl-compliance` skill. See `LICENSE`, `CONTRIBUTING.md`,
`SECURITY.md`.
