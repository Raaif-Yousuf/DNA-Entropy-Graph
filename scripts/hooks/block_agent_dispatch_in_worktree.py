"""PreToolUse hook: refuse `Agent` (sub-agent) dispatch from inside a worktree.

WHY THIS EXISTS AS CODE AND NOT AS A SENTENCE
---------------------------------------------
The `orchestrating-agents` skill (`.claude/skills/orchestrating-agents/SKILL.md`)
already says, in plain words: a dispatched agent must not dispatch its own
sub-agents. No `Agent`, no `fork`, not even for "read-only research". The
orchestrator dispatches; you execute.

That ban is unenforceable as prose alone, for a structural reason: a `fork` is
not isolated by default. It shares the parent's cwd, `.git`, and git identity,
so an instruction to it like "read-only" or "do not commit" is a suggestion
the fork's own model turn evaluates and can override, never a constraint the
caller can enforce from outside. A `fork` sharing its parent's worktree can
commit to the parent's branch under attribution indistinguishable from the
parent agent's own, or leave a half-finished edit in a worktree its parent is
concurrently editing. Neither failure needs malice; both need only one agent
reaching for `Agent`/`fork` as the obvious next tool while blocked on a slow
step. This hook is the enforcement, matched to
`scripts/hooks/block_git_stash.py`'s shape: a rule that depends on every
dispatched agent reading and honouring its own brief every time is not a
documentation problem waiting to be worded better, it is a control waiting to
be built.

WHAT SIGNAL "INSIDE A WORKTREE" USES, AND WHY
----------------------------------------------
`git rev-parse --git-dir` vs. `git rev-parse --git-common-dir`, both resolved to
absolute paths, run against the session's `cwd`. In the PRIMARY checkout the two
are the same path (there is exactly one `.git`, and it IS the common dir). In
every LINKED worktree (`git worktree add ...`) they differ: `--git-dir` points
at `<main-repo>/.git/worktrees/<name>` (this worktree's own per-worktree admin
area: HEAD, index, ORIG_HEAD) while `--git-common-dir` points at the shared
`<main-repo>/.git` that owns the objects, refs and config every worktree shares.
That divergence is git's own worktree bookkeeping, not a string this hook
maintains, so it is not spoofable by accident: a worktree cannot be renamed,
moved to a different parent directory, or nested somewhere unexpected without
this signal still telling it apart from the primary checkout correctly,
because it is asking git what git already knows about itself rather than
pattern-matching a path. The rejected alternative was "is `cwd` under this
repo's worktree root": that requires the worktree root to be known and fixed
in advance, breaks the moment a worktree lives somewhere else, and is exactly
the kind of assumption that should be verified against git itself rather than
trusted. See `scripts/hooks/README.md` for this repo's own worktree-directory
convention (used only for illustration in this file's tests and examples,
never by the detection logic above).

THE (a) vs (b) CALL: BLOCK EVERY `Agent` CALL, NOT JUST `fork`
----------------------------------------------------------------
The obvious offender is `fork`, which invites a narrower hook: block `fork`
(and whatever else "inherits the cwd"), allow read-only search types like
`Explore`. That narrower rule depends on a claim -- that worktree-sharing is a
property of `subagent_type` -- which this repo's own `Agent` tool schema
DISPROVES: isolation is a call-level parameter (`isolation: "worktree"` or
`"remote"`), independent of `subagent_type`, and it defaults to sharing the
caller's cwd/`.git` for every type, `fork` included. `Explore` is described as
read-only but is granted the `Bash` tool with no narrower permission set, so an
`Explore` dispatched without `isolation` can run `git commit` from the shared
worktree exactly as `fork` could -- the description "read-only search agent"
is a convention its own tool grant does not enforce. Because that claim could
not be verified as true, and disproving it was not optional, this hook takes
the strict reading: block `Agent` dispatch outright from inside any worktree,
regardless of `subagent_type` or `isolation`. A dispatched agent that needs
more hands says so plainly in its report; only the orchestrator, from the
primary checkout, dispatches.

WHO IS CALLING (issue #618)
---------------------------
The harness can move an orchestrator's cwd into the `agent-<id>` worktree of a
Sonnet it dispatched. Inside a linked worktree the hook allows a caller only on
positive proof: its agent id (payload `agent_id`, else the `agent-<id>.jsonl`
transcript basename) is known AND it owns no `agent-<id>` worktree in this repo
(directory listing of `<git-common-dir>/worktrees`, tolerant of truncated,
suffixed and re-cased names). A Sonnet always owns its own, so it is denied
wherever its cwd is. An unknown caller is denied. A fork sharing a Sonnet's cwd
has its own id and would be allowed, but it can only exist if the Sonnet
dispatched, which is denied, so that route is closed at its source. Every
decision is appended (key names only) to `<temp dir>/deg-hooks/agent_dispatch_payload_shapes.jsonl`, capped at 256 KB. See
`scripts/hooks/README.md`.

WHAT IT DOES NOT BLOCK
-----------------------
An `Agent` call made from the PRIMARY checkout is untouched: the orchestrator
must be able to dispatch constantly, and a hook that stopped that would be
worse than the bug it guards (this mirrors the git-stash hook's own priority:
never break the session over a hook's own caution). Nothing here inspects or
restricts `Bash`/`PowerShell`/`Edit`/etc. -- those already have their own gates.

CONTRACT
--------
Reads the PreToolUse payload on stdin, writes a JSON decision on stdout.
Silence plus exit 0 means "no opinion". It never blocks on its own failure: a
malformed payload, a missing `git`, or any unexpected exception exits 0
quietly -- a hook that breaks the orchestrator's own dispatching because IT has
a bug is worse than the sub-agent it is trying to catch.

    python scripts/hooks/block_agent_dispatch_in_worktree.py --self-test

builds a real throwaway git repo with a real linked worktree (never this
repo's own working tree) and proves classify()/verdict()/explain() across
primary checkout, linked worktree and unresolvable-cwd, plus the fails-open
contract above, with no pytest on PATH (issue #310); exits 0 on pass, 1 on a
self-test failure.
"""

