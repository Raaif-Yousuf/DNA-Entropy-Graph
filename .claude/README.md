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
| `memory/` | Per-session accumulated project memory (what broke, what was disproven, standing lessons), read from a **user-level, project-scoped** path — see "Installing memory" below. Empty until a session writes to it; `sync_memory.py` (once ported, see the tracker) mirrors it here. |

## Skills

| Skill | Use before |
| --- | --- |
| `tests-first` | Writing any feature, fix, or behaviour change |
| `wired-to-nothing` | Reporting any feature, fix, or wiring change as done |
| `working-an-issue` | Starting or closing any GitHub issue |
| `fixing-a-bug` (+ `bug-shapes.md`) | Writing any fix code for a bug, regression, or defect |
| `orchestrating-agents` | Dispatching subagents, writing a brief, or merging an agent's branch |
| `working-on-gcp` | Creating, inspecting, or debugging any Google Cloud resource (pending — see the open `docs:` issue for this skill; not yet ported) |
| `winui-dev` | Building, running, or debugging the WinUI 3 app (pending — no issue yet tracks authoring it; see the open gap issue) |

Only the first five exist today. The last two are named in
[`docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md`](../docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md)
§4 as part of the intended full set; check `gh issue list --search "skill"`
for their current status before assuming either is missing by accident.

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

`MEMORY.md`, once it exists, is the index loaded every session; other files
are pulled in on relevance. Start there.

## Memory and secrets

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
