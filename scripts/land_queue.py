"""scripts/land_queue.py -- a queue runner that lands lane branches one at a time and merges each when green.

    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py add feat/1-x <sha> 1 "feat: x (#1)" [--full] [--red "check name"]
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py run [--once]      # the loop (30 s idle poll); --once drains and returns
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py status            # queue, in-flight item, last results
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py retry feat/1-x@<sha>   # take an already-taken item again
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py stop              # creates runner.stop; the loop exits before the next item

It replaces the orchestrator's scratchpad land_queue.sh + merge_green.sh. State lives in `--state` (default
`%TEMP%\\deg-land-queue`): queue.txt, taken.log, done.log, flakes.log, runner.stop, runner.json, inflight.json, land-<slug>-<sha7>.log, body-<slug>-<sha7>.md.

QUEUE LINE: `branch|sha|issue|full(0/1)|title` plus an optional sixth field `red=<check>[,<check>]` (checks ignored for
this item only, KNOWN_RED). The title may contain `|`. Blank lines and `#` lines are skipped.

MEASURED 2026-10-15 DEFECTS OF THE SCRIPTS THIS REPLACES, AND THE FIX
(a) Items were de-duplicated by the whole line, so editing a queued line (or a stripped trailing space) re-took an
    already-landed branch. Items are keyed `branch@sha`; the rest of the line is irrelevant to identity.
(b) A rerun of a branch whose remote branch already exists (closed PR) failed in `git push`. If `origin` already holds
    the branch, land_wt is given `--land-branch <branch>-<sha7>`; a new sha is a new key and works.
(c) A merge done by hand while another PR was in CI left the next PR DIRTY (PR #674). Right before merging the PR's
    mergeStateStatus is read (UNKNOWN is polled); DIRTY closes the PR (deleting its branch) and re-lands the SAME sha
    once, on `<branch>-<sha7>-r2`, which merges the new origin/main. A second DIRTY is REFUSED.
(d) KNOWN_RED per item (`red=`), plus the KNOWN_RED environment variable (comma separated) for every item.
(e) `status`.

MERGE: land_wt.py `--merge-when-green` (#666) does the wait, one rerun and the merge; exit 5/6 leave the PR open and are recorded. An item
with known-red checks lands without the flag and the merge loop here ignores them. `run` is built to be launched detached: it writes
runner.json (pid + heartbeat) for `status [--check]`, refuses a second runner, consumes runner.stop on exit, skips (SKIPPED-STACK) any
item stacked on a failed sha, and every log is append-only (`retry` appends RETRY lines).

Console output is ASCII.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import Path

from land_wt import pid_alive

Runner = Callable[[list[str], "Path | None"], "tuple[int, str]"]

_DONE_KEY = re.compile(r"(\S+) @ ([0-9A-Za-z]+)\s*$")
_PR_URL = re.compile(r"pull/(\d+)")

IDLE_POLL_SECONDS = 30
RERUN_SETTLE_SECONDS = 60
MERGEABLE_POLLS = 6


def run_command(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
    """Bytes in, text out (never text=True on Windows: it re-encodes)."""
    try:
        proc = subprocess.run(argv, cwd=cwd, capture_output=True, check=False, env={**os.environ, "GIT_TERMINAL_PROMPT": "0"})
    except OSError as err:
        return 127, f"cannot run {argv[0]}: {err}"
    return proc.returncode, (proc.stdout + b"\n" + proc.stderr).decode("utf-8", errors="replace").strip()


# ---------------------------------------------------------------------------
# Items and state
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Item:
    branch: str
    sha: str
    issue: str
    full: bool
    title: str
    known_red: tuple[str, ...] = ()

    @property
    def key(self) -> str:
        return f"{self.branch}@{self.sha}"

    @property
    def slug(self) -> str:
        return re.sub(r"[^A-Za-z0-9._-]", "-", self.branch)

    def line(self) -> str:
        base = f"{self.branch}|{self.sha}|{self.issue}|{int(self.full)}|{self.title}"
        return base + (f"|red={','.join(self.known_red)}" if self.known_red else "")


def parse_line(raw: str) -> Item | None:
    text = raw.strip()
    if not text or text.startswith("#"):
        return None
    parts = text.split("|", 4)
    if len(parts) < 5 or not parts[0].strip() or not parts[1].strip():
        return None
    title = parts[4]
    red: tuple[str, ...] = ()
    head, sep, tail = title.rpartition("|red=")
    if sep:
        red = tuple(n.strip() for n in tail.split(",") if n.strip())
        title = head
    return Item(parts[0].strip(), parts[1].strip(), parts[2].strip(), parts[3].strip() == "1", title.strip(), red)


def pending_items(queue_text: str, taken: set[str]) -> list[Item]:
    seen: set[str] = set()
    out: list[Item] = []
    for raw in queue_text.splitlines():
        it = parse_line(raw)
        if it is None or it.key in taken or it.key in seen:
            continue
        seen.add(it.key)
        out.append(it)
    return out


class State:
    def __init__(self, root: Path) -> None:
        self.root = root
        root.mkdir(parents=True, exist_ok=True)

    def _p(self, name: str) -> Path:
        return self.root / name

    def _read(self, name: str) -> str:
        try:
            return self._p(name).read_text(encoding="utf-8")
        except OSError:
            return ""

    def _append(self, name: str, line: str) -> None:
        with self._p(name).open("a", encoding="utf-8", newline="\n") as fh:
            fh.write(line.rstrip("\n") + "\n")

    def append_queue(self, line: str) -> None:
        self._append("queue.txt", line)

    def append_done(self, line: str) -> None:
        self._append("done.log", line)

    def append_flake(self, line: str) -> None:
        self._append("flakes.log", line)

    def taken(self) -> set[str]:
        """Replay taken.log: `key` takes it, `RETRY key` gives it back. Append-only, so `tail -F` never replays."""
        out: set[str] = set()
        for raw in self._read("taken.log").splitlines():
            ln = raw.strip()
            if ln.startswith("RETRY "):
                out.discard(ln[6:].strip())
            elif ln:
                out.add(ln)
        return out

    def failed_keys(self) -> set[str]:
        """Keys whose latest done.log verdict is FAILED / REFUSED (a later MERGED or RETRY line clears it)."""
        out: set[str] = set()
        for raw in self._read("done.log").splitlines():
            m = _DONE_KEY.search(raw)
            if not m:
                continue
            key = f"{m.group(1)}@{m.group(2)}"
            if raw.startswith(("FAILED", "REFUSED")):
                out.add(key)
            elif raw.startswith(("MERGED", "RETRY")):
                out.discard(key)
        return out

    def mark_taken(self, it: Item) -> None:
        self._append("taken.log", it.key)

    def pending(self) -> list[Item]:
        return pending_items(self._read("queue.txt"), self.taken())

    def retry(self, key: str) -> bool:
        """Append-only: never rewrites taken.log or done.log in place (a rewrite makes `tail -F` monitors replay the file)."""
        if key not in self.taken():
            return False
        self._append("taken.log", f"RETRY {key}")
        branch, _, sha = key.rpartition("@")
        self._append("done.log", f"RETRY {branch} @ {sha}")
        return True

    def stop_requested(self) -> bool:
        return self._p("runner.stop").exists()

    def consume_stop(self) -> None:
        self._p("runner.stop").unlink(missing_ok=True)

    def write_runner(self, pid: int, ts: float) -> None:
        self._p("runner.json").write_text(json.dumps({"pid": pid, "ts": ts}), encoding="utf-8", newline="\n")

    def runner(self) -> dict[str, float] | None:
        try:
            data = json.loads(self._read("runner.json"))
        except ValueError:
            return None
        return data if isinstance(data, dict) and "pid" in data else None

    def clear_runner(self, pid: int) -> None:
        cur = self.runner()
        if cur is not None and int(cur["pid"]) == pid:
            self._p("runner.json").unlink(missing_ok=True)

    def set_inflight(self, it: Item, stage: str) -> None:
        self._p("inflight.json").write_text(json.dumps({"key": it.key, "title": it.title, "stage": stage}), encoding="utf-8", newline="\n")

    def clear_inflight(self) -> None:
        self._p("inflight.json").unlink(missing_ok=True)

    def inflight(self) -> dict[str, str] | None:
        try:
            return json.loads(self._read("inflight.json"))
        except ValueError:
            return None

    def tail(self, name: str, n: int) -> list[str]:
        return [ln for ln in self._read(name).splitlines() if ln.strip()][-n:]


@dataclass
class Env:
    repo: Path
    state: Path
    python: str
    runner: Runner = run_command
    sleep: Callable[[float], None] = time.sleep
    now: Callable[[], str] = lambda: time.strftime("%Y-%m-%dT%H:%M:%S")
    clock: Callable[[], float] = time.time
    pid: int = field(default_factory=os.getpid)
    alive: Callable[[int], bool] = pid_alive
    known_red: tuple[str, ...] = field(default_factory=lambda: tuple(n.strip() for n in os.environ.get("KNOWN_RED", "").split(",") if n.strip()))

    def run(self, *argv: str) -> tuple[int, str]:
        return self.runner(list(argv), self.repo)


# ---------------------------------------------------------------------------
# Merge when green
# ---------------------------------------------------------------------------

_RUN_ID = re.compile(r"runs/(\d+)")
_PENDING = {"pending", ""}


def _checks(env: Env, pr: str) -> list[dict[str, str]]:
    _, out = env.run("gh", "pr", "checks", pr, "--json", "name,bucket,link")
    try:
        data = json.loads(out)
    except ValueError:
        return []
    return [c for c in data if isinstance(c, dict)] if isinstance(data, list) else []


def _settle(env: Env, pr: str) -> list[dict[str, str]]:
    """Wait for every check to finish. `--watch` can return early (or before any check exists), so look again a few times."""
    checks: list[dict[str, str]] = []
    for _ in range(3):
        env.run("gh", "pr", "checks", pr, "--watch", "--interval", "30")
        checks = _checks(env, pr)
        if checks and not any(c.get("bucket") in _PENDING for c in checks):
            break
        env.sleep(20)
    return checks


def _failing(checks: Sequence[dict[str, str]], ignore: set[str]) -> list[dict[str, str]]:
    return [c for c in checks if c.get("bucket") in {"fail", "cancel"} and c.get("name") not in ignore]


def _names(checks: Sequence[dict[str, str]]) -> str:
    return ", ".join(str(c.get("name")) for c in checks)


def _mergeability(env: Env, pr: str) -> str:
    """CLEAN-ish / DIRTY / UNKNOWN. GitHub computes this lazily, so UNKNOWN is polled before it is given up on."""
    state = "UNKNOWN"
    for _ in range(MERGEABLE_POLLS):
        _, out = env.run("gh", "pr", "view", pr, "--json", "mergeStateStatus,mergeable")
        try:
            data = json.loads(out)
        except ValueError:
            data = {}
        status, mergeable = str(data.get("mergeStateStatus", "UNKNOWN")), str(data.get("mergeable", "UNKNOWN"))
        if status == "DIRTY" or mergeable == "CONFLICTING":
            return "DIRTY"
        if status != "UNKNOWN" and mergeable != "UNKNOWN":
            return status
        state = status
        env.sleep(5)
    return state


def merge_green(env: Env, pr: str, known_red: Sequence[str] = ()) -> tuple[str, str]:
    """(`MERGED` | `DIRTY` | `REFUSED`, detail). One rerun of the failed runs, logged to flakes.log."""
    ignore = set(env.known_red) | set(known_red)
    env.sleep(20)
    checks = _settle(env, pr)
    bad = _failing(checks, ignore)
    if bad:
        runs = sorted({m.group(1) for c in bad if (m := _RUN_ID.search(str(c.get("link", ""))))})
        for run in runs:
            State(env.state).append_flake(f"{env.now()} PR#{pr} run {run} rerun after: {_names(bad)}")
            env.run("gh", "run", "rerun", run, "--failed")
        env.sleep(RERUN_SETTLE_SECONDS)
        checks = _settle(env, pr)
        bad = _failing(checks, ignore)
    if bad:
        return "REFUSED", f"failing: {_names(bad)}"
    if not checks:
        return "REFUSED", "no checks were reported"
    if _mergeability(env, pr) == "DIRTY":
        return "DIRTY", ""
    rc, out = env.run("gh", "pr", "merge", pr, "--merge")
    if rc == 0:
        return "MERGED", ""
    if _mergeability(env, pr) == "DIRTY":
        return "DIRTY", ""
    return "REFUSED", f"merge command failed: {out.splitlines()[-1] if out else 'no output'}"


# ---------------------------------------------------------------------------
# One item, the loop, status
# ---------------------------------------------------------------------------


def _land(env: Env, it: Item, land_branch: str | None, delegate_merge: bool) -> tuple[int, str, str | None]:
    body = env.state / f"body-{it.slug}-{it.sha[:7]}.md"
    if not body.is_file():
        body.write_text(
            f"Closes #{it.issue}.\n\nVerified by scripts/land_wt.py in a throwaway worktree of {it.sha} with origin/main merged in.\n",
            encoding="utf-8",
            newline="\n",
        )
    argv = [env.python, "scripts/land_wt.py", "--branch", it.branch, "--sha", it.sha, "--title", it.title, "--body-file", str(body)]
    if it.full:
        argv.append("--full")
    if delegate_merge:
        argv.append("--merge-when-green")
    if land_branch:
        argv += ["--land-branch", land_branch]
    rc, out = env.run(*argv)
    (env.state / f"land-{it.slug}-{it.sha[:7]}.log").write_text(out + "\n", encoding="utf-8", newline="\n")
    found = _PR_URL.findall(out)
    return rc, out, found[-1] if found else None


def process_item(env: Env, it: Item) -> str:
    """Land one item. Without known-red checks the merge is land_wt's own `--merge-when-green` (exit 5/6 leave the PR open);
    with them (`red=` or KNOWN_RED) land_wt stops at the open PR and the merge loop here ignores the named checks."""
    st = State(env.state)
    st.set_inflight(it, "landing")
    land_branch: str | None = None
    _, remote = env.run("git", "ls-remote", "--heads", "origin", it.branch)
    if remote.strip():  # (b) a closed PR's branch is still there: never push over it
        land_branch = f"{it.branch}-{it.sha[:7]}"
    delegate = not (set(env.known_red) | set(it.known_red))
    attempt = 1
    while True:
        rc, out, pr = _land(env, it, land_branch, delegate)
        tag = f"(PR #{pr} stays open) " if pr else ""
        if delegate and rc == 0:
            return f"MERGED #{pr} {it.branch} @ {it.sha}" if pr else f"MERGED {it.branch} @ {it.sha}"
        if delegate and rc == 4 and pr and _mergeability(env, pr) == "DIRTY":
            verdict, detail = "DIRTY", ""  # land_wt could not merge: another merge made the PR conflict (c)
        elif rc != 0:
            return f"FAILED land_wt rc={rc} {tag}{it.branch} @ {it.sha}"
        elif pr is None:
            return f"FAILED land_wt printed no PR url {it.branch} @ {it.sha}"
        else:
            st.set_inflight(it, f"waiting for checks on #{pr}")
            verdict, detail = merge_green(env, pr, it.known_red)
            if verdict == "MERGED":
                return f"MERGED #{pr} {it.branch} @ {it.sha}"
        if verdict == "DIRTY" and attempt == 1 and pr:  # (c) re-land the same sha on top of the new main
            env.run(
                "gh", "pr", "close", pr, "--delete-branch", "--comment", "Conflicts with main after another merge; re-landing the same commit."
            )
            attempt = 2
            land_branch = f"{it.branch}-{it.sha[:7]}-r2"
            st.set_inflight(it, f"re-landing after #{pr} went DIRTY")
            continue
        if verdict == "DIRTY":
            return f"REFUSED #{pr} merge conflict with main persists after one re-land; resolve by hand {it.branch} @ {it.sha}"
        return f"REFUSED #{pr} {detail} {it.branch} @ {it.sha}"


class AlreadyRunning(RuntimeError):
    """Another runner for this state directory is alive."""


def _heartbeat(env: Env) -> None:
    State(env.state).write_runner(env.pid, env.clock())


def _stacked_on(env: Env, failed: set[str], it: Item) -> str | None:
    """The failed key whose sha is an ancestor of this item's sha (a stack on top of a failure), else None."""
    for key in sorted(failed):
        base = key.rpartition("@")[2]
        if base == it.sha or env.run("git", "merge-base", "--is-ancestor", base, it.sha)[0] == 0:
            return key
    return None


