# WCAG 2.1 AA — Documented self-assessment of the desktop shell

**Date of assessment:** 2026-09-12
**Commit assessed:** `e08fe07ccb5c36f3ab40a203196134067cf5b824` (`main`)
**Scope:** `src/PageForge.App.Wpf` — the shipping shell (TSD §12.1). Screens:
`MainWindow`, `DocumentView` (main surface), `FormFillView`, `ObjectEditView`,
`RedactView`, `ProtectDialog`, and the two code-built dialogs
(`AskEditText`, `PromptFieldName`).
**Method:** static review of every XAML/code-behind in scope, automated contrast
calculation for every explicit brush, and the existing UI-Automation inventory
(`tests/PageForge.UiSmoke.Tests`). This is a documented **self-assessment**, not
a third-party audit. Screen-reader verification with a live AT session was not
performed.

## Conformance statement (honest summary)

**The shell does NOT yet conform to WCAG 2.1 AA.** Strengths are concentrated in
the main toolbar (every icon button has `AutomationProperties.Name`, often with a
`ToolTip`, and undo/redo map to `ApplicationCommands` = Ctrl+Z/Ctrl+Y). Weaknesses
are structural: the document surface is a raster image with no accessible text
layer, several core interactions are mouse-only, sidebar text in the default
(light) theme fails 1.4.3, and no status message is announced. A prioritized
remediation plan is at the end. The known gaps below are tracked against the
Phase 6 "WCAG 2.1 AA pass on core screens" exit criterion so the exit review has
a concrete worklist rather than an implied clean pass.

Legend: **PASS** — meets the criterion in scope; **PARTIAL** — some instances
conform and some do not; **FAIL** — does not meet; **N/A** — not applicable to
a desktop application.

## Per-criterion results (WCAG 2.1 A/AA, applicable criteria)

### 1.1.1 Non-text Content (A) — **FAIL**

- The document page image is an unlabeled raster: `DocumentView.xaml:198-201`
  `<Image Source="{Binding Image.Bitmap}" .../>` has no `AutomationProperties.Name`
  and the PDF text is rasterized, so **there is no accessible text layer** —
  a screen reader cannot read, find, or search document content.
- Sidebar thumbnails `DocumentView.xaml:98-100` are unnamed (only the sibling
  "Page N" `TextBlock` gives the container a usable name by composition).
- Reapply screen (`FormFillView.xaml:39`), object-edit screen
  (`ObjectEditView.xaml:26`) and redact screen (`RedactView.xaml:47`) `Image`
  elements are unnamed.
- Tab close "✕" buttons in `MainWindow.xaml.cs:79-87` are **unlabeled** icon-only
  buttons: `Content = "✕"` with no `AutomationProperties.Name`/`ToolTip`.

### 1.3.1 Info and Relationships (A) — **PARTIAL**

- Lists: `ThumbList`, `OutlineList`, `SearchList`, `AnnotationList`, `PageList`
  are real `ListBox`es (good). But the document outline (`DocumentView.xaml:114-118`)
  fakes hierarchy with `Indent` spacers — it is a flat list, not a `TreeView`, so
  outline depth is invisible to AT.
- Form field rows are code-built `Border` cards (`FormFillView.xaml.cs:134-201`);
  each dynamically created `CheckBox` has `Content="Checked"` with **no
  association to the field label** (`:172-178`), and each `TextBox` is unnamed
  (`:185-191`). Redact region rows are plain `TextBlock`s in a `StackPanel`,
  not a list (`RedactView.xaml.cs:128-134`).
- No heading semantics anywhere: "Organize/Annotate/Edit" toolbar captions
  (`DocumentView.xaml:41,51,57`) and "Fields on this page" /
  "Regions marked on this page" are styled `TextBlock`s, never headings.
- Toolbar captions are not programmatically associated with their buttons.

### 1.3.2 Meaningful Sequence (A), 1.3.3 Sensory Characteristics (A) — **PASS**

- DOM/tab order matches visual layout; mode changes are always also written as
  text (`DocumentView.xaml.cs:473-475,503-505,535-537,567-569`) — never
  color/shape/position alone.

### 1.4.1 Use of Color (A) — **PASS**

- Zoom and mode are redundant with text (`ZoomText`, `StatusText`).

### 1.4.3 Contrast (Minimum) (AA) — **FAIL**

Light text on the default white list areas (the sidebar/content area has no
`Background`, so it renders White under the light theme):

