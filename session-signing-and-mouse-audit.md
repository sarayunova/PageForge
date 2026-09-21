# Session: signing, the mouse audit, and three dead surfaces

Date: 2026-09-20 evening to 2026-09-21 late. Branch `main`, fourteen commits
from `a9c82a1` to `da7a13a`.

Suite sizes at the end: Core 178, Fidelity 58, Api 47, UiSmoke 14, and
`--smoke` exits 0. `AGENTS.md` carries these numbers and was wrong about three
of them when this session started, which is worth expecting again.

---

## 1. FR-SEC-03: the signer that had never been called

The native signer (`pf_sign_pdf`, `pf_sig_crypt32.c`) had been compiled into
the shipped DLL for weeks and described as finished. No managed code had ever
called it. Adding the P/Invokes, the engine methods and a fidelity proof is
what finally ran it, and **five defects were waiting in that unreached path**:

- the signer read `fz_buffer_storage`'s length and pointer the wrong way
  round, so crypt32 was handed a bogus address and a length taken from the low
  half of a pointer — an access violation rather than a clean failure;
- the verifier used `CryptVerifyMessageSignature`, which cannot verify the
  detached signature a PDF actually carries;
- it opened the CMS certificate store with `CERT_STORE_PROV_MSG`, which wants
  an open message handle, not a blob;
- it read `CertVerifyCertificateChainPolicy`'s **return value** as the verdict
  instead of `policy_status.dwError`, so **every certificate verified as
  trusted**;
- it asked for each distinguished-name field with `CERT_NAME_RDN_TYPE`, so
  every field came back holding the whole name.

The trust bug is the one that matters: a check that cannot fail is
indistinguishable from a working one until it is relied on. The fidelity proof
therefore asserts both directions — a self-signed certificate must verify as
intact AND as untrusted, and a document edited after signing must stop
verifying.

Two of these were C4047 indirection warnings the compiler had been printing
all along. The rest came from a standalone crypt32 probe and stderr markers.

Shipped on top: `Sign…` and `Signatures` commands, placement by dragging a box
on the page (`SignPlaceView`) with a full keyboard path, and
`PdfSigningService` holding the decisions that are neither the dialog's nor
the engine's — the output is always a new file, the save is always
incremental, and a duplicate field name is refused before the native layer
turns it into a confusing certificate error.

## 2. Three interactive surfaces were inert, and nothing had noticed

Driving a real mouse at the new placement surface found it could not receive
input. The cause was two layers deep, and the second layer was not mine:

- **The page list sits on top.** It is declared after the tool surfaces in the
  grid, so it covers them; every other tool view collapses it and the new one
  did not.
- **A `Canvas` with no `Background` is invisible to hit testing.** The press
  lands on the page image underneath, which is not an ancestor of the canvas,
  so the handlers never run.

