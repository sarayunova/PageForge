# Session note: PDF render failure + UI modernization plan

Status: **Phase R (render) is done and committed** on branch
`fix/viewer-render-phase-r` as `bdd3a18`. Phase U (UI modernization) is planned
but not started, and is blocked on the four questions in §6. Read `AGENTS.md`
and the TRD/TSD first.

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

## 2. The PDF render bug — root cause found

**The rendering engine is fine.** `PageForge.App.Wpf.exe --smoke` was run in
this session and exits 0, rendering `sample-phase0.pdf` p1 at 794x1123 px to
`artifacts/sample-phase0-p1-wpfproof.png`, plus the organizer, annotation, edit
and 4-document corpus dogfood proofs. MuPDF, the shim, Interop and Core all
work. The failure is entirely in the WPF binding layer.

### Cause A — the render trigger never fired (primary)

`PageImageBehavior.RenderOnLoad` (`ViewModels/PageImageViewModel.cs`) tested
the realized element's DataContext with:

```csharp
if (element.DataContext is PageImageViewModel page) { await page.RenderAsync(); }
```

But the item templates in `Views/DocumentView.xaml` bind `Image.Bitmap` /
`Thumbnail.Bitmap`, so the DataContext of every page and thumbnail slot is a
**`PageSlotViewModel`**, never a `PageImageViewModel`. The pattern match always
failed, `RenderAsync` was never called, and both the page surface and the
thumbnail strip stayed blank on open. Because it is a silent `if`, nothing was
logged and nothing threw.

The working tree already contains the corrected version (cast to
`PageSlotViewModel`, call `slot.Image.RenderAsync()`, with catch arms that clear
the slot for retry instead of letting an exception escape the `async void`
`Loaded` seam). It is **uncommitted and unverified in a real window** — it
compiles (`dotnet build ... -c Debug` succeeded, 0 warnings) but has only been
exercised headlessly.

### Cause B — zoom blanks every page (real, still unfixed)

`DocumentTabViewModel.ApplyZoomToPages` sets `page.Image.RenderDpi = dpi` for
every slot. `PageImageViewModel.RenderDpi`'s setter drops the cached bitmap
(`Bitmap = null`) because the cache is stale at the new DPI — but **nothing
re-renders it**. `Loaded` does not fire again for containers that are already
realized, so after any zoom in/out/reset/fit the visible pages go white and stay
white until the container is scrolled out and recycled back in. With Cause A
also present the app looked permanently broken; with Cause A fixed this becomes
the next visible defect.

Fix direction: after clearing, kick a re-render for the slots that are currently
realized — either have `ApplyZoomToPages` await/queue `RenderAsync` for the
visible range, or raise an event the view handles by re-rendering realized
containers. Keep it cancellable so a fast zoom sequence supersedes in-flight
renders rather than queueing them all.

### Cause C — `Stretch` on the page image

The working tree also changes the page `Image` from `Stretch="None"` to
`Stretch="Uniform"`. Be careful here: this is a **fidelity-sensitive** change.
`Stretch="None"` shows the bitmap at exactly the DPI it was rendered at, which
is what makes zoom mean "re-render at higher DPI" rather than "scale a blurry
bitmap up". `Uniform` will make pages appear (blurred, resampled) even when the
DPI-driven re-render path is broken — which masks Cause B instead of fixing it.
Recommendation: **fix Cause B properly and revert this to `None`**, or make the
container size explicitly track `PixelWidth`/`PixelHeight` so `Uniform` is a
no-op at 1:1. The fidelity suite pins byte-identical PNG output
(`tests/PageForge.Fidelity.Tests/corpus/manifest.psd1`), so a resampling change
here is exactly the kind of thing the render-equality gate exists to catch.

### Cause D — no failure is ever surfaced

Even with the fix, a render failure results in a blank page and a
`Trace.TraceWarning` nobody reads. There is no on-page error state and no
retry affordance. This is why the bug survived to the user.

### What was actually done (commit `bdd3a18`)

All four causes are addressed:

- **A** — resolution moved into `PageImageBehavior.ResolveImage`, which maps a
  `PageSlotViewModel` DataContext to the right image and tells the page and
  thumbnail surfaces apart by binding path, so a thumbnail is no longer at risk
  of rendering at full-page DPI.
- **B** — `RenderDpi` now marks the cache stale while *keeping* the old pixels on
  screen (no white flash), and `DocumentView.RenderRealizedPages()` follows
  `ApplyZoomToPages()` with the new `DocumentTabViewModel.RenderSlotsAsync()`
  over the slots whose containers are realized. Each zoom cancels the previous
  re-render, so holding zoom does not render every intermediate DPI.