def _fast_forward_main(env: Env) -> None:
    """After a merge: bring the main checkout up to date, but only when it is ON main (the shell runner did this)."""
    env.run("git", "fetch", "-q", "origin")
    rc, branch = env.run("git", "rev-parse", "--abbrev-ref", "HEAD")
    if rc == 0 and branch.strip() == "main":
        env.run("git", "merge", "-q", "--ff-only", "origin/main")


def drain(env: Env, *, forever: bool = False) -> None:
    st = State(env.state)
    cur = st.runner()
    if cur is not None and int(cur["pid"]) != env.pid and env.alive(int(cur["pid"])):
        raise AlreadyRunning(f"a runner is already alive (pid {int(cur['pid'])}). Check `status`; `stop` ends it")
    _heartbeat(env)
    try:
        while True:
            _heartbeat(env)
            if st.stop_requested():
                st.append_done(f"STOPPED {env.now()}")
                st.consume_stop()
                return
            pending = st.pending()
            if not pending:
                if not forever:
                    return
                env.sleep(IDLE_POLL_SECONDS)
                continue
            it = pending[0]
            st.mark_taken(it)
            blocker = _stacked_on(env, st.failed_keys(), it)
            if blocker is not None:  # 3: never attempt what stands on a failure
                st.append_done(f"SKIPPED-STACK {it.branch} @ {it.sha} (stacked on failed {blocker.replace('@', ' @ ')})")
                continue
            try:
                result = process_item(env, it)
            except Exception as err:  # noqa: BLE001  (one bad item never stops the queue)
                result = f"FAILED unexpected {type(err).__name__}: {err} {it.branch} @ {it.sha}"
            st.append_done(result)
            st.clear_inflight()
            _fast_forward_main(env) if result.startswith("MERGED") else env.run("git", "fetch", "-q", "origin")
    finally:
        st.clear_runner(env.pid)