The second fault was shared by **`RedactView`** (drag-to-mark redactions had
never worked) and **`ObjectEditView`** (click-to-select, drag-to-move and
resize — the product's stated differentiator — had never worked). All three
rendered correctly, kept their accessible names, and passed screenshots, the
fidelity suite and an accessibility pass. Nothing had ever driven a pointer at
them. `FormFillView` is clean: its overlay is deliberately
`IsHitTestVisible="False"` because fields are edited through the cards.

Object editing had two further faults behind the hit-testing one, found only
once clicks got through: a plain click was treated as a zero-distance drag and
**committed a no-op move into the document** (undo entry, dirty flag, autosave
copy), and `Refresh()` — which runs on any view-model change — rebuilt the
overlay and dropped the selection, so a clicked object stayed selected for
about two hundred milliseconds.

## 3. Claims turned into measurements

- **FR-VIEW-01 scale.** Was carried as met because lazy-loading code exists,
  while the largest corpus fixture is four pages. Measured: a 2,000-page
  document opens in ~10ms and adds ~2 MiB; first, middle and last pages render
  in 102, 34 and 32ms at 96 DPI. A second fixture is written as raw PDF so
  every page has its own content stream — it grows the process by 9 MiB with
  forty pages rendered, and each sampled page proves its own identity, so
  random access returns the page asked for rather than merely a valid one.
- **FR-OCR-02 spreadsheet export.** Implemented: MuPDF has no spreadsheet
  writer, so the workbook is written directly as Office Open XML. Recognition
  goes through the same native pass as the searchable PDF, so both exports of
  one scan carry the same words. Rows are page/line/text — not reconstructed
  tables, which OCR cannot recover reliably.
- While wiring that up: the shell only ever offered the searchable PDF. DOCX
  and page-image export had been on the engine and in the CHANGELOG with no
  way in from the application — the same gap FR-SEC-03 had. All four are now
  reachable through the save dialog's format filter.

## 4. The reorder report, and why it took four rounds

Reported: with page 3 showing, entering reorder mode froze the viewer — no
thumbnail selection changed the page, and the page number did not move either.

The cause was one line: `ThumbList_SelectionChanged` returned early whenever
reorder mode was on, so that the selection a drag sets would not also
navigate. It disabled navigation for every other reason too. Selecting now
shows that page; in reorder mode the selection is kept rather than cleared,
because the drag and the Ctrl+arrow keys act on it.

**It kept being reported as unfixed because an old instance was still
running.** The tree held four app binaries, three stale — the published one
under `artifacts/` was eighteen days old. That has been deleted (307 MB, with
its zip), and both `bin/Debug` and `bin/Release` were rebuilt. One leftover
remains: `bin/Release/net8.0-windows/win-x64/` from 2026-09-12.

## 5. Traps in the harness, all of which produced false results first

Recorded because every one of them cost a round:

- **`dotnet test` on the UI suite does not rebuild the app it launches.** The
  first failure of the session was against a stale exe.
- **UI Automation selection is not a click.** `SelectionItemPattern.Select()`
  never runs the mouse handlers, so it cannot see anything wrong with a path
  that only breaks under the mouse. Three tests passed this way while the
  reported bug was live.
- **Asserting the page indicator is not asserting the page.** They are
  separate bindings; the indicator can move while the surface keeps rendering
  the old page — which was exactly the reported symptom, so that test could
  have passed with the bug fully intact.
- **A list item scrolled out of view still reports its whole rectangle**, and
  the overhang sits under the toolbar. Aiming at its centre clicks chrome, and
  an application ignoring a click it never received looks precisely like the
  bug under investigation. Click targets are now clipped to the strip's
  viewport, and the test window is sized so every thumbnail is reachable.
- **Synthesized input needs its preconditions checked, not assumed.** The test
  host is made DPI-aware (a virtualized process and physical `SendInput`
  coordinates disagree by the scale factor), the foreground window is
  re-acquired before every gesture and the result verified, and pointer
  delivery is confirmed with twelve pixels of slack. Moves *during* a drag are
  not verified, because the OLE drag loop owns the cursor and insisting
  aborted every real drag.
- **A motionless synthetic click is not a human click.** A hand drifts past
  the four-pixel drag threshold, which on a surface that arms drag-and-drop is
  a different code path. `SyntheticMouse.ClickWithDrift` exercises it.
- **xunit runs test classes in parallel.** Adding a second OCR class made an
  existing one fail; two concurrent recognitions over the same trained data is
  one too many. Both are in a serialized collection now.

---

## Open items for tomorrow

1. **`tools/sample-pdf/corpus/Reordered.pdf`** — a stray save from manual
   testing, sitting in the fidelity fixture source. Left in place because it
   is the user's file and they have not said what to do with it. The test
   project globs `corpus\*.pdf`, so the next build sweeps it into the
   regression corpus as a fifth fixture; it is not in `manifest.psd1`.
   **Decide: delete, or move outside the glob.**
2. **Make the corpus explicit.** The regression corpus is currently "whatever
   `.pdf` is in that folder", which is why one stray save can join it. The
   manifest should be the source of truth, and a `.pdf` present but unlisted
   should fail the build loudly. Proposed, not yet accepted.
3. **Drag-to-reorder is still unverified.** A synthesized drag reaches the
   press and starts `DoDragDrop`; the drop never arrives, which is as
   consistent with a harness limitation as with a defect. Recorded in the
   CHANGELOG as unverified rather than asserted by a test that cannot fail
   honestly. Needs thirty seconds of a person at the machine.
4. **TRD §6 string externalization** — the last unmet requirement. Roughly two
   hundred literals across the shell's XAML, dialogs and view-model status
   messages. Deliberately not started: it is wide, mechanical, and a
   half-migration that looks finished is this codebase's signature failure. It
   wants its own session.
5. **`bin/Release/net8.0-windows/win-x64/`** — stale self-contained publish
   from 2026-09-12, offered for deletion, not yet removed.
