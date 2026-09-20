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
- Password protection and permissions: open and permissions passwords, with an
  encrypted document refusing a wrong password and yielding no extractable text
  until it is authenticated.
- Accessibility pass against WCAG 2.1 AA on the WPF proof shell.
- **Sign…** and **Signatures** commands in the desktop shell (FR-SEC-03). Sign
  asks for a PKCS#12 certificate, its password, a field name and a page, then
  writes a signed *copy* — the open document is never modified, so a failed
  signing run cannot damage it. Signatures reports each field's verdict,
  keeping "the document changed after signing" separate from "the certificate
  is not trusted": the second is the normal outcome for a self-signed
  certificate and must not read as tampering. The shell does not reopen the
  signed copy, because the next ordinary save would rewrite it in full and void
  the signature. Placing the signature by dragging it on the page is not built
  yet; it goes in a fixed box near the bottom-left of the chosen page.
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

### Not in this release, despite work existing for it

- **Localized UI strings (TRD §6).** Strings are literals in XAML and code
  rather than resource files. English only, which matches the v1 plan, but the
  externalization the requirement asks for has not been done.
- **Spreadsheet export (FR-OCR-02).** OCR output converts to searchable PDF,
  DOCX and per-page PNG. Excel is not implemented.
- **Documents of the scale FR-VIEW-01 names.** Lazy page loading is implemented,
  but the largest fixture in the regression corpus is four pages; nothing
  exercises the 2,000-page figure, so the requirement is unproven rather than
  known-met.

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
