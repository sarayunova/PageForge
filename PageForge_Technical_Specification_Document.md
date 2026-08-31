# PageForge — Technical Specification Document (TSD)

**Product:** PageForge — open-source PDF viewer, editor & document platform for Windows
**Company:** LiVi Software Company (sibling product: FrameForge)
**Document version:** 1.0
**Companion document:** PageForge_Technical_Requirements_Document.md (the "what")
**Primary implementation agent:** Codex

---

## 1. Architecture overview

PageForge splits cleanly into two halves, connected by an optional network
boundary:

```
+-------------------------------------------------------------+
|  DESKTOP APP  (open source, AGPLv3, works fully offline)    |
|                                                               |
|   WinUI 3 UI  --  MuPDF engine  --  Local store (SQLite +   |
|   (viewer,        (render, edit,     filesystem, offline     |
|    editor,         OCR)              edit journal)           |
|    forms)                                                     |
+-------------------------------+-----------------------------+
                                  |
                     optional HTTPS API calls
                (sync, e-sign, review, batch OCR, billing)
                                  |
+-------------------------------v-----------------------------+
|  HOSTED SERVICES  (paid, requires an account)                |
|                                                               |
|  Accounts &   Sync &        E-sign       Team      Batch OCR |
|  billing      versioning    workflow     review    & convert |
+-------------------------------------------------------------+
```

Nothing above the line depends on anything below it working. The hosted
layer is strictly additive — it must never gate core viewing or editing.

## 2. Technology stack

| Layer | Technology | Rationale |
|---|---|---|
| Desktop UI | WinUI 3 (Windows App SDK), C#/.NET 8, MVVM via CommunityToolkit.Mvvm | Modern native Windows UI; see §12 spike for the WPF fallback trigger |
| PDF engine | MuPDF (AGPLv3) via a custom C# binding layer | Only free engine with genuine content-editing primitives |
| Local OCR | Tesseract OCR (Apache 2.0), bundled | Keeps FR-OCR fully offline |
| Local persistence | SQLite (metadata, recents, edit journal) + plain filesystem for documents | Documents are never locked into a proprietary container |
| Hosted API | ASP.NET Core Web API (.NET 8), C# | Shares language and skills with the desktop team |
| Hosted data store | PostgreSQL | Relational integrity for accounts, teams, versions |
| Object storage | S3-compatible storage (self-hosted MinIO or a cloud bucket) | Document version blobs |
| Cache / job queue | Redis | Session cache; background jobs for OCR and e-sign reminders |
| Billing | Stripe | Subscription billing for hosted tiers |
| Transactional email | Postmark or SendGrid | E-sign notifications and reminders |
| CI/CD | GitHub Actions | Build, test, package, release |
| Packaging | MSIX (Store-eligible) + signed unpackaged installer | Covers both Store and direct-download distribution |
| Monitoring | OpenTelemetry + a log/metrics sink (self-hosted Grafana/Loki or a managed APM) | Operational visibility for hosted services |

## 3. Component design

### 3.1 Desktop application
- **UI layer** — Views (DocumentView, ThumbnailPanel, PropertiesPanel, ModeRail) bound to ViewModels; behaviors handle canvas selection and drag-resize interactions.
- **MuPDF interop layer** — a thin `IPdfEngine` C# interface wrapping MuPDF's C API (page rendering to bitmap tiles, text extraction, content-stream editing, OCR hook), kept behind an interface so the engine is swappable in unit tests.
- **Editing command layer** — every mutation (text edit, object move, page reorder) is an `IEditCommand` with Do/Undo, pushed onto an `EditCommandStack`; commands serialize to a local journal for crash recovery.
- **Local store** — `PageForge.db` (SQLite) for recents, bookmark cache, and sync metadata; the original files remain ordinary files on disk.
- **Sync client** — a background service that queues local changes and, only when signed in with connectivity, pushes/pulls via the Sync API; fully inert with no account configured.

### 3.2 Hosted services
Shipped as a single ASP.NET Core modular monolith at launch, split into
separate deployables later only if load requires it:
- **Accounts & billing** — user/team CRUD, OAuth2/OIDC login, Stripe webhook handling.
- **Sync & versioning** — version storage, conflict detection, full-file sync at launch (delta sync is a post-launch optimization).
- **E-signature workflow** — request lifecycle state machine (Draft → Sent → Viewed → Signed → Completed/Declined), audit log, reminder scheduler.
- **Team review** — comment/annotation sync scoped to a shared document; polling at launch, with a SignalR/WebSocket upgrade path if near-real-time becomes a priority.
- **Batch OCR & conversion** — job submission API and a worker pool reusing the same MuPDF/Tesseract engine server-side, usage-metered per account.

