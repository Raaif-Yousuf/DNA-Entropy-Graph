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
import threading
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import Path

from land_wt import CHECKS_TIMEOUT_SECONDS, pid_alive, proc_start_time, run_bounded

Runner = Callable[[list[str], "Path | None"], "tuple[int, str]"]

_HB_LOCK = threading.Lock()
STDERR_MARK = "\n--- stderr ---\n"
STALE_MARGIN_SECONDS = 30 * 60  # no progress for checks_timeout + this is shown by `status` as NO PROGRESS (never a reason to replace)
_DONE_KEY = re.compile(r"(\S+) @ ([0-9A-Za-z]+)\s*$")
_PR_URL = re.compile(r"pull/(\d+)")

IDLE_POLL_SECONDS = 30
RERUN_SETTLE_SECONDS = 60
MERGEABLE_POLLS = 6


COMMAND_TIMEOUT_SECONDS = 600.0


def run_command(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
    """Bytes in, text out (never text=True on Windows: it re-encodes). gh and git get a generous timeout (rc 124, `TIMEOUT after`)
    that kills the whole process tree; land_wt.py has its own per-call deadlines and gets none."""
    limit = None if any(Path(a).name == "land_wt.py" for a in argv[1:3]) else COMMAND_TIMEOUT_SECONDS
    try:
        proc = run_bounded(argv, cwd=cwd, env={**os.environ, "GIT_TERMINAL_PROMPT": "0"}, timeout=limit)
    except subprocess.TimeoutExpired:
        return 124, f"TIMEOUT after {int(limit or 0)} s: {' '.join(argv[:4])}"
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


def owner_alive(cur: dict[str, float], alive: Callable[[int], bool], start_time: Callable[[int], float | None]) -> bool:
    """The recorded runner is alive: its pid exists AND (when both are known) its creation time is the recorded one.
    A pid that exists with a different creation time was reused by another program (Windows reuses pids): it is dead to us."""
    pid = int(cur["pid"])
    if not alive(pid):
        return False
    recorded = cur.get("started")
    if recorded is None:
        return True
    now = start_time(pid)
    return now is None or abs(now - float(recorded)) <= 1.0


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

    def write_runner(
        self, pid: int, ts: float, sleep: Callable[[float], None] = time.sleep, beat: float | None = None, started: float | None = None
    ) -> bool:
        """Heartbeat: replace our own runner.json atomically; `ts` is the main thread's last PROGRESS, `beat` the beater's own clock,
        `started` our process creation time (kept from the file when not given).
        Returns False (and writes nothing) when the file is missing, empty, unparsable or holds another pid: only a parsed file
        holding OUR pid is ever replaced, so a new owner's claim (or one still being written) is never overwritten. A file that cannot
        be read right now (`status` has it open, PermissionError on Windows) is retried and then skipped quietly: True, nothing written.
        The check-then-replace window is narrow and accepted (a claimer renames the file aside, so a late replace here lands
        on a file the claimer has already moved: the claimer's own `moved` check puts a fresh claim back)."""
        tmp = self._p(f"runner.json.{pid}.tmp")
        for attempt in range(4):
            kind, cur = self._runner_raw()
            if kind == "unreadable":
                if attempt < 3:
                    sleep(0.2)
                    continue
                return True
            if kind != "ok" or cur is None or int(cur["pid"]) != pid:
                return False
            record = {"pid": pid, "ts": ts, "beat": ts if beat is None else beat}
            kept = started if started is not None else cur.get("started")
            if kept is not None:
                record["started"] = kept
            try:
                tmp.write_text(json.dumps(record), encoding="utf-8", newline="\n")
                os.replace(tmp, self._p("runner.json"))
                return True
            except OSError:
                if attempt < 3:
                    sleep(0.2)
        with contextlib.suppress(OSError):
            tmp.unlink(missing_ok=True)
        return True

    def claim_runner(
        self,
        pid: int,
        ts: float,
        alive: Callable[[int], bool],
        sleep: Callable[[float], None] = time.sleep,
        *,
        start_time: Callable[[int], float | None] = proc_start_time,
        started: float | None = None,
        replace: bool = False,
    ) -> int | None:
        """Claim runner.json with O_EXCL so two starters cannot both win. Returns the pid it REPLACED (only with `replace`), else None.

        A runner whose pid is alive (and whose creation time still matches the recorded one) is NEVER replaced automatically,
        however old its heartbeat: AlreadyRunning. `replace=True` (`run --replace`, a human's call) takes over a live-but-stuck one.
        An empty or unparsable file is a claim in progress (the winner has created it and not written yet): wait briefly, and
        only replace it when it is old. A parsed file whose pid is dead (or reused by another program) is stale:
        it is renamed to a unique name (atomic: only one racer wins the rename) and the claim retried."""
        replaced: int | None = None
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
                    fh.write(json.dumps({"pid": pid, "ts": ts, "beat": ts, **({"started": started} if started is not None else {})}))
                return replaced
            kind, cur = self._runner_raw()
            if kind == "missing":
                continue
            if kind == "unreadable":  # held open mid-replace by its owner: it is alive, wait and look again
                if attempt < 39:
                    sleep(0.1)
                    continue
                raise AlreadyRunning("runner.json cannot be read right now. Check `status`")
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
                if judged != pid and owner_alive(cur, alive, start_time):
                    if not replace:
                        raise AlreadyRunning(
                            f"a runner is already alive (pid {judged}). Check `status`; `stop` ends it, `run --replace` takes over a stuck one"
                        )
                    replaced = judged
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

    def _runner_raw(self) -> tuple[str, dict[str, float] | None]:
        """(`missing` | `unreadable` | `empty` | `bad` | `ok`, data). A read error is NOT an empty file: the file may be mid-replace."""
        try:
            text = self._p("runner.json").read_text(encoding="utf-8")
        except FileNotFoundError:
            return "missing", None
        except OSError:
            return "unreadable", None
        if not text.strip():
            return "empty", None
        try:
            data = json.loads(text)
            out = {"pid": int(data["pid"]), "ts": float(data.get("ts", 0)), "beat": float(data.get("beat", data.get("ts", 0)))}
            if data.get("started") is not None:
                out["started"] = float(data["started"])
            return "ok", out
        except (ValueError, TypeError, KeyError, AttributeError):
            return "bad", None

    def runner(self) -> dict[str, float] | None:
        return self._runner_raw()[1]

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
    start_time: Callable[[int], float | None] = proc_start_time
    started: float | None = None  # our own creation time, recorded in runner.json by drain
    ff_main: bool = False
    heartbeat_seconds: float = 60.0
    progress: float = 0.0  # clock() at the main thread's last progress (a subprocess returned, a poll ran, a stage began)
    checks_timeout: float = CHECKS_TIMEOUT_SECONDS
    known_red: tuple[str, ...] = field(default_factory=lambda: tuple(n.strip() for n in os.environ.get("KNOWN_RED", "").split(",") if n.strip()))

    def run(self, *argv: str) -> tuple[int, str]:
        rc, out = self.runner(list(argv), self.repo)
        self.progress = self.clock()
        if rc == 124 and out.startswith("TIMEOUT"):
            State(self.state).append_flake(f"{self.now()} TIMEOUT {' '.join(argv[:4])}")
        return rc, out.replace(STDERR_MARK, "\n")

    def run_json_text(self, *argv: str) -> str:
        """stdout only, whatever the exit code (`gh pr checks --json` exits 8/1 WITH valid JSON on stdout)."""
        out = self.runner(list(argv), self.repo)[1]
        self.progress = self.clock()
        return out.split(STDERR_MARK)[0].strip()

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


def merge_green(env: Env, pr: str, known_red: Sequence[str] = (), head_oid: str | None = None) -> tuple[str, str]:
    """(`MERGED` | `DIRTY` | `PENDING` | `REFUSED`, detail). One rerun of the failed runs (flakes.log). Never merges with a check pending."""
    ignore = set(env.known_red) | set(known_red)
    if head_oid is None:  # pin the head we are about to wait on: a later push must make the merge fail, not ride along
        try:
            head_oid = str(json.loads(env.run_json_text("gh", "pr", "view", pr, "--json", "headRefOid")).get("headRefOid", ""))
        except (ValueError, AttributeError):
            head_oid = ""
    if not re.fullmatch(r"[0-9a-fA-F]{7,64}", head_oid):  # never merge without --match-head-commit
        return "PENDING", "could not read the PR head commit (gh pr view failed); check gh auth, then `retry`"
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
    rc, out = env.run("gh", "pr", "merge", pr, "--merge", "--match-head-commit", head_oid)
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


def _is_ancestor(env: Env, older: str, newer: str) -> bool | None:
    """True / False, or None when git cannot tell (rc 128: an unfetchable object is unknown, not false)."""
    rc = env.run("git", "merge-base", "--is-ancestor", older, newer)[0]
    return True if rc == 0 else False if rc == 1 else None


def _head_is_sha_plus_main_merges(env: Env, it: Item, oid: str) -> bool:
    """The PR head is the item's sha plus only merge commits whose second parent is already in origin/main (what land_wt adds)."""
    rc, out = env.run("git", "rev-list", "--first-parent", "--parents", f"{it.sha}..{oid}")
    rows = [ln.split() for ln in out.splitlines() if ln.strip()]
    if rc != 0 or not rows:
        return False
    if any(len(r) != 3 or _is_ancestor(env, r[2], "origin/main") is not True for r in rows):
        return False
    base = rows[-1][1]
    return base.startswith(it.sha) or it.sha.startswith(base)


def _find_existing_pr(env: Env, it: Item) -> tuple[str, str, str, str] | str | None:
    """A PR from an earlier attempt of this item: (`OPEN`|`MERGED`, number, head ref, head oid), an error text, or None.
    An open PR is resumed only when its head is this sha or this sha plus merges of main (never unverified commits)."""
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
            oid, state, num = str(row.get("headRefOid", "")), str(row.get("state", "")), str(row.get("number"))
            if state not in {"OPEN", "MERGED"}:
                continue
            if oid and (oid.startswith(it.sha) or it.sha.startswith(oid)):
                return state, num, ref, oid
            if state == "MERGED":
                if _is_ancestor(env, it.sha, "origin/main") is True:
                    return state, num, ref, oid
                anc = _is_ancestor(env, it.sha, oid) if oid else None
                if anc is True:
                    return state, num, ref, oid
                if anc is None:
                    return f"FAILED cannot verify merged PR #{num} (head not fetchable; check it by hand, then `retry`) {it.branch} @ {it.sha}"
            elif oid and _head_is_sha_plus_main_merges(env, it, oid):
                return state, num, ref, oid
            else:
                what = "not this sha plus merges of main"
                return f"FAILED open PR #{num} has unverified commits ({what}); close or merge it by hand, then `retry` {it.branch} @ {it.sha}"
    return None


def process_item(env: Env, it: Item) -> str:
    """Land one item. Without known-red checks the merge is land_wt's own `--merge-when-green` (exit 5/6 leave the PR open);
    with them (`red=` or KNOWN_RED) land_wt stops at the open PR and the merge loop here ignores the named checks."""
    st = State(env.state)
    env.progress = env.clock()
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
        return f"MERGED-RECOVERED #{found[1]} {it.branch} @ {it.sha}"
    head_ref = it.branch if land_branch is None else land_branch
    attempt = 2 if resume and resume[2].endswith("-r2") else 1
    resume_oid = resume[3] if resume else None
    while True:
        if resume:  # an earlier attempt left an open PR: wait on it, never open a second one
            (_, pr, head_ref, _oid), resume = resume, None
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
            verdict, detail = merge_green(env, pr, it.known_red, resume_oid if not used_delegate and resume_oid else None)
            resume_oid = None
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


def _heartbeat(env: Env) -> bool:
    """Write runner.json with the main thread's last progress. Never raises; False once the file is no longer ours."""
    try:
        with _HB_LOCK:
            return State(env.state).write_runner(env.pid, env.progress, env.sleep, env.clock(), env.started)
    except Exception as err:  # noqa: BLE001  (a heartbeat failure must never end the runner)
        with contextlib.suppress(Exception):
            State(env.state).append_flake(f"{env.now()} HEARTBEAT-FAIL {type(err).__name__}: {err}")
        return True


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


def drain(env: Env, *, forever: bool = False, replace: bool = False) -> None:
    st = State(env.state)
    env.progress = env.clock()
    env.started = env.start_time(env.pid)
    replaced = st.claim_runner(env.pid, env.progress, env.alive, env.sleep, start_time=env.start_time, started=env.started, replace=replace)
    if replaced is not None:
        st.append_done(f"REPLACED pid {replaced} {env.now()}")
    _recover_interrupted(st)
    stop_beat = threading.Event()

    def beat() -> None:
        while not stop_beat.wait(env.heartbeat_seconds):
            if not _heartbeat(env):
                return  # a new runner owns the file now: stop beating over it

    beater = threading.Thread(target=beat, name="land-queue-heartbeat", daemon=True)
    beater.start()
    try:
        while True:
            env.progress = env.clock()
            if not _heartbeat(env):  # runner.json is no longer ours: another runner owns this state directory
                st.append_done(f"DISPLACED {env.now()} (another runner took over runner.json; this one took no further item)")
                return
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
                st.append_done(f"SKIPPED-STACK (stacked on failed {blocker.replace('@', ' @ ')}) {it.branch} @ {it.sha}")
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
        stop_beat.set()
        beater.join(timeout=5)
        st.clear_runner(env.pid)


def runner_line(env: Env) -> tuple[str, bool]:
    """(text, alive). `alive` is the pid check (plus the creation-time check against a reused pid); the progress age is context
    (an item can wait 30 min on checks) and never changes `alive`: a stuck runner is for a human to replace with `run --replace`."""
    cur = State(env.state).runner()
    if cur is None:
        return "runner: not running", False
    pid, age = int(cur["pid"]), int(env.clock() - float(cur.get("ts", 0)))
    if owner_alive(cur, env.alive, env.start_time):
        if age > env.stale_after:
            return f"runner: alive, NO PROGRESS for {age // 60} min - inspect; replace with run --replace (pid {pid})", True
        return f"runner: alive pid {pid}, heartbeat {age} s ago", True
    if env.alive(pid):
        return f"runner: DEAD (pid {pid} now belongs to another program; last heartbeat {age} s ago). Start it again with `run`", False
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
    run.add_argument("--replace", action="store_true", help="take over a live but stuck runner (`status` says NO PROGRESS); logged REPLACED")
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
            drain(env, forever=not args.once, replace=args.replace)
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