from __future__ import annotations

import contextlib
import json
import os
import subprocess
import sys
import tempfile

MESSAGE = """`Agent` dispatch is blocked from inside a worktree.

The `orchestrating-agents` skill: a dispatched agent must not dispatch its own
sub-agents, not even a `fork`, not even for "read-only research". A `fork` is
NOT isolated -- it shares this worktree and this git identity, so an
instruction you give it (read-only, do not commit) is unenforceable. A fork
sharing its parent's worktree can commit to the parent's branch under
attribution indistinguishable from the parent's own, or leave a live
wired-to-nothing feature mid-edit in its parent's own worktree.

Do the work yourself, in this worktree. If the task genuinely needs more
hands than one agent, say so plainly in your report and stop -- that is the
orchestrator's call to make from the primary checkout, not yours to make by
spawning something invisible to it.

(If you are the orchestrator and believe you are seeing this in error: this
hook only fires when `cwd` resolves to a linked git worktree, never the
primary checkout. Dispatch from the primary checkout instead.)"""


# TIME BUDGET. run_hook.py kills a guard at 8 s and a kill DENIES the tool call, so the
# worst case of everything a hook does must fit with margin. One bounded git costs at most
# `timeout` + KILL_TAIL_SECONDS (the taskkill wait plus the post-kill wait below).
DISPATCHER_BUDGET_SECONDS = 8.0  # mirrors run_hook.DEFAULT_GUARD_TIMEOUT_SECONDS (tested)
TASKKILL_TIMEOUT_SECONDS = 1.0
POST_KILL_WAIT_SECONDS = 0.5
KILL_TAIL_SECONDS = TASKKILL_TIMEOUT_SECONDS + POST_KILL_WAIT_SECONDS
GIT_TIMEOUT = 4.0  # the one git this hook's verdict() runs


def worst_case_seconds(timeout: float) -> float:
    """Longest one `run_bounded(argv, timeout)` can take, kill tail included."""
    return timeout + KILL_TAIL_SECONDS


def _kill_tree(proc: subprocess.Popen) -> None:
    """Kill `proc` and everything it started, by the PID WE started (never by image name)."""
    try:
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/T", "/F", "/PID", str(proc.pid)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=TASKKILL_TIMEOUT_SECONDS,
            )
        else:
            import signal

            os.killpg(proc.pid, signal.SIGKILL)
    except Exception:
        pass
    with contextlib.suppress(Exception):
        proc.kill()


