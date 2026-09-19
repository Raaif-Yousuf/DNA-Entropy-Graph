# Hard rules: rationale and carve-outs

`CLAUDE.md` states each rule in its shortest enforceable form. This file
carries the *why*, the exact carve-out (if any), and which rules a machine
actually checks. Numbers match `CLAUDE.md` exactly — if you are reading rule
N here, it is the same rule N there.

**If you think a rule does not apply to you, read its entry here before
concluding that.** A carve-out that is not written down here does not exist;
if you need one, add it here AND to the guard's allowlist in the same
commit, or add it to neither.

## Which rules a machine checks

| Rule | Guard |
| --- | --- |
| 1, 3, 6, 7, 8, 9, 10, 12, 13, 20, 21 | Marked `(G)` in `CLAUDE.md`. Checked by `dotnet test app/tests/DnaEntropyGraph.Guards.Tests` and/or a `scripts/check_*.py` script named per rule below |
| 2, 4, 5, 11, 14, 15, 16, 17, 18, 19 | Not mechanically checked. Caught by review, by `wired-to-nothing`'s per-shape checklist, or not caught at all yet — a gap worth turning into a guard the first time it is broken (see the `a-ban-broken-five-times-needs-a-hook` memory seed) |

As of this file's writing, most of the `(G)` guards above are **not yet
implemented** — `app/` does not exist until issue #61, and the
`scripts/check_*.py` set does not exist until the issues that create it
land (see `docs/dev_commands.md` for the live status of each). This table
states what *will* check each rule once those land, not a claim that they
already do. Do not treat "not yet checked" as "does not apply."

---

### The science (carried from the prototype)

#### 1. The model is the only swappable detail that matters

**Why:** The prototype's own experience is that Evo-specific code (token
ids, tokenizer quirks, the `(L, 4)` unwrap — see the three predictor-boundary
memory seeds) leaks into unrelated modules the moment nothing stops it, and
every leak becomes something that has to change again the day a second model
is added. Confining it to one file makes "add a second predictor" a real,
bounded task instead of a repo-wide grep-and-replace.

**Carve-out:** None currently recorded. The mock pipeline (no GPU, no heavy
deps) must always install and run; if a future change makes that untrue,
that is the carve-out to write down here, not to skip silently.

**Guard:** A `scripts/check_*.py` census over `worker/src/dna_entropy/` for
`import torch`, `import evo2`, `import flash_attn`, or a hardcoded Evo token
id outside `predictors/evo.py`. Not yet built.

#### 2. Validate before predict, always

**Why:** A cloud VM costs real money from the moment it exists. Validating
locally in the app, before a single Google Cloud resource is created, means
a malformed file costs the user nothing. Validating again in the worker (the
same rules, not a subset) is defense in depth for a job the app never saw —
a re-run from a stored manifest, or a job driven by `CloudCli` directly.

**Carve-out:** None. "The app already checked it" is not a reason to skip
the worker's own validation — the two validators are allowed to duplicate
logic on purpose (Hard Rule notwithstanding the general anti-duplication
instinct: this one duplication is intentional, because the two run in
different trust domains and at different times).

**Guard:** Not mechanically checked; caught by `tests-first`'s neighbour-test
set (an invalid input case is a standing member of that set) and by review.

#### 3. The `(L, 4)` contract

**Why:** Every consumer downstream of a predictor (entropy computation,
windowing, the writers, the viewer) depends on this exact shape, dtype and
normalization. A predictor that quietly returns a different shape or an
unnormalized row produces output that is wrong in a way nothing downstream
can detect on its own — the three predictor-boundary bugs already MEASURED
in the prototype (nested tuple, uint8 mask, no-BOS row 0; see the memory
seeds of the same names) are exactly this class.

**Carve-out:** None.

**Guard:** `check_probability_matrix` at the boundary (`worker/src/
dna_entropy/predictors/`), asserting shape, dtype, and row-sum. Already
present in the ported worker package; extend it, never bypass it, when
adding a second predictor.

#### 4. One forward pass per window, never per position

**Why:** Evo 2 is autoregressive — one forward pass already yields every
position in its window. A per-base rolling window (one pass per base) costs
roughly a window's width more GPU time for no accuracy gain, which on a
metered VM is a direct, avoidable cost to the user.

