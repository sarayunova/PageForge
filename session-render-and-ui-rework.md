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
- **Phase U4, part two — started on `refactor/u4-child-views`, not finished.**
  See §8. The conversion turned up more bugs than refactoring; the branch is
  pushed and unmerged.
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

Two more problems sat behind that one, each invisible until the previous was
fixed:

**Isolation (`e2967bf`).** The suite reuses fixed addresses — `a@example.com` at
nine call sites, `alice@example.com` at five — and every class shared one
database, so parallel classes collided on registration. Each factory now owns a
database. Note that xUnit constructs a test class *per test*, so this is really
one database per test: the suite went from ~3s to ~19s, and databases accumulate
(CI discards its container; a local Postgres does not — cleanup command is in
`e2967bf`).

**A bucket race (`62d081a`), and this one is a real production bug.**
`EnsureBucketAsync` was check-then-act: `BucketExistsAsync`, then
`MakeBucketAsync` if absent. Concurrent callers both see "absent", both create,
and the loser gets `BucketAlreadyOwnedByYou` — which surfaced as a 400 on a
document version push and failed a different test each run. Two API instances
starting against a fresh bucket race identically; the isolation fix only made it
visible by starting many hosts at once. It now judges the postcondition: if the
bucket exists afterwards the contract is met, otherwise the failure is rethrown.
Deliberately not a type filter — MinIO 7.0 reports this as a plain
`ArgumentException` from response parsing, so `catch (MinioException)` would miss
it, and matching the message would break on any wording change.

**How it was found, because the method matters more than the fix.** Three
successive guesses were wrong. What worked was one commit (`4e1e19b`) putting the
HTTP response body into the assertion message, turning a useless
`Expected: Created, Actual: BadRequest` into `"Bucket already owned by you:
pageforge-documents (Parameter 'response')"` in a single run. It was then
reproduced locally before being fixed, by pointing the suite at a fresh bucket
name — which is what CI has every run and this machine never had: 2 failures
without the fix, 0 with it. **Instrument first; a CI-only failure usually means
the local environment differs in a way worth naming.**

That diagnostic is still in place and should be removed once the fix has held for
a few runs.

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

**All four CI jobs are green** — native shim, managed build + fidelity, hosted
API, and the WinUI shell build. Locally: Core 154, Fidelity 48, API hermetic 47,
hosted API 44 (the three native-OCR tests run on Windows only, see §5), UiSmoke
4, and `--smoke` exits 0.

Worth knowing: **the WinUI spike builds fine in CI** and fails only on this
machine, for the missing Visual Studio workload in §6.4. The spike is not rotting,
and CI already guards it.

**Phase U3 is done** — collapsible/resizable sidebar (`a68e0b7`), empty state
(`54c2c2f`), page shadows (`817dd8d`) and render placeholders (`844aec6`).

**U4 part two is underway on `refactor/u4-child-views`** — pushed, unmerged, six
commits. Read §8 before continuing it. Before deep U4 work, get an answer on the
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

## 8. U4 part two — `refactor/u4-child-views` (pushed, unmerged)

Six commits. Two of them are the refactor that was intended; four are defects it
uncovered. That ratio is the point of the section: this code had no automated
cover for *what it draws*, so nobody had ever looked at it.

**All three mode surfaces were blank, and always had been.** `RedactView`,
`FormFillView` and `ObjectEditView` each assigned `PageImage.Source = page.Bitmap`
*before* awaiting the render — when `Bitmap` is still null — and never reassigned
it. Redaction boxes were drawn over white, form outlines floated over nothing,
objects were selected and dragged over nothing. No exception, no log; the same
silent shape as the FR-VIEW-01 bug in §2. They were written from one template, so
the mistake was copied rather than made three times. **Check this first on any new
page-rendering surface.**

**The form-field outlines were mirrored.** `PdfFormField.Bounds` arrive
*top-down*, but the outline used the same Y flip the redaction overlay needs, so
every field was drawn on the opposite half of the page. Redaction regions come
from the engine bottom-up and genuinely do flip; form fields do not. The two
overlays differ, and `PageBoxGeometry` is deliberately shared by only the ones
that share a convention — generalising it further would have propagated this bug,
not prevented it.

**A test of mine was passing for the wrong reason.** The mode-panel check asserted
the panel did not overlap the page; with the page unrendered, the element had no
size and could not overlap anything. Once the page draws it is wider than its
viewport and extends under the panel — correct, and clipped — but UIA reports
unclipped bounds, so the assertion fails on good layout. It is removed: UIA has no
z-order, so occlusion cannot be detected from there at all, and catching it needs
a screenshot diff.

