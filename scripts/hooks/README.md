# scripts/hooks/ — testing these by hand

Six hooks live here, all registered as `PreToolUse` hooks in `.claude/settings.json`:

- `block_git_stash.py` -- refuses a mutating `git stash` (the stash stack is shared
  across every worktree of this repo and every concurrent Claude session).
- `block_recursive_delete.py` -- refuses a recursive, forced delete aimed at a path
  inside this repository.
- `block_agent_dispatch_in_worktree.py` -- refuses `Agent` (sub-agent) dispatch from
  inside a linked git worktree; only the orchestrator, from the primary checkout,
  dispatches.
- `block_unlabelled_vm_create.py` -- refuses a Compute Engine `instances create` (or
  this repo's own `CloudCli vm create`) that is missing `--labels` or
  `--max-run-duration`, per `docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md`
  §4.

- `block_primary_checkout_git.py` -- refuses history-changing git (`checkout`/`switch`,
  `merge` other than `--ff-only`, `commit`, `reset --hard|--soft|--mixed|<commit>`, `rebase`,
  `cherry-pick`, `revert`, `am`, `pull` other than `--ff-only`) when the effective directory
  is the PRIMARY checkout (issue #542). Registered for both `Bash` and `PowerShell`. Recovery shapes (`merge|rebase|cherry-pick|revert|am --abort`,
  `--quit`) stay allowed; `am --skip` does not. On a git timeout it answers "unknown" and ALLOWS
  (fail open on purpose, #629): the git runs through `run_bounded()` (temp file, process tree killed by
  PID), so a stalled git can never push the hook past `run_hook.py`'s 8 s kill, which would deny every shell.
  Time budget (asserted in `scripts/tests/test_block_primary_checkout_git.py`): one bounded git costs at most its
  timeout plus a 1.5 s kill tail (`taskkill` wait 1.0 s + post-kill wait 0.5 s); the dispatch hook runs exactly one
  (`GIT_TIMEOUT` 4.0 s, 5.5 s worst), this hook's calls share a 5.0 s total budget (6.5 s worst); both keep a 1.0 s
  margin under the 8 s kill. A `#` glued to the `)` of `$( )` is part of the word, not a comment; `bash<<EOF`
  (no space) is a shell consumer.
- `require_premerge_before_pr.py` -- refuses `gh pr create` and `gh pr merge` unless
  `scripts/premerge.py` passed (green, unfiltered) on the CURRENT HEAD, recorded by
  `scripts/premerge_stamp.py` in `<git-dir>/premerge-stamp.json` (12 hour expiry). Escape hatch, logged to
  `<git-dir>/premerge-bypass.log`: `DEG_SKIP_PREMERGE=<reason> gh pr create ...` or the same variable in the
  environment. Register it in `.claude/settings.json` exactly like the others, with matcher `Bash` and
  `run_hook.py require_premerge_before_pr`.

All six read a `PreToolUse` JSON payload on stdin and answer with silence (exit 0,
no stdout) or a deny decision. They never block on their own failure: a malformed
payload or an unexpected exception exits 0 quietly, because a hook that breaks the
session when IT has a bug is worse than the bug it guards. `scripts/tests/` holds the
real, repeatable checks; this file is only for a HUMAN probing one of them by hand
from a shell.

## This repo's worktree convention

`block_agent_dispatch_in_worktree.py` does not pattern-match a path at all (see its
own module docstring): it asks git whether `--git-dir` and `--git-common-dir` differ
for the session's `cwd`, which is true for any linked worktree regardless of where it
lives. The examples below use `<repo-root>\DNA-Entropy-Graph-wt\<branch>` as this
repo's own convention for where `git worktree add` puts a linked worktree (a sibling
of the primary checkout, one directory per branch, mirroring the
`superpowers:using-git-worktrees` skill's default), but any worktree location is
detected correctly.

## The Git Bash backslash trap

**Never hand a Windows path to one of these hooks as an inline backslash
string through Git Bash.** Use forward slashes, or write the JSON to a file
first.

This command, run from Git Bash to verify `block_agent_dispatch_in_worktree.py`
denies dispatch from a real linked worktree --

```
echo '{"tool_name":"Agent","tool_input":{},"cwd":"C:\\Users\\you\\DNA-Entropy-Graph-wt\\271-scripts"}' | python scripts/hooks/block_agent_dispatch_in_worktree.py
```

-- can produce **no output and exit 0**, which reads as "the hook does not
fire": a dead guard. The backslashes may not survive the shell, so the hook
receives a `cwd` that is not a real path; `_git_paths()` then fails exactly as
designed and the hook fails OPEN (allowed) -- correct behaviour for "cannot
tell", but indistinguishable from a genuine allow without more information. A
hook that breaks the orchestrator over its own parsing bug would be worse than
the bug it guards, so failing open here is correct; the trap is only in how
you **verify** it by hand.

Prefer this shape instead:

```
printf '{"tool_name":"Agent","tool_input":{},"cwd":"C:/Users/you/DNA-Entropy-Graph-wt/271-scripts"}' > .scratch/probe.json
python scripts/hooks/block_agent_dispatch_in_worktree.py < .scratch/probe.json
```

Forward slashes resolve fine on Windows through `git -C <path>`, and a file
sidesteps whatever quoting layer mangled the inline string.

## Telling "denied" apart from "could not tell"

`block_agent_dispatch_in_worktree.py --explain <cwd>` prints which of three
branches a `cwd` takes -- `PRIMARY CHECKOUT`, `LINKED WORKTREE`, or `UNKNOWN`
-- instead of the PreToolUse contract's silent exit 0 either way. Reach for
this whenever a manual probe of that hook comes back with no output and you
are not sure whether that means "allowed, this is the primary checkout" or
"the path could not be resolved at all". It never affects the real hook
decision; it is read-only.

`block_git_stash.py` and `block_recursive_delete.py` have no equivalent flag:
their decision space is a single boolean (deny/allow) with no third "could not
tell" state to distinguish, so there is nothing an `--explain` mode would add.

## The moved-cwd trap and the identity signal (#618)

MEASURED 2026-10-03 (twice, lane orchestrators): an orchestrator launched in the primary
checkout dispatches a Sonnet with `isolation: "worktree"`; when it completes, the harness
moves the orchestrator's session cwd into that Sonnet's worktree
(`.claude/worktrees/agent-<sonnetAgentId>`, id identical to the Sonnet's agent id). A hook
that only asked "is cwd a linked worktree" then denied every further `Agent` call. A
`Set-Location` or `cd` does not change the cwd the hook sees; it cleared by itself minutes
later.

`block_agent_dispatch_in_worktree.py` therefore decides on WHO is calling, not only where.
In a linked worktree it allows a caller only on positive proof: the caller's id is known
AND it owns no `agent-<id>` worktree anywhere in the repo (a listing of
`<git-common-dir>/worktrees/`; a name minus `agent-` that equals, starts with or is a
prefix of the id, case-insensitive, so a truncated, collision-suffixed or re-cased admin
name still counts as owned). A Sonnet always owns `agent-<its id>`, so it is denied
whichever worktree its cwd is in. Caller id = payload `agent_id`, else the `agent-<id>.jsonl`
basename of `transcript_path`. Unknown identity is denied (fail closed): a top-level session
(no `agent_id`, main-session transcript) whose cwd moves is still blocked until the log
below shows its shape.

A fork or subagent that shares a Sonnet's cwd has its own id and owns no worktree, so the
rule would allow it. That fork can only exist if the Sonnet dispatched it, and the Sonnet's
own dispatch is denied, so the route is closed at its source (tested).

- MEASURED 2026-10-03: the Claude Code hooks reference lists `agent_id` and `agent_type` as
  common input fields "present only inside subagents", next to `session_id`,
  `transcript_path`, `cwd`. The session transcript's `hook_success` records keep the hook's
  stdout only, never its stdin, so no real payload could be recovered from disk.
- THEORY (unverified): an orchestrator that is itself a subagent receives its own
  `agent_id` (different from the Sonnet's). If its payload carries no agent field it falls
  through to "unknown" and stays denied (today's behaviour, no regression).

Payload-shape log (always on): every decision appends one JSON line to
`%TEMP%\deg-hooks\agent_dispatch_payload_shapes.jsonl` (rotated to `.1` past 256 KB; a
failed write never affects the decision). Fields: `keys` (payload key names), `agent_id`,
`agent_type`, `transcript_basename`, `cwd_basename`, `verdict` (`allow`/`deny`). Never
`tool_input`. To read it after a moved-cwd occurrence: open the last lines and look at the
deny line whose `cwd_basename` is `agent-<someone else's id>`; if `agent_id` is null and
`transcript_basename` is a bare session uuid, the orchestrator has no agent identity and a
different signal is needed (file a follow-up). `DEG_HOOK_PAYLOAD_SHAPE_LOG` redirects the
file (tests only).

## Probing `block_unlabelled_vm_create.py`

```
printf '{"tool_name":"Bash","tool_input":{"command":"gcloud compute instances create deg-job-1 --zone us-central1-a"}}' > .scratch/probe.json
python scripts/hooks/block_unlabelled_vm_create.py < .scratch/probe.json
```

should deny (no `--labels`, no `--max-run-duration`). Adding both flags to the
command should produce silence and exit 0. Note `gcloud` itself is denied
separately by `.claude/settings.json`'s own permission deny-list; this hook
exists for the day a `CloudCli vm create` (or an app-side wrapper) reaches the
Compute API without going through `gcloud` at all, and to catch a `gcloud`
invocation typed directly into a scratch/debug session before that deny-list
check runs.

## Probing `block_primary_checkout_git.py`

Primary vs linked worktree is decided by git (`--git-dir` vs `--git-common-dir`, via
`classify()` imported from `block_agent_dispatch_in_worktree.py`), never by a path. The
effective directory is the session `cwd`, moved by a `cd`/`Set-Location`/`Push-Location`
prefix in the same command and by `git -C <path>`. Backslashes are taken literally, so an
unquoted `C:\...` path survives (the trap above). Unresolvable means allow.

```
printf '{"tool_name":"Bash","tool_input":{"command":"git switch -c x origin/main"},"cwd":"C:/path/to/a/primary/checkout"}' > .scratch/probe.json
python scripts/hooks/block_primary_checkout_git.py < .scratch/probe.json
```

denies; the same file with `git merge --ff-only origin/main` is silent, and so is any
command with a linked worktree as `cwd`. Point `cwd` at a throwaway repo, not at this
repo's real primary checkout. `python scripts/hooks/block_primary_checkout_git.py --self-test`
builds its own throwaway repo and worktree and covers every denied and allowed shape in both,
for both tool names. `git stash` is not handled here (`block_git_stash.py` owns it).