def run_bounded(argv: list[str], timeout: float, _wait=None) -> tuple[int, str] | None:
    """Run `argv`; return (returncode, stdout), or None on timeout / failure to start.

    WHY NOT `subprocess.run(capture_output=True, timeout=...)`: MEASURED (see
    docs/changelog.d/fix-629-git-guard-followups.md) that on Windows the timeout
    kills only the direct child (the git.exe launcher). The real git is its
    child and still holds the stdout/stderr PIPES, so the follow-up `communicate()`
    blocks until that grandchild exits: a stalled git (an unreachable UNC path)
    pushes the hook past run_hook.py's 8 s kill, which DENIES every Bash/PowerShell
    call. So: no pipes (stdout goes to a temp file, stderr to nowhere), `wait()`
    only waits for the direct child, and on timeout the whole process tree is
    killed by PID.

    `_wait(proc, timeout)` is a test seam for the waiting step (default `proc.wait`), so a
    test can trigger the timeout on a signal instead of on the clock.

    A timeout answers None, which every caller treats as "unknown" and ALLOWS.
    Deliberately fail-open: this guard exists to prevent accidents, and a hook
    that stalls every shell is a worse outcome than one missed block.
    """
    import tempfile

    try:
        with tempfile.TemporaryFile() as out:
            proc = subprocess.Popen(
                argv,
                stdin=subprocess.DEVNULL,
                stdout=out,
                stderr=subprocess.DEVNULL,
                start_new_session=os.name != "nt",
            )
            try:
                (_wait or (lambda p, t: p.wait(timeout=t)))(proc, timeout)
            except subprocess.TimeoutExpired:
                _kill_tree(proc)
                with contextlib.suppress(Exception):
                    proc.wait(timeout=POST_KILL_WAIT_SECONDS)
                return None
            out.seek(0)
            return proc.returncode, out.read().decode("utf-8", errors="replace")
    except Exception:
        return None


def _git_paths(cwd: str, timeout: float = GIT_TIMEOUT) -> tuple[str, str] | None:
    """Return (git_dir, git_common_dir), both absolute, or None if unavailable.

    None covers: no git on PATH, `cwd` not inside any git repository, or any
    other failure -- all of which this hook treats as "cannot tell", and it
    fails OPEN (does not block) rather than guess, per the contract above.
    """
    argv = ["git", "-C", cwd, "rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"]
    result = run_bounded(argv, timeout)
    if result is None:
        return None
    returncode, stdout = result
    if returncode != 0:
        return None
    lines = stdout.splitlines()
    if len(lines) != 2:
        return None
    git_dir, git_common_dir = lines[0].strip(), lines[1].strip()
    if not git_dir or not git_common_dir:
        return None
    return git_dir, git_common_dir


def classify(cwd: str, timeout: float = GIT_TIMEOUT) -> str:
    """One of "primary", "linked_worktree", or "unknown" -- the three-way
    answer this hook needs to be able to SHOW, not just act on.

    `is_linked_worktree()` below collapses "unknown" (git unavailable, `cwd`
    not inside a repo, a malformed `rev-parse` result -- ANY reason
    `_git_paths` returns None) into `False`,
    same as a genuine primary-checkout allow. That collapse is the right
    hook DECISION (doubt means allow, never block the orchestrator on a
    guess) but it means "the hook correctly determined this is the primary
    checkout" and "the hook could not determine anything at all" are the
    exact same silent exit 0 with no output. A Git Bash quoting slip that
    mangled a Windows path (backslashes did not survive the shell) produced
    the second case while reading, to a human watching stdout, exactly like
    the first -- a dead guard would look identical. `explain()` below
    reports this three-way split directly so that ambiguity is never silent
    again; the hook's own PreToolUse decision (`verdict`/`is_linked_worktree`)
    is unchanged.
    """
    return _classify_paths(_git_paths(cwd, timeout))


def _classify_paths(paths: tuple[str, str] | None) -> str:
    if paths is None:
        return "unknown"
    git_dir, git_common_dir = paths

    def norm(p: str) -> str:
        return os.path.normcase(os.path.normpath(p))

    return "linked_worktree" if norm(git_dir) != norm(git_common_dir) else "primary"


