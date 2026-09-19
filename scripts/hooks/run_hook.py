"""scripts/hooks/run_hook.py -- the PreToolUse dispatcher that fails closed.

    python scripts/hooks/run_hook.py <guard-name>
    python scripts/hooks/run_hook.py --self-test

WHY THIS EXISTS
---------------
Every `PreToolUse` entry in `.claude/settings.json` invokes a guard through
a bare shell fallback, in effect:

    PY=$(command -v python3 || command -v python); [ -n "$PY" ] && \\
      [ -f scripts/hooks/<name>.py ] && "$PY" scripts/hooks/<name>.py || true

If Python is not on PATH, the guard file is missing, or the guard itself
crashes, that whole expression falls through to `|| true`: the tool call is
ALLOWED and nothing says a word. These are the guards that stop a shared
`git stash` swap, a recursive delete inside the repo, an agent dispatched
from a worktree, and an unlabelled cloud VM create. A guard that silently
stops guarding is worse than no guard at all, because the ban has been
written down (`CLAUDE.md`, the memory seeds, the skills) and every session
believes it is enforced -- the same shape this repo's own memory file
`the-signing-step-silently-no-ops-without-the-secret.md` and skill
`a-check-that-cannot-fail.md` both already name, just for a different guard.

This script is what `.claude/settings.json` invokes INSTEAD of a bare
interpreter, for every guard, with the guard's name as its one argument.
Everything it can determine on its own -- guard file missing, guard exits
non-zero, guard crashes, guard hangs, guard prints something that is not a
decision -- becomes a DENY, not a silent pass. See "WHAT THIS FILE DOES NOT
COVER" below for the one layer underneath it that a Python script,
definitionally, cannot cover itself.

CONTRACT
--------
Reads the PreToolUse JSON payload from stdin once and passes it through to
the guard's own stdin unchanged. Resolves `<guard-name>` to
`scripts/hooks/<guard-name>.py` next to this file (a trailing `.py` on the
argument is accepted and stripped, so `run_hook.py block_git_stash` and
`run_hook.py block_git_stash.py` are the same call). Runs the guard under
`sys.executable` -- the SAME interpreter already running this dispatcher,
never re-discovered, because by the time this file is executing, that
question is already answered. Exits 0 always, exactly like every guard's
own contract: the JSON on stdout (or silence) is the decision.

FAILS CLOSED (denies) when:
  - no guard name was given
  - the named guard file does not exist
  - the guard subprocess could not be started at all
  - the guard subprocess does not finish within --timeout seconds
  - the guard subprocess exits non-zero
  - the guard subprocess exits 0 but prints something that does not parse
    as JSON (a crash that happened to corrupt only the last write, not the
    exit code, is not distinguishable from a real decision by exit code
    alone)
  - this dispatcher itself raises an exception it did not anticipate above
    (a top-level catch-all; see `main()`)

PASSES THROUGH UNCHANGED when:
  - the guard exits 0 and prints nothing: its own "no opinion" -- silence,
    exit 0, exactly as today
  - the guard exits 0 and prints valid JSON: its own decision, forwarded
    byte-for-byte, never re-serialised (so this dispatcher can never
    introduce a formatting difference a guard's own tests did not see)

WHAT THIS FILE DOES NOT COVER
------------------------------
"No Python interpreter exists on this machine at all" is the one failure
mode a Python script cannot report on its own -- if there is truly no
interpreter, nothing here ever runs to say so. That case is handled one
layer up, in the `.claude/settings.json` command that invokes this
dispatcher: it tries `python3`/`python` in pure bash, and if NEITHER is
found, it prints the deny JSON itself via `printf`, no interpreter required,
before ever trying to reach this file. That command string is not committed
by this change (`.claude/settings.json` is owned elsewhere this session);
see `scratchpad/handoff-scripts.md` for the exact text and why editing it
here is out of scope for this agent.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

HOOKS_DIR = Path(__file__).resolve().parent
DEFAULT_GUARD_TIMEOUT_SECONDS = 8.0


def _deny(reason: str, system_message: str) -> None:
    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            },
            "systemMessage": system_message,
        },
        sys.stdout,
    )


def _guard_path(guard_name: str, hooks_dir: Path) -> Path:
    stem = guard_name[:-3] if guard_name.endswith(".py") else guard_name
    return hooks_dir / f"{stem}.py"


def run(
    guard_name: str,
    stdin_payload: str,
    timeout: float,
    hooks_dir: Path = HOOKS_DIR,
) -> int:
    """Run one guard under this dispatcher's own interpreter and print its
    decision (or this dispatcher's own fail-closed deny) to stdout. Always
    returns 0 -- the return value exists so a caller (or --self-test) can
    still tell the difference by reading what was printed, not by exit code,
    matching the guard contract this dispatcher itself is a layer over."""
    guard_path = _guard_path(guard_name, hooks_dir)

    if not guard_path.is_file():
        _deny(
            f"The '{guard_name}' PreToolUse guard is missing: {guard_path} does not exist. "
            "No guard ran, so this tool call is refused rather than silently allowed through "
            "an unenforced guard.",
            f"Blocked: guard '{guard_name}' is missing.",
        )
        return 0

    try:
        proc = subprocess.run(
            [sys.executable, str(guard_path)],
            input=stdin_payload,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        _deny(
            f"The '{guard_name}' guard did not finish within {timeout:g}s and was killed. "
            "A hung guard cannot have an opinion, so this tool call is refused rather than "
            "silently allowed through an unenforced guard.",
            f"Blocked: guard '{guard_name}' timed out.",
        )
        return 0
    except OSError as exc:
        _deny(
            f"The '{guard_name}' guard could not be started under {sys.executable}: {exc}. "
            "Refused rather than silently allowed through an unenforced guard.",
            f"Blocked: guard '{guard_name}' could not be started.",
        )
        return 0

    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip()
        detail = detail[:400] + ("... (truncated)" if len(detail) > 400 else "")
        reason = f"The '{guard_name}' guard exited {proc.returncode} instead of 0"
        if detail:
            reason += f": {detail}"
        reason += (
            ". A crashing guard cannot have an opinion, so this tool call is refused rather "
            "than silently allowed through an unenforced guard."
        )
        _deny(reason, f"Blocked: guard '{guard_name}' crashed.")
        return 0

    out = proc.stdout
    if not out.strip():
        return 0  # the guard's own "no opinion" -- stay silent, exactly as today

    try:
        json.loads(out)
    except json.JSONDecodeError:
        snippet = out.strip()[:200]
        _deny(
            f"The '{guard_name}' guard exited 0 but printed something that is not valid JSON "
            f"({snippet!r}). A decision that cannot be parsed cannot be trusted, so this tool "
            "call is refused rather than silently allowed through an unenforced guard.",
            f"Blocked: guard '{guard_name}' produced an unreadable decision.",
        )
        return 0

    sys.stdout.write(out)  # the guard's own decision, forwarded byte-for-byte
    return 0


# ---------------------------------------------------------------------------
# Self-test: every arm, with real subprocesses (tiny synthetic fake guards
# written to a throwaway directory), never mocked. This is the file that
# decides whether three other guards actually run; it earns the most
# suspicious test suite in scripts/hooks/, not the least.
# ---------------------------------------------------------------------------

_FAKE_GUARD_CRASHES = "import sys\nsys.stderr.write('boom: intentional self-test crash\\n')\nsys.exit(3)\n"
_FAKE_GUARD_DENIES = (
    "import json, sys\n"
    "json.dump({'hookSpecificOutput': {'hookEventName': 'PreToolUse', "
    "'permissionDecision': 'deny', 'permissionDecisionReason': 'self-test guard says no'}, "
    "'systemMessage': 'Blocked by the self-test guard.'}, sys.stdout)\n"
)
_FAKE_GUARD_SILENT = "import sys\nsys.exit(0)\n"
_FAKE_GUARD_HANGS = "import time\ntime.sleep(30)\n"
_FAKE_GUARD_GARBAGE = "import sys\nsys.stdout.write('not json at all {{{')\nsys.exit(0)\n"


def _write_guard(hooks_dir: Path, name: str, source: str) -> None:
    (hooks_dir / f"{name}.py").write_text(source, encoding="utf-8")


def _is_deny(out: str) -> bool:
    try:
        data = json.loads(out)
    except json.JSONDecodeError:
        return False
    return data.get("hookSpecificOutput", {}).get("permissionDecision") == "deny"


def self_test() -> bool:
    import io
    import tempfile

    ok = True

    def check(label: str, condition: bool) -> None:
        nonlocal ok
        if condition:
            print(f"ok    {label}")
        else:
            print(f"FAIL  {label}")
            ok = False

    def capture(guard_name: str, hooks_dir: Path, timeout: float = 3.0, stdin_payload: str = "{}") -> str:
        buf = io.StringIO()
        old_stdout = sys.stdout
        sys.stdout = buf
        try:
            rc = run(guard_name, stdin_payload, timeout, hooks_dir=hooks_dir)
        finally:
            sys.stdout = old_stdout
        assert rc == 0, "run() must always return 0, matching the guard contract"
        return buf.getvalue()

    with tempfile.TemporaryDirectory() as tmp:
        hooks_dir = Path(tmp)

        out = capture("does-not-exist", hooks_dir)
        check("a missing guard file denies", _is_deny(out) and "does-not-exist" in out)

        _write_guard(hooks_dir, "crashes", _FAKE_GUARD_CRASHES)
        out = capture("crashes", hooks_dir)
        check("a guard that exits non-zero denies, with its stderr in the reason",
              _is_deny(out) and "boom" in out)

        _write_guard(hooks_dir, "denies", _FAKE_GUARD_DENIES)
        out = capture("denies", hooks_dir)
        check("a guard's own deny decision passes through unchanged",
              _is_deny(out) and "self-test guard says no" in out)

        _write_guard(hooks_dir, "silent", _FAKE_GUARD_SILENT)
        out = capture("silent", hooks_dir)
        check("a guard with no opinion stays silent (no output at all)", out == "")

        _write_guard(hooks_dir, "hangs", _FAKE_GUARD_HANGS)
        out = capture("hangs", hooks_dir, timeout=0.5)
        check("a guard that does not finish in time denies", _is_deny(out) and "hangs" in out)

        _write_guard(hooks_dir, "garbage", _FAKE_GUARD_GARBAGE)
        out = capture("garbage", hooks_dir)
        check("a guard that exits 0 but prints non-JSON denies (its decision cannot be trusted)",
              _is_deny(out))

    # A crashing guard's own stderr is folded into the deny reason, which is
    # also the cheapest way to prove stdin genuinely reaches the guard: make
    # it echo what it read on stdin to stderr, then exit non-zero, and look
    # for the exact payload in the resulting deny reason.
    with tempfile.TemporaryDirectory() as tmp:
        hooks_dir = Path(tmp)
        _write_guard(
            hooks_dir, "echo_and_fail",
            "import sys\nsys.stderr.write('got:' + sys.stdin.read())\nsys.exit(1)\n",
        )
        out = capture("echo_and_fail", hooks_dir, stdin_payload='{"marker": "xyz123"}')
        check("stdin is genuinely forwarded to the guard, not just present",
              _is_deny(out) and "xyz123" in out)

    print(f"\n{'PASS' if ok else 'FAIL'}: run_hook self-test")
    return ok


def _main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("guard", nargs="?", help="guard name, e.g. block_git_stash (a .py suffix is accepted)")
    ap.add_argument("--timeout", type=float, default=DEFAULT_GUARD_TIMEOUT_SECONDS,
                     help=f"seconds to allow the guard to run before treating it as hung "
                          f"(default {DEFAULT_GUARD_TIMEOUT_SECONDS:g})")
    ap.add_argument("--self-test", action="store_true",
                     help="exercise every arm against synthetic fake guards, with no real "
                          "PreToolUse payload, and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    if not args.guard:
        _deny(
            "scripts/hooks/run_hook.py was invoked with no guard name. Nothing ran, so this "
            "tool call is refused rather than silently allowed through an unenforced guard.",
            "Blocked: the guard dispatcher was invoked incorrectly (no guard name given).",
        )
        return 0

    try:
        stdin_payload = sys.stdin.read()
    except Exception:
        stdin_payload = ""

    return run(args.guard, stdin_payload, args.timeout)


def main(argv: list[str] | None = None) -> int:
    """The one guarantee this whole file exists to make: whatever happens
    inside `_main`, this process ends with valid output (or silence) and
    exit 0, never an uncaught traceback that a shell-level `|| true` (or
    its absence) would turn into either a silent allow or an unreadable
    hook failure."""
    try:
        return _main(argv)
    except SystemExit:
        raise
    except Exception as exc:  # noqa: BLE001 -- deliberately blanket: see the docstring above
        try:
            _deny(
                f"scripts/hooks/run_hook.py itself raised an unexpected exception: {exc!r}. "
                "Refused rather than silently allowed through an unenforced guard.",
                "Blocked: the guard dispatcher crashed unexpectedly.",
            )
        except Exception:
            pass  # even emitting the fallback failed; nothing more this process can safely do
        return 0


if __name__ == "__main__":
    sys.exit(main())
