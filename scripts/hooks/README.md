# scripts/hooks/ — testing these by hand

Four hooks live here, all registered as `PreToolUse` hooks in `.claude/settings.json`:

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

All four read a `PreToolUse` JSON payload on stdin and answer with silence (exit 0,
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
