# Phase 7 — Public beta & open-source launch: status & open items

# Phase 7 — Public beta & open-source launch: status & open items
source-offer endpoint live, signed installers published. Exit gate = **AGPL
compliance verified end to end**.

Last updated: 2026-09-04. Resume from "Next session starts here" near the bottom.
Signing is ON HOLD by the maintainer until the week of 2026-09-08 — see that section before acting on it.

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

## Signing: ON HOLD by the maintainer (2026-09-04, revisit the week of 2026-09-08)

The maintainer has deliberately parked the signing decision, to be revisited
next week. **Do not start implementing either path, and do not re-ask the
question unprompted** — bring them the options below when they raise it.

### Current behaviour if a tag were pushed today

`release.yml` fails safe, so nothing bad happens — but nothing useful does
either. With `AZURE_CLIENT_ID` unset, `sign-check` emits `signed=false`, the
Azure login / sign / `-RequireSignature` package steps are all skipped, an
unsigned preview zip is built and uploaded as a **workflow artifact** (90-day
expiry, GitHub login required to fetch), and the `publish-release` job is
skipped outright because it requires `signed == 'true'`.

Net effect: **tagging `v0.1.0-beta` today creates no GitHub Release and no
public download.** For an AGPL project whose Phase 7 gate is public
reachability, that is the real cost — larger than the SmartScreen warnings.

### The two options, when they come back to it

**A — configure Azure Artifact Signing.** ~$10/month, OIDC so no key material in
the repo, and the only reason it is viable at all: public CAs have not issued
exportable `.pfx` files since June 2023 (keys must sit on FIPS hardware), so the
original `PAGEFORGE_CERT_PFX_B64` design could never have held a production
certificate. The long pole is identity validation — individual ≈3 business days,
business needs 3+ years of verifiable history — so that is the item to start
first. Full steps are in CONTRIBUTING.md and in the blocker section above.

**B — ship the beta unsigned.** Defensible for an open-source beta and unblocks
distribution immediately. Requires three changes, and the third is not optional:

1. Relax the `publish-release` job's `if` so a release is created when
   `signed != 'true'`.
2. Expect users to hit SmartScreen "Windows protected your PC / Unknown
   publisher", possible Defender quarantine, Edge/Chrome download warnings or
   blocks, and outright refusal under WDAC/AppLocker. Document it in the release
   notes and README.
3. **Fix the release-notes body, which currently states `code-signed`
   unconditionally.** Publishing unsigned without changing it would ship a
   release page making a false security claim — the same "reports success while
   doing nothing" pattern as the rest of this session's sweep, except in
   user-facing text. This is latent but harmless today only because no release
   can currently be created at all.

Either way, Phase 7's written exit criterion says "signed installers published",
so choosing B means amending it in TSD §12.1 alongside the WPF-shell and
x64-only amendments rather than letting it slide.

Worth telling them either way: a brand-new certificate carries no SmartScreen
reputation, so early downloads warn even *with* signing. Signing buys a real
publisher name instead of "Unknown", and reputation that accrues over time — not
a clean first-run experience on day one.

## Azure signing setup — the steps, if option A is chosen (needs the maintainer)

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

## CI had never once passed — eight defects, now fully green (2026-09-04)

`gh` is now authenticated (`gh auth login`, device flow), which is what made all
of this visible. **Before this session CI had never been green on a single
commit.** Every run died in 15–24s at the first job, so the managed, fidelity
and WinUI lanes were being *skipped, not passing* — the byte-identical render
proof, the fidelity corpus and the signing pipeline had never executed on real
infrastructure even once.

Almost none of it could reproduce locally: the native build step only runs
against a freshly extracted tree, and this dev machine has a warm `native/out`
with artifacts left over from earlier builds.

Fixed, in the order each one unmasked the next:

