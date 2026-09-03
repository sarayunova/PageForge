# Phase 7 — Public beta & open-source launch: status & open items

**Phase 7 focus (TSD §12 row 7):** CONTRIBUTING.md, community governance,
source-offer endpoint live, signed installers published. Exit gate = **AGPL
compliance verified end to end**.

Last updated: 2026-09-03. Resume from "Next session starts here" at the bottom.

## Repository is live

`https://github.com/sarayunova/PageForge` — local `master` pushes to remote
`main` (`git push origin master:main`). `ci.yml` only triggers on `main`, which
is why the branch is mapped that way. **The first CI run on real infrastructure
has never been reviewed** — check it; `winui-build` is the interesting lane.
Confirm the repo is actually *public*: the AGPL source offer is only satisfied
if strangers can fetch it.

## Landed earlier (pre-existing)
- `8333be1` signed desktop release pipeline (script + CI lane)
- `eda03e0` community governance, contribution guide, security policy
- `c001846` AGPL view-source link + hosted source-offer verified in CI

## Landed this session
- `035dde6` Fix ambiguous `DispatcherQueue` in `src/PageForge.App/MainWindow.xaml.cs`
  (CS0104; `using Windows.System` collided with `Microsoft.UI.Dispatching`).
  Also refreshed AGENTS.md test counts.
- `8416375` this handoff note.
- `b1ce7a6` **Phase 7 item 4 CLOSED.** The `/source` endpoint and both desktop
  "View source" links pointed at a placeholder `github.com/pageforge/pageforge`.
  Now the real repo, with CI exporting `PAGEFORGE_REPO_URL` from
  `github.repository` so forks advertise their own source. New Api test
  `Source_endpoint_reports_the_public_repository` pins both the fallback and the
  env override.
- `3dad692`, `0abbf65`, `f82c5e9` — the signing work, below.

## Signing: four defects found and fixed

All four would have surfaced only at the first real release.

1. `signtool` was invoked as `/sha1 <pfx-path>`. `/sha1` takes a *thumbprint*
   for a store lookup; signing from a `.pfx` needs `/f <pfx> /p <password>`.
2. The distributable zip was built **before** signing. signtool rewrites the
   `.exe` in place, so the released zip would have held an unsigned binary while
   the staged folder looked signed.
3. The Azure branch was a stub: it recognized `PAGEFORGE_ATS_ENDPOINT`, printed
   a note, left `$signArgs` empty, and silently produced an unsigned payload.
4. The kits scan searched only `%ProgramFiles%`; the Windows SDK installs to
   `%ProgramFiles(x86)%` on 64-bit Windows **including GitHub windows-latest**,
   so signtool was never found in CI.

## Signing: current design (decided — Azure)

Production path is **Azure Artifact Signing** (Microsoft renamed it from Trusted
Signing; the action is `azure/artifact-signing-action@v2`, input
`signing-account-name`). Auth is OIDC via `azure/login@v2`, so no key material
lives in GitHub.

Because the action signs files outside the script, `tools/publish-release.ps1`
gained three switches:

- `-NoZip` — stage the payload, stop before packaging
- `-ZipOnly` — package an already-signed folder
- `-RequireSignature` — `signtool verify /pa` every shipped exe **before**
  zipping; fail rather than package an unsigned one

`release.yml` runs: stage (`-NoZip`) → Azure login → sign → package
(`-ZipOnly -RequireSignature`) → draft release. Unconfigured, it still uploads
an unsigned preview artifact and creates no release.

The `.pfx` path is retained for self-signed / internal-CA dry runs only. Public
CAs have not issued exportable `.pfx` files since June 2023, so the original
`PAGEFORGE_CERT_PFX_B64` secret design cannot hold a production certificate.

## Dry run: what is already proven

Rehearsed with a throwaway self-signed cert against a real published payload:
signtool located and invoked correctly, exe **signed and RFC3161-timestamped**,
sign-before-zip ordering holds, and `-RequireSignature` **refused to produce a
zip** for an untrusted chain. Verification failed only with "terminated in a
root certificate which is not trusted" — correct for self-signed;
`Get-AuthenticodeSignature` confirmed signature + timestamp were applied. The
trust-chain link is exactly what a real Azure certificate supplies. The cert,
`.pfx` and dry-run payload were deleted afterwards. Deliberately did NOT install
an untrusted root to force a green run.

## The only remaining Phase 7 blocker (needs the maintainer)

Azure setup — walkthrough is in CONTRIBUTING.md:

1. Azure Artifact Signing account + identity validation (individual ≈ 3 business
   days; business needs 3+ years of verifiable history), then a certificate
   profile.
2. Entra app registration with a **federated credential** for this repo (OIDC,
   no client secret).
3. Grant it **Trusted Signing Certificate Profile Signer** on the signing account.
4. Repo secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`;
   repo variables `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`,
   `AZURE_CERT_PROFILE`.

Then `git tag v0.1.0-beta && git push origin v0.1.0-beta` → signed draft release.
A new certificate has no SmartScreen reputation; early downloads still warn.

## Next session starts here — open gaps, ranked

1. **WinUI shell (biggest gap).** Releases ship `PageForge.App.Wpf`, the
   *fallback*, while `src/PageForge.App` is the locked product target. The
   `winui-build` lane is `continue-on-error: true` (`ci.yml:100`) so breakage is
   silent — that is why the CS0104 above went unnoticed. Plan: flip the lane to
   enforcing (expect CI to go red, that is the point), then diff the two shells
   for drift. WinUI does not build on this dev machine (missing
   `Microsoft.Build.Packaging.Pri.Tasks.dll`); it only builds in CI.
2. **ARM64 — needs a decision from the user.** `publish-release.ps1` is
   `-r win-x64` only, but the Phase 0 exit criterion named x64 *and* ARM64, and
   there is no ARM64 evidence, including for the native MuPDF shim (the hard
   part). Either commit to ARM64 for the beta or amend the TSD. Do not silently
   ship x64-only against a written criterion. **This question was asked and is
   still unanswered.**
3. **No CHANGELOG** at the repo root for a public beta. Cheap; write from git log.
4. **API excluded from releases** (`-SkipApi`). Fine if hosted deploys are
   separate, but nothing says so.
5. **Phase 6 exit evidence.** `tools/loadtest/` exists but no recorded run
   against TSD targets; no WCAG 2.1 AA audit artifact. An audit of the WPF shell
   would not transfer to WinUI anyway — ties back to (1).

Agreed next step when work resumes: **(1)**, starting with the CI lane flip,
plus **(3)** alongside it. **(2) is blocked on the user's answer.**

## Environment notes
- Build/test must EXCLUDE `src/PageForge.App` locally (see AGENTS.md).
- Suites: Core 154, Fidelity 48, Api 47 — all passing as of `f82c5e9`.
- `gh` CLI calls were blocked by the permission classifier this session, so CI
  status could not be read. Ask the user to paste results or allow `gh`.