- **C** — reverted to `Stretch="None"` with a comment recording why, and the
  fidelity suite passes.
- **D** — `PageImageViewModel.RenderError` is bound to a visible on-page error
  panel. The `ILogger` seam is still outstanding (see below).

Verification performed:

- `--smoke` exits 0, including a new `RunHeadlessSlotRenderProofAsync` writing
  `artifacts/slot-render-proof.txt`. It asserts both surfaces resolve and that a
  2x DPI change re-renders 794px → 1588px. **Reintroducing cause A makes the
  proof exit 1**, so it is a regression test that can actually fail.
- Core 154 passed, Fidelity 48 passed, UiSmoke 1 passed.
- Confirmed in a real window (launched, screenshotted): the sample renders on
  open with a rendered thumbnail, and zooming to 150% re-renders sharp.

Still outstanding from the render work:

1. A real `ILogger` seam to replace `Trace.TraceWarning`.
2. A retry affordance on the error panel (it reports, but cannot yet retry).
3. The `Fit` button calls `ZoomReset()` (100%), so it does not fit to width
   despite its tooltip. Separate small bug, noticed while tracing zoom.
4. `tests/PageForge.Api.Tests` was not run; the working tree carries unreviewed
   API changes from an earlier session.

## 3. Why the interface looks dated — concrete findings

`App.xaml` is **three lines with no `Application.Resources` at all**. There is no
theme, no control template, no design tokens, no typography scale. Every
control is stock WPF Aero2 — the 2010 look — painted over with hardcoded hex
literals scattered through the XAML (`#2b2b2b`, `#1e1e1e`, `#3a3a3a`, `#555`,
`#444`, `#bbb`, `#ddd`, `#999`). That is the whole reason it reads as an old
application: grey 3-D bevelled buttons and a square default TabControl on a dark
strip.

Specific problems, in the order they hurt:

- **One 30-button toolbar.** `DocumentView.xaml` puts navigation, zoom,
  rotation, Organize, Annotate, Edit, object, form, redact, undo/redo, OCR,
  Protect and search in a single horizontal `StackPanel`. It overflows off-screen
  on narrower windows with no overflow handling, and it exposes every mode at
  once with no hierarchy.
- **Unicode glyphs as icons** (`⇩ ⟲ ⟳ ✂ ⧉ ✱ 🗨 ✒ ⚟ ✎ ⬒ ☑ ✖ ↩ ↪ ⇺ 🔒`). These
  render inconsistently per font fallback, do not scale or recolor, and several
  are semantically opaque. A real icon font (Fluent/Lucide) or vector paths is
  needed.
- **No window chrome work.** Default title bar, no Mica/acrylic backdrop, no
  rounded corners — the Windows 11 cues users read as "modern".
- **Colors are not tokens.** Phase 6 raised contrast to WCAG 2.1 AA by editing
  hex literals in place. Any restyle will silently undo that unless the palette
  first becomes named brushes with the contrast ratios recorded.
- **Fixed 270 px sidebar**, no collapse, no splitter, no responsive behavior.
- **No light theme and no system-theme following.** Dark is hardcoded.
- **No empty state.** With no document open the user sees a bare grey area.
- **Density and spacing are ad hoc** — `Padding="8,3"`, `"10,3"`, `"12,4"`
  chosen per control.
- **Imperative view wiring.** `DocumentView.xaml.cs` is 1254 lines assigning
  `ItemsSource` and text by hand (`Refresh()`, `RefreshAndScroll()`) instead of
  binding. This is what will make a restyle expensive, so it is worth
  addressing as part of the same work.

## 4. Libraries worth adding (and the licence constraint)

PageForge is **AGPLv3**. Any UI dependency must be AGPL-compatible and must be
recorded in `THIRD-PARTY-NOTICES.md`. Permissive (MIT/Apache-2.0) is fine;
commercial component suites (Syncfusion, DevExpress, Telerik) are a licence and
distribution problem for an AGPL open-source beta and should be ruled out.

Recommended, minimal set:

| Package | Licence | Why |
|---|---|---|
| **WPF-UI** (`lepoco/wpfui`) | MIT | The single highest-leverage addition. Fluent/Windows 11 control styles, Mica backdrop, `NavigationView`, `TitleBar`, the Fluent System Icons set, and light/dark theming that can follow the OS. Merges into `App.xaml` resources. |
| **CommunityToolkit.Mvvm** | MIT | Source-generated `ObservableProperty`/`RelayCommand`. Replaces the hand-rolled `ObservableObject` and lets the 1254-line code-behind become bindings — the prerequisite for restyling cheaply. |
| **Microsoft.Xaml.Behaviors.Wpf** | MIT | Proper behaviors/triggers, replacing the hand-rolled attached-property `Loaded` hooks — which is exactly where the render bug lived. |
| **Microsoft.Extensions.DependencyInjection + Logging** | MIT | Real DI and a logging seam so render failures are observable instead of `Trace`. |

