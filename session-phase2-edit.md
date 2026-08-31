# Phase 2 — FR-EDIT slice 2A: editing command layer (undo/redo + journal)

**Scope:** Phase 2 first slice. Stands up the editing command foundation — the
TSD §3.1 "editing command layer" and the FR-EDIT-05 undo/redo backbone — in pure
Core C#, with zero native changes (the MuPDF shim and fidelity lane stay
untouched). Later 2B–2F slices hang the per-operation commands off this layer.

**Status:** **DONE** — Core 59/59, Fidelity 20/20, WPF build clean, `--smoke`
exit 0, native DLL untouched.

---

## What was built (`src/PageForge.Core/Editing/`)

- **`IEditCommand.cs`** — `Name` + `ExecuteAsync`/`UndoAsync`. Contract: a
  command is applied on push/redo and reversed on undo; implementations belong
  to the document's single owner worker (threading mirrors the engine seam).
- **`EditCommandStack.cs`** — unlimited undo/redo (FR-EDIT-05, nothing evicted):
  `PushAsync` executes Do and records the command while clearing the redo
  branch; `UndoAsync`/`RedoAsync` move the top command between LIFO branches;
  `CanUndo`/`CanRedo`/`UndoDepth`/`RedoDepth`; `StateChanged` event; `Clear()`.
  A command whose Do throws is NOT recorded. Re-entrant push/undo/redo from
  inside an in-flight command throws (busy guard).
- **`DelegateEditCommand.cs`** — closure-backed command with sync (`Action`) and
  async (`Func<CancellationToken, ValueTask>`) overloads; handy for recording
  snapshot-style mutations before a dedicated command type is written.
- **`CompositeEditCommand.cs`** — macro grouping: executes children in order,
  undoes them in reverse, recorded as ONE stack entry (root multi-step gestures
  as a single undo step). Requires ≥1 non-null child.
- **`EditJournal.cs`** — append-only crash-recovery journal (TSD §3.1): line
  format `PF-EDJ 1` / `D<TAB>seq<TAB>name<TAB>payloadB64` / `U<TAB>seq<TAB>undoesSeq`.
  - Opaque payloads via an encode/decode delegate pair, so the journal is
    command-agnostic; payload-less (closure-only) commands are recorded but not
    restorable.
  - Replay rebuilds the timeline, marks undone edits via references, tolerates a
    torn TRAILING record (crash mid-append → truncates + reports), and throws on
    middle-file corruption or an undo reference to an unknown sequence.
  - Sequence space is global across Do and Undo records, so appending after a
    replay never collides.

### Tests (`tests/PageForge.Core.Tests/`)

- `EditCommandStackTests.cs` (13) — push/undo/redo, redo-branch clearing,
  unlimited depth (1000-op batch), failed-Do not recorded, clear, empty-stack
  nulls, re-entrancy rejection, delegate + composite ordering/reversal.
- `EditJournalTests.cs` (11) — do-record replay, undo flags, applied-only
  replay, torn-tail trim + sequence continuation, mid-file corruption fatal,
  unknown-undo-reference fatal, payload-less not-restorable, empty journal,
  missing magic, sequence resume, payload byte round-trip.

## Key facts / painful lessons

- `dotnet test` on this SDK takes exactly one project per invocation (MSB1008
  with two) — ran Core and Fidelity separately.
- .NET `Convert.FromBase64String` AUTO-PADS `"AQ"` to `"AQ=="` and succeeds —
  a torn-record test wrote `"AQ"` expecting it to fail; had to use an invalid
  character (`"!!!"`) to simulate a torn payload.
- Journal sequence numbering must count Do AND Undo records in one monotonic
  space, otherwise a new edit appended after `D1 D2 U1` would reuse `seq=3` and
  explode on the next replay (per-line `seq == expected + 1`).
- `Reconcile` must produce `TrailingDataTrimmed` from the caller's trim decision
  — it was hardcoded `false` at first, silently swallowing the flag.
- Keep the journal byte-level: line offsets are computed over the raw byte
  array (never `string.Split` lengths), or non-ASCII payloads desync the
  truncation point.

## Verification (all green)

- **Core tests: 59/59** (34 prior + 25 new).
- **Fidelity tests: 20/20** — real shim, renders byte-identical, native shim
  untouched.
- **WPF build clean** (0 warnings / 0 errors); `--smoke` EXIT=0 (command layer
  is pure Core logic — no new smoke stage yet; 2B adds the edit proof).

## How to verify

```
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug
```

## Outstanding / next slices