| Text | Value | Background | Ratio | Verdict |
|---|---|---|---|---|
| Outline title `#ddd` (`DocumentView.xaml:116`) | #dddddd | #ffffff | ≈1.9:1 | FAIL |
| Outline page label `#888` (`:118`) | #888888 | #ffffff | ≈3.5:1 | FAIL |
| Search page label `#7fd` (`:130`) | #77ffdd | #ffffff | ≈1.2:1 | FAIL |
| Search snippet `#ccc` (`:132`) | #cccccc | #ffffff | ≈1.6:1 | FAIL |
| Annotations caption `#888` (`:141`) | #888888 | #ffffff | ≈3.5:1 | FAIL |
| Annotation type `#9df` (`:148`) | #99ddff | #ffffff | ≈1.5:1 | FAIL |
| Annotation description `#ccc` (`:150`) | #cccccc | #ffffff | ≈1.6:1 | FAIL |
| ProtectDialog instruction `#bbb` (`ProtectDialog.xaml:13`) | #bbbbbb | #ffffff (dialog has no Background) | ≈1.9:1 | FAIL |
| FormFill kind-tag `Brushes.Gray` FontSize=10 (`FormFillView.xaml.cs:160`) | 50% gray | #2d2d2d | ≈3.5:1 | FAIL |

Passing: all toolbar/status text on the `#2b2b2b` chrome (≥6:1), and
`Bounds #666` at `DocumentView.xaml:152` (≈5.7:1).

### 1.4.4 Resize text (AA) — **PARTIAL**

- No `FontSize` on most controls (DPI-scaled, ok), but hardcoded `10/11/12`
  (`DocumentView.xaml:102,116,130,132,141,148,150,152`, `FormFillView.xaml.cs:160`)
  are small and there is no application-level text-scaling beyond OS DPI. No
  `SystemFonts`/theme resources are used (`App.xaml` is empty of resources).

### 1.4.5 Images of Text (A→AA), 1.4.11 Non-text Contrast (AA) — **PARTIAL**

- No images of text in chrome (PASS). Selection/resize strokes are borderline:
  selection border `#2b8cff` on white ≈3.3:1 (passes 3:1 non-text); unselected
  boxes ≈2.9–3.4:1 (`ObjectEditView.xaml.cs:121-191`) — borderline, and the 8×8
  resize handles (`HandleSize=8`) are small targets.

### 2.1.1 Keyboard (A) — **FAIL**

Mouse-only interactions with no keyboard path:
- Object select/move/resize (`ObjectEditView.xaml.cs:55-57,218-381`).
- Redaction box drawing (`RedactView.xaml.cs:140-223`).
- Text-edit hit-tested word selection (`DocumentView.xaml.cs:611-680`).
- Page reorder drag-and-drop (`DocumentView.xaml.cs:268-315`).
- Main `PageList` items are `Focusable="False"` (`DocumentView.xaml:190`), so the
  main page surface is not arrow-navigable.

Passing: all toolbar buttons, dialogs (`IsDefault`/`IsCancel`, Enter/Esc, focus +
select-all on load: `DocumentView.xaml.cs:684-726`, `FormFillView.xaml.cs:300-342`),
and Ctrl+Z/Ctrl+Y for undo/redo.

### 2.1.2 No Keyboard Trap (A), 2.1.4 Character Key Shortcuts (A) — **PASS**

### 2.4.1 Bypass Blocks (A) — **PARTIAL**
Single-window app; the toolbar is reachable but there is no way to skip the
long toolbar to the document surface.

### 2.4.2 Page Titled (A) — **PASS**
Windows/callouts have meaningful `Title`s (`MainWindow.xaml:7`,
`ProtectDialog.xaml:7`, dialog `Title="Edit text"` / `"New text field"`).

### 2.4.3 Focus Order (A) — **PARTIAL**
Natural tab order is fine, but reorder mode and edit modes move interaction to
mouse-only surfaces that keyboard users cannot navigate.

### 2.4.4 Link Purpose (A) — **PASS** («View source» says what it is).

### 2.4.5 Multiple Ways (AA) — **PARTIAL**
Page navigation via toolbar, thumbnails, and next/prev — reasonable.

### 2.4.6 Headings and Labels (AA) — **FAIL**
No headings; form-field inputs (`FormFillView.xaml.cs:185-191`), the
`ProtectDialog` `MethodCombo` (`ProtectDialog.xaml:22-27`, label not associated),
and the `AskEditText`/`PromptFieldName` TextBoxes are unlabeled.

### 2.4.7 Focus Visible (AA) — **PASS** (system focus visuals).

