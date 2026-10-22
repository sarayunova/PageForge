# Handoff: where LiVi's PageForge stands (2026-09-30)

Read this first when resuming. It covers the work of 2026-09-29 and 2026-09-30 in
one place; the per-topic `session-*.md` files hold the detail.

## State
- `main` is clean and pushed. Last commits: `fbc3b76` (consistent product name),
  `a346817` (theme menu), `ac7d891` (tab/status/search polish), `39c7b4d` (quit crash fix).
- Tests (all local, all green at last run): Core 196, Fidelity 62, Api 47, UiSmoke 32.
  CI was green through `ac7d891`; the last two commits were still running when this was written.
  Check with `gh run list --limit 3`.
- Build: `powershell -ExecutionPolicy Bypass -File tools/build-managed.ps1 -Configuration Release`
  with PageForge CLOSED (a running app locks its own DLLs and the build silently leaves stale ones).
  Native library: `PF_MUPDF_SKIP_DOWNLOAD=1 powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1`.

## Done in this stretch
1. **Destructive actions confirm** (rule from the user): closing a tab or quitting with
   unsaved edits, declining crash recovery, Delete page, Flatten annotations, Snip clear /
   page change. 29 error messages carry a next step. WCAG 3.3.1/3.3.3/3.3.4 now PASS.
2. **CI repaired** (red since 9/25): render-proof step raced the WPF exe (`Start-Process -Wait`);
   MinIO image no longer pullable from quay.io, now a digest-pinned Chainguard image started from a step.
3. **OCR text**: hidden OCR layer is flagged (`PdfTextRun.IsHiddenOcrText`) and edited by
   cover-and-replace (native `pf_cover_replace_text`, `ScanTextEditCommand`). Details in
   `session-scan-text-edit.md`.
4. **Bugs found and fixed on the way**: redaction options file parsed on tabs only (options
   silently ignored, images removed); `build-mupdf.ps1` broken by a half-installed Windows SDK
   and missing x86 CRT libs; quit confirmation crashed the app on EVERY quit
   (`Close()` inside the Closing event; now posted with `Dispatcher.BeginInvoke`).
5. **UI polish**: shared tab style (must name `PART_SelectedContentHost`), "1 page" plural,
   search hint, theme menu (Follow Windows / Light / Dark, saved in `%LOCALAPPDATA%\PageForge\settings.json`),
   product name "LiVi's PageForge" throughout.

## Decisions made
- **Product name** is "LiVi's PageForge" for anything a person reads. Identifiers, namespaces,
  folders, the data folder, the repo URL, licence headers stay `PageForge`.
- **File names stay `PageForge`** (decided 2026-09-30): the executable `PageForge.App.Wpf.exe`,
  `pageforge_mupdf.dll` and the `pageforge-*.zip` release asset are not renamed.
- **Signing**: no signing path yet. Betas ship unsigned draft releases (documented in release notes).
- **Scan-text replacement font** is Helvetica whatever the scan uses; Latin-1 characters only.

## Open items (nothing here is started)
- **Next UI batch, to be chosen later**: "Skip to page" is permanently visible although it is a
  keyboard bypass (show on focus only); the Organize row repeats its tab's name as a label;
  light-theme surface levels differ between sidebar and toolbar rows; Snip annotation is
  mouse-only; the theme button always shows a moon (swap sun/moon); empty state (no document) unreviewed.
- **WCAG still PARTIAL**: native heading levels (needs WinUI 3 port or .NET 10), 2.4.5 Multiple Ways.
- **Not clicked through by the user yet**: the destructive-action dialogs (need a document with
  unsaved edits, which the UI suite cannot stage) and the recovery-decline path.
- **Scan text edit on a real, textured or coloured scan**: the user confirmed it works; colour
  match on unusual backgrounds is unproven. Rotated pages fall back to white paper / black ink.
- Post-beta: port the shell to WinUI 3; the UI suite cannot see z-order or colour correctness
  (screenshot baselines would be a separate decision).
- Signing path (SignPath or another) when the user is ready.

## Traps worth remembering
- UiSmoke drives the real desktop: it fails when another app holds the foreground or the
  mouse is moved during a synthesized drag. Re-run the single test before suspecting the code.
- A test that has never failed is not known to work; revert the fix and watch it fail.
- Passing a resource key through a variable hides it from the unused-strings guard; write
  `UiStrings.Get("Literal")` at the call site.
- Do not name a WPF-UI control with `x:Name` (our namespace `PageForge.App.Wpf` shadows `Wpf.Ui`).
- A build that "succeeds" while the app is running leaves the old DLLs; always close the app first.
