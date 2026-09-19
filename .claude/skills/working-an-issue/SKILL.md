---
name: working-an-issue
description: Use before starting OR closing any GitHub issue on Raaif-Yousuf/DNA-Entropy-Graph. Also use when an issue lacks a "Done when", when triaging, or when an issue is an epic rather than a task.
---

# Working an issue

GitHub Issues is the only tracker (Hard Rule 17). No `ROADMAP.md`,
`TODO.md`, or `known_issues.md` — the repository itself is the record of what
is known, what is open, and what already shipped. This skill exists so an
agent asks the repository what it already knows before rebuilding, and states
what "done" means before closing. Filing a new, project-tracked issue uses
`scripts/new_issue.ps1`, which renders this repo's canonical body shape
(Why / Done when / Observable / Docs touched / Tests / Out of scope / Cloud
money) directly; add `-Apply` to file it via `gh issue create` once the
rendered body looks right.

## 1. Before you start: ask the commit graph

```powershell
python scripts\issue_precheck.py 267 268 269
python scripts\issue_precheck.py --all-open --suspect-only
```

It decides from the **commit graph**, not from prose. Read its verdict and
every commit it names *before* reading the issue body — the body describes
what somebody wanted, the log describes what happened, and when they
disagree the log is right.

`SUSPECT` means "commits naming this issue changed source files while it
stayed open." Read the diff before writing a line of code.

**If the issue has a checklist, precheck the item, not just the issue
number.** A multi-item issue can get one verdict for the whole thing while a
single checklist item already shipped under a different commit subject.
Search the log for the item's own subject line.

**`REVIEWED` and `NOTE` are not "safe to start", they mean "go read the
thread."** Always run `gh issue view <n> --comments`, newest comment first.
The body is what somebody wanted when they filed it; the thread is what has
been learned since, and can supersede the body entirely.

## 2. Does the issue say what "done" means?

Most stale issues are not hard, they are **unclosable**: nobody can tell
whether they are finished, so they never get picked up and never get closed.
If the issue has no acceptance criteria, supplying them *is* the work — do
that first, in the issue body, and often you will find it is already
satisfied.

A criterion is usable only if it is **falsifiable**: a person runs one
command, or looks at one screen, and says yes or no.

| Not a criterion | A criterion |
| --- | --- |
| "Cloud errors are handled well" | `CloudErrorClassifier` has a recorded-payload test for every one of the 9 error classes, and each maps to a `.resw` key naming an action |
| "The worker is fast enough" | A 100 kb GenBank file completes `predict` in under N seconds on an L4, measured by `scripts/cloud_gpu_test.ps1`'s own timing output |
| "Fewer wired-to-nothing bugs" | `Guards.Tests/DiResolutionTests` resolves every registered service, and the count only ever goes up |
| "Better provenance" | Every point in the exported track links to the window and direction that produced it, and the seam position is in the output file |

For a **container / epic** issue: enumerate what it actually contains, check
each child against the code, then either point the epic at its sub-issues
(Hard Rule 17's epic structure: sub-issues, never a checklist in the body) or
rescope the remaining item down. A rescoped issue must come out **smaller and
sharper** than it went in.

An undecided epic is the failure state. "Declined, because —" is a success.

## 3. Before you close: name the observable

Closing records that the **code** is believed done. Never close on "the diff
looks right."

State three things in the closing comment, always:

1. **The commit sha(s)** that did it.
2. **The acceptance criteria** you are closing against (the "Done when" list).
3. **The one observable that would differ if the change were wired to
   nothing** — and go check it. See the `wired-to-nothing` skill for the
   per-shape checklist.

```powershell
gh issue close 267 --repo Raaif-Yousuf/DNA-Entropy-Graph --reason completed --comment "$(Get-Content body.md -Raw)"
```

`gh issue close` has no `--comment-file`. Write the body to a file and pass
`--comment "$(Get-Content <path> -Raw)"`, or `gh issue comment --body-file`
then close separately, rather than fighting shell quoting in a one-liner.

## 4. If the behaviour is unproven on a real build: add a ToTest row

Closing the issue and proving the behaviour are **different statements**, and
both are useful. `docs/ToTest.md` is Hard Rule 17's one carve-out for exactly
this — closed issues whose behaviour is unproven on a real installer build, a
real GPU VM, or a second Google account. Add a row with the issue number, the
closing commit, `Needs` (`app-dev`, `installer`, `gpu-vm`, `cpu-vm`,
`two-accounts`, `local-gpu`), the precise action, what passing looks like,
and what a *false* pass looks like.

The row is deleted when someone verifies it. If verification fails, reopen
the issue with the measured evidence. It is a queue, not a backlog: it must
drain (`scripts/check_totest_format.py` fails CI on a row older than 45
days).

## 5. Recording a cause

Hard Rule 18: a causal claim carries its evidence — `MEASURED <date>:` plus
the observation, or `THEORY (unverified):`. `docs/entry_points.md` §3 is the
disproven-diagnoses table; read it before re-deriving a cause, and when you
disprove a theory, **replace** it rather than appending a correction
elsewhere.

## Red flags

| Thought | What it actually means |
| --- | --- |
| "This looks like nobody started it" | Run `issue_precheck.py` first, always. |
| "The diff is obviously right, close it" | Closing on code evidence alone has shipped disproven fixes before. Name the observable. |
| "I'll write the criteria after I build it" | Then you cannot tell whether you are finished, and neither can anyone else. |
| "It's an epic, I'll just work top to bottom" | Check every child's state first; epics outlive their children. |
| "The tests pass, so it works" | No test reliably catches a wired-to-nothing bug. Name the observable. |
| "I'll close it and note the build check somewhere" | The somewhere is `docs/ToTest.md`, with the row format it already uses. |
