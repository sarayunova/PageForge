# Changelog

All notable changes to PageForge are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims
to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) once it
reaches 1.0.

## [Unreleased]

Everything below is the work leading up to the first public beta,
`v0.1.0-beta`, which has not been tagged yet. It will be moved under that
version heading when the tag is pushed.

### Desktop application

- PDF viewer built on a vendored MuPDF 1.28.3 engine reached through a native
  shim (`native/mupdf_shim`), with page rendering, navigation, zoom, text
  selection and search.
- Annotation tools: highlight, underline, strike-through, freehand ink, notes
  and shapes, persisted back into the PDF.
- Page operations: reorder, rotate, insert, delete, extract and merge.
- Object editing: select, move, resize and replace text, image and vector
  objects directly on the page.
- AcroForm support: fill existing text fields, create new ones and flatten
  filled forms into static page content.
- True redaction that removes the underlying content rather than drawing over
  it, plus offline OCR for scanned documents.
- OCR converts to a **spreadsheet** as well as a searchable PDF, a Word
  document and per-page images (FR-OCR-02), one row per line of recognized
  text carrying its page and line number. MuPDF has no spreadsheet writer, so
  the workbook is written directly as Office Open XML rather than by taking on
  a dependency the desktop build would have to ship; recognition still goes
  through the same native pass as the searchable PDF, so the two exports of a
  scan always contain the same words. The layout is rows of text, not
  reconstructed tables: OCR cannot recover a table reliably, and guessing
  would invent structure the document never had.
- All four OCR conversions are now reachable from the **OCR…** command, which
  picks the format from the extension chosen in the save dialog. Only the
  searchable PDF had a way in before — the other three existed on the engine
  and could not be produced by anyone using the application.
- Password protection and permissions: open and permissions passwords, with an
  encrypted document refusing a wrong password and yielding no extractable text
  until it is authenticated.
- Accessibility pass against WCAG 2.1 AA on the WPF proof shell.
- **Sign…** and **Signatures** commands in the desktop shell (FR-SEC-03). Sign
  first shows the page so the signature can be **placed by dragging a box**
  where it belongs — any page, with the box re-projected on zoom — and then
  asks for a PKCS#12 certificate, its password and a field name, writing a
  signed *copy*. The open document is never modified, so a failed signing run
  cannot damage it, and the shell does not reopen the signed copy, because the
  next ordinary save would rewrite it in full and void the signature.
  Placement has a full keyboard path (Enter begins a box, arrows size it,
  Ctrl+arrows move it, Enter places it, Esc cancels) and a one-press "use the
  default box", because putting the only route to signing behind a mouse drag
  would make it unreachable for keyboard and assistive-technology users, and
  signing is the last operation anyone should have to delegate.
  Signatures reports each field's verdict, keeping "the document changed after
  signing" separate from "the certificate is not trusted": the second is the
  normal outcome for a self-signed certificate and must not read as tampering.
- Local digital signing and verification reachable from the engine (FR-SEC-03):
  `SignAsync`, `SaveIncrementalAsync` and `ListSignaturesAsync` on `IPdfEngine`,
  over the existing native signer. Signing uses a PKCS#12 credential through the
  Windows crypto provider and verification checks the digest and the certificate
  chain against the OS trust stores — both entirely offline. An incremental save
  keeps earlier signatures valid.

  Wiring it up proved the native signer had never worked. It had been compiled
  into the shipped DLL and described as finished, but no managed code had ever
  called it, and five defects were sitting in that unreached path: the signer
  read `fz_buffer_storage`'s length and pointer the wrong way round (and so
  faulted inside crypt32); the verifier used `CryptVerifyMessageSignature`,
  which cannot verify the detached signature a PDF uses; it opened the CMS
  certificate store with `CERT_STORE_PROV_MSG`, which expects an open message
  handle rather than a blob; it read `CertVerifyCertificateChainPolicy`'s return
  value as the verdict, so **every certificate verified as trusted**; and it
  asked for each distinguished-name field with `CERT_NAME_RDN_TYPE`, so every
  field came back holding the whole name. A signature field whose contents no
  longer parse is now reported as an unverifiable signature instead of failing
  the entire listing.
- The FR-VIEW-01 scale figure is now measured rather than assumed, on two
  fixtures. A 2,000-page document of **distinct** pages — written as raw PDF
  so no content can be shared between pages — grows the process by 9 MiB with
  forty pages rendered across it, and each sampled page proves its own
  identity, so random access returns the page that was asked for rather than
  merely a valid one. On a 2,000-page document built from the corpus, opening
  takes about 10ms and adds about 2 MiB of working set, and the first, middle
  and last pages render in 102, 34 and 32ms at 96 DPI —
  inside the 150ms the TRD asks for during scroll. Opening is gated against
  the same 1.5s ceiling TRD §6 sets for a 100-page document, twenty times
  smaller, because lazy loading means open time should not follow the page
  count. Both gates were confirmed to fail when tightened.
