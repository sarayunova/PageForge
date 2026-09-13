# Session note: PDF render failure + UI modernization

Status: **Phase R (render) and Phases U0–U2 (tokens, Fluent chrome, toolbar) are
done and committed** on branch `fix/viewer-render-phase-r`, which is pushed to
`origin`. **Phase U4 (MVVM cleanup) is part-done** — the command bar, status line
and list controls are converted; the per-mode child views are not. Phase U3
(document surface) is still open, though three of its layout bugs were fixed
along the way. Read `AGENTS.md` and the TRD/TSD first.

## 1. What this project actually is

PageForge is **not a web app** — it is a Windows desktop application. The
shipping shell for v0.1 beta is `src/PageForge.App.Wpf` (WPF, net8.0-windows,
x64), per TSD §12.1 and `AGENTS.md`. `src/PageForge.App` is a retained WinUI 3
spike that renders page one only and does not build on this machine.

Layering is clean and worth preserving: `App.Wpf -> MuPdfInterop -> Core ->
pageforge_mupdf.dll (C shim) -> MuPDF 1.28.3`. `services/PageForge.Api` is a
separate ASP.NET Core hosted service (accounts, sync, billing, e-sign, batch
OCR) that desktop features must never depend on.

Consequence for the UI question: any "frontend plugin" must be a **XAML/WPF**
library. React/Tailwind/shadcn and the rest of the web toolchain are not
applicable to the shipping shell.

## 2. The PDF render bug — fixed in `bdd3a18`

**The rendering engine was never at fault.** MuPDF, the shim, Interop and Core
all worked; the failure was entirely in the WPF binding layer. Four causes, all
addressed:

- **A (primary) — the render trigger never fired.**
  `PageImageBehavior.RenderOnLoad` matched the realized element's DataContext
  against `PageImageViewModel`, but the page and thumbnail templates both bind
  through a **`PageSlotViewModel`**. The pattern match always failed, so
  `RenderAsync` was never called and the viewer stayed blank. Because it was a
  silent `if`, nothing logged and nothing threw. Resolution now lives in
  `PageImageBehavior.ResolveImage`, which maps a slot to the right image and
  tells the page and thumbnail surfaces apart by binding path — they render the
  same page at very different DPIs.
- **B — zoom blanked every page.** `RenderDpi`'s setter dropped the cached
  bitmap and nothing re-rendered it, so every zoom step left white pages until
  the container recycled. `RenderDpi` now marks the cache stale while *keeping*
  the old pixels on screen, and `DocumentView.RenderRealizedPages()` follows
  `ApplyZoomToPages()` with `RenderSlotsAsync()` over the realized slots. Each
  zoom cancels the previous re-render, so holding zoom does not render every
  intermediate DPI.
- **C — `Stretch` on the page image.** Reverted to `Stretch="None"`, with a
  comment recording why. This is fidelity-critical: `None` shows the bitmap at
  exactly the DPI it was rendered at, so zoom means "re-render sharper" rather
  than "resample a cached bitmap up". `Uniform` would have masked cause B
  instead of fixing it, and the fidelity suite pins byte-identical PNG output.
- **D — no failure was ever surfaced.** `PageImageViewModel.RenderError` is
  bound to a visible on-page error panel, which now also carries a **Try again**
  button (`RetryAsync` clears the error first, so the attempt is visible).

Regression cover: `--smoke` runs `RunHeadlessSlotRenderProofAsync`, which
asserts both surfaces resolve and that a 2x DPI change re-renders 794px →
1588px. **Reintroducing cause A makes the proof exit 1**, so it is a test that
can actually fail.

## 3. Why the interface looked dated — and what was done

`App.xaml` used to be three lines with no `Application.Resources` at all: no
theme, no templates, no tokens, no type scale. Every control was stock WPF
Aero2 painted over with hex literals scattered through the XAML. That was the
whole reason it read as an old application.

**U0 + U1 — tokens and Fluent chrome (`c80bea0`, `24df7fe`).**
`Themes/Tokens.Dark.xaml` and `Tokens.Light.xaml` hold the palette as named
brushes, ported one-for-one from FrameForge's `tailwind.config.ts` — PageForge
and FrameForge are sibling LiVi products, so the visual language is inherited
rather than invented. `Typography.xaml` carries the dense 10–15px scale and the
Segoe UI Variable stack. `ThemeManager` follows the OS theme and swaps
dictionaries live. `MainWindow` is a WPF-UI `FluentWindow` with Mica, rounded
corners and `ExtendsContentIntoTitleBar`; "Open PDF…" and the AGPL §13 source
link moved into the title bar, removing a whole band of chrome.