**Carve-out:** None. If a future model is not autoregressive in this way,
that is a new rule for that predictor, not a carve-out of this one.

**Guard:** Not mechanically checked. A test on `analysis/windowing.py`
asserting the number of predictor calls for a given `(L, K)` pair is the
natural guard; not yet written.

#### 5. ASCII-safe console output in the worker; files are UTF-8 with LF

**Why:** MEASURED trap (`cp1252-console-crashes-on-glyphs` memory seed): the
Windows console's default codepage cannot render arbitrary Unicode, and a
`print()` of a glyph-bearing string can raise or silently mangle text. Files
do not have this constraint and should carry full fidelity.

**Carve-out:** None. If a future console-facing feature genuinely needs a
non-ASCII character (a species name, say), print an ASCII transliteration
and put the real value in a file.

**Guard:** Not mechanically checked yet. A `scripts/check_*.py` grep for a
worker `print()` call containing a non-ASCII literal is the natural guard.

---

### The split (new)

#### 6. The app never imports torch and never runs inference in-process

**Why:** This is the entire reason the app/worker split exists: a WinUI
desktop app has no business embedding a multi-gigabyte ML runtime, and doing
so would make every install carry GPU-stack weight even for users who never
run a job locally. The LocalEngine launches the worker as a **child
process**, never in-process.

**Carve-out:** None recorded for `app/`. The v0.1 CPU walking skeleton still
runs the real worker package, just on a VM instead of a real GPU (the
MockPredictor is for tests, not for the walking skeleton itself).

**Guard:** `dotnet test app/tests/DnaEntropyGraph.Guards.Tests` scanning
every `.csproj` under `app/` for a `torch`/`onnx`/inference package
reference. Not yet built (`app/` does not exist yet, issue #61).

#### 7. Every Google Cloud call goes through an interface in `Core/Cloud/`

**Why:** Two reasons. First, testability: ViewModel and runner tests need to
run against scripted failures (billing off, quota, stockout, 403, preempt)
without touching a real project, which only works if every real call is
behind a fake-able interface. Second, blast radius: if `Google.*` can only
be referenced from one project, an accidental real cloud call from
unrelated code is a compile error, not a runtime surprise.

**Carve-out:** None.

**Guard:** A guard test asserting no `.csproj` other than
`DnaEntropyGraph.Cloud` references any `Google.*` package. Not yet built.

#### 8. MVVM: no logic in code-behind

**Why:** Code-behind (`*.xaml.cs`) cannot be unit-tested without a UI
thread. Every branch that lives there is a branch nothing can exercise
except a real running window — the same shape `winui-dev`'s pitfalls list
warns about for `x:Bind Mode=OneTime`.

**Carve-out:** None; `InitializeComponent()` and constructor DI are the only
permitted content.

**Guard:** A guard test (regex or Roslyn-based) over every `*.xaml.cs`
asserting it contains no `if`/`for`/`while`/`switch`. Not yet built.

---

### The cloud (new)

#### 9. Never a shared singleton cloud resource

**Why:** Two lab members may share one Google account. Any fixed-name
resource (the prototype's own `dna-entropy-box` is the recorded example)
collides the moment two runs overlap, on the same account or even the same
run by accident. Every resource must be independently identifiable by the
job that created it.

**Carve-out:** None. This is a hard boundary, not a default.

**Guard:** `VmSpec` (and its bucket equivalent) rejects construction if the
job id or installation id is missing, at the point the object is built, not
at the point it is sent.

#### 10. Every cloud resource carries the standard labels and a max run duration

**Why:** An unlabelled resource is invisible to the Cloud page's discovery
(by-label, per Rule 9) and therefore a leak nobody can find by looking at
the app — only by going to the GCP console directly, which defeats the
entire "no terminal, no gcloud" promise to the user. `maxRunDuration` and
`instanceTerminationAction=DELETE` bound the cost of a run that goes wrong.

**Carve-out:** None. See `termination-action-does-not-fire-on-guest-
shutdown` (memory seed) for the related pitfall this rule alone does not
cover — the VM must also self-delete via the Compute API, not rely on
`maxRunDuration` as the only mechanism.

**Guard:** `VmSpec` rejects a spec missing any required label or duration
field before the request is built. `scripts/hooks/
block_unlabelled_vm_create.py` is a second, tool-call-level backstop.

#### 11. Every run ends in a recorded terminal state; "keep alive" always has an expiry

**Why:** An indefinitely-kept-alive VM is silent, ongoing cost with no
natural end. Defaulting to Stop (not Delete) preserves a user's ability to
inspect a failed run without losing the evidence, while still bounding cost;
an explicit "keep alive until `<time>`" is the only way to extend that, and
it must have an end.

**Carve-out:** None.

**Guard:** Not mechanically checked at the UI level; the app's own
verification of the terminal state after `result.json` is the closest thing
to a guard today, and is itself an app feature (`CloudJobRunner`), not a
static check.

#### 12. No secrets in the repo

**Why:** This is a public repository. A committed service-account key,
refresh token, or API key is not a mistake that gets caught in review after
the fact — it is live on the internet the moment the push completes, and
rotation after the fact does not undo the exposure window.

**Carve-out:** The OAuth **client id** is committed deliberately (Google
treats a Desktop-app client id as public by design). The Desktop-app
client "secret" is not confidential per Google's own model either, but this
repo still treats it as a build-time injected value
(`app/secrets/oauth_client.local.json`, gitignored, or a CI secret) rather
than committing it, as a second line of defense.

**Guard:** `scripts/sync_memory.py`'s push-time secret-pattern scan covers
`.claude/memory/`. A repo-wide secret scan over every commit is not yet
built; GitHub's own push-protection / secret scanning (if enabled on the
repo settings) is the standing backstop until then.

---

### The user (new)

#### 13. User-facing copy rules

**Why:** A hardcoded string in XAML or C# cannot be localized, audited for
tone, or scanned for the em-dash ban in one place. No-jargon-without-
plain-phrase and named-action-per-error exist because the target user is a
lab biologist, not a developer — "a rented computer with a graphics card (a
VM)" is the house style precisely because "a VM" alone assumes context the
user does not have.