- Crash recovery of unsaved edits (TRD §6). Edited documents are copied aside
  every thirty seconds, discarded when a tab is closed deliberately, and the
  whole buffer is deleted on a clean exit; whatever a crashed run left behind is
  offered back at startup. Each instance holds a lock file, so a live session's
  documents are never offered to another window as recoverable, and autosaves
  are staged under a temporary name and moved into place so a half-written file
  is never handed back as the user's work. Known limitation: MuPDF's dirty flag
  does not clear on save, so it means "edited at some point" rather than
  "changed since the last autosave" — every edited document is rewritten each
  tick, and the flag is not sufficient for a close-time "unsaved changes"
  prompt.

### Fixed before the first release

- **Every page thumbnail announced its class name.** The thumbnail strip — the
  most-used list in the application — had no accessible name on its list items,
  so each one fell back to `ToString()` on the view model and a screen reader
  read "PageForge.App.Wpf.ViewModels.PageSlotViewModel". The redaction and
  object-edit lists had been given proper names; the page list, which predates
  them, was missed.

- **Object editing did not respond to the mouse at all (FR-EDIT).** Click to
  select, drag to move and drag-a-handle to resize all run through one handler
  on an overlay canvas that had no `Background`, so — as with redaction below —
  no press ever reached it. Two further faults sat behind that one, found once
  the clicks got through: a plain click was treated as a zero-distance drag and
  **committed a no-op move into the document**, putting an entry on the undo
  stack, marking the document dirty and handing it to the autosave buffer; and
  the refresh that follows any view-model change rebuilt the overlay and
  dropped the selection, so a selected object stayed selected for about two
  hundred milliseconds. A click is no longer an edit, and a selection now
  survives a refresh.
- **Drag-to-mark redactions never worked (FR-SEC-02).** The redaction overlay
  is a `Canvas` with no `Background`, and a WPF panel without one is invisible
  to hit testing: every press went to the page image underneath, which is not
  an ancestor of the canvas, so none of the drag handlers ever ran. The
  surface rendered correctly and the keyboard path worked, which is how it
  survived screenshots, a fidelity suite and an accessibility pass — nothing
  had ever driven a pointer at it. It was found when the new signature
  placement surface, modelled on this one, hit the same wall. Both overlays
  now paint a transparent background, and both gestures are driven by a real
  synthesized mouse in the UI smoke suite.

### Not in this release, despite work existing for it

- **Localized UI strings (TRD §6).** Strings are literals in XAML and code
  rather than resource files. English only, which matches the v1 plan, but the
  externalization the requirement asks for has not been done.
- **Page reorder by dragging a thumbnail, verified.** Reordering with
  Ctrl+Up/Ctrl+Down is covered by the UI suite and works. The drag-and-drop
  gesture could not be verified: a synthesized drag reaches the press and
  starts the OLE drag loop, but the drop never arrives, and that is as
  consistent with a harness limitation as with a defect. It is recorded as
  unverified rather than asserted by a test that cannot fail honestly, and it
  needs a manual check before the release.

### Hosted service

- Accounts, billing and document sync.
- Send-for-signature workflow with an audit trail and a completion certificate.
- Shared team review comments.
- Usage-metered batch OCR and conversion jobs, producing DOCX (a native OOXML
  writer, not a template) and per-page PNG raster archives, with a download
  endpoint.
- A load-test harness for the hosted API under `tools/loadtest`.

### Licensing and distribution

- Released under AGPL-3.0-only. The hosted API exposes a `/source` endpoint and
  the desktop shells carry "View source" links, so network users can obtain the
  corresponding source as the licence requires. CI exports the repository URL,
  so a fork advertises its own source rather than this one.
- Community governance: contribution guide, code of conduct and security policy.
- Release pipeline (`tools/publish-release.ps1` plus the `release` workflow).
  Azure Artifact Signing over OIDC is the preferred path, with a
  `-RequireSignature` gate that refuses to package an unsigned binary **when
  signing is configured**. Until signing is configured, `v*` tags ship an
  unsigned beta with release notes that say so and warn about SmartScreen
  "unknown publisher" prompts — decided 2026-09-12, recorded in TSD §12.1.
- Third-party notices for the vendored native dependencies.

### Known limitations

- The beta ships the WPF shell. `src/PageForge.App` is a WinUI 3 spike that
  renders one page; porting the UI to WinUI 3 is post-beta work. Recorded as an
  amendment in TSD §12.1.
- Beta releases are unsigned until Azure Artifact Signing is configured
  (TSD §12.1); Windows SmartScreen, Defender and browsers may warn "unknown
  publisher" on first download.
- Release builds are `win-x64` only. ARM64 Windows runs them under emulation; a
  native ARM64 build is deferred post-beta (TSD §12.1).