## 4. Data model (representative entities)

| Entity | Key fields |
|---|---|
| Document | Id, OwnerId, Title, LocalPathHash, CreatedAt |
| DocumentVersion | Id, DocumentId, VersionNumber, StorageKey, CreatedAt, DeviceId |
| User | Id, Email, DisplayName, AuthProvider |
| Team | Id, Name, OwnerId |
| TeamMember | TeamId, UserId, Role |
| SignatureRequest | Id, DocumentVersionId, Status, CreatedAt, CompletedAt |
| Signer | Id, SignatureRequestId, Email, Status, SignedAt |
| Comment | Id, DocumentVersionId, AuthorId, PageNumber, AnchorRect, Body, CreatedAt |
| OcrJob | Id, OwnerId, DocumentVersionId, Status, PagesProcessed, CreatedAt |

## 5. API surface (representative, REST/JSON, versioned under `/api/v1`)

| Module | Endpoint | Purpose |
|---|---|---|
| Accounts | `POST /accounts/login` | OAuth2/OIDC login exchange |
| Accounts | `POST /billing/subscribe` | Create/change a Stripe subscription |
| Sync | `POST /documents/{id}/versions` | Push a new local version |
| Sync | `GET /documents/{id}/versions/latest` | Pull the latest version, with conflict metadata |
| E-sign | `POST /signature-requests` | Create a request and notify signers |
| E-sign | `GET /signature-requests/{id}` | Poll status/audit trail |
| Team review | `GET /documents/{id}/comments` | Fetch shared comments |
| Batch OCR | `POST /ocr-jobs` | Submit a batch OCR/conversion job |

## 6. Editing engine internal design

- **Text edit flow**: hit-test to a content-stream text run → extract the run and its font reference → present an editable overlay → on commit, the interop layer rewrites the run's operators and recalculates its bounding box → the command layer records the old/new operator list for undo.
- **Overflow handling**: if the new box height/width exceeds the original by more than a configurable threshold, compute intersection against sibling objects' bounding boxes; on intersection, render a warning outline and require explicit confirmation before committing (see FR-EDIT-02).
- **Font-fidelity check**: before committing an inserted character, check the run's embedded font subset for the required glyph; if absent, resolve a substitute from a bundled font-fallback table and flag the run (see FR-EDIT-03).

## 7. Security & AGPL compliance design

- **Source-availability mechanism**: the hosted services expose a `/source` endpoint (and the desktop app carries an in-app "View source" link) pointing to the public repository and the exact commit SHA running in production — satisfying AGPLv3 §13 for network use.
- **AuthN/AuthZ**: OAuth2/OIDC login, JWT access tokens with refresh-token rotation.
- **Data protection**: TLS 1.2+ in transit, AES-256 at rest for stored document versions, per-team data isolation enforced at the database row level.

## 8. Testing strategy

- **Unit tests** — xUnit for command-stack logic, font-fallback resolution, and API controllers.
- **Fidelity regression suite** — a curated corpus of real-world PDFs (contracts, forms, scanned documents, multi-column layouts), edited programmatically and diffed against expected output on every CI run. Given the fidelity risk identified during design, this suite is the single highest-priority test asset in the project.
- **UI automation** — WinAppDriver-based smoke tests for critical flows (open, edit, save, sign).
- **Load testing** — k6 (or equivalent) against the hosted sync and e-sign endpoints ahead of public launch.

## 9. DevOps & release engineering

- **Branching** — trunk-based development with short-lived feature branches; Codex opens pull requests, a human maintainer approves merges.
- **CI pipeline** — build → unit tests → fidelity regression suite → UI smoke tests → package (MSIX + installer) → sign → publish to a pre-release channel.
- **Release channels** — Insider (auto-updated from `main`), Stable (manually promoted).

## 10. Repository structure

```
pageforge/
  src/
    PageForge.App/            # WinUI 3 desktop app
    PageForge.MuPdfInterop/   # MuPDF binding layer
    PageForge.Core/           # editing engine, command stack, shared models
    PageForge.Sync.Client/    # local sync client
  services/
    PageForge.Api/            # ASP.NET Core hosted services (modular monolith)
  tests/
    PageForge.Core.Tests/
    PageForge.Fidelity.Tests/ # regression corpus + diff harness
    PageForge.Api.Tests/
  docs/
    PageForge_Technical_Requirements_Document.md
    PageForge_Technical_Specification_Document.md
  LICENSE                     # AGPLv3
  CONTRIBUTING.md
```

