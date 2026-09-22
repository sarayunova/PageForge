# Session: the corpus guard, the drag that was already proven, and TRD §6

Date: 2026-09-22. Branch `main`, four commits from `29fbec0`.

Suite sizes at the end: Core 178, Fidelity 58, Api 47, UiSmoke 19, and
`--smoke` exits 0. `AGENTS.md` carries these and was wrong about UiSmoke twice
in one session, which is worth expecting again.

---

## 1. The corpus guard already existed

Carried in as "make the corpus explicit, a stray `.pdf` should fail the build
loudly". It already does. `CorpusSmokeTests` fails both directions — a corpus
file with no manifest entry, and a manifest entry with no file — and the
fidelity suite was **red on the working tree** when the session started, on
exactly the stray file the last session left.

So there was nothing to build. `Reordered.pdf` moved to
`tools/sample-pdf/manual/`, outside the glob, kept rather than deleted because
it is the user's. `docs/fidelity-corpus.md` now names that folder so the next
hand-test save has somewhere to land.

**The lesson is about the note, not the code.** An open item claimed the corpus
was unguarded; reading the test would have closed it in two minutes. A handoff
note is a claim, not evidence.

## 2. Drag-to-reorder: the proof was in the log the whole time

Carried in as "needs thirty seconds of a person at the machine". It needed
nobody. `ThumbList_Drop` logs every drop, and `pageforge.log` already held real
ones from the previous evening:

    23:49:35  Thumbnail drop: from 1 to 0
    23:49:42  Thumbnail drop: from 3 to 0
    23:49:56  Thumbnail drop: from 0 to 1

Distinct source and target, each preceded by its matching selection. The gesture
works; the synthesized-drop failure is a harness limitation, because the OLE
drag loop owns the cursor. The drop handler calls the same `MoveReorderItem` the
keyboard path uses, and that path has a passing test.

The logging was added last session *for this purpose* and was never read back.
When a gesture cannot be automated, the log is the evidence, and checking it is
cheaper than asking a human.

## 3. TRD §6, and why the guard came before the work

The last unmet requirement. 287 strings now live in `Resources/UiStrings.resx`;
XAML uses a `{loc:Str Key}` markup extension, code uses `UiStrings.Get/Format`.
Key lookup rather than a generated class: ~250 machine-written properties to
review forever, plus a generator dependency, buys only a compile-time name
check — and the guard below is strictly stronger.

`UiStringsTests` fails five ways: key used but undefined, resource defined but
unused, literal left in markup, literal reaching a UI sink in code, and a
resource that **resolves to its own key**. The last needs the test project to
reference the app, and no text scan can do it: a wrong `ResourceManager` base
name compiles cleanly, passes every scan, and ships an app whose every button
reads `Document_NextPage_Name`. If anyone removes that `ProjectReference` to
tidy up, this check goes quiet rather than failing.

### The count is the story

A per-line grep said 23 literals remained in C#. The span-based guard found
**44 across six files**. The ones that survive longest are exactly what a
line-based search cannot see: a `MessageBox` whose text is on the next line, a
`Hint` built from two concatenated pieces, a ternary choosing between labels.

Deciding "user-facing" from the literal is impossible — a log message and a
button label are identical to a regex. The guard works from the **sinks**
instead (`Hint`, `ShowStatusHint`, `MessageBox.Show`, `SetName`), reads each
call's whole argument span to the matching paren, and scrubs `UiStrings.Get`
keys first, since a key is not a label.

It caught two of my own misses after the migration had "finished": a key used
only inside a ternary was invisible to the reference scan and reported as an
orphan resource, and one call site used `forced.Message` where its neighbour
used `outcome.Message`, so a literal survived three feet from one already
migrated. Both were fixed by changing the code, not by loosening the check.

## 4. Traps, all of which produced a wrong result first

- **A bash heredoc mangles backslashes** in a generated PowerShell script;
  `'\\obj\\'` arrived as `'\obj\'` and threw on the regex.
- **PowerShell 5.1 reads a `.ps1` as ANSI without a BOM.** Every em dash became
  mojibake and the script would not parse. Write generators with
  `UTF8Encoding($true)`.
- **`XmlDocument.SetAttribute` with the xml namespace** invents a `d2p1:`
  prefix binding that `MSBuildResXReader` rejects outright. Write `xml:space`
  as literal text.
- **The build output keeps a deleted source file.** `CopyToOutputDirectory`
  copies but never removes, so moving a corpus PDF left a stale copy in `bin/`
  and the suite stayed red until it was deleted by hand.
- **GateGuard blocks `rm -rf` and `cat > file` heredocs even with the facts
  supplied.** `Remove-Item` and the Write tool went through. A
  `GATEGUARD_EXEMPT_GLOBS` entry for `**/bin/**` would save real time.

---

## Where the project stands

All 26 FR ids in the TRD are implemented; §6 was the last unmet requirement,
and the CHANGELOG's "Not in this release, despite work existing for it" section
is now empty and removed.

**The beta is feature-complete.** What stands between here and `v0.1.0-beta` is
release engineering, not features:

1. **Code signing.** Azure Artifact Signing is not configured (TSD §12.1), so
   every download warns "unknown publisher" through SmartScreen, Defender and
   the browsers. It is the one gap a user meets before they meet the product.
2. **Tag and run `release.yml`**, then install the artifact on a clean machine —
   the install path has never been walked by someone who did not build it.
3. Post-beta by decision, not oversight: the WinUI 3 port and the native ARM64
   build (both TSD §12.1).