def is_linked_worktree(cwd: str) -> bool:
    """True only when `cwd` resolves to a linked worktree, never on doubt.

    The primary checkout has `--git-dir == --git-common-dir` (there is exactly
    one `.git`, and it owns itself). A linked worktree's `--git-dir` is its own
    `.git/worktrees/<name>` admin area, distinct from the shared
    `--git-common-dir`. Any failure to determine this (no git, no repo, a
    malformed result) returns False: doubt means allow, matching this hook's
    "never block the orchestrator on a guess" contract. Delegates to
    `classify()` so the hook's real decision and its `--explain` diagnostic
    can never disagree about which branch a given `cwd` takes.
    """
    return classify(cwd) == "linked_worktree"


def explain(cwd: str) -> str:
    """Diagnostic text for `--explain <cwd>`: which of the three
    `classify()` branches this `cwd` takes, spelled out in one line so a
    shell-quoting slip reads as "could not determine" instead of silently
    looking exactly like a real allow. Never used by the real PreToolUse
    path -- a pure read-only affordance for testing the hook by hand."""
    paths = _git_paths(cwd)  # ONE git call: classify() would run a second one
    result = _classify_paths(paths)
    if result == "unknown" or paths is None:  # paths is None exactly when result is unknown
        return (
            f"UNKNOWN for cwd={cwd!r}: could not resolve --git-dir/--git-common-dir "
            "(no git on PATH, cwd not inside any git repository, or a malformed "
            "`git rev-parse` result -- often a shell that mangled the path before "
            "git ever saw it). Fails OPEN: Agent dispatch is ALLOWED, and this is "
            "an ambiguous case that would otherwise be indistinguishable from a "
            "genuine primary-checkout allow."
        )
    git_dir, git_common_dir = paths
    if result == "primary":
        return f"PRIMARY CHECKOUT for cwd={cwd!r}: --git-dir == --git-common-dir == {git_common_dir!r}. Agent dispatch ALLOWED."
    return f"LINKED WORKTREE for cwd={cwd!r}: --git-dir={git_dir!r} != --git-common-dir={git_common_dir!r}. Agent dispatch DENIED."


def caller_agent_id(payload: dict) -> str | None:
    """The calling agent's id, or None when it cannot be determined.

    `agent_id` is documented as present only when the hook fires inside a subagent
    (code.claude.com/docs/en/hooks, "Fields Present Only Inside Subagents").
    Fallback: a `transcript_path` whose basename is `agent-<id>.jsonl` (subagent
    transcripts live at `<session>/subagents/agent-<id>.jsonl`). A main-session
    transcript (`<session-uuid>.jsonl`) names no agent, so it yields None.
    """
    raw = payload.get("agent_id")
    if isinstance(raw, str) and raw.strip():
        return raw.strip().removeprefix("agent-")
    tp = payload.get("transcript_path")
    if isinstance(tp, str):
        name = tp.replace("\\", "/").rsplit("/", 1)[-1]
        if name.startswith("agent-") and name.endswith(".jsonl"):
            return name[len("agent-") : -len(".jsonl")] or None
    return None


def _owns_agent_worktree(caller_id: str, common_dir: str) -> bool:
    """True when `<common_dir>/worktrees/` holds an admin entry that belongs to
    `caller_id`: the name minus `agent-` equals, starts with, or is a prefix of the id,
    case-insensitively. Tolerates naming drift (truncation, git's collision suffix,
    case). A directory listing only, no git call. An unreadable listing counts as
    "owns one" (fail closed)."""
    cid = caller_id.lower().removeprefix("agent-")
    try:
        names = os.listdir(os.path.join(common_dir, "worktrees"))
    except OSError:
        return True
    for name in names:
        n = name.lower().removeprefix("agent-")
        if n and cid and (n == cid or n.startswith(cid) or cid.startswith(n)):
            return True
    return False


def verdict(cwd: str, caller_id: str | None = None) -> str | None:
    """Return the reason to deny, or None to stay silent.

    Issue #618: the harness can move an ORCHESTRATOR's cwd into the linked worktree of
    a Sonnet it dispatched. In a linked worktree, allow ONLY on positive proof that the
    caller is not a worktree agent: its id is known AND it owns no `agent-<id>` worktree
    anywhere in this repo. A Sonnet always owns `agent-<its id>`, so it is denied
    whichever worktree its cwd is in, even if the admin name drifted. Unknown identity
    is denied (fail closed).

    A fork or subagent sharing a Sonnet's cwd has its own id and owns no worktree, so
    it would be allowed; it can only exist if the Sonnet dispatched, which is denied
    here, so that route is closed at its source.
    """
    paths = _git_paths(cwd, GIT_TIMEOUT)
    if paths is None or _classify_paths(paths) != "linked_worktree":
        return None
    if caller_id and not _owns_agent_worktree(caller_id, paths[1]):
        return None
    return MESSAGE


