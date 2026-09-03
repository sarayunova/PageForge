# PageForge

Open-source (AGPLv3) PDF viewer, editor and document platform for Windows.

## Source of truth
- What: `PageForge_Technical_Requirements_Document.md`
- How: `PageForge_Technical_Specification_Document.md`
- Agent verification: `PageForge_Artifact_Verification_Playbook.md`

Read all three before starting any work. The TRD/TSD take precedence if this file conflicts.

## Build / test / lint
- .NET 8 solution at repo root. This machine's SDK is user-scope with `-NoPath`, so ALWAYS invoke it via `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"` (aliases like `dotnet` fail in non-path shells).
- Build: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build PageForge.sln -c Debug` — build/test/managed-only CI lane must EXCLUDE `src/PageForge.App` (WinUI) because this dev machine lacks the VS UWP/MSIX MSBuild tasks (`Microsoft.Build.AppxPackage.dll`, `Microsoft.Build.Packaging.Pri.Tasks.dll`); the WinUI spike builds only in the `winui-build` CI lane, which is enforcing (no `continue-on-error`) so the spike cannot rot.
- Test: run the suites SEPARATELY (SDK 8.0.424 errors MSB1008 if both are given in one `dotnet test`):
  - `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests` (154 tests)
  - `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests` (48 tests)
  - `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Api.Tests` (46 tests)
  Fidelity suite must pass before merge; a corpus regression blocks the merge. Render equality gate: spike/WPF/mutool PNGs must stay byte-identical (sha256 pins live in `tools/`/`tests/PageForge.Fidelity.Tests/corpus/manifest.psd1` and `.opencode/command/goldendiff.md`).
- Native: `powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1` — reproducible MuPDF 1.28.3 AGPL + `pageforge_mupdf.dll` shim build via VS Build Tools 2022/vcvars64; idempotent; `mutool info` smoke at the end.
- UI shell decision (v0.1 beta, see TSD §12.1): `src/PageForge.App.Wpf` (net8.0-windows, x64, with a `--smoke` headless mode for CI/renders) is the SHIPPING shell and holds all product UI. `src/PageForge.App` is the retained WinUI 3 spike — page-one render only — and is NOT buildable on this dev machine. Porting the shell to WinUI 3 is post-beta work. Do not treat the two as peers to keep in sync, and never delete App.Wpf.

## Working rules
- Follow the matching skills in `.opencode/skills/` for their topics (WinUI/.NET conventions, MuPDF interop, fidelity regression, AGPL compliance, API design).
- Core features must never gate on hosted services; everything desktop works fully offline.
- Never silently overlap content on text growth — grow the box or raise the FR-EDIT-02 collision warning.
- Use the `/rendercheck`, `/goldendiff`, `/smoke`, `/structcheck` commands for artifact-layer visual verification.
- After editing `opencode.jsonc` or any skill/command/agent file, tell the user to restart opencode.
