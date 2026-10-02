"""scripts/land_pr.py -- land one lane as a PR from a shared checkout, touching only what it is given (#445).

    python scripts/land_pr.py --branch fix/445-x --paths scripts/a.py docs/b.md \\
        --title "fix: ..." --body-file body.md [--allow-no-checks] [--dry-run]
    python scripts/land_pr.py --list-hunks worker/src/dna_entropy/pipeline.py
    python scripts/land_pr.py --branch fix/412-x --hunks worker/src/dna_entropy/pipeline.py=0 --paths docs/c.md ...
    python scripts/land_pr.py --self-test

WHY (MEASURED 2026-10-02): landing one lane of a shared-checkout wave is the same seven git/gh steps
~15-20 times a night while other agents have uncommitted edits in the same tree, and each step has a
trap that eventually fires. This runs them in one place, refuses on the known traps, and never
touches a working-tree file it was not given.

WHAT IT DOES, IN ORDER
----------------------
1. Preflight (changes nothing; `--dry-run` stops here): refuses when `scripts/premerge.py` has not passed on
   the current HEAD (the stamp, see premerge_stamp.py; `--no-premerge-check` overrides, deliberately; this is
   the gate for the PR the tool itself opens, which the `gh pr create` hook cannot see), refuses a path that is a directory, a path
   with no change, a path outside the repo, a hunk spec on an untracked file, a hunk index that does
   not exist, a branch name that already exists locally or on the remote, a detached HEAD, and an
   index that already holds staged content it did not stage.
2. `git switch -c <branch>` from the current HEAD (working-tree edits travel with it, so every other
   lane's uncommitted work stays exactly where it is).
3. Stages exactly the given paths (`git add -A -- <file>` per literal path, so deletions work and a
   directory can never sweep in another lane's new file) and exactly the given hunks, then verifies
   that the staged set equals the requested set before committing. A mismatch aborts before the commit.
4. `git commit -F` (hooks run; never --no-verify), `git push -u origin <branch>`, `gh pr create`.
5. Waits on `gh pr checks`; merges (`gh pr merge --merge`, never squash: docs/branching_and_prs.md)
   only when every check passed. A red check stops ON the branch and names the check. Zero checks is
   refused unless `--allow-no-checks`, because CI here can be on demand and "no checks" is not "green".
6. Returns the shared tree to main with `git fetch origin; git checkout -B main origin/main`, NOT
   `git switch main`. MEASURED 2026-10-02: `switch main` refuses when a file the lane landed is also
   still locally modified (a second hunk another issue owns). `checkout -B` rewrites only files that
   differ between the branch tip and origin/main, which after the merge are none of the other lanes'
   edits; git itself refuses if one would be overwritten. It refuses first if local main holds a
   commit origin/main lacks (that commit would be orphaned). Then the local and remote branch go.

HUNKS: THE BYTES TRAP
---------------------
Selected hunks of one file (two issues in one file is routine) are staged by building a patch from
`git diff` and piping it to `git apply --cached --recount -`. MEASURED 2026-10-02: on Windows both
ends must be BYTES. Python's `text=True` decodes with cp1252, which corrupted a section sign in a
context line, and translates `\\n` to `\\r\\n` on stdin; either made `patch does not apply` with no
other hint. This module never decodes the diff at all: it splits bytes on `\\n`. Hunk indexes are
0-based as printed by `--list-hunks`, which reads the same diff (`-U3`) the staging step uses.

TESTABILITY
-----------
`--gh` (or env LAND_PR_GH) replaces the `gh` argv, so tests stub GitHub completely; `--self-test`
builds a bare remote and a clone under a temp dir and never touches the real checkout or GitHub.

Exit codes: 0 landed (or dry-run clean), 1 refused in preflight (nothing changed), 2 bad usage,
3 a check failed (PR open, you are on the branch), 4 failed after changes began (state is printed),
5 timed out waiting on checks.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import Path

import premerge_stamp

BASE = "main"
REMOTE = "origin"
DEFAULT_POLL_SECONDS = 20.0
DEFAULT_WAIT_SECONDS = 30 * 60.0
DEFAULT_GRACE_SECONDS = 120.0

EXIT_OK, EXIT_REFUSED, EXIT_USAGE, EXIT_RED, EXIT_FAILED, EXIT_TIMEOUT = 0, 1, 2, 3, 4, 5


class LandError(Exception):
    def __init__(self, message: str, code: int = EXIT_FAILED) -> None:
        super().__init__(message)
        self.code = code


# ---------------------------------------------------------------------------
# Process helpers (bytes in, bytes out: never text=True, see the module docstring)
# ---------------------------------------------------------------------------


@dataclass
class Ctx:
    repo: Path
    gh: list[str] = field(default_factory=lambda: ["gh"])
    gh_env: dict[str, str] | None = None
    log: list[str] = field(default_factory=list)
    sleep: Callable[[float], None] = time.sleep  # injectable so tests never wait for real
    clock: Callable[[], float] = time.monotonic

    def say(self, message: str) -> None:
        print(message)
        self.log.append(message)


def run(argv: Sequence[str], cwd: Path, stdin: bytes | None = None, env: dict[str, str] | None = None
        ) -> subprocess.CompletedProcess[bytes]:
    full_env = dict(os.environ if env is None else env)
    full_env["GIT_TERMINAL_PROMPT"] = "0"
    return subprocess.run(list(argv), cwd=cwd, input=stdin, capture_output=True, check=False, env=full_env)


def git(ctx: Ctx, *args: str, stdin: bytes | None = None, check: bool = True) -> subprocess.CompletedProcess[bytes]:
    proc = run(["git", "--literal-pathspecs", *args], ctx.repo, stdin)
    if check and proc.returncode != 0:
        raise LandError(f"git {' '.join(args[:3])} failed ({proc.returncode}): {_text(proc.stderr or proc.stdout)}")
    return proc


def git_text(ctx: Ctx, *args: str) -> str:
    return _text(git(ctx, *args).stdout).strip()


def _text(raw: bytes) -> str:
    return raw.decode("utf-8", errors="replace").strip()


# ---------------------------------------------------------------------------
# Hunks
# ---------------------------------------------------------------------------


def parse_diff(diff: bytes) -> tuple[bytes, list[bytes]]:
    """Split one file's unified diff into (header bytes, [hunk bytes]). A line starting `@@` inside a
    hunk body is impossible (body lines start with space, + or -), so the split cannot misfire."""
    header: list[bytes] = []
    hunks: list[list[bytes]] = []
    for line in diff.splitlines(keepends=True):
        if line.startswith(b"@@"):
            hunks.append([line])
        elif hunks:
            hunks[-1].append(line)
        else:
            header.append(line)
    return b"".join(header), [b"".join(h) for h in hunks]


def file_diff(ctx: Ctx, path: str) -> bytes:
    return git(ctx, "diff", "--no-color", "--no-ext-diff", "-U3", "--", path).stdout


def list_hunks(ctx: Ctx, path: str) -> list[tuple[int, str, str]]:
    _, hunks = parse_diff(file_diff(ctx, path))
    rows = []
    for index, hunk in enumerate(hunks):
        lines = hunk.splitlines()
        header = _text(lines[0])
        first = next((_text(x) for x in lines[1:] if x[:1] in (b"+", b"-")), "")
        rows.append((index, header, first))
    return rows


def build_patch(header: bytes, hunks: list[bytes], wanted: Sequence[int]) -> bytes:
    return header + b"".join(hunks[i] for i in wanted)


def stage_hunks(ctx: Ctx, path: str, wanted: Sequence[int]) -> None:
    header, hunks = parse_diff(file_diff(ctx, path))
    bad = [i for i in wanted if not 0 <= i < len(hunks)]
    if bad or not hunks:
        raise LandError(f"{path}: hunk index {bad or list(wanted)} out of range ({len(hunks)} hunk(s))", EXIT_REFUSED)
    patch = build_patch(header, hunks, sorted(set(wanted)))
    proc = git(ctx, "apply", "--cached", "--recount", "-", stdin=patch, check=False)
    if proc.returncode != 0:
        raise LandError(f"git apply --cached refused the hunk patch for {path}: {_text(proc.stderr)}")


# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------


def parse_hunk_specs(specs: Sequence[str]) -> dict[str, list[int]]:
    out: dict[str, list[int]] = {}
    for spec in specs:
        path, sep, indexes = spec.rpartition("=")
        if not sep or not path or not re.fullmatch(r"\d+(,\d+)*", indexes):
            raise LandError(f"--hunks wants PATH=INDEX[,INDEX...] (0-based, see --list-hunks); got {spec!r}", EXIT_USAGE)
        out.setdefault(path, []).extend(int(i) for i in indexes.split(","))
    return out


def normalise_path(ctx: Ctx, raw: str) -> str:
    """Repo-relative POSIX path; refuses anything outside the repo."""
    candidate = (Path.cwd() / raw).resolve() if not Path(raw).is_absolute() else Path(raw).resolve()
    if not candidate.is_relative_to(ctx.repo):
        candidate = (ctx.repo / raw).resolve()
    try:
        return candidate.relative_to(ctx.repo).as_posix()
    except ValueError:
        raise LandError(f"{raw}: is outside the repository {ctx.repo}", EXIT_REFUSED) from None


def preflight(ctx: Ctx, branch: str, paths: Sequence[str], hunk_specs: dict[str, list[int]],
              require_premerge: bool = False) -> list[str]:
    problems: list[str] = []
    if require_premerge:
        fresh, why = premerge_stamp.check_stamp(ctx.repo)
        if not fresh:
            problems.append(
                f"scripts/premerge.py has not passed on this HEAD ({why}). Run `scripts/premerge.py --fast` first "
                "(it runs over the working tree, before this tool commits), or pass --no-premerge-check deliberately"
            )
    if git(ctx, "check-ref-format", "--branch", branch, check=False).returncode != 0:
        problems.append(f"{branch!r} is not a valid branch name")
    if git(ctx, "symbolic-ref", "-q", "HEAD", check=False).returncode != 0:
        problems.append("HEAD is detached; check out the branch you want the new branch cut from")
    if git(ctx, "rev-parse", "--verify", "-q", f"refs/heads/{branch}", check=False).returncode == 0:
        problems.append(f"branch {branch} already exists locally")
    if _text(git(ctx, "ls-remote", "--heads", REMOTE, branch, check=False).stdout):
        problems.append(f"branch {branch} already exists on {REMOTE}")
    staged = [n for n in git(ctx, "diff", "--cached", "--name-only", "-z").stdout.split(b"\0") if n]
    if staged:
        shown = ", ".join(_text(n) for n in staged[:5])
        problems.append(f"the index already holds staged content this tool did not stage ({shown}); unstage or commit it first")
    if not paths and not hunk_specs:
        problems.append("nothing to land: give --paths and/or --hunks")
    both = set(paths) & set(hunk_specs)
    if both:
        problems.append(f"{', '.join(sorted(both))} is in both --paths and --hunks; choose one")
    for path in paths:
        full = ctx.repo / path
        if full.is_dir():
            problems.append(f"{path} is a directory; name the files (a directory sweeps in other lanes' new files)")
            continue
        status = git(ctx, "status", "--porcelain=v1", "-z", "--", path).stdout
        if not status.strip(b"\0"):
            problems.append(f"{path} has no change to land (unchanged, ignored or a typo)")
    for path, indexes in hunk_specs.items():
        tracked = git(ctx, "ls-files", "--error-unmatch", "--", path, check=False).returncode == 0
        if not tracked:
            problems.append(f"{path} is not tracked; hunks only apply to a tracked, modified file (use --paths for a new file)")
            continue
        hunks = parse_diff(file_diff(ctx, path))[1]
        if not hunks:
            problems.append(f"{path} has no unstaged change to take hunks from")
        elif any(not 0 <= i < len(hunks) for i in indexes):
            problems.append(f"{path} has {len(hunks)} hunk(s); asked for {sorted(set(indexes))} (0-based, see --list-hunks)")
    return problems


# ---------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------


def _gh(ctx: Ctx, *args: str) -> subprocess.CompletedProcess[bytes]:
    return run([*ctx.gh, *args], ctx.repo, env=ctx.gh_env)


def wait_for_checks(ctx: Ctx, pr: str, poll: float, timeout: float, allow_none: bool,
                    grace: float = DEFAULT_GRACE_SECONDS) -> None:
    """MEASURED 2026-10-02 (#472): GitHub registers no checks for a few seconds after `gh pr create`, so an empty
    list inside `grace` seconds means "not yet", not "none". Only after the grace period is it "no checks"."""
    deadline = ctx.clock() + timeout
    grace_deadline = ctx.clock() + grace
    while True:
        proc = _gh(ctx, "pr", "checks", pr, "--json", "name,bucket")
        text = _text(proc.stdout)
        rows: list[dict] = []
        if text.startswith("["):
            rows = json.loads(text)
        elif "no checks reported" not in _text(proc.stderr + proc.stdout).lower():
            raise LandError(f"gh pr checks failed ({proc.returncode}): {_text(proc.stderr or proc.stdout)}")
        if not rows:
            if ctx.clock() < grace_deadline:
                ctx.sleep(max(poll, 1.0))
                continue
            if allow_none:
                ctx.say(f"no checks reported after waiting {grace:.0f} s; continuing because --allow-no-checks was given")
                return
            raise LandError(
                f"PR {pr} has no checks reported after waiting {grace:.0f} s, so there is nothing "
                f"proving it green. Run `gh workflow run ci-docs.yml "
                f"--ref <branch>` or the local gates (scripts/premerge.py) and re-run with --allow-no-checks once satisfied. "
                f"You are on the branch; the PR is open.", EXIT_RED)
        red = [r["name"] for r in rows if r.get("bucket") in ("fail", "cancel")]
        if red:
            raise LandError(f"check(s) failed on PR {pr}: {', '.join(red)}. Nothing was merged; you are on the branch.", EXIT_RED)
        if all(r.get("bucket") in ("pass", "skipping") for r in rows):
            ctx.say(f"all {len(rows)} check(s) passed")
            return
        if ctx.clock() > deadline:
            raise LandError(f"timed out waiting for checks on PR {pr}; it is still open and you are on the branch.", EXIT_TIMEOUT)
        ctx.sleep(poll)


# ---------------------------------------------------------------------------
# The whole flow
# ---------------------------------------------------------------------------


def land(ctx: Ctx, branch: str, paths: Sequence[str], hunk_specs: dict[str, list[int]], title: str, body_file: Path | None,
         allow_no_checks: bool, dry_run: bool, poll: float, timeout: float, merge: bool = True,
         require_premerge: bool = False, grace: float = DEFAULT_GRACE_SECONDS) -> int:
    problems = preflight(ctx, branch, paths, hunk_specs, require_premerge)
    if problems:
        for problem in problems:
            print(f"REFUSED: {problem}", file=sys.stderr)
        return EXIT_REFUSED
    ctx.say(f"preflight clean: {len(paths)} path(s), {sum(len(v) for v in hunk_specs.values())} hunk(s) across {len(hunk_specs)} file(s)")
    if dry_run:
        ctx.say("dry run: nothing was changed")
        return EXIT_OK

    body = body_file.read_bytes() if body_file else b""
    expected = sorted({*paths, *hunk_specs})
    try:
        git(ctx, "fetch", REMOTE)
        git(ctx, "switch", "-c", branch)
        ctx.say(f"on new branch {branch}")
        for path in paths:
            git(ctx, "add", "-A", "--", path)
        for path, indexes in hunk_specs.items():
            stage_hunks(ctx, path, indexes)
        staged = sorted(_text(n) for n in git(ctx, "diff", "--cached", "--name-only", "-z").stdout.split(b"\0") if n)
        if staged != expected:
            git(ctx, "reset", "-q", check=False)
            raise LandError(f"staged set {staged} != requested set {expected}; unstaged and stopped before committing. You are on {branch}.")
        message = title.encode("utf-8") + (b"\n\n" + body if body.strip() else b"") + b"\n"
        with tempfile.TemporaryDirectory() as tmp:
            message_file = Path(tmp) / "msg.txt"
            message_file.write_bytes(message)
            git(ctx, "commit", "-q", "-F", str(message_file))
        commit = git_text(ctx, "rev-parse", "HEAD")
        ctx.say(f"committed {commit[:9]}")
        git(ctx, "push", "-q", "-u", REMOTE, branch)
        create = ["pr", "create", "--base", BASE, "--head", branch, "--title", title]
        if body_file:
            create += ["--body-file", str(body_file)]
        else:
            create += ["--body", title]
        proc = _gh(ctx, *create)
        match = re.search(r"/pull/(\d+)", _text(proc.stdout))
        if proc.returncode != 0 or not match:
            raise LandError(f"gh pr create failed: {_text(proc.stderr or proc.stdout)}. The branch is pushed; you are on {branch}.")
        pr = match.group(1)
        ctx.say(f"opened PR #{pr}")
        if not merge:
            ctx.say("--no-merge: stopped with the PR open")
            return EXIT_OK
        wait_for_checks(ctx, pr, poll, timeout, allow_no_checks, grace)
        merged = _gh(ctx, "pr", "merge", pr, "--merge")
        if merged.returncode != 0:
            raise LandError(f"gh pr merge failed: {_text(merged.stderr or merged.stdout)}. PR #{pr} is open; you are on {branch}.")
        ctx.say(f"merged PR #{pr}")
        return_to_main(ctx, branch, commit)
    except LandError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return exc.code
    ctx.say(f"LANDED: PR #{pr} merged; local {BASE} is at {git_text(ctx, 'rev-parse', '--short', BASE)}; "
            "other working-tree edits were not touched.")
    return EXIT_OK


def return_to_main(ctx: Ctx, branch: str, commit: str) -> None:
    git(ctx, "fetch", REMOTE)
    if git(ctx, "merge-base", "--is-ancestor", commit, f"{REMOTE}/{BASE}", check=False).returncode != 0:
        raise LandError(f"{commit[:9]} is not in {REMOTE}/{BASE} after the merge; not moving local {BASE}. You are on {branch}.")
    if git(ctx, "merge-base", "--is-ancestor", BASE, f"{REMOTE}/{BASE}", check=False).returncode != 0:
        raise LandError(f"local {BASE} has commits {REMOTE}/{BASE} lacks; moving it would orphan them. Resolve by hand. You are on {branch}.")
    # `switch main` would refuse here when a landed file is still locally modified; -B does not.
    git(ctx, "checkout", "-q", "-B", BASE, f"{REMOTE}/{BASE}")
    ctx.say(f"returned to {BASE} at {REMOTE}/{BASE}")
    git(ctx, "branch", "-d", branch)
    if _text(git(ctx, "ls-remote", "--heads", REMOTE, branch, check=False).stdout):
        git(ctx, "push", "-q", REMOTE, "--delete", branch)
    ctx.say(f"deleted {branch} locally and on {REMOTE}")


# ---------------------------------------------------------------------------
# Self-test world: a bare remote, a clone, and a stub `gh`. Never the real checkout, never GitHub.
# ---------------------------------------------------------------------------

STUB_GH = r'''
import json, os, subprocess, sys, tempfile
from pathlib import Path

state_path = Path(os.environ["STUB_STATE"])
state = json.loads(state_path.read_text()) if state_path.exists() else {"calls": [], "prs": {}, "polls": 0}
args = sys.argv[1:]
state["calls"].append(args)
mode = os.environ.get("STUB_CHECKS", "pass")

def done(code=0):
    state_path.write_text(json.dumps(state))
    sys.exit(code)

def git(*a, cwd):
    subprocess.run(["git", "-c", "user.name=Stub", "-c", "user.email=stub@example.com", *a], cwd=cwd, check=True, capture_output=True)

if args[:2] == ["pr", "create"]:
    n = str(len(state["prs"]) + 1)
    state["prs"][n] = args[args.index("--head") + 1]
    print("https://example.invalid/owner/repo/pull/" + n)
    done()
if args[:2] == ["pr", "checks"]:
    state["polls"] += 1
    if mode == "pass":
        print(json.dumps([{"name": "ci", "bucket": "pass"}]))
    elif mode == "fail":
        print(json.dumps([{"name": "ci-docs", "bucket": "fail"}, {"name": "ci-worker", "bucket": "pass"}]))
    elif mode == "appear_third":
        print(json.dumps([] if state["polls"] < 3 else [{"name": "ci", "bucket": "pass"}]))
    elif mode == "pending_then_pass":
        print(json.dumps([{"name": "ci", "bucket": "pending" if state["polls"] < 3 else "pass"}]))
    else:
        print("no checks reported on the 'x' branch", file=sys.stderr)
        done(1)
    done()
if args[:2] == ["pr", "merge"]:
    n = args[2]
    head = state["prs"][n]
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["git", "clone", "-q", os.environ["STUB_REMOTE"], tmp], check=True, capture_output=True)
        git("checkout", "-q", "main", cwd=tmp)
        git("fetch", "-q", "origin", head, cwd=tmp)
        git("merge", "--no-ff", "-q", "-m", "Merge pull request #" + n + " from " + head, "FETCH_HEAD", cwd=tmp)
        git("push", "-q", "origin", "main", cwd=tmp)
    state["merged"] = n
    done()
sys.exit("stub gh: unhandled " + repr(args))
'''


@dataclass
class World:
    root: Path
    remote: Path
    work: Path
    stub: Path
    state: Path

    def env(self, checks: str = "pass") -> dict[str, str]:
        return {**os.environ, "STUB_STATE": str(self.state), "STUB_REMOTE": str(self.remote), "STUB_CHECKS": checks}

    def ctx(self, checks: str = "pass") -> Ctx:
        return Ctx(self.work, gh=[sys.executable, str(self.stub)], gh_env=self.env(checks))

    def calls(self) -> list[list[str]]:
        return json.loads(self.state.read_text())["calls"] if self.state.exists() else []

    def g(self, *args: str, stdin: bytes | None = None) -> str:
        return _text(run(["git", *args], self.work, stdin).stdout)


FILE_LINES = [f"line {i:02d} section sign \u00a7 and \u0141 stay intact\n" for i in range(1, 41)]


def make_world(root: Path) -> World:
    remote, work = root / "remote.git", root / "work"
    subprocess.run(["git", "init", "-q", "--bare", "-b", BASE, str(remote)], check=True, capture_output=True)
    subprocess.run(["git", "clone", "-q", str(remote), str(work)], check=True, capture_output=True)
    for key, value in (("user.name", "T"), ("user.email", "t@example.com"), ("core.autocrlf", "false")):
        subprocess.run(["git", "config", key, value], cwd=work, check=True, capture_output=True)
    (work / "f.txt").write_bytes("".join(FILE_LINES).encode("utf-8"))
    (work / "other.txt").write_bytes(b"other\n")
    (work / "unrelated.txt").write_bytes(b"unrelated\n")
    for cmd in (["add", "-A"], ["commit", "-q", "-m", "init"], ["branch", "-M", BASE], ["push", "-q", "-u", "origin", BASE]):
        subprocess.run(["git", *cmd], cwd=work, check=True, capture_output=True)
    stub = root / "gh_stub.py"
    stub.write_text(STUB_GH, encoding="utf-8", newline="\n")
    return World(root, remote, work, stub, root / "stub_state.json")


def edit_two_hunks(world: World) -> None:
    lines = list(FILE_LINES)
    lines[2] = "line 03 EDITED for issue A \u00a7\n"
    lines[35] = "line 36 EDITED for issue B \u00a7\n"
    (world.work / "f.txt").write_bytes("".join(lines).encode("utf-8"))


def self_test() -> bool:
    ok = True

    def expect(label: str, condition: bool, extra: str = "") -> None:
        nonlocal ok
        print(("ok    " if condition else "FAIL: ") + label + ("" if condition else f" {extra}"))
        ok = ok and condition

    with tempfile.TemporaryDirectory() as tmp:
        world = make_world(Path(tmp))
        ctx = world.ctx()
        edit_two_hunks(world)
        (world.work / "unrelated.txt").write_bytes(b"another lane's edit\n")
        before_unrelated = (world.work / "unrelated.txt").read_bytes()
        before_f = (world.work / "f.txt").read_bytes()

        rows = list_hunks(ctx, "f.txt")
        expect("two hunks are listed", len(rows) == 2, str(rows))
        code = land(ctx, "fix/a", [], {"f.txt": [0]}, "fix: issue A", None, False, False, 0.0, 30.0)
        expect("landing one hunk exits 0", code == EXIT_OK, str(code))
        expect("another lane's modified file is byte-identical", (world.work / "unrelated.txt").read_bytes() == before_unrelated)
        expect("the other hunk is still an uncommitted working-tree edit", (world.work / "f.txt").read_bytes() == before_f
               and len(list_hunks(ctx, "f.txt")) == 1)
        expect("main gained exactly one merge commit", world.g("rev-list", "--merges", "--count", "main") == "1")
        expect("the merged file has hunk A and not hunk B",
               "issue A" in world.g("show", "origin/main:f.txt") and "issue B" not in world.g("show", "origin/main:f.txt"))
        expect("the branch is gone locally and on the remote",
               world.g("branch", "--list", "fix/a") == "" and world.g("ls-remote", "--heads", "origin", "fix/a") == "")
        expect("the section sign survived the patch round trip", "\u00a7" in world.g("show", "origin/main:f.txt"))
    print(f"\n{'PASS' if ok else 'FAIL'}: land_pr self-test")
    return ok


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Land one lane as a PR from a shared checkout.")
    ap.add_argument("--branch", help="new branch name, e.g. fix/445-land-pr")
    ap.add_argument("--paths", nargs="+", default=[], metavar="FILE", help="whole files to land (never a directory)")
    ap.add_argument("--hunks", action="append", default=[], metavar="FILE=I[,I]", help="selected hunks of a tracked file (0-based)")
    ap.add_argument("--list-hunks", metavar="FILE", help="print the hunks of FILE's working-tree diff and exit")
    ap.add_argument("--title", help="commit subject and PR title")
    ap.add_argument("--body-file", type=Path, help="PR body (also the commit body)")
    ap.add_argument("--allow-no-checks", action="store_true",
                    help="merge even when the PR reports no checks AFTER the grace period; never a fix for 'checks not registered yet'")
    ap.add_argument("--no-merge", action="store_true", help="stop after opening the PR")
    ap.add_argument("--no-premerge-check", action="store_true",
                    help="do not require a green scripts/premerge.py stamp on HEAD (use deliberately)")
    ap.add_argument("--dry-run", action="store_true", help="run the preflight and stop; changes nothing")
    ap.add_argument("--gh", default=os.environ.get("LAND_PR_GH", "gh"), help="gh command (default: gh; for tests)")
    ap.add_argument("--poll-seconds", type=float, default=DEFAULT_POLL_SECONDS)
    ap.add_argument("--grace-seconds", type=float, default=DEFAULT_GRACE_SECONDS,
                    help="how long to wait for the first check to appear before calling it 'no checks'")
    ap.add_argument("--wait-seconds", type=float, default=DEFAULT_WAIT_SECONDS)
    ap.add_argument("--repo", type=Path, default=Path.cwd(), help="repository (default: cwd)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1
    if shutil.which("git") is None:
        print("ERROR: git is not installed or not on PATH.", file=sys.stderr)
        return EXIT_USAGE
    probe = run(["git", "rev-parse", "--show-toplevel"], args.repo)
    if probe.returncode != 0:
        print(f"ERROR: {args.repo} is not inside a git repository.", file=sys.stderr)
        return EXIT_USAGE
    ctx = Ctx(Path(_text(probe.stdout)).resolve(), gh=shlex.split(args.gh))

    try:
        if args.list_hunks:
            path = normalise_path(ctx, args.list_hunks)
            rows = list_hunks(ctx, path)
            if not rows:
                print(f"{path}: no unstaged hunks")
            for index, header, first in rows:
                print(f"{index}  {header}  | {first}".encode("ascii", "backslashreplace").decode("ascii"))
            return EXIT_OK
        if not args.branch or not args.title:
            ap.error("--branch and --title are required (unless --list-hunks or --self-test)")
        paths = [normalise_path(ctx, p) for p in args.paths]
        hunk_specs = {normalise_path(ctx, p): idx for p, idx in parse_hunk_specs(args.hunks).items()}
        return land(ctx, args.branch, paths, hunk_specs, args.title, args.body_file, args.allow_no_checks, args.dry_run,
                    args.poll_seconds, args.wait_seconds, merge=not args.no_merge,
                    require_premerge=not args.no_premerge_check, grace=args.grace_seconds)
    except LandError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return exc.code


if __name__ == "__main__":
    sys.exit(main())