# ---------------------------------------------------------------------------
# Self-test: builds a REAL throwaway git repo with a REAL linked worktree
# (tempfile.TemporaryDirectory, never this repo's own working tree, matching
# scripts/tests/test_hooks.py's own git_repo_with_worktree fixture), proves
# every classify()/verdict()/explain() branch plus the fails-open contract,
# runnable with no pytest on PATH (issue #310).
# ---------------------------------------------------------------------------


def _make_git_repo_with_worktree(tmp_dir):
    """Mirrors scripts/tests/test_hooks.py's git_repo_with_worktree fixture,
    without pytest: one primary checkout with one commit, one linked
    worktree off a new branch. Returns (primary_path, worktree_path) as
    pathlib.Path. Raises on any git failure -- self_test() below treats
    that as a self-test failure, not a silent skip."""
    import pathlib
    import subprocess

    def run(*args, cwd):
        subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)

    tmp = pathlib.Path(tmp_dir)
    primary = tmp / "primary"
    primary.mkdir()
    run("init", "-q", cwd=primary)
    run("config", "user.email", "test@example.com", cwd=primary)
    run("config", "user.name", "Test", cwd=primary)
    (primary / "README.md").write_text("hello\n", encoding="utf-8", newline="\n")
    run("add", "README.md", cwd=primary)
    run("commit", "-q", "-m", "initial", cwd=primary)

    worktree = tmp / "primary-wt" / "branch-a"
    worktree.parent.mkdir(parents=True, exist_ok=True)
    run("worktree", "add", "-q", "-b", "branch-a", str(worktree), cwd=primary)

    return primary, worktree


def _run_end_to_end(payload: dict):
    import json as _json
    import subprocess

    proc = subprocess.run(
        [sys.executable, __file__],
        input=_json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=15,
    )
    out = None
    if proc.stdout.strip():
        out = _json.loads(proc.stdout)
    return proc.returncode, out


def _is_deny(decision) -> bool:
    if decision is None:
        return False
    return decision.get("hookSpecificOutput", {}).get("permissionDecision") == "deny"


def self_test() -> int:
    """classify()/verdict()/explain() against a real primary checkout, a
    real linked worktree and an unresolvable cwd, plus the fails-open
    contract. A guard that has only ever been run on a clean tree is not a
    guard; it is a line that has never said no."""
    import pathlib
    import subprocess
    import tempfile

    failures = 0
    os.environ["DEG_HOOK_PAYLOAD_SHAPE_LOG"] = os.path.join(tempfile.gettempdir(), "deg-selftest-shapes.jsonl")

    def check(label: str, condition: bool) -> None:
        nonlocal failures
        if condition:
            print(f"ok    {label}")
        else:
            print(f"FAIL  {label}", file=sys.stderr)
            failures += 1

    try:
        with tempfile.TemporaryDirectory() as tmp:
            primary, worktree = _make_git_repo_with_worktree(tmp)

            check("primary checkout classifies as primary and is allowed", classify(str(primary)) == "primary" and verdict(str(primary)) is None)
            check(
                "linked worktree classifies as linked_worktree and is denied",
                classify(str(worktree)) == "linked_worktree" and verdict(str(worktree)) is not None,
            )
            check("--explain names PRIMARY CHECKOUT for the primary checkout", "PRIMARY CHECKOUT" in explain(str(primary)))
            check("--explain names LINKED WORKTREE for the linked worktree", "LINKED WORKTREE" in explain(str(worktree)))

            not_a_repo = pathlib.Path(tmp) / "not-a-repo"
            not_a_repo.mkdir()
            check(
                "an unresolvable cwd classifies as unknown and fails open (allowed)",
                classify(str(not_a_repo)) == "unknown" and verdict(str(not_a_repo)) is None,
            )
            check("--explain names UNKNOWN for an unresolvable cwd", "UNKNOWN" in explain(str(not_a_repo)))

            code, decision = _run_end_to_end({"tool_name": "Agent", "tool_input": {}, "cwd": str(worktree)})
            check("end-to-end: a real Agent-call payload from the worktree denies", code == 0 and _is_deny(decision))

            code, decision = _run_end_to_end({"tool_name": "Agent", "tool_input": {}, "cwd": str(primary)})
            check("end-to-end: the same payload from the primary checkout is silent (allowed)", code == 0 and decision is None)

            # Issue #618: identity decides inside an `agent-<id>` worktree.
            agent_wt = pathlib.Path(tmp) / "primary-wt" / "agent-aaa111"
            subprocess.run(
                ["git", "worktree", "add", "-q", "-b", "agent-b", str(agent_wt)], cwd=primary, check=True, capture_output=True, text=True
            )
            base = {"tool_name": "Agent", "tool_input": {}, "cwd": str(agent_wt)}
            code, decision = _run_end_to_end({**base, "agent_id": "bbb222"})
            check("#618: an orchestrator moved into another agent's worktree is allowed", code == 0 and decision is None)
            code, decision = _run_end_to_end({**base, "agent_id": "aaa111"})
            check("#618: the agent running in its own worktree is denied", code == 0 and _is_deny(decision))
            code, decision = _run_end_to_end(base)
            check("#618: unknown identity in a linked worktree is denied", code == 0 and _is_deny(decision))
            code, decision = _run_end_to_end({**base, "transcript_path": "/s/subagents/agent-bbb222.jsonl"})
            check("#618: identity from the transcript_path basename is honoured", code == 0 and decision is None)
    except Exception as exc:
        check(f"building the throwaway git repo/worktree did not raise ({exc!r})", False)

    proc = subprocess.run(
        [sys.executable, __file__],
        input="not json at all",
        capture_output=True,
        text=True,
        timeout=15,
    )
    check("malformed stdin fails open (exit 0, silent stdout)", proc.returncode == 0 and proc.stdout.strip() == "")

    if failures:
        print(f"\nFAIL: block_agent_dispatch_in_worktree self-test ({failures} failure(s))", file=sys.stderr)
        return 1
    print("\nPASS: block_agent_dispatch_in_worktree self-test")
    return 0


