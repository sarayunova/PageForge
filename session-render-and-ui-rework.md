# Session note: PDF render failure + UI modernization

Status: **Phase R (render) and Phases U0–U2 (tokens, Fluent chrome, toolbar) are
done and committed** on branch `fix/viewer-render-phase-r`, which is pushed to
`origin`. Phase U3 (document surface) and U4 (MVVM cleanup) are the remaining
work. Read `AGENTS.md` and the TRD/TSD first.

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

### Still open on the UI

- **Phase U3 — the document surface.** Page shadows and a proper canvas
  background, a designed empty state (with no document open the user still sees
  a bare panel), per-page loading skeletons, and a **collapsible sidebar with a
  splitter** — it is still a fixed `Width="270"` with no collapse
  (`DocumentView.xaml:355`).
- **Phase U4 — MVVM cleanup.** `DocumentView.xaml.cs` is **1,335 lines** of
  imperative wiring, assigning `ItemsSource` and text by hand (`Refresh()`,
  `RefreshAndScroll()`) instead of binding. There is **no `ICommand` anywhere in
  the shell** — every control is a `Click` handler. This is what makes any
  future UI change expensive, and it is where the original render bug lived. Do
  it incrementally, one surface at a time, keeping UiSmoke green: it looks up
  controls by `AutomationId`, so preserve those names through every rename.
- **No fit-to-width.** The old "Fit" button called `ZoomReset()` (100%) despite
  its tooltip; it was renamed "Reset zoom to 100%", so it is honest now, but
  actual fit-to-width / fit-page does not exist.
- **No automated cover for the error panel or the retry button.** The XAML binds
  and compiles, but no test forces a render failure, so neither the panel nor
  the error-path logging is proven on screen. A `--smoke` proof that injects a
  failing render would cover both.

Accessibility is a gate on every UI phase, not a phase of its own: Phase 6
reached WCAG 2.1 AA and the restyle must not regress contrast, keyboard paths,
automation names or heading structure. Verify `docs/phase6-evidence` still holds
after each phase.

## 4. Dependency policy

PageForge is **AGPLv3**. Any UI dependency must be AGPL-compatible and recorded
in `THIRD-PARTY-NOTICES.md`. Permissive (MIT/Apache-2.0) is fine; commercial
suites (Syncfusion, DevExpress, Telerik) are a licence and distribution problem
for an AGPL open-source beta and are ruled out.

Taken so far, both MIT and both recorded: **WPF-UI** 4.3.0 (Fluent control
styles, Mica, the icon set) and **Microsoft.Extensions.Logging** 8.0.1.

Still worth taking when the relevant phase starts:

| Package | Licence | Why |
|---|---|---|
| **CommunityToolkit.Mvvm** | MIT | Source-generated `ObservableProperty`/`RelayCommand`. The prerequisite for Phase U4 — it is what lets the 1,335-line code-behind become bindings. |
| **Microsoft.Xaml.Behaviors.Wpf** | MIT | Proper behaviors/triggers, replacing the hand-rolled attached-property `Loaded` hooks — exactly where the render bug lived. |
| **Microsoft.Extensions.DependencyInjection** | MIT | A real container, so `AppLog`'s static holder can become injection. `AppLog.Factory` is the single seam to repoint. |

Explicitly **not** recommended: any web/React/Tailwind/shadcn route, unless you
first decide to rebuild the shell as WebView2 + a web UI — a strategic
re-platform, not a restyle, and it would put the whole PDF surface behind an
interop boundary the current MuPDF bitmap pipeline is not designed for.

Note that the post-beta plan of record is a WinUI 3 port (TSD §12.1). WPF-UI is
the right hedge: its visual language is WinUI's, so the tokens, icon set and
layout decisions port even though the XAML does not.

## 5. The API's outstanding debt

`0c1d119` made the hosted CI lane (`PAGEFORGE_HOSTED_CI=1`, real Postgres +
MinIO) pass: it provisions the schema, drains `OcrJobWorker` before the host's
provider is disposed, and drops the Windows Event Log provider that could be
disposed mid-write.

**`services/PageForge.Api` has no `Migrations/` folder.** `MigrateAsync()` was
therefore a silent no-op and the hosted lane's database stayed empty; the fix
was `EnsureCreatedAsync()`, which builds the schema from the model. That is a
test-lane provision, **not a production story** — `EnsureCreated` cannot evolve
a schema. Real EF migrations are owed before the API is deployed anywhere
durable.

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

Branch `fix/viewer-render-phase-r` is pushed and well ahead of
`origin/main`; **no PR has been opened and nothing is merged**. The working tree
is clean.

Suites all green at the time of writing: Core 154, Fidelity 48, API hermetic 47,
UiSmoke 1, and `--smoke` exits 0.

Next step is **Phase U3**, doing U4 incrementally alongside it as each surface is
touched. Before deep U4 work, get an answer on the WinUI port timing (§6.4).
