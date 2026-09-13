# PageForge — Installation

> **SPDX-License-Identifier: AGPL-3.0-only**
> PageForge is an open-source (AGPLv3) PDF viewer, editor and document platform
> for Windows. This file is install-only guidance; build authority lives in
> `AGENTS.md` and the source-of-truth TRD/TSD artifacts.

## 1. What you get

```
PageForge.sln                            — .NET 8 solution (root)
services/PageForge.Api                   — API service (Npgsql + MinIO, hosted)
src/PageForge.App.Wpf                    — SHIPPING desktop shell, x64, net8.0-windows
src/PageForge.App                        — retained WinUI 3 spike (page-one render only;
                                           NOT buildable on this dev machine)
src/PageForge.Core                       — core library
tests/PageForge.Api.Tests                — hermetic API integration lane (release authority)
tests/PageForge.Core.Tests               — core unit suite
tests/PageForge.Fidelity.Tests           — fidelity regression (render byte-pins)
docker-compose.yml                       — hosted lane: Postgres 16 + MinIO
LICENSE, THIRD-PARTY-NOTICES.md          — AGPL + third-party notices
```

## 2. Prerequisites

- **Windows 10/11 x64** — the shipping shell is `net8.0-windows`, x64.
- **.NET 8 SDK.** This machine's SDK is user-scope with `-NoPath`, so invoke it
  via the full local path (aliases like `dotnet` fail in non-path shells):

  ```powershell
  $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
  ```

  If not present, install from https://dotnet.microsoft.com/download/dotnet/8.0.
- **`git`** — for building from source.
- **Docker (only if you run the hosted lane):** Postgres 16 + MinIO containers
  for the `api-hosted` CI job and local hosted runs. Not needed to run or test
  hermetically.

## 3. Quick start (hermetic — the release authority)

The hermetic lane runs the API integration suite with **no external services**
(in-memory EF provider, in-memory MinIO/blob fake, in-memory email capture). This
is the lane that must stay **47/47 byte-identical green** and is the release gate.

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"

# 1. Restore + build
& $dotnet restore PageForge.sln

# 2. Run the release-authority suite — MUST show 47/47 passed
& $dotnet test tests\PageForge.Api.Tests\PageForge.Api.Tests.csproj -c Release
```

In addition, run the other tracked lanes separately (do not bundle multiple in
one `dotnet test`):

```powershell
& $dotnet test tests\PageForge.Core.Tests\PageForge.Core.Tests.csproj -c Release
& $dotnet test tests\PageForge.Fidelity.Tests\PageForge.Fidelity.Tests.csproj -c Release
```

> Hermetic never touches PostgreSQL, MinIO, SMTP or any network. Any suite that
> needs those is a separate hosted lane (below), which is infrastructure-only and
> hermetic/assertion-neutral.

## 4. Run the API locally (hermetic / dev)

```powershell
& $dotnet run --project services\PageForge.Api\PageForge.Api.csproj -c Debug
```

The API starts with EF startup migration **off** by default (`Database:AutoMigrate`
unset/false); schema side effects only happen when explicitly requested. For local
dev this needs no live PostgreSQL or MinIO.

## 5. Hosted lane (real PostgreSQL + MinIO via Docker Compose)

For the `api-hosted` CI job (real Npgsql context + real MinIO-backed blob
storage), provision the real services first:

```powershell
docker compose up -d postgres minio
$env:PAGEFORGE_HOSTED_CI = "1"      # flips the factory into the hosted lane
& $dotnet test tests\PageForge.Api.Tests\PageForge.Api.Tests.csproj -c Release
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
  log echo can never occur there; hermetic's 47/47 assertions stay the pinned
  release authority.
- Tracked as **test-infrastructure debt** (per-test DI-provider lifecycle
  contention), not as a product defect.

## 6. MuPDF native engine

The API interop + fidelity lanes use a pinned MuPDF 1.28.3 AGPL build plus a
managed `pageforge_mupdf` shim. Reproducible native builds live in `native/`:

```powershell
powershell -ExecutionPolicy Bypass -File native\build-mupdf.ps1
```

This is idempotent and ends with a `mutool info` smoke check voice.

## 7. AGPL + source offer

PageForge is **AGPLv3**. Running a hosted deployment of the API makes you the
operator of a network service; a public source-offer / AGPL-compliance factsheet
lives with the `agpl-compliance` skill. See `LICENSE`, `CONTRIBUTING.md`,
`SECURITY.md`.
