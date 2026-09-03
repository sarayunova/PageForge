# Phase 7 — Public beta & open-source launch: status & open items

**Phase 7 focus (TSD §12 row 7):** CONTRIBUTING.md, community governance,
source-offer endpoint live, signed installers published. Exit gate = **AGPL
compliance verified end to end**.

## What's already done (from git log)
- `8333be1` Phase 7: signed desktop release pipeline (script + CI lane)
- `eda03e0` Phase 7 docs: community governance, contribution guide, security policy
- `c001846` Phase 7 AGPL: view-source link + hosted source-offer verified in CI

So the repo side (release.yml, publish-release.ps1, CONTRIBUTING.md) is in place.

## What we need from the user to CLOSE Phase 7 (CONTRIBUTING.md:92-97)
These are the prereqs that make `release.yml` fully live — without them the
workflow builds an artifact but never publishes. **These are the "thing you
needed from me".**
1. A code-signing certificate (PFX + password, or an Azure Trusted Signing
   profile). Unsigned builds are never published.
2. Repository secrets `PAGEFORGE_CERT_PFX_B64` (base64 of the `.pfx`) and
   `PAGEFORGE_CERT_PASSWORD`.
3. A public hosted repository — feeds the `/source` endpoint's
   `PAGEFORGE_REPO_URL` (required for the AGPL source-offer).

## Next time we start
Pick up right here: user provides the three items above, wire them into the
repo/CI, push a `v*` tag, confirm a signed draft release drop, verify the AGPL
source-offer end to end, and call Phase 7 closed.