Optional / evaluate later:

- **MahApps.Metro** (MIT) — an alternative to WPF-UI with a more "Metro" look.
  Pick one, not both.
- **Material Design In XAML Toolkit** (MIT) — only if you deliberately want
  Material rather than a native Windows look. For a Windows PDF editor, native
  Fluent is the better read.
- **Dirkster.AvalonDock** (MIT) — dockable/floating panels, if the sidebar
  should become a real tool-window system.
- **Fluent System Icons** (MIT) — ships with WPF-UI; use it instead of the
  Unicode glyphs.

Explicitly **not** recommended: any web/React/Tailwind/shadcn route, unless you
first decide to rebuild the shell as WebView2 + a web UI — which is a strategic
re-platform, not a restyle, and would put the whole PDF surface behind an
interop boundary the current MuPDF bitmap pipeline is not designed for.

Note that the post-beta plan of record is a WinUI 3 port (TSD §12.1). Effort
spent on WPF chrome is partly throwaway if that port happens soon. WPF-UI is
the right hedge: its visual language is WinUI's, so the design tokens, icon set
and layout decisions port even though the XAML does not.

## 5. Proposed plan

**Phase R — make it render. DONE (`bdd3a18`).** See §2. No visual changes went
into that commit beyond the error panel. Opening a PDF now reliably shows pages,
and zoom re-renders instead of blanking.

**Phase U0 — design tokens, no visual change.**
Extract every hex literal into a `Themes/` resource dictionary as named brushes
with recorded contrast ratios; add a typography and spacing scale. Verify the
Phase 6 WCAG evidence in `docs/phase6-evidence` still holds. The app should
look identical after this phase.

**Phase U1 — adopt WPF-UI.**
Add the package, merge its dictionaries into `App.xaml`, map the Phase U0 tokens
onto its theme resources, switch `MainWindow` to a Fluent window with Mica and a
custom title bar, and enable OS theme following (this delivers light mode).
Update `THIRD-PARTY-NOTICES.md`.

**Phase U2 — restructure the toolbar.**
Replace the 30-button row with a small persistent command bar (open, page nav,
zoom, search) plus a mode/tool switcher for Organize / Annotate / Edit / Forms /
Redact, each revealing only its own contextual controls. Swap Unicode glyphs for
Fluent icons with text labels. Add overflow so nothing is unreachable at
1280 px.

**Phase U3 — the document surface.**
Real page shadows and a proper canvas background, a collapsible sidebar with a
splitter, a designed empty state, loading skeletons per page, and the visible
render-error/retry state from Phase R.

**Phase U4 — MVVM cleanup.**
Move `DocumentView.xaml.cs` logic to commands and bindings using
CommunityToolkit.Mvvm. Do this incrementally, one surface at a time, keeping the
UiSmoke automation green — it looks up controls by `AutomationId`, so preserve
those names through every rename.

Accessibility is a gate on every UI phase, not a phase of its own: Phase 6
reached WCAG 2.1 AA and the restyle must not regress contrast, keyboard paths,
automation names or heading structure.

## 6. Open questions for the user

1. Is the WinUI 3 port still planned, and roughly when? If it is imminent,
   Phase U should be scoped to WPF-UI theming only, skipping deeper WPF work.
2. Light theme, dark theme, or follow-the-OS as the default?
3. Is there any brand direction — colors, logo, product name treatment — or
   should the restyle stay neutral Fluent?
4. Is adding NuGet dependencies to the desktop shell acceptable given the AGPL
   notices burden, or is a hand-rolled theme preferred?

## 7. Next session starts here

Phase R is committed on `fix/viewer-render-phase-r` (`bdd3a18`); it has not been
merged or pushed. Next step is Phase U0 (design tokens, no visual change) — but
**the four questions in §6 are still unanswered and should be asked before Phase
U1 picks a direction**, because the answer to Q1 (WinUI port timing) decides how
much WPF work is worth doing at all.

Uncommitted working-tree state at the time of writing, all pre-existing and
**not reviewed in this session**: `services/PageForge.Api/Program.cs`,
`tests/PageForge.Api.Tests/PageForgeApiFactory.cs`, and an untracked
`docs/INSTALL.md`. Someone should decide whether those land or get reverted.
