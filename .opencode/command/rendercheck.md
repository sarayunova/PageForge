---
description: Phase 0 render verification — engine vs mutool artifacts to artifacts/.
agent: build
---

Run the render spike so the engine (shim) and `mutool draw` each produce a PNG of the same
corpus page, then compare. On this dev box `dotnet` lives at the full SDK path and the built
mutool is produced by `native/build-mupdf.ps1`.

1. `dotnet run --project tests/PageForge.RenderSpike` (use `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run`) on the PDF in $ARGUMENTS with `--out artifacts --mutool-path native/out/_work/mupdf-1.28.3-source/platform/win32/x64/Release/mutool.exe`.
2. Confirm artifacts named `<name>-p<N>-spike.png` and `<name>-p<N>-mutool.png` exist.
3. Byte-compare them (fidelity contract for Phase 0 is byte-identity); if different, inspect by eye
   if the model supports image input, otherwise fall back to a pixel/hash diff and report.
Report: page region in pt, pixel dimensions, and whether engine == mutool render.