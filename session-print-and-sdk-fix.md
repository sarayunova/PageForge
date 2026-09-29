# Session: Print feature + broken SDK toolchain fix

**Date:** 2026-09-29

## What happened
User noticed the PDF viewer had no print option. Investigation found "Print"
only existed as a permission-flag label inside the password/protection
dialog (`Protect_Print`) — no actual print command, dialog, or pipeline
anywhere in the codebase, and it was never scoped in the TRD.

## What was scoped and built
- `PageForge_Technical_Requirements_Document.md`: added `FR-VIEW-05` — print
  the open document to any Windows-installed printer via the standard
  Windows print dialog, fully offline.
- `DocumentTabViewModel.cs`: `PrintPage` record + `RenderPagesForPrintAsync`,
  rendering each page at 300 DPI through the existing `IPdfEngine`
  (`GetPageSizeAsync` + `RenderPageToPngAsync`) — no engine changes needed.
- `DocumentView.xaml` / `.xaml.cs`: a **Print** button in the document-wide
  toolbar row (next to Undo/Redo — printing applies regardless of active
  tool group, same reasoning as page nav living there). `Print_Click` shows
  `PrintDialog`, builds a `FixedDocument` (one `FixedPage` per page, sized
  from `PdfPageRegion` pt→DIP at 96/72 scale), and calls
  `PrintDialog.PrintDocument`.
- `UiStrings.resx`: `Document_Print_Name`, `Document_Print_Tip`,
  `Error_PrintFailed`.
- Out of scope (deliberate): print preview, per-page fit/scale options
  beyond "fill page", n-up printing. The OS dialog still covers
  printer/paper-size/copies/collate.

## The toolchain problem this surfaced
Building to verify the change failed for EVERY project (including ones
never touched), not just the new code:
`MSB4022: The result "" of evaluating the value
"$(MicrosoftNETBuildTasksAssembly)"... is not valid`.

Root cause: the documented dev-machine SDK
(`%LOCALAPPDATA%\Microsoft\dotnet`, version 8.0.424, installed `-NoPath`)
is broken, and its host does not multi-level-lookup the machine-wide
install (`C:\Program Files\dotnet`, which has both a working 8.0.425 and a
10.0.401) to route around it.

Fix: added `global.json` pinning the SDK to `8.0.425` (rollForward
`latestMinor`), which only the machine-wide, on-PATH `dotnet` has. Updated
`tools/build-managed.ps1` to prefer `dotnet` (now resolves correctly via
PATH) over the broken `%LOCALAPPDATA%` copy, and updated AGENTS.md's
build/test command examples to `& "C:\Program Files\dotnet\dotnet.exe"`
accordingly. User confirmed via AskUserQuestion: commit the `global.json`
fix (not just keep it local).

## Verification
- `PageForge.App.Wpf` builds clean (0 errors/warnings) with the pinned SDK.
- `PageForge.Core.Tests` (178), `PageForge.Fidelity.Tests` (59),
  `PageForge.Api.Tests` (47) all pass.
- `PageForge.UiSmoke.Tests` NOT run this session (needs an interactive
  desktop session per AGENTS.md) — the Print button/dialog itself is
  therefore unverified end-to-end; only the code around it compiles and the
  existing suites still pass.
- Did not manually click through the Print button in a live app session
  (would trigger a real OS `PrintDialog`, a blocking modal).

## Notes for next session
- If the `%LOCALAPPDATA%` 8.0.424 SDK ever gets repaired/reinstalled, revisit
  `global.json` and the AGENTS.md note — they currently assume it stays
  broken.
- Consider adding a UI-smoke assertion for the Print button's presence/
  enablement (not the OS dialog itself, matching the existing Save-dialog
  trade-off already documented in AGENTS.md for issue #6).
- Commit `ff55843` is local only — not pushed to origin.

## Update: print preview (later the same day)
- The Windows print dialog's own preview pane says "This app doesn't support
  print preview" for WPF `PrintDialog`, so PageForge has its own preview:
  `Views/PrintPreviewWindow.cs` (one page at a time, rendered on demand at
  100 DPI, All / Current / range) and `Views/PdfPrintPaginator.cs` (streams
  300 DPI pages to the spooler lazily). A first version that rendered every
  page up front was replaced because it cannot work for a 1,273-page PDF.
- Verified by the user on a real document: preview works and printing works.
- Commits `24272a5` and `642e347`, pushed to origin.
- Still open: a UI-smoke assertion for the Print button's presence.
