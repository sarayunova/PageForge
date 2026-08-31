# PageForge

Open-source (AGPLv3) PDF viewer, editor and document platform for Windows.

## Source of truth
- What: `PageForge_Technical_Requirements_Document.md`
- How: `PageForge_Technical_Specification_Document.md`
- Agent verification: `PageForge_Artifact_Verification_Playbook.md`

Read all three before starting any work. The TRD/TSD take precedence if this file conflicts.

## Build / test / lint
- .NET 8 solution at repo root. This machine's SDK is user-scope with `-NoPath`, so ALWAYS invoke it via `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"` (aliases like `dotnet` fail in non-path shells).
- Build: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build PageForge.sln -c Debug` — build/test/managed-only CI lane must EXCLUDE `src/PageForge.App` (WinUI) because this dev machine lacks the VS UWP/MSIX MSBuild tasks (`Microsoft.Build.AppxPackage.dll`, `Microsoft.Build.Packaging.Pri.Tasks.dll`); the WinUI app builds only in the `winui-build` CI lane (continue-on-error).
- Test: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests tests/PageForge.Fidelity.Tests` — Fidelity suite must pass before merge; a corpus regression blocks the merge. Render equality gate: spike/WPF/mutool PNGs must stay byte-identical (sha256 pins live in `tools/`/`tests/PageForge.Fidelity.Tests/corpus/manifest.psd1` and `.opencode/command/goldendiff.md`).
- Native: `powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1` — reproducible MuPDF 1.28.3 AGPL + `pageforge_mupdf.dll` shim build via VS Build Tools 2022/vcvars64; idempotent; `mutool info` smoke at the end.
- UI shell decision (locked): WinUI 3 (`src/PageForge.App`) is the product target but is NOT buildable on this dev machine; the runnable desktop proof is `src/PageForge.App.Wpf` (net8.0-windows, x64) with a `--smoke` headless mode for CI/renders. Keep the two in sync; never delete App.Wpf until WinUI builds locally.

## Working rules
- Follow the matching skills in `.opencode/skills/` for their topics (WinUI/.NET conventions, MuPDF interop, fidelity regression, AGPL compliance, API design).
- Core features must never gate on hosted services; everything desktop works fully offline.
- Never silently overlap content on text growth — grow the box or raise the FR-EDIT-02 collision warning.
- Use the `/rendercheck`, `/goldendiff`, `/smoke`, `/structcheck` commands for artifact-layer visual verification.
- After editing `opencode.jsonc` or any skill/command/agent file, tell the user to restart opencode.