**U2 — toolbar (`ae18e82`).** The single ~30-control `StackPanel` became three
rows: an always-available command bar, a mode switcher, and a contextual row per
mode. Unicode glyphs (`⇩ ⟲ ⟳ ✂ …`) were replaced with real icons. Previously,
below about 1500px the last commands ran off the right edge and were simply
unreachable — at the default 1200x820 window "Save order…" was already clipped.

**Icon (`5aa987e`, `59db5f4`).** `tools/make-icon.ps1` generates
`Assets/pageforge.ico` (16–256px) and a 256px PNG reproducibly from
`assets/brand/pageforge-icon-source.png`, stamped into the exe via
`ApplicationIcon`. The art is the flat full-bleed LiVi tile; the earlier
gradient badge is kept as `assets/brand/pageforge-icon-alt-badge.png`. The flat
art was chosen because its heavy strokes stay legible at 16px, where the
gradient badge collapsed into a blue blob.

**Logging + retry (`2df7f98`).** `Diagnostics/AppLog.cs` is the logging seam,
writing through `Microsoft.Extensions.Logging` to
`%LOCALAPPDATA%\PageForge\logs\pageforge.log`. It replaced
`System.Diagnostics.Trace`, which in a released WPF build writes nowhere anybody
can read — that is how the blank-viewer bug reached a user with no trail. The
sink (`Diagnostics/FileLoggerProvider.cs`) is in-repo, because every dependency
needs an AGPL licence check and a notices entry, and that is a poor trade for an
append behind a lock.

**Phase U4, part one (`5e3d916`, `69b19c3`, `6abf2d8`).** The command bar, the
status line and all five list controls now use commands and bindings. Three
things are worth carrying forward from it:

- Dropping `x:Name` from a bound element broke UiSmoke instantly — `x:Name` is
  what supplies the `AutomationId`. Keep the names through every rename.
- A `Command` binding that does not resolve leaves the button visible, enabled
  and completely inert: no exception, no log. Same silent-failure shape as the
  blank-viewer bug. UiSmoke now invokes Zoom in and asserts the readout reaches
  125%.
- The status line needed a concept, not just a binding. Mode hints belong to the
  view and must come down when a mode is switched off; document outcomes belong
  to the view model. They were one field, so turning a mode off could not restore
  what the line said before — it had already been overwritten with the hint.
  `Status` now reads `_statusHint ?? _status`.

**Three layout bugs found and fixed (`2c2fd04`, `edf93bf`, `9884465`).** All had
been shipping:

- The form and redaction **side panels were invisible**. Both set
  `DockPanel.Dock="Right"` on a child of a `Grid`, where the attached property is
  silently ignored, so the panel and the page shared one cell and the page —
  declared second — covered it. Nobody could see the list of form fields or of
  marked redaction regions.
- Fixing that exposed the **Apply bar being pushed off its own panel**: a
  `DockPanel` gives the fill slot to its *last* child whatever its `Dock` says.
- The **mode toolbars overflowed**, hint and buttons in one `StackPanel`, so the
  last command sat past the right edge. Same class Phase U2 fixed on the main
  toolbar; the contextual rows never got it.

Also: nine hardcoded dark-theme colours survived Phase U0 in the panels that
build chrome in code, invisible on a light background. `Views/ThemedElements.cs`
resolves them from tokens via `SetResourceReference`, so they follow a live theme
swap.

### Still open on the UI

- **Phase U3 — the document surface.** Page shadows and a proper canvas
  background, a designed empty state (with no document open the user still sees
  a bare panel), per-page loading skeletons, and a **collapsible sidebar with a
  splitter** — it is still a fixed `Width="270"` with no collapse
  (`DocumentView.xaml:355`).
- **Phase U4, part two — the per-mode child views.** `DocumentView.xaml.cs` is
  down to **1,306 lines**, but `ObjectEditView`, `FormFillView` and `RedactView`
  still expose imperative `Refresh()` methods the parent calls on every state
  change. Each rebuilds overlay rectangles positioned from PDF geometry, so
  converting them means an `ItemsControl` over a `Canvas` with bound positions,
  per view. That is a design change rather than a mechanical one and probably
  wants its own session.
