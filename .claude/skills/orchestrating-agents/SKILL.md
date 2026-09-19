---
name: orchestrating-agents
description: Use when dispatching work to subagents, writing a brief, deciding how many to run, or merging an agent's branch. Also use when an agent reports done, goes quiet, or when choosing reuse vs spawn.
---

# Orchestrating agents

You are the orchestrator. Agents do the work; you decide direction, verify
claims, and own the merge (or, for a tree-level run, own the final commit).
Treat everything below as load-bearing, not boilerplate.

## Before you dispatch

**Check the commit graph first, for EVERY lane, in one batched call.**
`python scripts\issue_precheck.py 271 272 273 ...` takes a list. Running it
per-lane, one at a time, is exactly the shape that makes it feel skippable
when there are many lanes — it is one call, always run it before dispatching,
not just on the *next* wave's candidates.

**Prefer reuse and batching over a new spawn.** A fresh agent pays a cold
start re-deriving context you already hold. Send a follow-up via
`SendMessage` to a running agent, or hand one agent several related tasks in
priority order. Split by *shared context*, not by ticket count: two issues
touching the same module belong to one agent; two touching unrelated
surfaces do not.

**Give the agent what you already know**, with evidence: facts you have
checked, what is already ruled out and why, which theories are disproved
(Hard Rule 18). An agent that re-derives your findings has burned a cold
start for nothing.

**A skill must never be invoked merely because its name appears in the
user's text.** Match the actual task shape, not a substring.

**A read-only agent type's tool grant is a description, not an enforced
boundary**, unless this repo's own hooks narrow it. Do not rely on an agent
type's name alone for a safety property a hook does not actually check;
dispatch a nominally read-only agent for exploration only, never as a
substitute for a real permission boundary.

## Writing the brief

Every brief needs, at minimum:

