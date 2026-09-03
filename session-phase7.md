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

## Landed 2026-09-03 (second session)

- `95bfd28` **winui-build lane is now enforcing** (`continue-on-error` removed
  from `ci.yml`) and a root `CHANGELOG.md` was written from the slice history.
- `7637770` **TSD/TRD amended** — new TSD §12.1 records both beta decisions,
  and the stack table, phase table, risk register, TRD platform row, TRD risk
  list, README, AGENTS.md and the CHANGELOG all point at it.

Pushed as `3277d07..7637770 master -> main`.

## Two decisions the user made (previously open)

1. **Shipping shell: WPF.** Investigating gap (1) showed it was never parity
   drift: `src/PageForge.App` is still the 185-line Phase 0 spike (renders page
   one of a sample doc), while `src/PageForge.App.Wpf` is ~4,450 lines holding
   every product feature. Porting is an unwritten ~4,400-line job whose every
   build cycle would have to round-trip through CI, since WinUI does not build
   on this machine. The user chose to declare WPF the beta product target and
   defer the port post-beta. The WinUI lane stays enforcing so the spike cannot
   rot.
2. **ARM64: deferred.** Beta ships x64 only; ARM64 Windows runs it under
   emulation. A native build (shim cross-build + second release lane) is
   post-beta. **This question is now answered — do not ask it again.**

## Next session starts here — open gaps, ranked

1. **Read the CI result for `7637770`.** This is the first run with the WinUI
   lane enforcing, and it is expected to go red — the lane was originally
   excused because the runner image may lack the UWP/MSIX build tasks. If it is
   an image/tooling gap rather than a code defect, the fix is to install the
   workload in the lane (`dotnet workload install` / the Windows App SDK
   tasks), not to re-add `continue-on-error`.
2. **Phase 6 exit evidence.** `tools/loadtest/` exists but no recorded run
   against TSD targets, and no WCAG 2.1 AA audit artifact. Now that WPF is the
   declared product target, a WPF accessibility audit finally counts as real
   evidence rather than something that would not transfer — so this is
   unblocked and is the largest remaining substantive gap.
3. **Azure Artifact Signing setup** — still the only hard Phase 7 blocker, and
   it needs the maintainer (steps unchanged, above). Until it is done, tagging
   produces an unsigned preview artifact and no release.
4. **API excluded from releases** (`-SkipApi`). Fine if hosted deploys are
   separate, but nothing states so; one sentence in the README would close it.
5. **Tag `v0.1.0-beta`** once (1) and (3) are settled.