1. `468dafa` **vcvars discovery.** `build-mupdf.ps1` looked for `vcvars64.bat`
   under a hardcoded `...\Microsoft Visual Studio\2022\{BuildTools,Enterprise}`.
   The runner image does not match those literals. Now uses `vswhere` (fixed
   install path, any VS version/edition) with a recursive glob fallback and a
   diagnostic dump of what *is* installed when discovery fails.
2. `ef22d69` **`bin2coff.vcxproj` patching, three defects.** A `-replace` spread
   over continuation lines parses as three operands under pwsh 7 and throws; the
   replacement anchored on the `Release|Win32` *opening tag* and would have
   nested the new x64 element inside it, producing malformed XML; and the
   idempotency guard checked `Include="Release|x64"` (double quotes) against
   inserted text using single quotes, so it could never match its own output.
   The `PlatformToolset` v143 upgrade also moved out of the x64 branch — nested
   inside it, it never ran on a tree that already had x64, **which is why the
   local working tree is still on v142**.
3. `e6280bb` **The hang.** `Invoke-MsBuild` wrote its batch script to a file
   named `pageforge-mupdf-msbuild.log` and handed it to `cmd /c`. Because the
   extension is `.log`, Windows dispatches it by *file association* rather than
   executing it, and on a headless runner that never returns — one run sat there
   for over an hour and had to be cancelled. Verified locally: identical bytes
   as `.log` hang, as `.cmd` exit immediately. Same commit stopped piping
   msbuild output to `Out-Null` (which is why the hour was silent) and switched
   the exit check from `$?`-after-a-pipeline to `$LASTEXITCODE`, which had been
   letting real compile failures pass.
