# Handoff: moving to the other computer (2026-10-04)

Read this first, then `session-handoff-2026-09-30.md` (state, decisions, traps; still current).

## State
- `main` is pushed and level with `origin/main`. Last code commit: `cb07d12`
  "Fix UI smoke waits and resize under DPI scaling" (fast-forward merge of
  `uitest/visible-page-indicator`; only `tests/PageForge.UiSmoke.Tests/PageForgeApp.cs` changed).
- Verified on this machine in Debug: UiSmoke 32/32 pass. Core, Fidelity and Api suites and a
  Release build were NOT re-run for this change (it touches only the UI test helper).
- `wip/crash-recovery-buffer` had one local-only commit, `71002d9` "Stop the smoke run ending
  itself halfway through"; pushed so the other computer can see it. It is WIP, NOT verified.

## Resuming on the other computer
1. `git fetch origin && git checkout main && git pull`.
2. Work done there that was never pushed will not be here: check `git log origin/main` against
   what you remember, and push anything missing from that machine first.
3. Build with `tools/build-managed.ps1` (see AGENTS.md; do not build `PageForge.sln`), close the
   app before building, run suites separately.
4. Native DLL: `PF_MUPDF_SKIP_DOWNLOAD=1 powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1`
   if `pageforge_mupdf.dll` is missing (it is built, not tracked).

## Unmerged remote branches (none inspected this session)
ci/pixel-blank-check, ci/ui-smoke-lane, docs/truthful-beta-scope, feat/crash-recovery-buffer,
feat/unsaved-changes-flag, fix/a11y-list-item-names, fix/dialog-postcondition,
proof/form-overlay-geometry, refactor/form-field-cards, refactor/u4-child-views,
test/organizer-page-order, wip/local-signing (all dated 2026-09-16..20).
Local `master` is stale (behind `main`); ignore it.

## Open items
Unchanged from `session-handoff-2026-09-30.md`. Unanswered: whether the unmerged branches above
should be reviewed and merged or are superseded.

## Next session starts here
Decide what to do with the unmerged branches (review/merge/delete), then pick the next UI batch.
