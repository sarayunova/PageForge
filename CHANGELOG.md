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
- Password protection and permissions, and digital signature signing and
  verification through the native `pf_sig_crypt32` signer.
- Accessibility pass against WCAG 2.1 AA on the WPF proof shell.

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
- Signed release pipeline (`tools/publish-release.ps1` plus the `release`
  workflow) using Azure Artifact Signing over OIDC, with a
  `-RequireSignature` gate that refuses to package an unsigned binary.
- Third-party notices for the vendored native dependencies.

### Known limitations

- The beta ships the WPF shell. `src/PageForge.App` is a WinUI 3 spike that
  renders one page; porting the UI to WinUI 3 is post-beta work. Recorded as an
  amendment in TSD §12.1.
- Release builds are `win-x64` only. ARM64 Windows runs them under emulation; a
  native ARM64 build is deferred post-beta (TSD §12.1).