4. `f6c7afd` **`bin2coff` is a host tool.** `bin2coff.targets` invokes it as the
   literal path `Release\bin2coff.exe` whatever platform is being built. It was
   never built explicitly, so under `Platform=x64` it landed in `x64\Release\`.
   `Invoke-MsBuild` now takes `-Platform` (default x64) and bin2coff is built
   Win32 before the library loop, then asserted present. Invisible locally
   because a stale `Release\bin2coff.exe` sits in the working tree.
5. `0f551bf` **sodochandler.** The 1.28.3 tarball ships
   `platform/win32/sodochandler.vcxproj` but omits `thirdparty/so` entirely. The
   existing patch read only `libmutool.vcxproj` and matched only a *self-closing*
   `<ProjectReference ... />`, so the paired
   `<ProjectReference>…</ProjectReference>` form was left in place. Now scans
   every vcxproj, handles both spellings, and throws if a file mentions
   sodochandler but the pattern misses.
6. `6028a1f` **Artifact unpack path.** Both consumer jobs downloaded
   `native-shim` into `native/out/PageForge.MuPdfShim/Release`, but
   `upload-artifact` roots an archive at the *least common ancestor* of its
   inputs — and this artifact holds the shim DLL and `mutool.exe`, whose common
   ancestor is `native/out`. The payload therefore landed a level too deep.
   Both steps now unpack at `native/out`. Confirmed by downloading the real
   artifact and listing it.

### Result — CI IS FULLY GREEN (first time in the project history)

Run 33817621227 (`e09d0e3`): **all three lanes pass.**

- `MuPDF native + shim` — success, ~8m30s. MuPDF, Tesseract, Leptonica,
  HarfBuzz, ZXing and the shim all build from source.
- `Managed build + tests + fidelity proof` — success. Core, Fidelity and Api
  suites pass **against the real engine** (the shim DLL now loads), the corpus
  dogfood clears 4 documents with zero crashes, and the **Phase 0 fidelity
  criterion is proven**: the engine and WPF renders are byte-identical to
  mutool at `5D2501313A03F2BA7B99154185C8E7141F04D89D9CA2266FA2C77627E828345C`
  — the same hash this dev machine produces.
- `WinUI shell build` — success, 0 warnings, 0 errors.

Two further fixes were needed after the six above:

7. `5f85744` **The byte-compare was incoherent.** It hashed *every* png in
   `artifacts/` and demanded they all be equal, which can never hold:
   `annot-phase1-p1.png` and `edit-proof-p1.png` are renders of deliberately
   modified pages. It now compares the engine and WPF renders against the
   mutool reference by name and asserts each expected file exists. Validated
   both directions locally — it passes on the real artifacts and still reports
   a mismatch when handed the annotated render, so it is not vacuous.
8. `e09d0e3` **The WinUI lane could never have worked.** It failed MSB4062,
   unable to load `Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent`. Those
   MSIX/PRI tasks ship with Visual Studio, not the .NET SDK, so `dotnet build`
   could not reach them — and despite `setup-dotnet` requesting 8.0.x the build
   resolved against the runner default SDK 10.0.400. The lane now uses Visual
   Studio's MSBuild via vswhere. **The runner does carry the UWP workload**; the
   tooling was never the problem.

**The runner image has Visual Studio 18 Enterprise**, not 2022 — the final
confirmation that the original hardcoded
`...\Microsoft Visual Studio\2022\{BuildTools,Enterprise}` path could never have
matched it. Never hardcode a Visual Studio year, edition, or Program Files root;
use vswhere, and always pass `-requires`, because a bare `-products *` also
matches shell-based installs (it returns SQL Server Management Studio on this
dev machine).

### One false green found along the way

Run 33814499028's render proof reported **success** while its log contains no
RenderSpike output and no byte-identical message — it passed without producing
the proof at all. With the wrong number of pngs on disk the old
`$unique.Count -ne 1` test is satisfied trivially and exits 0 having verified
nothing. `5f85744` closes that path by asserting each expected file exists
before hashing.

## The "reports success while doing nothing" sweep (2026-09-04)

Nine instances found. Four were provably hiding a real problem; the rest were
structural hazards removed before they could. Ranked by what they concealed.

### Confirmed to have hidden something real

1. **`093e256` — CI swallowed test failures.** A pwsh step takes its exit code
   from the *last* command, and a failing native command neither stops the
   script nor fails the step. Three `dotnet test` calls in sequence reported
   only whether the third passed. Run 33817621227 shipped a green check with
   `PageForge.Fidelity.Tests` at **Failed: 1, Passed: 47**. The managed *build*
   loop above it had the identical defect, so a project could fail to compile
   and still show green. Both now check `$LASTEXITCODE` per item and name every
   failure.
2. **`093e256` — the corpus was being corrupted on checkout.** What the
   swallowed failure was hiding: no `.gitattributes` existed, so git's content
   heuristic classified five of six committed PDFs as text and, with
   `core.autocrlf=true` (the Windows default, hosted runners included), rewrote
   LF to CRLF *inside* them. That is also why hosted runs logged "cannot
   recognize xref format / trying to repair broken xref" for every corpus
   document and local runs never did — MuPDF was silently repairing damaged
   files on every open. After the fix that warning count is **0**.
   One manifest hash had to be re-pinned: `form-application.pdf` had been
   recorded from an already-mangled working copy, so it could only ever have
   matched a corrupted checkout. Verified with mutool that every corpus blob
   opens cleanly at its expected page count before changing it.
3. **`123c4c4` — three OCR tests passed without running.** They returned early
   when the native toolchain was absent, commented "so the suite stays green".
   xunit 2.5.3 has no runtime skip, so an early return reports **passed**.
   Before the native lane went green the shim was never in CI either, so all
   three reported passing on every run without executing one assertion — the
   suite advertised live MuPDF+Tesseract coverage it was not providing.
4. **`441066a` — `Condition="Exists(...)"` skipped the engine silently.**
   `PageForge.MuPdfInterop` and `PageForge.Api` copy the shim DLL (and the Api,
   tessdata) only if present. While the native artifact was unpacking to the
   wrong path that condition was false, the copy was skipped, the build passed,
   and the suites ran with no engine at all.

### Structural hazards removed (not observed hiding anything)

5. **`d8dc96c` — `--smoke` exit codes were overwritable.** Every proof assigned
   `Environment.ExitCode` directly, including `0` on success, so a later pass
   could clear an earlier failure. The dogfood proof carried a comment about
   running last so a crash would "dominate the process exit code" — a
   workaround for the masking rather than a fix. Failures now go through
   `FailProof()`, first-failure-wins. **Scope honestly:** an A/B could not be
   constructed (every early proof shares the same sample document, and the
   original binary exited non-zero on the same scenario), so this is a hazard
   removed, not a caught bug.
6. **`5f85744` — the byte-compare could pass vacuously.** It hashed every png in
   `artifacts/` and required them all equal, which can never hold since two are
   renders of deliberately modified pages. Run 33814499028's render proof
   reported success with no RenderSpike output and no byte-identical line in the
   log at all — it passed having produced no proof. Now compares named files and
   asserts each exists.
7. **`0f551bf` — a patch step that printed a success it never performed.** The
   sodochandler guard fired while its regex matched nothing, wrote the file back
   unchanged, and printed "removed sodochandler ProjectReference" regardless. It
   now throws when a file mentions sodochandler and the pattern misses.
8. **`e6280bb` — msbuild output was piped to `Out-Null`** and the exit check
   tested `$?` after a pipeline, which reports the pipeline rather than msbuild.
   Real compile failures could pass, and an hour-long hang produced no output.
9. **Pre-existing (fixed the previous session)** — the Azure signing branch that
   printed a note, left `$signArgs` empty and produced an unsigned payload.

### Verified armed, not merely green

`PAGEFORGE_REQUIRE_NATIVE: 1` was confirmed present in the managed job's
environment in run 33821037007. That matters: both new guards are no-ops when
the variable is unset, so a green run alone would not have proved they work.
They passed because the payload is genuinely there.

Every fix in this sweep was verified in **both** directions — that it passes on
the healthy case and still fails when handed a broken one. Making a check laxer
is how a green build gets manufactured, so a fix that only demonstrates "it
passes now" proves nothing.

### Top remaining sweep item — deliberately not fixed

`tools/generate-corpus.ps1` sets `$ErrorActionPreference = 'Continue'`, makes
**nine** mutool invocations each redirecting stderr to `$null`, and checks
`$LASTEXITCODE` **zero** times. It can therefore emit a silently broken corpus,
which is how a bad hash gets pinned into the manifest in the first place.

It was left alone on purpose: its output paths are hardcoded to
`tools/sample-pdf/corpus` and `golden` with no override, and its own header says
re-running changes every hash. The only way to exercise a fix would be to
regenerate and re-pin the artifacts this session just stabilized. **Do it in two
steps:** first add an `-OutDir` parameter so the script can run against a
throwaway directory, verify it reproduces the committed bytes, and only then add
the exit-code checks. `tools/publish-release.ps1` is already clean — five
invocations, five checks.

### CI environment notes

- Jobs now carry `timeout-minutes` (60/45/30). They previously inherited
  GitHub's 360-minute default, which is how the hang burned an hour unnoticed.
- The `Node.js 20 is deprecated` message GitHub emails out is a **warning, not a
  failure**. All four `actions/*` pins are far behind: `checkout` v4→v7.0.1,
  `setup-dotnet` v4→v6.0.0, `upload-artifact` v4→v7.0.1, `download-artifact`
  v4→v8.0.1. Deliberately NOT bumped mid-repair so that a red run had only one
  candidate cause; do it as its own commit, reading each project's
  breaking-change notes (v4→v8 on download-artifact is not a free ride).
- `main` has **no branch protection and no rulesets** (confirmed via `gh api`),
  and `main` is the default branch. `git push origin master:main` publishes
  straight to a public default branch with nothing in the way.
- Repo visibility is **public, verified** by an unauthenticated GitHub API read.
  That closes the Phase 7 question of whether the AGPL source offer is actually
  reachable by strangers.

## Next session starts here — open gaps, ranked

1. **Keep CI green — it now is, and that is new.** All three lanes have passed
   on four consecutive commits (`e09d0e3` through `d8dc96c`), the last of them
   with the test-failure swallowing fixed, so the green is now trustworthy in a
   way the earlier one was not. If a lane goes red, read the error rather than
   assuming a flake: every failure this session was a genuine defect and not one
   was flaky.
2. **Finish the "reports success while doing nothing" sweep.** Nine instances
   found and fixed — see the sweep section above. One item is knowingly left:
   `tools/generate-corpus.ps1` has nine unchecked mutool invocations, and fixing
   it safely needs an `-OutDir` parameter added first so it can be exercised
   without regenerating the committed corpus. Details and the recommended
   two-step approach are in that section.
3. **Bump the `actions/*` pins** (`checkout` v4→v7, `setup-dotnet` v4→v6,
   `upload-artifact` v4→v7, `download-artifact` v4→v8) as their own commit, once
   CI is green so breakage is attributable. This also clears the Node 20
   deprecation warning GitHub keeps emailing about.
4. **Phase 6 exit evidence.** `tools/loadtest/` exists but has no recorded run
   against TSD targets, and there is no WCAG 2.1 AA audit artifact. Now that WPF
   is the declared product target (TSD §12.1), a WPF accessibility audit counts
   as real evidence. Largest remaining *substantive* gap.
5. **Signing — ON HOLD, revisit the week of 2026-09-08.** The maintainer parked
   the decision deliberately. Do not start either path and do not re-ask
   unprompted; the two options, their consequences, and the release-notes bug
   that option B must fix are written up in the "Signing: ON HOLD" section above.
   Until then tagging produces an unsigned workflow artifact and no release,
   which is a safe failure mode.
6. **API excluded from releases** (`-SkipApi`). Fine if hosted deploys are
   separate, but nothing states so; one sentence in the README closes it.
   **Unanswered — this question was put to the maintainer and not yet answered.**
7. **WCAG 2.1 AA audit scope — unanswered.** Asked what standard of artifact is
   wanted (documented self-assessment against the checklist, automated tooling
   output, or something customer-facing); the answer changes the size of the job
   materially. Ask again before starting item 4.
8. **Tag `v0.1.0-beta`** once (1) and (5) are settled.

**Do not re-ask** the WinUI-shell or ARM64 questions: both were decided this
session and are recorded in TSD §12.1.

## Unresolved: auto-mode config write

A refreshed `autoMode.environment` block was drafted for
`~/.claude/settings.json` (verified repo visibility, default branch, no branch
protection, WPF-not-WinUI, `gh` state, local build constraints). **It could not
be applied** — the auto-mode classifier blocks writes to `~/.claude/settings.json`
from both Bash and the edit tool, which is a sensible guard on the file that
governs auto mode. The draft was left at the session scratchpad path and the
user was asked to paste it in or add a permission rule. Ask again if it still
carries the stale "`.NET 8 / WinUI`" description.


## Environment notes
- Build/test must EXCLUDE `src/PageForge.App` locally (see AGENTS.md).
- Suites: Core 154, Fidelity 48, Api 47 — passing locally, and as of
  `e09d0e3` passing in CI against the real engine as well.
- `gh` is **authenticated** as of the 2026-09-04 session (`gh auth login`,
  browser device flow). CI status, branch protection and artifact contents are
  all readable now; earlier notes saying otherwise are obsolete.
- This dev machine has a **warm `native/out`** — a prebuilt shim DLL, an
  extracted MuPDF tree, and a stale `Release\bin2coff.exe`. That warm state is
  why five separate native-build defects could never reproduce here. When
  something passes locally but fails in CI, suspect leftover artifacts first,
  and consider deleting `native/out` to reproduce a clean checkout.
- The repo has no `.gitattributes`, so every text write warns about LF -> CRLF.
  Harmless, but expect the noise on each commit.
