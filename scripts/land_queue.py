"""scripts/land_queue.py -- a queue runner that lands lane branches one at a time and merges each when green.

    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py add feat/1-x <sha> 1 "feat: x (#1)" [--full] [--red "check name"]
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py run [--once]      # the loop (30 s idle poll); --once drains and returns
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py status            # queue, in-flight item, last results
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py retry feat/1-x@<sha>   # take an already-taken item again
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py stop              # creates runner.stop; the loop exits before the next item

It replaces the orchestrator's scratchpad land_queue.sh + merge_green.sh. State lives in `--state` (default
`%TEMP%\\deg-land-queue`): queue.txt, taken.log, done.log, flakes.log, runner.stop, runner.json, inflight.json,
land-<slug>-<sha7>.log, body-<slug>-<sha7>.md.

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
import contextlib
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

from land_wt import CHECKS_TIMEOUT_SECONDS, pid_alive

Runner = Callable[[list[str], "Path | None"], "tuple[int, str]"]

STDERR_MARK = "\n--- stderr ---\n"
STALE_MARGIN_SECONDS = 30 * 60  # a runner heartbeat older than checks_timeout + this is stale (a reused pid, or a hung runner)
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
    if proc.returncode == 0:  # stdout only: stderr noise must never reach a json.loads
        return 0, proc.stdout.decode("utf-8", errors="replace").strip()
    out = proc.stdout.decode("utf-8", errors="replace").strip()
    return proc.returncode, out + STDERR_MARK + proc.stderr.decode("utf-8", errors="replace").strip()


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


class AlreadyRunning(RuntimeError):
    """Another runner for this state directory is alive."""


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

    def write_runner(self, pid: int, ts: float, sleep: Callable[[float], None] = time.sleep) -> None:
        """Heartbeat: replace our own runner.json atomically. Another process (`status`) may hold it open (PermissionError on
        Windows): retry a few times, then give up quietly. A heartbeat failure must never end the runner."""
        tmp = self._p(f"runner.json.{pid}.tmp")
        for attempt in range(4):
            try:
                tmp.write_text(json.dumps({"pid": pid, "ts": ts}), encoding="utf-8", newline="\n")
                os.replace(tmp, self._p("runner.json"))
                return
            except OSError:
                if attempt < 3:
                    sleep(0.2)
        with contextlib.suppress(OSError):
            tmp.unlink(missing_ok=True)

    def claim_runner(
        self,
        pid: int,
        ts: float,
        alive: Callable[[int], bool],
        stale_after: float = 3600.0,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        """Claim runner.json with O_EXCL so two starters cannot both win.

        An empty or unparsable file is a claim in progress (the winner has created it and not written yet): wait briefly, and
        only replace it when it is old. A parsed file whose pid is dead, or whose heartbeat is ancient (a reused pid), is stale:
        it is renamed to a unique name (atomic: only one racer wins the rename) and the claim retried."""
        path = self._p("runner.json")
        for attempt in range(40):
            try:
                fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            except FileExistsError:
                pass
            except PermissionError:
                raise AlreadyRunning("runner.json is in use by another process. Check `status`") from None
            else:
                with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
                    fh.write(json.dumps({"pid": pid, "ts": ts}))
                return
            cur = self.runner()
            if cur is None:
                try:
                    age = time.time() - path.stat().st_mtime
                except OSError:
                    continue  # it vanished: try to claim again at once
                if age < 30 and attempt < 39:
                    sleep(0.1)
                    continue
                if age < 30:
                    raise AlreadyRunning("another runner is claiming this state directory right now. Check `status`")
                judged = None
            else:
                judged = int(cur["pid"])
                if judged != pid and alive(judged) and ts - float(cur.get("ts", 0)) <= stale_after:
                    raise AlreadyRunning(f"a runner is already alive (pid {judged}). Check `status`; `stop` ends it")
            aside = self._p(f"runner.json.stale.{pid}.{attempt}")
            try:
                os.replace(path, aside)
            except FileNotFoundError:
                continue
            except OSError:
                raise AlreadyRunning("runner.json is in use by another process. Check `status`") from None
            try:
                moved = json.loads(aside.read_text(encoding="utf-8") or "null")
            except (OSError, ValueError):
                moved = None
            if judged is not None and not (isinstance(moved, dict) and int(moved.get("pid", -1)) == judged):
                # we moved a FRESH claim made between our read and our rename: put it back if the slot is still empty
                with contextlib.suppress(OSError):
                    os.link(aside, path)
                raise AlreadyRunning("another runner claimed this state directory at the same moment. Check `status`")
        raise AlreadyRunning("could not claim this state directory. Check `status`")

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
    ff_main: bool = False
    checks_timeout: float = CHECKS_TIMEOUT_SECONDS
    known_red: tuple[str, ...] = field(default_factory=lambda: tuple(n.strip() for n in os.environ.get("KNOWN_RED", "").split(",") if n.strip()))

    def run(self, *argv: str) -> tuple[int, str]:
        rc, out = self.runner(list(argv), self.repo)
        return rc, out.replace(STDERR_MARK, "\n")

    def run_json_text(self, *argv: str) -> str:
        """stdout only, whatever the exit code (`gh pr checks --json` exits 8/1 WITH valid JSON on stdout)."""
        return self.runner(list(argv), self.repo)[1].split(STDERR_MARK)[0].strip()

    @property
    def stale_after(self) -> float:
        return self.checks_timeout + STALE_MARGIN_SECONDS


# ---------------------------------------------------------------------------
# Merge when green
# ---------------------------------------------------------------------------

_RUN_ID = re.compile(r"runs/(\d+)")
_PENDING = {"pending", ""}


def _checks(env: Env, pr: str) -> list[dict[str, str]]:
    out = env.run_json_text("gh", "pr", "checks", pr, "--json", "name,bucket,link")
    try:
        data = json.loads(out)
    except ValueError:
        State(env.state).append_flake(f"{env.now()} PARSE-FAIL gh pr checks {pr}: {out[:120]!r}")
        return []
    return [c for c in data if isinstance(c, dict)] if isinstance(data, list) else []


def _settle(env: Env, pr: str) -> tuple[list[dict[str, str]], bool]:
    """Poll until no check is pending or `env.checks_timeout` passes. (checks, timed_out). No unbounded `--watch`."""
    deadline = env.clock() + env.checks_timeout
    while True:
        checks = _checks(env, pr)
        if checks and not any(c.get("bucket") in _PENDING for c in checks):
            return checks, False
        if env.clock() >= deadline:
            return checks, True
        env.sleep(20)


def _failing(checks: Sequence[dict[str, str]], ignore: set[str]) -> list[dict[str, str]]:
    return [c for c in checks if c.get("bucket") in {"fail", "cancel"} and c.get("name") not in ignore]


def _names(checks: Sequence[dict[str, str]]) -> str:
    return ", ".join(str(c.get("name")) for c in checks)


def _mergeability(env: Env, pr: str) -> str:
    """CLEAN-ish / DIRTY / UNKNOWN. GitHub computes this lazily, so UNKNOWN is polled before it is given up on."""
    state = "UNKNOWN"
    for _ in range(MERGEABLE_POLLS):
        out = env.run_json_text("gh", "pr", "view", pr, "--json", "mergeStateStatus,mergeable")
        try:
            data = json.loads(out)
        except ValueError:
            State(env.state).append_flake(f"{env.now()} PARSE-FAIL gh pr view {pr}: {out[:120]!r}")
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
    """(`MERGED` | `DIRTY` | `PENDING` | `REFUSED`, detail). One rerun of the failed runs (flakes.log). Never merges with a check pending."""
    ignore = set(env.known_red) | set(known_red)
    env.sleep(20)
    checks, timed_out = _settle(env, pr)
    bad = _failing(checks, ignore)
    if bad and not timed_out:
        runs = sorted({m.group(1) for c in bad if (m := _RUN_ID.search(str(c.get("link", ""))))})
        for run in runs:
            State(env.state).append_flake(f"{env.now()} PR#{pr} run {run} rerun after: {_names(bad)}")
            env.run("gh", "run", "rerun", run, "--failed")
        env.sleep(RERUN_SETTLE_SECONDS)
        checks, timed_out = _settle(env, pr)
        bad = _failing(checks, ignore)
    if bad:
        return "REFUSED", f"failing: {_names(bad)}"
    if timed_out:
        waiting = [c for c in checks if c.get("bucket") in _PENDING]
        return "PENDING", f"still pending after {int(env.checks_timeout)} s: {_names(waiting) or 'no checks were reported'}"
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


def _find_existing_pr(env: Env, it: Item) -> tuple[str, str, str] | str | None:
    """A PR from an earlier attempt of this item: (`OPEN`|`MERGED`, number, head ref), an error text, or None.
    Matches the candidate head refs and requires the PR head to be this sha or contain it (land_wt merges main in, so the head
    is usually a descendant)."""
    env.run("git", "fetch", "-q", "origin")
    for ref in (it.branch, f"{it.branch}-{it.sha[:7]}", f"{it.branch}-{it.sha[:7]}-r2"):
        rc, out = env.run("gh", "pr", "list", "--state", "all", "--head", ref, "--json", "number,headRefOid,state", "--limit", "20")
        if rc != 0:
            return f"FAILED gh pr list rc={rc} (cannot tell if already landed; check gh auth, then `retry`) {it.branch} @ {it.sha}"
        try:
            rows = json.loads(out) if out.strip() else []
        except ValueError:
            return f"FAILED gh pr list rc={rc} printed no JSON {it.branch} @ {it.sha}"
        for row in rows if isinstance(rows, list) else []:
            oid, state = str(row.get("headRefOid", "")), str(row.get("state", ""))
            if state not in {"OPEN", "MERGED"}:
                continue
            same = bool(oid) and (oid.startswith(it.sha) or it.sha.startswith(oid))
            if same or (oid and env.run("git", "merge-base", "--is-ancestor", it.sha, oid)[0] == 0):
                return state, str(row.get("number")), ref
    return None


def process_item(env: Env, it: Item) -> str:
    """Land one item. Without known-red checks the merge is land_wt's own `--merge-when-green` (exit 5/6 leave the PR open);
    with them (`red=` or KNOWN_RED) land_wt stops at the open PR and the merge loop here ignores the named checks."""
    st = State(env.state)
    st.set_inflight(it, "landing")
    land_branch: str | None = None
    rc, remote = env.run("git", "ls-remote", "--heads", "origin", f"refs/heads/{it.branch}")
    if rc != 0:
        return f"FAILED ls-remote rc={rc} (nothing was landed; check the network, then `retry`) {it.branch} @ {it.sha}"
    if any(ln.split("\t")[-1].strip() == f"refs/heads/{it.branch}" for ln in remote.splitlines()):
        land_branch = f"{it.branch}-{it.sha[:7]}"  # (b) a closed PR's branch is still there: never push over it
    delegate = not (set(env.known_red) | set(it.known_red))
    found = _find_existing_pr(env, it)
    if isinstance(found, str):
        return found
    resume = found if found and found[0] == "OPEN" else None
    if found and found[0] == "MERGED":
        return f"MERGED #{found[1]} {it.branch} @ {it.sha} (recovered)"
    head_ref = it.branch if land_branch is None else land_branch
    attempt = 1
    while True:
        if resume:  # an earlier attempt left an open PR: wait on it, never open a second one
            (_, pr, head_ref), resume = resume, None
            rc, out, used_delegate = 0, "", False
            st.set_inflight(it, f"resuming #{pr}")
        else:
            rc, out, pr = _land(env, it, land_branch, delegate)
            used_delegate = delegate
            head_ref = it.branch if land_branch is None else land_branch
        tag = f"(PR #{pr} stays open) " if pr else ""
        if used_delegate and rc == 0:
            return f"MERGED #{pr} {it.branch} @ {it.sha}" if pr else f"MERGED {it.branch} @ {it.sha}"
        if used_delegate and rc == 4 and pr and _mergeability(env, pr) == "DIRTY":
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
            close = ["gh", "pr", "close", pr, "--comment", "Conflicts with main after another merge; re-landing the same commit."]
            if head_ref != it.branch:  # only a branch this queue created, never the lane's own
                close.append("--delete-branch")
            close_rc, close_out = env.run(*close)
            if close_rc != 0:
                last = close_out.splitlines()[-1] if close_out else "no output"
                return f"REFUSED #{pr} could not close the conflicting PR ({last}); close it by hand {it.branch} @ {it.sha}"
            attempt = 2
            land_branch = f"{it.branch}-{it.sha[:7]}-r2"
            st.set_inflight(it, f"re-landing after #{pr} went DIRTY")
            continue
        if verdict == "DIRTY":
            return f"REFUSED #{pr} merge conflict with main persists after one re-land; resolve by hand {it.branch} @ {it.sha}"
        if verdict == "PENDING":
            return f"FAILED rc=6 (PR #{pr} stays open) {detail} {it.branch} @ {it.sha}"
        return f"REFUSED #{pr} {detail} {it.branch} @ {it.sha}"


def _heartbeat(env: Env) -> None:
    State(env.state).write_runner(env.pid, env.clock(), env.sleep)


def _stacked_on(env: Env, failed: set[str], it: Item) -> str | None:
    """The failed key whose sha is an ancestor of this item's sha (a stack on top of a failure), else None."""
    for key in sorted(failed):
        if key.rpartition("@")[0] == it.branch:  # a fix-forward of the same branch is not a stack on its own failure
            continue
        base = key.rpartition("@")[2]
        if base == it.sha or env.run("git", "merge-base", "--is-ancestor", base, it.sha)[0] == 0:
            return key
    return None