**Carve-out:** Comments, docstrings, and `docs/` are exempt from the em-dash
ban; nothing user-facing is.

**Guard:** `ci-docs.yml`'s em-dash scan already covers `docs/user_guide/**`,
`*.resw`, `.github/ISSUE_TEMPLATE/**`, and `README.md`. A guard scanning
every string literal reachable from XAML for the same pattern, and for a
missing named action in an error string, is not yet built (needs `app/`).

#### 14. The user's files are read-only to us

**Why:** Trust. A lab biologist handing over a sequence file (which can
itself be sensitive, unpublished research data) needs a hard guarantee this
app never modifies or deletes their input, and always gives them a way back
to a prior result.

**Carve-out:** None. "We needed to normalize the input" is not a reason to
write next to it — write the normalized copy to the app's own output
location instead.

**Guard:** Not mechanically checked; a filesystem-access test asserting the
input path is never opened for write is the natural guard once `app/`
exists.

---

### The process (inherited discipline, carried from the prototype work)

#### 15. Tests first

**Why:** The `tests-first` skill's own rationale: a test written before the
code tests behaviour; a test written after tends to test whatever the code
already does, including its bugs. `dotnet test`/`pytest -m "not gpu"` green
before commit is the floor; GPU tests are excluded from that floor only
because the laptop cannot physically run them (Intel Arc, no CUDA), not
because they matter less.

**Carve-out:** None on the never-GPU-locally rule. `scripts/
cloud_gpu_test.ps1` on a labelled VM in the owner's project is the only
sanctioned way to run a `gpu`-marked test.

**Guard:** `ci-worker.yml` runs `pytest -m "not gpu"` on every PR touching
`worker/**`. `ci-app.yml` runs `dotnet test` (excluding UI tests) once
`app/` exists. Neither is a guard for "was a test written first" — that part
of the rule is process discipline, not mechanically checkable, and is the
`tests-first` skill's job to enforce by habit.

#### 16. Docs in the same commit

