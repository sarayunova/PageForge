---
name: api-design-conventions
description: Use when building or changing the hosted API (PageForge.Api) — REST/OpenAPI conventions shared across all five hosted modules (accounts, sync, e-sign, team review, batch OCR).
---

# API design conventions (PageForge) — REST/OpenAPI

All five hosted modules must stay consistent. Base path: `/api/v1`.

## Conventions
- REST + JSON only. Verbs, not actions: `POST /documents/{id}/versions`, `GET /documents/{id}/comments`.
- Resources plural and kebab-case in paths; deterministic order for query params.
- Every endpoint documented in an OpenAPI 3.1 spec that CI validates against (a breaking change without a spec bump is a merge-blocker).
- **Errors:** uniform shape `{ "error": { "code", "message", "traceId" } }`; HTTP codes map to semantic meaning only.
- **Pagination:** cursor-based (`nextCursor`) for list endpoints; page/size only for small fixed sets.
- **Time:** UTC ISO-8601 with `Z` everywhere; never local time.
- **Auth:** OAuth2/OIDC → JWT access + rotating refresh tokens (TSD §7). Endpoints declare scopes.
- **Idempotency:** mutating endpoints accept an `Idempotency-Key` header (Stripe-style) so retries are safe.
- **Versioning:** breaking changes bump the major (`/api/v2`), additive changes are backward-compatible.

## AGPL note
The API runs network-accessible AGPLv3 code — the `/source` endpoint (see the agpl-compliance skill) is part of the contract.