### How the bugs were found, since it generalises

Not by reading. By opening a document that actually exercises the path
(`tools/sample-pdf/corpus/form-application.pdf` has two form fields; the default
sample has none), and by zooming out so that "below the fold" could be told apart
from "not drawn". When geometry looked wrong, one log line of the computed values
settled it in a single run — the same instrument-first move that solved the MinIO
race in §5.

### Still open here

- **Closed in §9** — `ObjectEditView`'s overlay is not converted, and its box and
  selection-handle geometry is **unverified**; the sample has no editable
  objects, so nothing draws. Given the form overlay was mirrored, treat this as
  suspect until seen against a document that has some. *It was right to be
  suspect: four defects, two of them in the engine. Note also that the premise
  was wrong — `scan-letters.pdf` has had image objects all along, and nobody had
  looked.*
- The form-field *cards* in the side panel are still built in code. They carry a
  live value and write back through `SetFormFieldValueAsync`, so binding them
  needs a view model with a value and a command, not a rectangle.
- A `--smoke` proof covers the redaction geometry (`RunRedactGeometryProof`).
  Nothing equivalent covers the form or object overlays. *The object overlay has
  one as of §9; the form overlay still does not.*

## 9. U4 part three — the object-edit surface, and two engine bugs behind it

The suspicion recorded in §8 was right, and it went deeper than the UI. Treating
`ObjectEditView` as suspect turned up **four defects, two of them in the engine**,
and FR-EDIT-04's move/resize did not work at all.

### The evidence came first, and took one run

`tools/sample-pdf/corpus/scan-letters.pdf` is the only corpus document with image
objects (two pages, one full-page scan each) — the note in §8 said the object path
had no fixture, but it did; nothing had looked. A twenty-line probe against the
real shim printed the listed bounds, and the first line settled it:

```
page 0: 612x792pt, 1 object(s)
  id=0 Image bounds=(0.0,0.0)-(780300.0,1306800.0)
```

**Bug 1 — listed bounds were in the wrong unit entirely.** `pf_obj_bbox` mapped
the object's cm matrix over the point `(obj_w, obj_h)`, and `obj_w`/`obj_h` were
read from the image XObject's `/Width` and `/Height` — its size in **pixels**. But
an image XObject is always painted into the unit square (PDF 32000-1 §8.9.5.2);
the cm carries the placement. So the bbox came out scaled by the pixel count:
1275x1650 pixels on a 612x792pt page gives exactly 780300x1306800. Any overlay
drawing those bounds would have drawn a box a thousand times the page. Both
consumers of `obj_w`/`obj_h` wanted the unit square, so the lookup is gone.

**Bug 2 — move/resize never moved anything, and this is the serious one.** With
the bounds fixed, a move to (72,500)-(272,620) round-tripped as (0,0)-(1,1). The
replacement operator was built as `"%g %g %g %g %g %g /%s Do"` — six numbers and
**no `cm` operator to consume them**. The matrix was never applied; worse, the
span being replaced covers the object's *original* `cm` too, so the object lost
the placement it already had and was painted through the identity CTM: a 1pt
square in the page corner. Every move and resize FR-EDIT-04 has ever performed
did that. The fix emits the `cm`, and wraps the matrix in `q`/`Q` when the object
had no `cm` of its own, since a matrix introduced there would otherwise stay in
force and displace everything painted after it.

### Why the fidelity gate did not catch either

`ObjectEditFidelityTests` moved an object and then asserted: a receipt came back,
its old and new operators differed, the document saved, reopened, listed a
non-empty object collection, and rendered more than 100 bytes of PNG. **All of
that is true of a document whose image has been shrunk to a point in the corner.**
Nothing asserted *where the object landed* — the one fact the test exists for.

It does now, to a quarter of a point, before and after save/reopen, plus a check
that listed bounds lie on the page at all (which Bug 1 failed by three orders of
magnitude). Both new assertions were confirmed to fail against the unfixed shim
and pass against the fixed one, per the §3 rule. The reopen check also read page 0
regardless of which page had been edited; it now reads the page that was.

### Two more in the view

**Bug 3 — the drag axis was mirrored.** `Overlay_MouseMove` computed
`dyPdf = (cur.Y - start.Y) / _scale` with the comment *"screen Y grows down; PDF Y
grows up"* on the same line, and did not negate. Dragging down moved the object
up, and the N and S handles each grew the box where they should shrink it. The
keyboard path beside it has always had the sign right (`Up` is `+step`), so the
two disagreed in the same file. This is the third Y-flip defect in three
overlays — see the form-field mirror in §8 — and the reason `PageBoxGeometry`
keeps earning its place.