- **No fit-to-width.** The old "Fit" button called `ZoomReset()` (100%) despite
  its tooltip; it was renamed "Reset zoom to 100%", so it is honest now, but
  actual fit-to-width / fit-page does not exist.
- **No automated cover for the error panel or the retry button.** The XAML binds
  and compiles, but no test forces a render failure, so neither the panel nor
  the error-path logging is proven on screen. A `--smoke` proof that injects a
  failing render would cover both.

### What UiSmoke covers now, and what it still cannot

All six tool groups are checked at **1200x820 and again at 900x600** (the
window's declared `MinWidth`): every command must have a non-zero size and lie
inside the window, and each side panel's header must not overlap the page.
`ResizeAsync` refuses to return if the window did not actually reach the
requested width, so a silent no-op cannot make those assertions vacuous.

Every one of those assertions was checked against the bug it exists for, by
reverting the fix and watching it fail. Do the same for any new one — a test that
has never failed is not known to work.

It still cannot see **occlusion by a same-rect sibling** (UIA has no z-order) or
**colour correctness** beyond the single `ContentMutedBrush` token asserted in
`--smoke`. Catching those means screenshot-diffing against approved baselines,
which is its own infrastructure decision rather than more UiSmoke.

Use `--theme light|dark|system` to inspect a palette without touching the
machine's Windows setting.

Accessibility is a gate on every UI phase, not a phase of its own: Phase 6
reached WCAG 2.1 AA and the restyle must not regress contrast, keyboard paths,
automation names or heading structure. Verify `docs/phase6-evidence` still holds
after each phase.

## 4. Dependency policy

PageForge is **AGPLv3**. Any UI dependency must be AGPL-compatible and recorded
in `THIRD-PARTY-NOTICES.md`. Permissive (MIT/Apache-2.0) is fine; commercial
suites (Syncfusion, DevExpress, Telerik) are a licence and distribution problem
for an AGPL open-source beta and are ruled out.

Taken so far, all MIT and all recorded: **WPF-UI** 4.3.0 (Fluent control styles,
Mica, the icon set), **Microsoft.Extensions.Logging** 8.0.1 and
**CommunityToolkit.Mvvm** 8.4.0.

Still worth taking when the relevant phase starts:

| Package | Licence | Why |
|---|---|---|
| **Microsoft.Xaml.Behaviors.Wpf** | MIT | Proper behaviors/triggers, replacing the hand-rolled attached-property `Loaded` hooks — exactly where the render bug lived. |
| **Microsoft.Extensions.DependencyInjection** | MIT | A real container, so `AppLog`'s static holder can become injection. `AppLog.Factory` is the single seam to repoint. |

Explicitly **not** recommended: any web/React/Tailwind/shadcn route, unless you
first decide to rebuild the shell as WebView2 + a web UI — a strategic
re-platform, not a restyle, and it would put the whole PDF surface behind an
interop boundary the current MuPDF bitmap pipeline is not designed for.

Note that the post-beta plan of record is a WinUI 3 port (TSD §12.1). WPF-UI is
the right hedge: its visual language is WinUI's, so the tokens, icon set and
layout decisions port even though the XAML does not.

## 5. The hosted API lane

The `api-hosted` lane (`PAGEFORGE_HOSTED_CI=1`, real Postgres + MinIO) **had
never executed once** before this branch. It died in `Initialize containers`
every time, because `minio/minio` on Docker Hub stopped serving anonymous pulls.
`a5a4fce` points it at a pinned release on quay.io, and `015baea` makes the suite
behind it pass **47/47 against real Postgres and MinIO**.

> **The API does have EF migrations.** Seven of them, in
> `services/PageForge.Api/Migrations/`, including `AddOcrJobs`, which creates
> `OcrJobItems`. An earlier version of this note, `0c1d119`'s commit message and
> the PR description all claimed it had none. That claim came from a comment in an
> inherited working tree and was repeated without anyone listing the directory.
> **Check the tree before repeating an inherited claim about it** — this one
> caused the bug it was supposed to prevent.

`0c1d119` acted on that false premise and replaced `MigrateAsync()` with
`EnsureCreatedAsync()`. `EnsureCreated` builds a schema only when the *database*
does not exist, and the lane's Postgres container pre-creates `pageforge` via
`POSTGRES_DB` — so it found a database, created no tables, and `OcrJobWorker` hit
the exact `42P01 relation "OcrJobItems" does not exist` the change was meant to
prevent. `Program.cs` is back to `MigrateAsync()`.

Restoring that alone was **not** enough, which is worth knowing before touching
this again: `Database:AutoMigrate` drives a block between `builder.Build()` and
`app.Run()`, and `WebApplicationFactory` captures the host at build time without
running the rest of the entry point, so the block never executes under test at
all. The hermetic branch has always provisioned its own schema for this reason;
the hosted branch now does the same, applying the migrations once per process
behind a lock because every test class builds its own host against one shared
database.

The last failure there was not a defect: PostgreSQL `timestamptz` keeps
microseconds and a .NET tick is 100 nanoseconds, so a round-tripped `DateTime`
came back with its final digit dropped. The in-memory provider stores the value
as-is and never showed it; that assertion now compares within a millisecond.

**Still open:** the hosted lane has no per-run database isolation. CI gets a fresh
container so it passes, but re-running it against a used database fails on
duplicate registrations — the suite reuses fixed addresses (`a@example.com` at
nine call sites, `alice@example.com` at five) across classes.

`85c6b7b` fixed a flake that failed
`OcrJobsApiTests.Submit_job_completes_and_notifies_owner` about one run in three.
It was not timing: `PageForgeApiFactory`'s in-memory database name and root are
**static**, so every test class's host shares one database, while each host has
its own `RecordingEmailSender`. `OcrJobWorker` swept items still marked Queued at
start-up, so a host coming up could pick up another host's job, complete it in
its own scope, and deliver the completion email to the wrong recorder. Sweeping
is right for a deployed API, so it stays on by default behind
`Ocr:SweepQueuedOnStart` and the test factory turns it off.

That shared static database is still the underlying smell. If anything similar
resurfaces, giving each factory its own database name is the real fix.

## 6. Answers to the questions this note used to ask

1. **Light, dark, or follow-the-OS?** Follow-the-OS, implemented. Both token
   dictionaries exist and `ThemeManager` swaps them live.
2. **Brand direction?** Yes — LiVi Software Company. The palette is ported from
   the sibling product FrameForge, and the app icon is the LiVi tile.
3. **Are NuGet dependencies acceptable?** Yes, with the licence discipline in §4.
4. **WinUI 3 port timing?** Still unanswered, and still the question that
   decides how much deeper WPF work is worth doing. It does not block U3; it
   does bear on how much of U4 to attempt.

## 7. Next session starts here

Branch `fix/viewer-render-phase-r` is pushed and open as **PR #1** against
`origin/main`; nothing is merged. The working tree is clean.

Suites all green at the time of writing: Core 154, Fidelity 48, API hermetic 47,
UiSmoke 4, and `--smoke` exits 0. The hosted API lane passes 47/47 too, against
real Postgres and MinIO.

**Phase U3 is done** — collapsible/resizable sidebar (`a68e0b7`), empty state
(`54c2c2f`), page shadows (`817dd8d`) and render placeholders (`844aec6`).

Next is **U4 part two**: the per-mode child views. Each rebuilds overlay
rectangles positioned from PDF geometry, so it means an `ItemsControl` over a
`Canvas` with bound positions, per view — a design change rather than a
mechanical one, and probably its own session. After that, per-run database
isolation for the hosted API lane (§5). Before deep U4 work, get an answer on the
WinUI port timing (§6.4).

One sequencing note, because it changes the answer to §6.4. Doing U4 **before**
the WinUI port is not optional polish. Code-behind is the part of a WPF shell
that does not port; view models and bindings port nearly as they are. Every
`Click` handler left in place gets rewritten once for WinUI and again for MVVM.
So the port is the strongest argument *for* finishing U4, not a reason to skip
it.

If the port does go ahead, the hard blocker is that the WinUI spike **does not
build on this machine**:

```
MrtCore.PriGen.targets(914,5): error MSB4062: The
"Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent" task could not be
loaded from ...\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll
```

That assembly ships with Visual Studio's **Windows application development**
workload, not the .NET SDK. Until it is installed nothing WinUI can be compiled
or run. The four genuine rewrites once it is: worker-thread bitmaps (WinUI has no
`Freeze()`), `MessageBox` → async `ContentDialog` (31 call sites), `DynamicResource`
→ `ThemeResource` (93 sites), and page rotation (`LayoutTransform` has no WinUI
equivalent that resizes the layout slot).