**Why:** A behaviour change with no doc update is exactly how a doc goes
stale the moment it is written, and a stale doc is worse than no doc because
it is trusted. `docs/changelog.d/` exists specifically so concurrent branches
do not all fight over the same lines of `sprint_log.md`.

**Carve-out:** None on the commit-together part. The changelog fragment path
is the one place a branch writes asynchronously; everything else about a
behaviour change updates in the same commit as the code.

**Guard:** `ci-docs.yml`'s changelog-fragment-shape check
(`scripts/compile_sprint_log.py --check`, once that script exists — it does
today) validates fragment format, not that one was written; "was the right
doc actually updated" stays a review judgement call.

#### 17. GitHub Issues is the only tracker

**Why:** A second place to record "what needs doing" (a `ROADMAP.md`, a
`TODO.md`) drifts from Issues the moment either is updated without the
other, and then nobody knows which one is current. `docs/ToTest.md`'s one
carve-out exists because "closed and unproven" is a genuinely different
state from "open" — it is not a second backlog, it is a queue that must
drain.

**Carve-out:** `docs/ToTest.md` only. Never `ROADMAP.md`, `TODO.md`,
`known_issues.md`, or any equivalent.

**Guard:** None currently prevents someone from creating a forbidden file by
that name; `ci-docs.yml` could add a check for exactly these filenames if
one is ever created by mistake (worth filing as an issue the first time it
happens, per the `a-ban-broken-five-times-needs-a-hook` memory seed).

#### 18. Mark theories as theories

**Why:** A causal claim stated as fact, when it was never directly observed,
gets trusted and built on by the next session. When it turns out wrong,
"replace, don't append" keeps `entry_points.md`'s disproven table as the one
place to check, instead of scattering corrections across files that
disagree with each other.

**Carve-out:** None.

**Guard:** Not mechanically checked; a scan for a causal-sounding claim
(regex over "because", "caused by", "the reason is") with no nearby
`MEASURED`/`THEORY` tag is possible in principle and not built.

#### 19. PowerShell, not Unix, on the dev box

**Why:** The dev box is Windows. `rm -rf`, `tail`, `grep` either do not
exist natively or behave differently enough to be a trap (a `.gitignore`d
directory deleted by a Unix-style command run from the wrong shell has real
consequences). Bash is scoped to `worker/vm/*.sh`, which actually runs on
Ubuntu, so Unix syntax there is correct, not an exception to this rule.

**Carve-out:** `worker/vm/*.sh` only, because that code runs on the VM's
Ubuntu image, not on the dev box.

**Guard:** `scripts/hooks/block_recursive_delete.py` is a mechanical
backstop for the most dangerous instance of this (a recursive delete inside
the repo) at the tool-call level, regardless of shell.

#### 20. The venv is `worker\.venv` and nothing else

**Why:** MEASURED trap (`msys2-python-on-path` memory seed): the `python`
resolved by PATH on this dev machine is an MSYS2 build with no usable
wheels. Any workflow that implicitly falls through to PATH resolution
instead of the venv's own interpreter silently gets the wrong Python.

**Carve-out:** None.

**Guard:** `.claude/settings.json`'s allowlist only grants
`worker/.venv/Scripts/python.exe`-prefixed commands, not a bare `python`;
this nudges the habit but is not a hard block on typing `python` directly
outside a Claude Code session.

#### 21. Licences: MIT/Apache/BSD only in anything that ships

**Why:** A GPL/LGPL/AGPL dependency in a shipped binary creates a
copyleft obligation the project has not agreed to take on. Checking this
once, at the time a dependency is added, is far cheaper than discovering it
after a release has shipped.

**Carve-out:** None currently recorded — unlike the donor project's own
history (a dev-only MPL-2.0 carve-out), no such carve-out has been decided
here. If one is needed, it is a `DECISION`-labelled issue, not a silent
addition to this file.

**Guard:** `scripts/check_third_party_notices.py` (once it exists) is meant
to fail if `THIRD-PARTY-NOTICES.md` is stale against the real dependency
tree; a separate licence-allowlist check (flagging a GPL/LGPL/AGPL package
at add-time) is not yet built. Issue #34 tracks the generator and staleness
check.