## 11. Development tooling for Codex: MCP servers, plugins & skills

Codex CLI supports the Model Context Protocol, so the following should be
connected before development begins.

### MCP servers

| Server | Purpose |
|---|---|
| Filesystem/Git MCP | Read/write the repo, commit, branch, diff |
| GitHub MCP | Open PRs, track issues, read CI run status, manage the release milestone board |
| Postgres MCP | Inspect/query the hosted schema during backend development and migrations |
| Docker MCP | Build/run local containers for the hosted API and its dependencies (Postgres, Redis, MinIO) |
| Docs-lookup MCP (e.g. Context7 or equivalent) | Pull current WinUI 3, .NET 8, and MuPDF API documentation into context — these move faster than any model's training data |
| Stripe MCP | Scaffold and test billing webhook handling against a sandbox account |
| UI test-runner MCP (WinAppDriver-adjacent) | Drive UI automation runs and read results |
| Error-monitoring MCP (e.g. Sentry) | Read production error reports when debugging post-release issues |

### Skills to load into Codex's working context

- **`.NET / WinUI 3 conventions`** — house style for XAML, MVVM structure, and naming, to keep generated code consistent across a long, multi-phase build.
- **`MuPDF interop patterns`** — the project's specific P/Invoke signatures, marshaling gotchas, and threading rules, since this is exactly the kind of environment-specific detail not present in general training data.
- **`AGPL compliance checklist`** — a pre-commit/PR checklist ensuring license headers are present and the source-offer endpoint stays in sync with the deployed commit.
- **`API design conventions`** — REST/OpenAPI conventions so all five hosted-service modules stay consistent with each other.
- **`Fidelity test corpus`** — how to add a new PDF to the regression suite and what a passing diff looks like.

## 12. Phase-wise development program

| Phase | Focus | Key deliverables | Exit criteria | Est. duration |
|---|---|---|---|---|
| 0 — Foundation | Repo scaffolding, CI skeleton, MuPDF binding spike, WinUI 3-vs-WPF integration spike | A minimal WinUI 3 app rendering one PDF page via MuPDF | Spike proves MuPDF renders correctly through the chosen binding on x64 and ARM64, or the WPF fallback decision is made | 2–3 weeks |
| 1 — MVP viewer & organizer | FR-VIEW, FR-PAGE, FR-ANNOT | Working viewer, annotator, and page organizer | Internal dogfood on real documents with zero crashes across the fidelity corpus | 4–6 weeks |
| 2 — Content editing core | FR-EDIT, undo/redo, font-fidelity system | Text/image editing with overflow and font-fidelity handling | Fidelity regression suite passes on the full corpus; a real contract survives an edit-and-reopen-in-Acrobat-Reader test | 6–10 weeks (hardest phase) |
| 3 — Forms & local OCR | FR-FORM, FR-OCR (local), FR-SEC | Forms fill/create, local OCR, redaction, encryption | Forms and OCR output pass the fidelity suite | 3–4 weeks |
| 4 — Hosted foundation | FR-ACC, FR-SYNC | Account creation, Stripe billing, working cross-device sync with conflict handling | Two devices sync a document with a deliberate conflict resolved correctly | 4–5 weeks |
| 5 — E-sign & team review | FR-ESIGN, FR-TEAM | Send-for-signature flow, shared comments | A full signature-request lifecycle completes end to end with email notifications | 4–5 weeks |
| 6 — Batch services & hardening | FR-BATCH, performance pass, accessibility pass, load testing | Batch OCR/convert live; performance and accessibility targets met | Load-test targets met; WCAG 2.1 AA pass on core screens | 3–4 weeks |
| 7 — Public beta & open-source launch | CONTRIBUTING.md, community governance, source-offer endpoint live, signed installers published | Public repository live, beta build installable | AGPL compliance verified end to end | 2–3 weeks |
| 8 — GA & post-launch iteration | Incorporate beta feedback | v2 backlog (e.g. delta sync, near-real-time review) | Ongoing | — |

## 13. Risk register

| Risk | Mitigation |
|---|---|
| WinUI 3 + MuPDF binding immaturity | Phase 0 spike gate; WPF fallback path kept viable through Phase 1 |
| Content-editing fidelity gaps | Fidelity regression corpus enforced in CI from Phase 2 onward |
| AGPL network-use obligation for hosted services | Source-offer endpoint built as a Phase 4 requirement, not an afterthought |
| Scope creep into full page reflow | Explicitly out of scope per the TRD; box-bounded reflow only |