def _fast_forward_main(env: Env) -> None:
    """Opt-in (`run --ff-main`): bring the main checkout up to date, only when it is ON main, clean and not mid-git. Outcome is logged."""
    st = State(env.state)
    env.run("git", "fetch", "-q", "origin")
    rc, branch = env.run("git", "rev-parse", "--abbrev-ref", "HEAD")
    if rc != 0 or branch.strip() != "main":
        st.append_done(f"FF-MAIN skipped: checkout is not on main ({branch.strip() or 'unknown'})")
        return
    _, gitdir = env.run("git", "rev-parse", "--absolute-git-dir")
    if gitdir.strip() and (Path(gitdir.strip()) / "index.lock").exists():
        st.append_done("FF-MAIN skipped: index.lock exists (another git command is running)")
        return
    rc, dirty = env.run("git", "status", "--porcelain")
    if rc != 0 or dirty.strip():
        st.append_done("FF-MAIN skipped: working tree is dirty")
        return
    rc, out = env.run("git", "merge", "-q", "--ff-only", "origin/main")
    st.append_done("FF-MAIN ok" if rc == 0 else f"FF-MAIN failed rc={rc}: {out.splitlines()[-1] if out else 'no output'}")


def _recover_interrupted(st: State) -> None:
    """An inflight.json with no verdict means the previous runner died mid-item: record it so `retry` applies."""
    fly = st.inflight()
    if not fly or "key" not in fly:
        return
    branch, _, sha = str(fly["key"]).rpartition("@")
    tail = f"{branch} @ {sha}"
    has_verdict = False
    for raw in st._read("done.log").splitlines():
        if raw.rstrip() == tail or raw.rstrip().endswith(" " + tail):
            has_verdict = not raw.startswith("RETRY")
    if not has_verdict:
        st.append_done(f"FAILED interrupted {tail}")
    st.clear_inflight()


