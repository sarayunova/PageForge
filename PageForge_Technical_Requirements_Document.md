# PageForge — Technical Requirements Document (TRD)

**Product:** PageForge — open-source PDF viewer, editor & document platform for Windows
**Company:** LiVi Software Company (sibling product: FrameForge)
**Document version:** 1.0
**Status:** Draft for development kickoff
**Companion document:** PageForge_Technical_Specification_Document.md (the "how")

---

## 1. Purpose and scope

This document defines **what** PageForge must do. It covers functional and
non-functional requirements for the v1 Windows desktop application and its
companion hosted services. It does not describe implementation, technology
choices, or build sequencing — see the Technical Specification Document (TSD)
for those.

## 2. Product vision

PageForge is a free, open-source (AGPLv3) Windows desktop application for
viewing, annotating, and editing PDF documents — a genuine alternative to
Adobe Acrobat. The desktop application is fully functional with **no network
connection**. LiVi Software Company monetizes optional hosted services
(cross-device sync, e-signature workflow, team review, batch OCR/conversion)
and support contracts — never the software itself, and never a closed-source
edition.

## 3. Glossary

| Term | Meaning |
|---|---|
| Content stream | The sequence of low-level drawing operators that make up a PDF page's visible content |
| Reflow | Text re-wrapping to fit a container as content changes |
| AGPLv3 | GNU Affero GPL v3 — copyleft license that extends to network-accessible use |
| Fidelity | How faithfully an edited PDF preserves the original's visual and structural correctness |
| AcroForm | The standard PDF interactive form field format |
| PDF/UA | The PDF accessibility (Universal Accessibility) standard |

## 4. Stakeholders

| Role | Party |
|---|---|
| Product owner | LiVi Software Company |
| End users | Individuals and businesses replacing Adobe Acrobat / Foxit |
| Contributors | Open-source community (post-launch) |
| Primary implementation agent | Codex (OpenAI coding agent), operating against this TRD and the TSD |

## 5. Functional requirements

### 5.1 Document viewing (FR-VIEW)
- **FR-VIEW-01**: Render pages at interactive frame rates for documents up to at least 2,000 pages, loading pages lazily (never the whole document into memory at once).
- **FR-VIEW-02**: Support continuous-scroll and single-page navigation modes, zoom, and view rotation.
- **FR-VIEW-03**: Provide a page-thumbnail panel, an outline/bookmark panel, and full-text search with in-page result highlighting.
- **FR-VIEW-04**: Support multiple documents open in tabs within one window.

### 5.2 Annotation (FR-ANNOT)
- **FR-ANNOT-01**: Highlight, underline, strikethrough, free-hand ink, text notes/comments, stamps, and basic shape annotations.
- **FR-ANNOT-02**: Annotation flattening (convert to static page content) on export, selectable per annotation type.

### 5.3 Content editing (FR-EDIT) — core differentiator
- **FR-EDIT-01**: In-place editing of existing text within a paragraph's original bounding region — click-to-edit, Word-like text cursor and selection.
- **FR-EDIT-02**: When edited content exceeds the original text box's bounds, grow the box; detect potential visual collisions with neighboring objects and require explicit user confirmation rather than silently overlapping content.
- **FR-EDIT-03**: Detect when a font required for edited/inserted characters is not fully embedded in the document, substitute an available font, and surface this to the user both inline and in a properties panel.
- **FR-EDIT-04**: Move, resize, and replace embedded images and vector objects.
- **FR-EDIT-05**: Unlimited undo/redo for all editing operations within a session.
- **FR-EDIT-06**: Preserve document structure (tags, bookmarks, form fields, accessibility metadata) not touched by a given edit.

### 5.4 Forms (FR-FORM)
- **FR-FORM-01**: Fill and flatten AcroForm fields (text, checkbox, radio, dropdown, signature fields).
- **FR-FORM-02**: Create new form fields on any page, with basic validation types.

### 5.5 Page organization (FR-PAGE)
- **FR-PAGE-01**: Merge, split, insert, delete, rotate, reorder, and extract pages, including drag-and-drop thumbnail reordering.

### 5.6 Local OCR & conversion (FR-OCR)
- **FR-OCR-01**: Run OCR on scanned pages locally, with no network required, to make them searchable and text-selectable.
- **FR-OCR-02**: Export to Word, Excel, and image formats locally.