- 2B: native shim text-run introspection + rewrite primitive (DONE — see below).
- 2C: overflow/collision (FR-EDIT-02) (DONE — Core; see below).
- 2D: font-fidelity (FR-EDIT-03) (DONE — Core; see below).
- 2E: image/vector move-resize-replace (FR-EDIT-04) (DONE — Core layer + engine
  seam per approved fork; the native content-stream transform is **2E-native**,
  a documented follow-up).
- 2F: exit gate — full-corpus edits programmatic diff + edit-and-reopen test +
  WPF `--smoke` edit proof + WPF undo/redo toolbar wiring.

---

# Phase 2 — FR-EDIT slice 2B: native text-run introspection + in-place rewrite

**Scope:** Second slice. Turns on the real MuPDF-backed edit path: hit-test a
text run, resolve its font, rewrite the page's content-stream operators in
place, recalc the run's bbox (FR-EDIT-05/06), and surface a receipt for
undo/redo. Most of the surface (native `pf_list_text_runs` /
`pf_rewrite_text_run` / `pf_revert_text_rewrite`; bindings; `MuPdfEngine`;
Core `TextEditModels`/`TextEditService`/`TextEditCommand`; Core tests;
`TextEditFidelityTests`) was pre-scaffolded but **broken** — the fidelity lane
hung then crashed, so effectively nothing in the native path had ever run.

**Status:** **DONE** — Core 71/71, Fidelity 23/23 (suite no longer hangs),
WPF build clean, `--smoke` exit 0 on the rebuilt DLL.

## What was fixed (native `mupdf_shim.c`)

1. **Content-walk infinite loop (the hang → MSB4166 child crash).**
   `pf_decode_literal` stopped at the FIRST `)` regardless of nesting, so a
   string like `(...thirty (30) days...)` left a standalone `)` token, and in
   `pf_next_tok` the operator-branch `while (!pf_is_delim(...))` loop advanced
   `pos` zero times → `pf_walk_content` spun forever.
   - Rewrote `pf_decode_literal` to track paren **depth** (`( ` +1, `)` −1,
     escaped `\(`/`\)` copied without depth change, octal/`\n\r\t\b\f`/
     line-continuation unchanged).
   - Added a forward-progress guard in `pf_next_tok` (`if (p < *pos || p ==
     guard) p = guard + 1;`) so a stray delimiter byte can never stall the
     walker again.

2. **Run/operator coordinate space mismatch.** `pf_build_runs` returned stext
   origins in device space (top-left origin, y-down) while `pf_op_geometry`
   computed PDF-space origins (bottom-left, y-up); the title at PDF y=720 read
   device y=72. Added `PF_FLIPY(h,y)` and `page_h` (from `fz_bound_page`) and
   applied the flip to `runs[n].y0/.y1/.origin_y` at both store sites.

3. **Receipt header delimiter.** Native wrote `PF-TRW 1` (space) but the
   managed `TextEditReceiptSerializer` splits `\t` and expects `PF-TRW\t1`
   → `FormatException: invalid header`. Changed the native writer (and the
   `mupdf_shim.h` doc) to the tab form. `pf_parse_receipt`'s `strncmp(...,"PF-TRW",6)` still matches, so revert is unaffected.

## Other changes

- `TextEditFidelityTests.Unencodable_new_character_is_reported_cleanly`
  asserted `pf_edit:` in the message, but the rewrite error path (all
  `fz_throw(..., "pf_rewrite_text_run: ...")`) reports
  `"... not encodable by the run's font"` — the actual, accurate text. Updated
  the assertion to `Assert.Contains("not encodable", ...)`.
- Removed ALL temporary debug scaffolding from `mupdf_shim.c`: the `pf_step`
  function, every `pf_step(...)` call (s1…s10), the `DBG` op/run dump, and the
  `pf-trace.txt` reference. Deleted `pf-trace.txt`.

## Verification (all green)

- **Fidelity: 23/23** (was hang+crash). Round-trip: rewrite w/ bbox recalc →
  render → undo → redo → save → reopen; unencodable char; font metadata.
  Includes the render byte-determinism + pinned-manifest-hash gates — goldens
  unchanged (render path untouched).
- **Core: 71/71.**
- **WPF build clean** (0/0); `--smoke` EXIT=0 on the rebuilt `pageforge_mupdf.dll`
  (viewer/organizer/annotation/corpus-dogfood proofs pass).
