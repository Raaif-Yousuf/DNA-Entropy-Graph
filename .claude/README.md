# `.claude/` — session config, skills and memory

This directory is checked into this **public** repo. It carries the things a
Claude Code session needs that do not live in the code itself, so a session
on a second machine (or a fresh agent on this one) starts with the same
knowledge as this one instead of re-deriving it. Because this repo is
public, nothing here may ever contain a secret, a real email address, a
project number, a token-shaped string, or a billing account id — see
"Memory and secrets" below.

## What is here

| Path | What it is |
| --- | --- |
| `settings.json` | Project-scoped Claude Code settings: the permissions allow/deny list and the `PreToolUse`/`SessionStart`/`Stop` hooks. Applies automatically to every session in this repo. |
| `skills/` | This repo's process-discipline skills, invoked with the `Skill` tool. See the table below. |
| `agents/` | Subagent definitions under `.claude/agents/*.md` (frontmatter `name`/`description`/`tools`/`model` plus a body). Currently: `cold-diff-reviewer.md`, a framing-free second-opinion reviewer for a diff. |
| `memory/` | 27 standing lessons (what broke, what was measured, what is still a carried-forward theory), one file per lesson, indexed by `memory/MEMORY.md`. Committed here so a fresh machine or a fresh agent starts with the same knowledge instead of re-deriving it — see "Installing memory" below for how a live session actually reads it. |

## Skills

| Skill | Use before |
| --- | --- |
| `tests-first` | Writing any feature, fix, or behaviour change |
| `wired-to-nothing` | Reporting any feature, fix, or wiring change as done |
| `working-an-issue` | Starting or closing any GitHub issue |
| `fixing-a-bug` (+ `bug-shapes.md`) | Writing any fix code for a bug, regression, or defect |
| `orchestrating-agents` | Dispatching subagents, writing a brief, or merging an agent's branch |
| `working-on-gcp` | Creating, inspecting, or debugging any Google Cloud resource, or before `scripts/cloud_gpu_test.ps1` runs |
| `winui-dev` | Building, running, testing or debugging the WinUI 3 app in `app/` |

All 7 exist. They are named in
[`docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md`](../docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md)
§4, which is the paste-ready source each was adapted from.

## Installing the memory on another device

Memory is read from a **user-level, project-scoped** path, not from the
repo, so cloning this repo is not enough. Copy it into place once:

```powershell
# Windows
$dst = "$env:USERPROFILE\.claude\projects\<project-slug>\memory"
New-Item -ItemType Directory -Force $dst
Copy-Item .claude\memory\* $dst -Force
```

```bash
# macOS / Linux
mkdir -p ~/.claude/projects/<project-slug>/memory
cp .claude/memory/* ~/.claude/projects/<project-slug>/memory/
```

**The directory name is derived from the repo path**, so if this repo is
cloned somewhere other than its usual location, the slug changes. Check what
your session actually uses (`claude config` or the path a session prints on
start) before copying, or the memory will sit there unread.

`MEMORY.md` is the index loaded every session; other files are pulled in on
relevance. Start there. Two of the 27 lessons are marked `(THEORY)` in the
index — a claim this session could not verify against a real current tool
version (Geneious's track-import support, igv.js building a genome directly
from GenBank) — check the file itself before trusting either as fact, and
replace the note with a `MEASURED <date>:` finding once checked (Hard Rule
18).

## Memory and secrets

> **The memory-sync hooks are switched off right now.** `hooks.SessionStart` and
> `hooks.Stop` in `settings.json` are empty arrays on purpose, pending the `DECISION`
> issue on memory sync (#302). The `--push` hook did two things on its first real runs
> that nobody intended: it published four of the owner's personal session memories into
> this public directory, and on every later run it overwrote `memory/MEMORY.md`, the
> hand-written index of this repo's own lesson files, with the unrelated session-memory
> index. The secret scan was working; credentials were never the exposure. Restore both
> hook entries (they are in git history at commit `8875b51`) once `sync_memory.py`
> filters on the memory type and leaves `MEMORY.md` alone.

This repo is **public**. Before any `sync_memory.py --push` hook writes a
memory file into this directory, it must run a secret-pattern scan and
refuse to push a file containing an email address, a Google Cloud project
number, a token-shaped string, or a billing account id — printing which
pattern matched rather than pushing silently. This is a hard requirement,
not a nice-to-have: a private-repo version of this idea can skip the scan
because it never needs one, and that assumption does not carry over to a
public repo.

## What is deliberately NOT here, and why

- **`~/.claude/.credentials.json`** — auth credentials. Never commit this.
- **`~/.claude/history.jsonl`** — raw session history. High leak risk (it
  records everything typed, including anything pasted) and no value to
  another machine.
- **`~/.claude/plugins/`** — installed third-party plugins. Re-installable,
  and would dwarf the repo.
- **`~/.claude/skills/` (user-level)** — generic, re-installable guidance not
  specific to this repo. The skills under `skills/` here ARE specific to
  this repo's stack and process, and are committed.
- `sessions/`, `shell-snapshots/`, `cache/`, `tasks/` — transient, per-run
  state with no standing value.

## Before you add anything else here

Being a public repo is precisely why nothing here may hold a secret. Anyone
adding a file under `.claude/` should scan it for the same patterns
`sync_memory.py`'s push-time scan checks for (email, project number,
token-shaped string, billing account id) before it is ever committed — do
not rely on the hook alone to catch what you added by hand outside the
sync path.