**Bug 4 — the selection handles accumulated.** `RedrawSelectionVisuals` appended
eight `Rectangle`s to the overlay Canvas and removed none, and it runs on every
mouse-move of a drag. One drag across the page left hundreds of stale handles
along the path the pointer took, and deselecting left the last eight on screen
permanently.

### The conversion itself

`ObjectBoxViewModel` (observable, because a drag moves it and selection repaints
it) now backs a bound `ItemsControl`, with selection styling in a `DataTrigger`.
The handles stay in the `Canvas` — they are transient view state belonging to
whichever box is selected, not items in their own right, the same split
`RedactView` makes for its rubber band. Setting `Bounds` recomputes the screen
rectangle through `PageBoxGeometry`, so the two cannot drift; the old code kept
the object, a screen `Rect` and a `Rectangle` in a triple and updated them by hand
at four call sites. Object bounds share the redaction overlay's flip, confirmed
against the shim rather than assumed — they are PDF user space, bottom-left
origin. The commit path now takes `PdfRect` directly instead of converting to
screen coordinates and straight back, which deleted the last local copy of the
flip in this file.

`RunObjectGeometryProof` in `--smoke` covers the box, the direction it moves when
its bounds change, the handle positions and the hit-test. It was confirmed to fail
with the flip removed (`Top was 600, expected 100`).

### Still open here

- **The form overlay still has no `--smoke` proof.** The object and redaction
  overlays now have one each.
- The form-field *cards* in the side panel are still built in code (unchanged
  from §8): they carry a live value and write back through
  `SetFormFieldValueAsync`, so binding them needs a view model with a value and a
  command, not a rectangle.
- **A Form XObject's bbox is the mapped unit square, not its `/BBox`.** For a form
  whose BBox is not the unit square that reports placement and scale rather than
  the exact painted extent. Images are exact, and the corpus has no vector-object
  fixture to measure a form against — worth one if vector editing is ever more
  than nominal.
- The object surface has still not been **seen** with a document loaded. The
  geometry is proven by assertion at three levels and the moved object was
  confirmed by rendering the edited PDF, but nobody has looked at the overlay
  drawn over a page. Open `scan-letters.pdf` in object-edit mode and look.

## 10. Object replace — verified, and it works

The remaining FR-EDIT-04 function, checked because its gate had the same
assertion shape as the one that hid the move/resize bug: a receipt came back, it
differed from the original, the document saved, reopened and rendered — all true
of a replace that did nothing at all. Worse, the replacement source was a render
of *the very page being edited*, so a working replace and a no-op replace
produced near-identical pages.

**It works.** Probed against `scan-letters.pdf` with a flat magenta square: the
centre pixel goes `#FFFFFF` → `#FF00FF`, bounds are preserved exactly
(0,0)-(612,792) through replace, undo restores the original, redo re-applies, and
the swap survives save and reopen. So FR-EDIT-04 was one-for-two broken, not
two-for-two.

The gate now says so. It replaces with a generated solid-magenta PNG that nothing
in the corpus resembles, and asserts on **rendered PNG bytes** rather than
decoding pixels — byte equality of renders is already this suite's currency, and
it avoids an imaging dependency that would need an AGPL check and a notices entry
to draw a coloured square. Five assertions where there were none: the page must
render differently after the replace, identically to the original after undo,
identically to the replacement after redo, and identically to the replacement
again after save and reopen through a fresh engine — plus bounds preserved, since
the contract is that only the interior is swapped.

The undo leg matters more than it looks. A replace leaves the old image in the
document's resources and swaps only the name token before the `Do`, so an undo
that failed to swap it back would leave the replacement on the page while every
structural assertion still passed.

Confirmed to fail: splicing the *original* name back in `pf_replace_object` —
a perfect no-op that leaves every structural check intact — now fails the gate on
`scan-letters.pdf`.

`PdfRectAssert` is extracted and shared by both object gates rather than copied,
for the reason §9 gives about duplicated geometry helpers.

### Where the weak-assertion pattern does and does not reach

Checked rather than assumed, before deciding how far to take this. It is **not**
systemic. `RedactionFidelityTests` proves the covered text is genuinely gone from
extraction and that untouched text on the same page survives — the hard FR-SEC-02
gate, not a paint-over. `TextEditFidelityTests` proves the new string is present,
the old one absent, the neighbouring run untouched and the run box recalculated.
Both are strong. The weakness was confined to the object family, and both of its
members are now covered.