- Email the fix reminder: after every shim rebuild, copy
  `native\out\PageForge.MuPdfShim\Release\pageforge_mupdf.dll` over the copy in
  `tests\PageForge.Fidelity.Tests\bin\Debug\net8.0\` and
  `src\PageForge.App.Wpf\bin\Debug\net8.0-windows\` (the testhost/app don't
  rebuild it).

## How to verify

```
powershell -ExecutionPolicy Bypass -File native/build-mupdf.ps1   # or the msbuild cmd in AGENTS.md
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug
src\PageForge.App.Wpf\bin\Debug\net8.0-windows\PageForge.App.Wpf.exe --smoke
```

## Outstanding / next slices

- 2C: overflow/collision (FR-EDIT-02) — box growth + sibling intersection +
  confirmation gateway. (DONE — see the 2C section below.)
- 2D: font-fidelity (FR-EDIT-03) — embedded-subset glyph check + bundled
  fallback table + inline/properties surfacing.
- 2E: image/vector move-resize-replace (FR-EDIT-04) as commands.
- 2F: exit gate — full-corpus edits programmatic diff + edit-and-reopen test +
  WPF `--smoke` edit proof + WPF undo/redo toolbar wiring.

---

# Phase 2 — FR-EDIT slice 2C: overflow / collision + confirmation gateway

**Scope:** Third slice. Lands the FR-EDIT-02 ruleset in pure Core C# — box
growth beyond a configurable threshold, sibling-object intersection, and the
"require explicit confirmation rather than silently overlapping" gateway. The
TSD §6 design is Core-domain and unit-testable: "if the new box … exceeds the
original by more than a configurable threshold, compute intersection against
sibling objects' bounding boxes; on intersection … require explicit
confirmation." **Zero native changes** — the MuPDF shim and fidelity lane stay
untouched.

**Status:** **DONE** — Core 83/83, Fidelity 23/23, WPF build clean (0/0),
`--smoke` exit 0.

## What was built (`src/PageForge.Core/Pdf/`)

- **`TextOverflowModels.cs`** — the pure FR-EDIT-02 value types:
  - `PdfRect` (X0,Y0,X1,Y1) with `OverlapArea` AABB intersection.
  - `OverflowOptions` — `GrowthThreshold` (fraction, default 0.25) +
    `MinGrowthPoints` (absolute floor, default 2pt).
  - `CollisionHit` (sibling + overlap area), `TextEditOverflowResult`
    (`GrewBeyondThreshold`, `GrowthFraction`, `GrowthX/Y`, `GrownBox`,
    `EstimatedBox`, `Collisions`, and `NeedsConfirmation` =
    grew-beyond-threshold AND ≥1 collision).
  - `PreparedTextEdit` — the gateway outcome a shell uses to render a warning
    outline (`WarningBox`) and gate the commit.
- **`TextOverflowDetector.cs`** — pure geometry:
  - `AverageAdvance` (per-char advance from the original run).
  - `EstimatedGrownBox` (anchored at the run's bottom-left, width scaled by new
    text length — the engine computes the exact box on commit; this is advisory).
  - `Analyze(original, grownBox, siblings, options)` — flags overflow past the
    threshold, then AABB-scans the grown box against the siblings (excluding the
    edited run) and reports those with positive overlap.
- **`TextEditService.PrepareRewriteAsync(...)`** — the FR-EDIT-02 confirmation
  gateway: lists the page runs, locates the target by run index, builds the
  sibling set (all other runs), estimates the grown box, and analyzes it. The
  caller surfaces the warning when `prepared.NeedsConfirmation` and must obtain
  explicit confirmation before calling `RewriteRunAsync` — never a silent
  commit.

### Tests (`tests/PageForge.Core.Tests/TextOverflowTests.cs`, 12 new)

Fitting/no-growth not flagged; growth below threshold (5% < 25%) not flagged;
growth beyond threshold with no collision grows cleanly (safe); growth into a
sibling flagged + `NeedsConfirmation`; overlap area is the shared-rectangle area
(a 50×10 = 500pt² case); edge-touching (zero area) is not a collision; threshold
tuned to 0 flags any growth (still safe without a collision); `EstimatedGrownBox`
scales width by new length; `PrepareRewrite` safe when no collision / flags a
collision against a sibling run / throws on an out-of-range run; the negative
points floor suppresses tiny growth.

## Design decisions / painful lessons

- **Sibling set = the other text runs on the page.** The engine surface exposes
  run boxes (`ListTextRunsAsync`) but no general "list page objects with
  bounds" primitive yet; text runs are the natural first-class neighbors for
  text-box growth, and extending to embedded images/vectors is the FR-EDIT-04
  slice's concern (TSD §6 note). The analyzer is box-agnostic, so any object
  box list can be fed later without changing the rules.
- **Confirmation lives at the gateway, not in the atomic rewrite.** The engine's
  rewrite is one shot; "require confirmation before committing" is expressed as
  a pre-commit decision (prepare → decide → confirm → commit). This keeps the
  geometry pure/UI-free and preserves the undo receipt contract of
  `RewriteRunAsync`.
- **Pure overflow ≠ collision.** Per TSD, growth alone grows the box cleanly;
  only growth that also hits a sibling must gate (`NeedsConfirmation` is the
  logical AND). Two distinct flags (`GrewBeyondThreshold`, `Collisions`) keep
  that distinction explicit and testable.
- **Edge contact is not overlap.** AABB `OverlapArea` returns 0 for a shared
  edge (zero area) — verified by a test so a just-touching neighbor is not a
  false collision.
- **Estimate vs. exact.** `EstimatedGrownBox` uses the average per-char advance;
  the engine recalculates the true box on commit (fidelity test already proves
  `edited.X1 > title.X1`). The warning box is therefore advisory — intentional,
  since collision gating must happen before the irreversible commit.

## Verification (all green)

- **Core tests: 83/83** (71 prior + 12 new).
- **Fidelity tests: 23/23** — shim untouched, goldens byte-stable.
- **WPF build clean** (0 warnings / 0 errors); `--smoke` EXIT=0 (slice is pure
  Core logic; no new smoke stage yet — 2F adds the edit proof).
- New/edited files carry the AGPL header; no MuPDF/native source touched.

## How to verify

```
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug
```

## Outstanding / next slices

- 2D: font-fidelity (FR-EDIT-03) �?" embedded-subset glyph check + bundled
  fallback table + inline/properties surfacing. (DONE — see the 2D section below.)
- 2E: image/vector move-resize-replace (FR-EDIT-04) as commands.
- 2F: exit gate �?" full-corpus edits programmatic diff + edit-and-reopen test +
  WPF `--smoke` edit proof + WPF undo/redo toolbar wiring (plus wiring the 2C
  warning gateway into the WPF edit overlay).

---

# Phase 2 — FR-EDIT slice 2D: font-fidelity (substitution + surfacing)

**Scope:** Fourth slice. Lands the FR-EDIT-03 font-fidelity ruleset in pure Core
C# — detect when a run's font cannot faithfully render an edited/inserted
character (not embedded, missing glyph, or outside the family set), resolve a
bundled substitute, and surface it inline + for the properties panel. The TSD
§6 design is Core-domain and unit-testable: "check the run's embedded font
subset for the required glyph; if absent, resolve a substitute from a bundled
font-fallback table and flag the run." **Zero native changes** — the MuPDF shim
and its encodability hard gate stay untouched; the analyzer runs BEFORE commit
so the shell can substitute/flag instead of failing the edit.

**Status:** **DONE** — Core 96/96, Fidelity 23/23, WPF build clean (0/0),
`--smoke` exit 0.

## What was built (`src/PageForge.Core/Pdf/`)

- **`FontFidelityModels.cs`** — the FR-EDIT-03 value types:
  - `FontFidelityReason` (MissingGlyph / NonEmbedded / UnsupportedCharacter).
  - `FontSubstitution` (replacement char + optional fallback font name + reason).
  - `FontFidelityIssue` (character, unicode, resolved substitution or null).
  - `FontFidelityResult` (run font name + embedded flag, issues list,
    `HasIssues` / `HasSubstitutions` / `RunNotEmbedded`).
- **`FontFallbackTable.cs`** — the bundled substitution rules:
  - A character-normalization map: smart quotes / en&em dash / ellipsis / nbsp /
    (TM)/(R)/(c)/zero-width → ASCII renderings any text font can paint.
  - A base-14 core-font family register (Helvetica / Times / Courier and their
    Bold/Oblique variants via case-insensitive prefix match) that both resolves
    a fallback PostScript name and tells the analyzer a non-embedded core font
    cannot be trusted beyond Latin-1.
  - `Resolve(rune, fontName, fontEmbedded)` and `FindFallbackFont(...)`.
- **`FontFidelityAnalyzer.cs`** — pure scanner: `Analyze(target, newText)` walks
  the Runes, flags every non-ASCII/non-Latin character once, resolves a
  substitution per character, returns a `FontFidelityResult`.
- **`TextEditService.CheckFontFidelityAsync(...)`** — the FR-EDIT-03 surfacing
  hook: lists page runs, finds the target by run index, runs the analyzer.
  Returns a result the shell renders inline + in the properties panel before
  commit.

### Tests (`tests/PageForge.Core.Tests/FontFidelityTests.cs`, 13 new)

Plain ASCII needs no substitution; curly quotes → straight quotes (each distinct
code point flagged once); em dash → "--"; ellipsis → "..."; nbsp → space;
a duplicate character is flagged exactly once; a non-embedded Helvetica run with
a non-Latin char (Δ) is flagged (NonEmbedded, no paintable replacement —
surfaced, not silently substituted); `RunNotEmbedded` reported for a non-embedded
font; the family register resolves `Helvetica-BoldOblique` → Helvetica; an
unknown embedded custom family returns no fallback; the service hook returns a
result for a real page run; out-of-range run throws; empty new text is rejected.

## Design decisions / painful lessons

- **Pure Core, zero native.** The engine already lists `FontEmbedded`/`FontName`
  per run and `pf_rewrite_text_run` hard-gates encodability natively (the
  fidelity test asserts a clean `not encodable` error). FR-EDIT-03's "detect +
  substitute + surface" rules are therefore implemented as a pre-commit Core
  analyzer; the native gate remains the authoritative backstop. This keeps the
  fidelity lane byte-stable and avoids a shim rebuild for this slice. (A true
  per-embedded-subset glyph bitmap query would be a follow-up native primitive
  — the header already reserves "FR-EDIT-03's subset check lands in a later
  slice".)
- **Substitution is not silent painting.** Printable substitutes (curly quote →
  `"`, em dash → `--`) DO resolve to a replacement the run's font can paint.
  Non-Latin-1 characters (Δ etc.) carry a substitution with `FallbackFontName`
  but **no replacement byte** — they surface as flagged but unpainted rather
  than silently dropping/replacing glyphs (FR-EDIT-03 "never silently overlap /
  always surface"). A future slice can actually repaint those via a fallback
  font embedded at commit time.
- **One issue per unique code point**, not per occurrence, so the inline marker
  and the properties list stay concise.
- **Family prefix matching** resolves `Helvetica-Bold`, `HelveticaItalic`, etc.
  to a single register entry without enumerating every variant.
- **Latin-1 (0xA1..0xFF) is a judgment call**: base-14 fonts (WinAnsi) encode
  these, but an unembedded/subset font may not — so they go through the same
  flagging path with reason chosen by the embedded flag.

## Verification (all green)

- **Core tests: 96/96** (83 prior + 13 new).
- **Fidelity tests: 23/23** — shim untouched, goldens byte-stable.
- **WPF build clean** (0 warnings / 0 errors); `--smoke` EXIT=0 (slice is pure
  Core logic; no new smoke stage yet — 2F adds the edit proof).
- New files carry the AGPL header; no MuPDF/native source touched.

## How to verify

```
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug
```

## Outstanding / next slices

- 2E: image/vector move-resize-replace (FR-EDIT-04) as commands.
- 2F: exit gate �?" full-corpus edits programmatic diff + edit-and-reopen test +
  WPF `--smoke` edit proof + WPF undo/redo toolbar wiring (plus wiring the 2C
  warning gateway and the 2D font-fidelity surfacing into the WPF edit overlay).

---

# Phase 2 — FR-EDIT slice 2E: image/vector move-resize-replace (Core layer)

**Scope:** Fifth slice. Per the approved fork, this slice delivers the FR-EDIT-04
**Core layer + engine seam** first — object listing, pure transform geometry, and
undoable move/resize/replace commands — with the actual MuPDF content-stream
surgery explicitly deferred to a "2E-native" follow-up. Same shape as 2C/2D:
Core-domain, unit-testable against the fake engine, **zero native changes**, so
the fidelity lane stays byte-stable. The real engine surfaces honest
`NotSupportedException`s until the native transform lands.

**Status:** **DONE** — Core 114/114, Fidelity 23/23, WPF build clean (0/0),
`--smoke` exit 0.

## What was built

- **`src/PageForge.Core/Pdf/PageObjectModels.cs`** — FR-EDIT-04 value types:
  `PageObjectKind` (Image / Vector), `PdfPageObject` (kind, opaque stable `Id`,
  `Bounds` as `PdfRect`, optional `Name`, display `Label`), and
  `PdfObjectReplacement` (source path + format for replace).
- **`src/PageForge.Core/Pdf/PageObjectGeometry.cs`** — pure transform math in
  PDF points: `Translate`, `ResizeFromBottomLeft`, `ScaleFromCenter`,
  `ResizeToWidthAspect`, `KeepsAspectRatio`. Fully unit-tested, no engine.
- **`IPdfEngine` seam (+3 methods)** in `src/PageForge.Core/Pdf/IPdfEngine.cs`:
  `ListObjectsAsync`, `MoveResizeObjectAsync`, `ReplaceObjectAsync`. The latter
  two return a `PdfTextEditReceipt` so undo/redo reuses the existing revert path.
  `src/PageForge.MuPdfInterop/MuPdfEngine.cs` implements all three as honest
  `NotSupportedException` stubs (message = "2E-native"), documenting that the
  native transform is the follow-up; `tests/.../FakePdfEngine.cs` models them
  in-memory for tests.
- **`src/PageForge.Core/Editing/ObjectEditCommand.cs`** and
  **`ReplaceObjectCommand.cs`** — undoable `IEditCommand`s mirroring
  `TextEditCommand`: Execute stores the engine receipt; undo/redo splice old/new
  via `RevertTextEditAsync`. Stable `Name` labels ("Move object"/"Replace object").
- **`src/PageForge.Core/Pdf/PageObjectService.cs`** — pure helper over the seam
  (like `TextEditService`): `ListObjectsAsync`, `MoveResizeAsync`,
  `ResizeToWidthAsync`, `MoveByAsync`, `ReplaceAsync` — validation + command
  construction, shared by shells and unit-testable.
- **Tests:** `tests/PageForge.Core.Tests/PageObjectTests.cs`, 18 new facts.

## Design decisions / painful lessons

- **Zero native, Core-first.** The MuPDF content walker currently models only
  text operators (TJ/'/Tj), not image `cm`/`Do` invocations. Landing real
  move/resize/replace would require a large, fidelity-risky shim rebuild, so — per
  the approved fork — this slice locks the whole Core command surface and the
  engine contract, leaving the native transform as the explicitly scoped
  follow-up. The `NotSupportedException` stubs are deliberate and honest, not
  silent no-ops (a silent empty `ListObjects` would misreport).
- **Receipt reuse.** Because both object ops return a `PdfTextEditReceipt`, the
  existing `RevertTextEditAsync` splice path is the single undo/redo mechanism —
  geometry is never re-matched, so undo/redo is faithful even after sibling edits.
  The fake engine routes object receipts via an instance-keyed map so undo/redo of
  an object transform doesn't collide with text-run revert.
- **Replace is interior-only.** `PdfObjectReplacement` swaps the painted interior
  while preserving bounds (and a future native pass preserves the transform) —
  the "keep the box, swap the pixels" semantics the TRD calls for.
- **Aspect preservation is Core-pure.** `KeepsAspectRatio` lets a shell decide
  whether a user drag warps an image (and warn) without any engine call.

## Verification (all green)

- **Core tests: 114/114** (96 prior + 18 new).
- **Fidelity tests: 23/23** — shim untouched, goldens byte-stable.
- **WPF build clean** (0 warnings / 0 errors); `--smoke` EXIT=0.
- New files carry the AGPL header; no MuPDF/native source touched.

## How to verify

```
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Core.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/PageForge.Fidelity.Tests
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src/PageForge.App.Wpf/PageForge.App.Wpf.csproj -c Debug
```

## Outstanding / next slices

- **2E-native (follow-up):** the MuPDF content-stream transform for
  `ListObjectsAsync` / `MoveResizeObjectAsync` / `ReplaceObjectAsync` — extend
  the content walker to model image/vector `cm`/`Do` invocations, then implement
  the three seams for real and add a shim-rebuild + goldens verification pass.
- ~~2F: exit gate — full-corpus edits programmatic diff + edit-and-reopen test +
  WPF `--smoke` edit proof + WPF undo/redo toolbar wiring (plus wiring the 2C
  warning gateway and the 2D font-fidelity surfacing into the WPF edit overlay).~~
  → DONE below.

# Phase 2 — FR-EDIT slice 2F: edit exit gate (full-corpus diff + WPF edit surface)

**Scope:** Phase 2 exit gate — full-corpus programmatic edit diff + edit-and-reopen
persistence, WPF `--smoke` edit proof, and the WPF undo/redo toolbar + edit overlay
(with the 2C overflow/collision confirmation gateway and the 2D font-fidelity
surfacing wired into the interactive path).

## Done

- **2F — full-corpus programmatic edit diff + edit-and-reopen (fidelity suite):**
  new `tests/PageForge.Fidelity.Tests/ProgrammaticEditFidelityTests.cs`. Theory over
  all 4 corpus PDFs; per page the first editable run is rewritten with a `" (ED)"`
  suffix, each run is attempted, and runs that hit the native FR-EDIT-03 "not
  encodable" hard gate are skipped (surfaced, not bypassed). Proves the undo/redo
  splice, save, reopen, and re-extract persistence on the **real shim** at the
  fidelity layer. Artifact base is `AppContext.BaseDirectory` (deterministic).
  Fidelity 22→**27** (+4 new; shared-theory rows).
- **2F — WPF `--smoke` edit proof:** `App.OnStartup` gains
  `RunHeadlessEditProofAsync()` between the annotation and corpus proofs. Drives
  `TextEditCommand` + `EditCommandStack` on the contract-multipage title, runs
  `PrepareRewriteAsync` (2C) + `CheckFontFidelityAsync` (2D) pre-commit, undo/redo,
  saves `artifacts/edit-proof.pdf`, reopens + asserts persistence, writes
  `artifacts/edit-proof.txt` + `edit-proof-p1.png`. Observed:
  `rewritten=True undo=True redo=True persists=True`, overflow/font gates clear.
- **2F — WPF undo/redo toolbar + edit overlay:**
  - `DocumentView.xaml`: new "Edit" toolbar group after flatten — `EditModeToggle`
    ("✎ text", Checked/Unchecked), `EditUndo_Click`, `EditRedo_Click`; the page
    `<Image>` gains `MouseLeftButtonUp="PageImage_MouseLeftButtonUp"`.
  - `DocumentView.xaml.cs`: `EditModeToggle_Changed`, `EditUndo_Click`,
    `EditRedo_Click`, `PageImage_MouseLeftButtonUp` (maps click → PDF points via
    `slot.Image.RenderDpi`, flips Y to PDF bottom-left origin, `HitTestAsync`,
    prompts via programmatic `AskEditText` dialog, commits
    `EditTextRunAsync(allowCollision:false)`); FR-EDIT-02 `NeedsConfirmation`
    outcome opens the Yes/No collision gateway, FR-EDIT-03 font-blocked outcome
    surfaces a message. Ctrl+Z / Ctrl+Y wired via `ApplicationCommands.Undo`/`Redo`
    CommandBindings (CanExecute honors `CanUndo`/`CanRedo`).
  - `DocumentTabViewModel.cs`: `_editStack` (`EditCommandStack`) +
    `_editGate` (`SemaphoreSlim(1,1)`), `CanUndo`/`CanRedo` subscribed to
    `_editStack.StateChanged`, `HitTestAsync`, `EditTextRunAsync` (runs 2C+2D gates
    then pushes `TextEditCommand`, returns `TextEditOutcome`),
    `UndoEditAsync`/`RedoEditAsync`, `ClearEditHistory`,
    `RefreshCurrentPageRenderAsync` (clears current page slot bitmap then
    re-renders on the UI thread). New `TextEditOutcome` record + `TextEditOutcomeKind`
    enum (`Applied`/`FontBlocked`/`NeedsConfirmation`) + `Blocked()/Overflow()/Success()`.
- **Verification (all green):** Core **114/114**; Fidelity **27/27**; WPF build
  **0 warnings / 0 errors**; `--smoke` **EXIT=0** with the edit proof passing. The
  shim is untouched by 2F — the fidelity lane keeps its byte-identity goldens.

## Outstanding / next slices after 2F

- **2E-native:** **DONE** — see the dedicated "# Phase 2 — FR-EDIT slice 2E-native" section below.
- **FR-EDIT-04 full replace:** the content-stream transform now lists and
  move/resizes image/vector `Do` invocations natively; swapping the XObject
  stream (replace interior) is the deferred, highest-fidelity-risk tail.
- **Interactive object edit UI (2E follow-on):** the WPF overlay covers text runs;
  click-to-move/resize/replace objects on the page image is deferred with the
  native transform.
- **WinAppDriver UI smoke (TSD §8):** Phase 1+; the headless `--smoke` remains the
  stand-in.

## Phase 2 — FR-EDIT slice 2E-native (completion)

**Scope:** MuPDF content-stream transform for the object seams, done on the real
shim: the native content walker now recognizes image/vector `cm`/`Do`
invocations, `ListObjectsAsync`/`MoveResizeObjectAsync` work for real (list the
page's `Do` objects; splice a new `cm` matrix to move/resize them), and image
replace is explicitly recorded as the deferred tail. This is the shim rebuild
that the fidelity lane was protected against; the rebuild + full byte-identity
pass is complete and green.

### Native shim (`native/PageForge.MuPdfShim/mupdf_shim.c`, `mupdf_shim.h`)

- New constants: `PF_OBJ_OP_DO 3`, `PF_OBJ_TAG_IMAGE 1`, `PF_OBJ_TAG_FORM 2`.
- `pf_text_op_s` gains `obj_ctm`, `obj_name`, `obj_tag`, `obj_bytes`,
  `obj_nbytes`, `obj_has_cm`, `obj_w`, `obj_h`.
- Walker state gains `have_obj_name`, `tmp_obj_name`, `num_first_armed`,
  `num_first_start`, `num_last_end`, `pending_cm`/`pending_cm_m`/
  `pending_cm_start`/`pending_cm_end`.
- A `Do` branch (outside text) resolves the XObject from page resources by name
  (`pdf_new_name`/`pdf_dict_get`/`pdf_resolve_indirect`), reads
  Subtype/Width/Height, and pushes a `PF_OBJ_OP_DO` op whose region spans from
  the `cm` to the `Do`. `cm` handler captures the pending matrix+span; the name
  handler stores the target name; the number handler tracks byte offsets so the
  region in the content stream is known exactly.
- New exports (via `PF_EXPORT` in the header): `pf_list_objects` (TSV:
  `idx<TAB>tag<TAB>name<TAB>x0<TAB>y0<TAB>x1<TAB>y1<TAB>flags<TAB>…`) and
  `pf_move_resize_object` (splices `[span_start, span_end)` with a rebuilt
  `%g %g %g %g %g %g /%s Do` and writes a `PF-TRW	1 / R / O=/N=` receipt using
  the same generic machinery as text so `pf_revert_text_rewrite` handles object
  undo/redo via exact byte splice).
- Verified the two new symbols export from the rebuilt DLL; the render path
  (`pf_render_page_to_png`) is untouched.

### Managed interop (`src/PageForge.MuPdfInterop/`)

- `MuPdfShimBindings.cs` — `pf_list_objects` / `pf_move_resize_object`
  DllImports.
- `ObjectListParser.cs` — parses the object TSV into `List<PdfPageObject>`
  (tag 1→Image, 2→Vector; `Id` = the zero-based index string; `Bounds` in PDF
  points), reusing the existing engine gate+temp-file+receipt pattern.
- `MuPdfEngine.cs` — `ListObjectsAsync` / `MoveResizeObjectAsync` implemented
  for real (validate id, gate, temp-file, parser/receipt; on failure surface the
  shim error). `ReplaceObjectAsync` stays a stub raising `NotSupportedException`
  (2E-native tail).

### Test + verification (all green)

- **Fidelity 31/31** (was 27; +4 rows of the new
  `ObjectEditFidelityTests.Object_move_resize_persists_across_save_reopen`,
  theory over the corpus). The object move/resize round-trips on the real shim,
  including `scan-letters.pdf`'s full-page image (`612 0 0 792 0 0 cm /IMG Do` →
  a scaled `0.235294 0 0 0.242424 100 100 cm /IMG Do`), undo/redo splices
  exactly, save/reopen persists, and the page still renders. `scan-letters.pdf`
  is the image-bearing corpus doc that gives the gate its teeth; the text-only
  corpus docs still exercise the empty-list path.
- **Core 114/114.** WPF build **0 warnings / 0 errors**.
- **`--smoke` EXIT=0** with the rebuilt shim:
  `rewritten=True undo=True redo=True persists=True`, corpus dogfood
  4 docs zero crashes.
- **Render byte-identity preserved:** the golden diff gate passes — spike,
  mutool, and wpfproof `sample-phase0-p1-*.png` are byte-identical (single SHA-256
  `5d2501313a03…45c`), and `Corpus_commit_matches_the_pinned_manifest_hash` still
  matches every pinned `sha256` in `manifest.psd1`. The shim rebuild did not
  drift any render.

### Build note

- The full `native/build-mupdf.ps1` rebuilds every MuPDF project (libmupdf,
  mutool, …) which is the long tail. Because 2E-native changed ONLY
  `mupdf_shim.c`, the incremental path was: `MSBuild.exe
  PageForge.MuPdfShim.vcxproj /t:Rebuild /p:Config=Release /p:Platform=x64
  /p:PlatformToolset=v143` against the already-built static libs — fast and
  deterministic. A subsequent managed build copies the fresh DLL to output via
  the csproj `PreserveNewest` `None`/`Link` item.