def drain(env: Env, *, forever: bool = False) -> None:
    st = State(env.state)
    st.claim_runner(env.pid, env.clock(), env.alive, env.stale_after, env.sleep)
    _recover_interrupted(st)
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
            if result.startswith("MERGED") and env.ff_main:
                _fast_forward_main(env)
            else:
                env.run("git", "fetch", "-q", "origin")
    finally:
        st.clear_runner(env.pid)


def runner_line(env: Env) -> tuple[str, bool]:
    """(text, alive). `alive` is the pid check; the heartbeat age is context (an item can wait 30 min on checks)."""
    cur = State(env.state).runner()
    if cur is None:
        return "runner: not running", False
    pid, age = int(cur["pid"]), int(env.clock() - float(cur.get("ts", 0)))
    if env.alive(pid) and age > env.stale_after:
        return f"runner: STALE? (pid {pid} exists but its heartbeat is {age} s old; a reused pid or a hung runner). `run` will replace it", False
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


def _invalid_add(branch: str, sha: str, title: str) -> str:
    if not branch or re.search(r"[|\s]", branch):
        return f"branch {branch!r} must not be empty or contain `|` or whitespace. Pass the lane branch name"
    if not re.fullmatch(r"[0-9a-fA-F]{7,40}", sha):
        return f"sha {sha!r} is not 7 to 40 hex characters. Pass the commit id of the branch tip"
    if "|red=" in title or "\n" in title or "\r" in title:
        return "title must be one line and not contain `|red=`. Use --red for known-red checks"
    return ""


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
    run.add_argument(
        "--ff-main", action="store_true", help="after a merge, fast-forward the main checkout (only if on main, clean, no index.lock)"
    )
    sub.add_parser("status").add_argument("--check", action="store_true", help="exit 1 when the runner is not alive")
    sub.add_parser("stop")
    retry = sub.add_parser("retry")
    retry.add_argument("key", help="branch@sha")
    args = ap.parse_args(argv)
    env = Env(repo=args.repo, state=args.state, python=sys.executable, alive=alive, ff_main=getattr(args, "ff_main", False))
    st = State(args.state)
    if args.cmd == "add":
        problem = _invalid_add(args.branch, args.sha, args.title)
        if problem:
            print(f"ERROR: {problem}")
            return 1
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
        if not runner_line(env)[1]:
            print("ERROR: no live runner, so no stop file was written (it would stop the next `run` at once). Check `status`")
            return 1
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