def runner_line(env: Env) -> tuple[str, bool]:
    """(text, alive). `alive` is the pid check; the heartbeat age is context (an item can wait 30 min on checks)."""
    cur = State(env.state).runner()
    if cur is None:
        return "runner: not running", False
    pid, age = int(cur["pid"]), int(env.clock() - float(cur.get("ts", 0)))
    if env.alive(pid):
        return f"runner: alive pid {pid}, heartbeat {age} s ago", True
    return f"runner: DEAD (pid {pid} is gone; last heartbeat {age} s ago). Start it again with `run`", False


def status(env: Env) -> str:
    st = State(env.state)
    lines = [runner_line(env)[0]]
    pend = st.pending()
    lines.append(f"pending ({len(pend)}):")
    lines += [f"  {it.branch} @ {it.sha}  {it.title}" for it in pend]
    fly = st.inflight()
    lines.append(f"in flight: {fly['key'].replace('@', ' @ ')} ({fly['stage']})" if fly else "in flight: none")
    if st.stop_requested():
        lines.append("stop file present: the loop exits before the next item")
    lines.append("last results:")
    lines += [f"  {ln}" for ln in st.tail("done.log", 8)] or ["  (none)"]
    flakes = st.tail("flakes.log", 3)
    if flakes:
        lines.append("recent flakes:")
        lines += [f"  {ln}" for ln in flakes]
    return "\n".join(lines)