SHAPE_LOG_CAP = 256 * 1024


def shape_log_path() -> str:
    """Fixed, always-on path (issue #618 measurement); the env var only redirects tests."""
    override = os.environ.get("DEG_HOOK_PAYLOAD_SHAPE_LOG")
    if override:
        return override
    return os.path.join(tempfile.gettempdir(), "deg-hooks", "agent_dispatch_payload_shapes.jsonl")


def _log_payload_shape(payload: dict, verdict_name: str) -> None:
    """Append one JSON line: payload key names, agent fields, transcript_path basename,
    cwd basename and the verdict. Never tool_input. Past the cap the file is rotated
    to `<name>.1` (one generation). Best effort: never raises, never fails the hook."""
    try:
        path = shape_log_path()
        cap = int(os.environ.get("DEG_HOOK_PAYLOAD_SHAPE_LOG_CAP", SHAPE_LOG_CAP))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        if os.path.isfile(path) and os.path.getsize(path) > cap:
            os.replace(path, path + ".1")
        tp = payload.get("transcript_path")
        line = {
            "keys": sorted(payload),
            "agent_id": payload.get("agent_id"),
            "agent_type": payload.get("agent_type"),
            "transcript_basename": os.path.basename(str(tp).replace("\\", "/")) if tp else None,
            "cwd_basename": os.path.basename(str(payload.get("cwd", "")).replace("\\", "/")),
            "verdict": verdict_name,
        }
        with open(path, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps(line) + "\n")
    except Exception:
        pass


def main() -> int:
    # `--explain <cwd>` is a diagnostic mode, never reached by the
    # real PreToolUse invocation (which always feeds JSON on stdin with no
    # argv). It exists so testing this hook by hand distinguishes "allowed
    # because primary checkout" from "allowed because the cwd could not be
    # determined at all" -- see `explain()` and its own docstring.
    if len(sys.argv) >= 3 and sys.argv[1] == "--explain":
        print(explain(sys.argv[2]))
        return 0

    if len(sys.argv) >= 2 and sys.argv[1] == "--self-test":
        return self_test()

    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    try:
        cwd = payload.get("cwd") or os.getcwd()
        reason = verdict(cwd, caller_agent_id(payload))
        _log_payload_shape(payload, "allow" if reason is None else "deny")
        if reason is None:
            return 0
        json.dump(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                },
                "systemMessage": "Blocked an Agent dispatch from inside a worktree.",
            },
            sys.stdout,
        )
    except Exception:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