### 5.7 Security (FR-SEC)
- **FR-SEC-01**: Password protection (open password and permissions password).
- **FR-SEC-02**: True redaction — content removal from the underlying stream, not a visual overlay.
- **FR-SEC-03**: Digital signature verification and application; encryption per the PDF 2.0 specification.

### 5.8 Hosted — Accounts & billing (FR-ACC)
- **FR-ACC-01**: Account creation/login, team membership management, and subscription billing for paid hosted tiers.

### 5.9 Hosted — Sync & versioning (FR-SYNC)
- **FR-SYNC-01**: Opt-in cross-device sync of documents with version history and restore.
- **FR-SYNC-02**: Conflict detection and a resolution UI when the same document changed on two devices before syncing.

### 5.10 Hosted — E-signature workflow (FR-ESIGN)
- **FR-ESIGN-01**: Send a document for signature to one or more recipients; track status; send reminders; produce a completion certificate/audit trail.

### 5.11 Hosted — Team review (FR-TEAM)
- **FR-TEAM-01**: Shared commenting/annotation visible to invited team members, with near-real-time update propagation.

### 5.12 Hosted — Batch OCR & conversion (FR-BATCH)
- **FR-BATCH-01**: Submit multiple documents for OCR or format conversion via the hosted API, usage-metered, with completion notification.

## 6. Non-functional requirements

| Category | Requirement |
|---|---|
| Offline-first | All FR-VIEW / FR-ANNOT / FR-EDIT / FR-FORM / FR-PAGE / FR-OCR(local) / FR-SEC features function with no network connection. |
| Performance | Open a 100-page text document in under 1.5s on reference hardware; render a visible page within 150ms during scroll. |
| Platform | Windows 10 version 1809 (build 17763) and later, and Windows 11; x64 and ARM64. **v0.1 beta ships x64 only** — ARM64 runs it under emulation and a native ARM64 build is deferred post-beta (TSD §12.1). |
| Accessibility | Desktop UI targets WCAG 2.1 AA where applicable to native apps; editing operations must not strip existing PDF/UA structure. |
| Licensing | Entire application source is published under AGPLv3; MuPDF attribution retained per its license terms. |
| Security | Hosted API traffic over TLS 1.2+; documents at rest in hosted storage encrypted with AES-256; authentication via an OAuth2/OIDC-compatible flow. |
| Localization | UI strings externalized to resource files; English is the only shipped language at v1. |
| Reliability | The local application must not lose unsaved edits on crash — an autosave/recovery buffer is required. |

## 7. Constraints and known risks

- WinUI 3 and the chosen MuPDF .NET binding are both less mature than WPF/native alternatives; a WPF fallback path must remain viable through Phase 1 of development (see TSD). **This risk materialized: the v0.1 beta ships the WPF shell, with WinUI 3 retained as a spike and its port deferred post-beta (TSD §12.1).**
- Reflowing existing PDF text is bounded to the original object's box; whole-page reflow across independent objects is explicitly out of scope for v1.
- AGPLv3 requires that any network-accessible deployment (including the hosted services) offer corresponding source to interacting users; the hosted services must implement a source-offer mechanism from Phase 4 onward.

## 8. Assumptions and dependencies

- MuPDF continues to be maintained under AGPLv3 by Artifex for the life of v1 development.
- Codex is the primary implementation agent, operating against this TRD and the TSD as its source of truth, with a human maintainer approving merges.

## 9. Out of scope for v1

- macOS, Linux, and mobile clients.
- Real-time simultaneous multi-user co-editing of the same page (team review is asynchronous commenting, not live co-editing).
- Enterprise SSO/SCIM provisioning.
- A proprietary/closed-source edition — explicitly rejected per the business-model decision behind this product.

## 10. Acceptance criteria (representative)

- A test operator can open a real-world 20-page contract, edit an existing paragraph, save, and reopen the result in Adobe Acrobat Reader with no visible corruption.
- The application launches and successfully opens a local PDF with the network adapter disabled.
- A document edited past its original text-box bounds either grows cleanly or produces a clear, actionable collision warning — never a silent visual defect.
- A hosted sync conflict between two devices is always surfaced to the user, never silently resolved by discarding either version.