def main(argv: Sequence[str] | None = None, *, alive: Callable[[int], bool] = pid_alive) -> int:
    ap = argparse.ArgumentParser(description="Land queued lane branches and merge each when green.")
    ap.add_argument("--state", type=Path, default=Path(tempfile.gettempdir()) / "deg-land-queue")
    ap.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent, help="the main checkout (git fetch runs here)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    add = sub.add_parser("add")
    add.add_argument("branch")
    add.add_argument("sha")
    add.add_argument("issue")
    add.add_argument("title")
    add.add_argument("--full", action="store_true")
    add.add_argument("--red", default="", help="comma separated check names to ignore for this item")
    run = sub.add_parser("run")
    run.add_argument("--once", action="store_true")
    sub.add_parser("status").add_argument("--check", action="store_true", help="exit 1 when the runner is not alive")
    sub.add_parser("stop")
    retry = sub.add_parser("retry")
    retry.add_argument("key", help="branch@sha")
    args = ap.parse_args(argv)
    env = Env(repo=args.repo, state=args.state, python=sys.executable, alive=alive)
    st = State(args.state)
    if args.cmd == "add":
        red = tuple(n.strip() for n in args.red.split(",") if n.strip())
        st.append_queue(Item(args.branch, args.sha, args.issue, args.full, args.title, red).line())
        print(f"OK: queued {args.branch} @ {args.sha}")
    elif args.cmd == "run":
        try:
            drain(env, forever=not args.once)
        except AlreadyRunning as err:
            print(f"ERROR: {err}")
            return 1
    elif args.cmd == "status":
        print(status(env))
        if args.check and not runner_line(env)[1]:
            return 1
    elif args.cmd == "stop":
        (args.state / "runner.stop").write_text("", encoding="utf-8", newline="\n")
        print("OK: runner.stop created")
    elif args.cmd == "retry":
        if not st.retry(args.key):
            print(f"ERROR: {args.key} is not in taken.log. Check the key with `status`")
            return 1
        print(f"OK: {args.key} is pending again")
    return 0


if __name__ == "__main__":
    sys.exit(main())