### 2.5.1 Pointer Gestures (A), 2.5.2 Pointer Cancellation (A) — **FAIL/PARTIAL**
Drag-paths for object move and redact drawing have no single-pointer
alternative (2.5.1); pointer-cancel semantics are implicit, not enforced (2.5.2
partial).

### 2.5.3 Label in Name (A) — **PASS**
`AutomationProperties.Name` values match visible labels for the toolbar.

### 3.1.1 Language of Page (A) — **PASS** (OS-provided; no fragment content).

### 3.3.1 Error Identification (A), 3.3.3 Error Suggestion (AA) — **PARTIAL**
Failures surface via `MessageBox` (`MainWindow.xaml.cs:62-63,102`,
`FormFillView.xaml.cs:217,248`) — identified, but suggestions beyond a retry
are absent.

### 3.3.2 Labels or Instructions (A) — **PARTIAL**
Entering form-fill/inline resize spills to code dialogs whose inputs are
unnamed; `ProtectDialog` permutations are not grouped (`:30-34` CheckBoxes have
no `GroupBox`).

### 3.3.4 Error Prevention (AA) — **PARTIAL**
Destructive Apply in RedactView confirms via `MessageBox`
(`RedactView.xaml.cs:232-238`); form/protect operations have no in-flow recovery.

### 4.1.1 Parsing (A), 4.1.2 Name Role Value (A) — **PARTIAL**
Every toolbar icon control has a proper name/role/value via
`AutomationProperties.Name` (`DocumentView.xaml:20-76`); but the unnamed tab-✕
buttons and unnamed document image break 4.1.2, and `PageList` non-focusable
items break its value on the main surface.

### 4.1.3 Status Messages (AA) — **FAIL**
No `AutomationProperties.LiveSetting` exists anywhere in the product. All state
change via `StatusText` (`DocumentView.xaml:165-166`) or `HintText`
(`FormFillView.xaml:14`, `ObjectEditView.xaml:13`, `RedactView.xaml:17`) is
**silent to screen readers**, including "N match(es)", reorder-mode toggles,
OCR/protect/flatten completion, and field set/creation results.

## Strengths worth keeping (do not regress)

- Main toolbar labeling discipline (`AutomationProperties.Name` + `ToolTip`) —
  already load-bearing for `tests/PageForge.UiSmoke.Tests`, which drives real
  UIA patterns (`InvokePattern`/`TogglePattern`/`SelectionItemPattern`).
- Undo/redo via `ApplicationCommands` (Ctrl+Z/Ctrl+Y).
- Modal dialogs: `IsDefault`/`IsCancel`, auto-focus, select-all.
- Status text always accompanies mode/zoom changes (never color-only).

## Prioritized remediation plan

1. **Name the content surfaces** — give the document `Image` an
   `AutomationProperties.Name` ("Document page N") on all five screens, and name
   tab close buttons ("Close <document>"); ship the accessible text layer
   (PDF text extraction surfaced to UIA) as the 1.1.1/4.1.2 fix for the core
   surface. *Highest user impact.*
2. **Keyboard paths** for object select/move/resize, redaction boxes, and page
   reorder (arrow-key move in reorder mode; Tab/arrows + Enter to place boxes).
3. **Fix the sidebar/dialog contrast palette** on light surfaces — the 1.4.3
   fails at `DocumentView.xaml:116-152` and `ProtectDialog.xaml:13` — and the
   `#2d2d2d` kind-tag at `FormFillView.xaml.cs:160`.
4. **Wire `AutomationProperties.LiveSetting` on `StatusText`/`HintText`**
   (Assertive/Polite) for 4.1.3, and render `IsBusy` progress.
5. **Label all dynamic inputs** — form-field rows (`FormFillView.xaml.cs:172-195`),
   `ProtectDialog` `MethodCombo`, dialog TextBoxes
   (`DocumentView.xaml.cs:684-726`, `FormFillView.xaml.cs:300-342`).
6. **Semantics** — real headings/groups or at least `AutomationProperties` names
   for the toolbar captions; `TreeView` for the outline; list roles for field/
   region cards.
7. **Re-run UiSmoke after each fix** and extend it toward a UIA conformance
   probe (focus order, live-region announcements, name completeness).

## Verification

Run `dotnet test tests/PageForge.UiSmoke.Tests` after shell changes (not wired
into CI; requires a desktop session). The static checkpoints in this document
(brush values, `AutomationProperties.Name` presence) are greppable:
`rg "AutomationProperties.Name" src/PageForge.App.Wpf`.