| Must include | Why |
| --- | --- |
| The paths the agent owns, and the paths it must NOT touch | Concurrent agents corrupt each other's work outside their lane |
| The exact worktree path and branch (if using worktrees), or the shared-tree convention this run uses | It works nowhere else |
| "Run long steps in the FOREGROUND" | An agent that backgrounds a build and polls its own task stalls repeatedly |
| "Commit on your branch and stop" (or: leave changes uncommitted for the orchestrator, per this run's convention) | Say explicitly which — an agent that guesses wrong either loses work or corrupts another agent's |
| The Hard Rules that actually apply to its files, not all of them | Keeps the brief short enough to be read |
| The changelog fragment path (`docs/changelog.d/<branch-or-slug>.md`) and that it must start with `- ` | A heading makes the compiler silently skip the whole fragment |
| "Run only tests covering what you touched" | Concurrent full suites saturate the machine (Hard Rule 15: the dev laptop cannot run GPU tests at all) |
| The one wired-to-nothing observable for this lane's change | See Verifying, below |
| **The skills it must invoke, BY NAME, with the Skill tool** | See below — inlining the principles is not the same thing |
| **"Never a GPU test on the laptop; GPU tests only via `scripts/cloud_gpu_test.ps1`"** | Hard Rule 15 |
| **"Never create a cloud resource outside `scripts/cloud_gpu_test.ps1` unless the brief says so and names the budget"** | No cloud spend without an explicit, budgeted exception |
| **"Never close a GitHub issue; the orchestrator closes on verification"** | An agent closing its own unmerged or unverified fix reproduces the exact issue `working-an-issue` exists to prevent |
| **The git model, in one line** | Shared checkout: "never run any `git` command at all; the orchestrator holds every one". Worktree per agent: "never `git stash`, `git push --force`, or a recursive delete inside the repo". See "While they run"; do not mix the two wordings |
| **"Never a recursive delete inside the repo"** | Denied at the settings/hook level (see `.claude/settings.json`) but state it anyway, so the agent has the right instinct and not just the block |
| **The exact paths it owns, and the paths it must not touch** | The single check the shared-checkout model rests on. `scripts/agent_wave.ps1 -Start` refuses a wave whose assignments overlap |
| **Its own pytest `--basetemp`** | MEASURED 2026-09-19: concurrent runs race in the default Windows temp directory, and the teardown `PermissionError` reads as a real test failure |

**Name the skills. Do not paraphrase them.** Every brief that touches a bug,
a fix, or anything reported as done must tell the agent, in these words, to
invoke them with the `Skill` tool before writing code:

- `fixing-a-bug` — any bug, regression or defect, **before writing any fix
  code**; also when a fix "should work" but the symptom persists, or when
  adding a guard or ratchet.
- `wired-to-nothing` — **before reporting anything as done**; it carries the
  per-shape checklist (binding, command, DI registration, startup-script
  step, bucket rule, label, setting).
- `working-an-issue` — **before starting or closing any GitHub issue.**
- `tests-first` — before writing any feature, fix, or behaviour change.

A brief that paraphrases a skill's principles from memory drops whatever the
orchestrator did not happen to remember, and it drops silently — the brief
still looks complete. Name the skill; let the agent load the real thing.

**Pick the model per lane; do not default every lane to one model.**

| Lane shape | Model | Why |
| --- | --- | --- |
| Mechanical: run a harness, re-measure, fold changelogs, triage against the commit graph, port a file with a known adaptation list | `sonnet` (or the model configured as this run's fast default) | The work is procedure; a stronger model adds cost, not correctness |
| A real fix with a mechanism to find, a guard with a false-positive risk to measure, a corpus or fixture to design | a stronger reasoning model | Needs judgement about what NOT to do; the issue body can be wrong |
| Cross-surface diagnosis where every link "looks wired", a design decision delegated by the owner, a review whose framing must not be trusted | the strongest available / a framing-free reviewer | The scarce thing is not speed but not being fooled |

**Tell the agent the owner will not read its narration.** Sub-agent output
goes to the orchestrator only. Briefs should say, in these words: "Do not
narrate. Spend no tokens on progress prose, restating the brief, or
announcing what you are about to do. Report outcomes, numbers, file paths,
and the required end sections."

**Warn it when the issue body is probably wrong.** Issue bodies can carry a
wrong proposed fix. Say so, and say to verify the mechanism first
(`fixing-a-bug` step 6).

**Reserve strong markers for actual surprise.** A finding that changes what
the orchestrator believed going in earns emphasis; a clean, expected result
does not — using it for routine status is how the marker stops meaning
anything.

## While they run

**Agents stall waiting on their own background tasks.** When it happens:
check the process yourself, then message the agent with the *facts* ("the
build is finished, here is the size and mtime, stop polling and run the
remaining steps in the foreground"). Do not just say "continue".

**Decide which git model the wave runs under, and say it in the brief.**
There are two, and the recipes below differ entirely between them.
`scripts/agent_wave.ps1` implements both.

**Shared checkout (the default, and what `agent_wave.ps1 -Start` assumes).**
Every agent works in the one tree, on a disjoint set of owned paths. In this
model **agents run no `git` command at all** — not `add`, not `commit`, not
`checkout`, not even a read-only `status` or `diff`. The orchestrator holds
every git command and reads the tree on an agent's behalf. This is deliberately
stricter than banning the dangerous subcommands: "never touch git" is one
instinct an agent can hold, where "never touch git except for this named
recipe" is a rule with an exception, and the exception is what gets reached for
under pressure. Put it in the brief in those words.

The cost is that an agent cannot revert-check its own fix. That is the
orchestrator's job in this model: the agent reports which test should go red
without the fix, and the orchestrator does the reverting, or accepts a
test-first transcript as the evidence instead.

MEASURED 2026-09-19: a wave of three agents in one tree ran a whole night this
way with no collision. All three then died within seconds of each other on a
spend limit, mid-edit, and because no agent had ever run git, every partial
change was exactly where the path assignment said it would be.

**Worktree per agent** (`agent_wave.ps1 -Isolation Worktree`). Each agent has
its own tree and may use git inside it. Here the stash ban matters and needs a
replacement, because the stash stack is shared across every worktree of a
repository — they all share one `.git` — so two agents revert-checking in the
same window can have their working sets swap. Give the recipe, not just the
ban; a bare ban with no alternative loses to the reflex, because
revert-checking is something you actively ask for:

```
git diff > <scratchpad>/fix.patch      # keep the fix
git checkout -- <the source files>     # revert ONLY the source, keep the tests
<run the targeted tests; they must go RED>
git apply <scratchpad>/fix.patch       # restore
git diff --stat                        # prove the restore is byte-exact
```

`git checkout -b` also preserves uncommitted changes in place and is the way to
park work. `git stash` is not, and is blocked by a hook either way.

**Killing a process by IMAGE NAME is banned during a wave. Kill only PIDs
you started, and only by PID.** A cleanup command that matches every process
with a given name (`taskkill /IM python.exe`, `pkill python`) can kill the
owner's running app and every other agent's run in the same stroke. Record
the PID at spawn and kill that one.

**Tell an agent to STOP and report if its worktree or working tree has
changes it did not write.** That is the sign another agent (or another
session) is already touching the same files.

**Cap concurrency.** Ask, do not assume; the right number moves with what
else is running. Too many concurrent heavy processes (full test suites,
`dotnet build`, a GPU VM poll loop) saturates the box hard enough that `gh`
calls start failing with timeouts, and a starved worker looks exactly like a
real failure.

## Before you touch `main`: check for a second session

If this environment supports listing peer agent sessions, check for one at
the start of every orchestration session. A row that is interactive and busy
is very likely another session working for the same owner, and quite
possibly merging or committing right now.

The tell that you already collided: an unexplained "Already up to date" on a
branch you just verified was not an ancestor of `main`, or `HEAD` moving
without your own commit. Neither session is wrong to be there, and the
answer is not to race:

- **One session owns `main`** — every commit, the guard commands, the
  `docs/changelog.d/` folding.
- **The other owns dispatch and filing** — agents work in their own
  worktrees or lanes, each committing (or leaving uncommitted, per this
  run's convention) only its own scope, handed over as `branch @ sha` or a
  list of changed paths when it lands.
- **Say your concurrency number and honour theirs.** A starved gate flakes
  and retries, which looks exactly like a real red.
- **Name the files your branches will touch that the other session also
  touches** — a shared guard script or `.claude/settings.json` is the usual
  collision point.
- **Tell them about defects your agents found in what they are merging.** A
  session merging a feature deserves to know it will look broken on arrival
  for an unrelated reason.

Do not commit into a tree another session owns, not even a doc or a skill
edit: their working tree is not yours to dirty.

## Verifying what they report

**Check the claim in the diff, not the report.** An agent can report a doc
update it never made, or a changelog fragment written to the wrong
directory that a folding script never scans.

**Read the tests they wrote, not just the count.** A test can assert the
reported bug as correct behaviour and explain that in its own comment — the
count goes up, the bug stays.

**A raised baseline is a claim, not a fix.** If a ratchet or allowlist
moved, ask what was tried first. Documented and justified is fine; silent is
not.

**For a diff you are not sure about, dispatch a framing-free reviewer
instead of re-reading it yourself.** Give it only the diff and the file
paths — never the ticket, the author's reasoning, or your own theory of the
fix. Stripping that framing is mandatory, not optional: framing is exactly
what makes a reviewer agree with a broken diagnosis, and an orchestrator who
explains the fix first has already spent the independence this review exists
to buy. (`cold-diff-reviewer` under `.claude/agents/` is built for this.)

## Merging / landing the work

Land one lane at a time, and run the guard set after **each**:

```powershell
dotnet test app/tests/DnaEntropyGraph.Guards.Tests
worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu"
```

**Structural gates go red at merge time, not in the agent's worktree.**
Undocumented new modules, an unfolded changelog backlog, a line-count ceiling
(`AGENTS.md` under 20 lines) can all pass in isolation and fail once
combined. Fix by doing the work: document the module, fold the fragments.
Never raise a baseline just to make a gate pass.

**A commit message that says an issue is NOT fixed can still close it.**
GitHub's closing-keyword parser matches `fixed: #876` even inside "Not
fixed: #876 ... out of scope here" and ignores the leading "Not". Write
"does not fix #876", or leave the issue number out of that sentence
entirely.

**A decision you take on the owner's behalf must be marked as such.** A
comment closing an issue with a call the owner did not make, written as if
they had, is indistinguishable from a real owner reply once it is in the
history. Start such a comment with `DECISION (agent-made, reversible):`, and
reserve unmarked text for something the owner actually wrote.

## Red flags

- "The agent said it is done" (check the diff)
- "All its tests pass" (read what they assert)
- "I will merge/land everything then run the gates" (one lane at a time)
- "It has been quiet for a while" (it is probably waiting on its own task — check, then message with facts)
- "I will just do this bit myself" (you are the orchestrator; your context is the scarce resource)
