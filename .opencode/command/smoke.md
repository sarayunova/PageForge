---
description: Smoke — headless WPF render + viewer proof + full managed test run.
agent: build
---

Run the headless render proof through the real engine AND the Phase 1 viewer-correct proof
through `DocumentViewModel` (outline + full-text search), then the full managed test suite,
and report the result.

1. `& src/PageForge.App.Wpf/bin/Debug/net8.0-windows/PageForge.App.Wpf.exe --smoke` — must print
   `rendered ... -> artifacts/sample-phase0-p1-wpfproof.png` (794x1123 px at 96 DPI) and
   `viewer proof: pages=... outline=... searchHits=...`, then exit 0.
2. `dotnet test tests/PageForge.Core.Tests` and `dotnet test tests/PageForge.Fidelity.Tests`
   (use `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"`).
3. Verify `artifacts/sample-phase0-p1-wpfproof.png` matches the client render hashes (see
   `/goldendiff`), and that `artifacts/viewer-phase1-p1.png` is byte-identical to it.

Gate: all tests pass AND the Phase-0 proof artifact exists AND its hash equals the `spike`/`mutool`
hash AND `artifacts/viewer-phase1-p1.png` is byte-identical. A WinAppDriver UI smoke (TSD §8) is
Phase 1+; this `--smoke` is the Phase 1 stand-in.