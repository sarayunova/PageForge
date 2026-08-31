---
name: agpl-compliance
description: Use before every commit or pull request on PageForge — AGPL-required license headers, MuPDF attribution, dependency copyleft checks, and the hosted source-offer endpoint.
---

# AGPL compliance checklist (PageForge)

PageForge is AGPLv3 (desktop app fully offline). The hosted services are also network-accessible AGPLv3 code, so §13 applies to them too.

## Pre-commit / pre-PR checklist
- [ ] AGPLv3 license header present on every new source file.
- [ ] MuPDF attribution retained per Artifex terms; never under a GPL-incompatible dependency.
- [ ] Tesseract (Apache 2.0) and other bundled libs — license + notices present.
- [ ] No proprietary/closed-source edition artifacts committed (explicitly rejected, TRD §9).

## Hosted source-offer mechanism (TRD §7, TSD §7)
- `/source` endpoint returns the public repo URL **and the exact commit SHA** running in production.
- Desktop app carries an in-app "View source" link pointing to the same.
- After any deploy, verify the endpoint reports the deployed SHA — this is checked in CI from Phase 4 onward.

## Failure mode
If the source-offer endpoint is out of sync with the deployed commit, treat it as a release-blocker, not a cosmetic issue.