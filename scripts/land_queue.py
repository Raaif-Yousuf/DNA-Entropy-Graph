"""scripts/land_queue.py -- a queue runner that lands lane branches one at a time and merges each when green.

    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py add feat/1-x <sha> 1 "feat: x (#1)" [--full] [--red "check name"]
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py run [--once]      # the loop (30 s idle poll); --once drains and returns
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py status            # queue, in-flight item, last results
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py retry feat/1-x@<sha>   # take an already-taken item again
    worker\\.venv\\Scripts\\python.exe scripts\\land_queue.py stop              # creates runner.stop; the loop exits before the next item

It replaces the orchestrator's scratchpad land_queue.sh + merge_green.sh. State lives in `--state` (default
`%TEMP%\\deg-land-queue`): queue.txt, taken.log, done.log, flakes.log, runner.stop, inflight.json, body-<slug>-<sha7>.md.

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

OVERLAP: PR #724 (feat/666) adds `--merge-when-green` to land_wt.py. That is a single-PR tool with no queue, no
mergeability recheck and no per-item known-red. When it lands, this runner can pass `--merge-when-green` instead of its
own merge loop; until then the loop here is the only merge path (land_wt.py without the flag stops at the open PR).

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

Runner = Callable[[list[str], "Path | None"], "tuple[int, str]"]

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
        return {ln.strip() for ln in self._read("taken.log").splitlines() if ln.strip()}

    def mark_taken(self, it: Item) -> None:
        self._append("taken.log", it.key)

    def pending(self) -> list[Item]:
        return pending_items(self._read("queue.txt"), self.taken())

    def retry(self, key: str) -> bool:
        keys = [ln for ln in self._read("taken.log").splitlines() if ln.strip()]
        if key not in keys:
            return False
        self._p("taken.log").write_text("".join(k + "\n" for k in keys if k != key), encoding="utf-8", newline="\n")
        return True

    def stop_requested(self) -> bool:
        return self._p("runner.stop").exists()

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


def _land(env: Env, it: Item, land_branch: str | None) -> tuple[int, str, str | None]:
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
    if land_branch:
        argv += ["--land-branch", land_branch]
    rc, out = env.run(*argv)
    found = re.findall(r"pull/(\d+)", out)
    return rc, out, found[-1] if found else None


def process_item(env: Env, it: Item) -> str:
    st = State(env.state)
    st.set_inflight(it, "landing")
    land_branch: str | None = None
    _, remote = env.run("git", "ls-remote", "--heads", "origin", it.branch)
    if remote.strip():  # (b) a closed PR's branch is still there: never push over it
        land_branch = f"{it.branch}-{it.sha[:7]}"
    attempt = 1
    while True:
        rc, out, pr = _land(env, it, land_branch)
        if rc != 0:
            return f"FAILED land_wt rc={rc} {it.branch} @ {it.sha}"
        if pr is None:
            return f"FAILED land_wt printed no PR url {it.branch} @ {it.sha}"
        st.set_inflight(it, f"waiting for checks on #{pr}")
        verdict, detail = merge_green(env, pr, it.known_red)
        if verdict == "MERGED":
            return f"MERGED #{pr} {it.branch} @ {it.sha}"
        if verdict == "DIRTY" and attempt == 1:  # (c) re-land the same sha on top of the new main
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


def drain(env: Env, *, forever: bool = False) -> None:
    st = State(env.state)
    while True:
        if st.stop_requested():
            st.append_done(f"STOPPED {env.now()}")
            return
        pending = st.pending()
        if not pending:
            if not forever:
                return
            env.sleep(IDLE_POLL_SECONDS)
            continue
        it = pending[0]
        st.mark_taken(it)
        try:
            result = process_item(env, it)
        except Exception as err:  # noqa: BLE001  (one bad item never stops the queue)
            result = f"FAILED unexpected {type(err).__name__}: {err} {it.branch} @ {it.sha}"
        st.append_done(result)
        st.clear_inflight()
        env.run("git", "fetch", "-q", "origin")


def status(env: Env) -> str:
    st = State(env.state)
    lines = []
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


def main(argv: Sequence[str] | None = None) -> int:
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
    sub.add_parser("status")
    sub.add_parser("stop")
    retry = sub.add_parser("retry")
    retry.add_argument("key", help="branch@sha")
    args = ap.parse_args(argv)
    env = Env(repo=args.repo, state=args.state, python=sys.executable)
    st = State(args.state)
    if args.cmd == "add":
        red = tuple(n.strip() for n in args.red.split(",") if n.strip())
        st.append_queue(Item(args.branch, args.sha, args.issue, args.full, args.title, red).line())
        print(f"OK: queued {args.branch} @ {args.sha}")
    elif args.cmd == "run":
        drain(env, forever=not args.once)
    elif args.cmd == "status":
        print(status(env))
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
