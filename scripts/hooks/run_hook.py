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
    alone), or that parses as JSON but is not a JSON object (`[]`, `3`,
    `null`: valid JSON, but no decision)
  - this dispatcher itself raises an exception it did not anticipate above
    (a top-level catch-all; see `main()`)

PASSES THROUGH UNCHANGED when:
  - the guard exits 0 and prints nothing: its own "no opinion" -- silence,
    exit 0, exactly as today
  - the guard exits 0 and prints a JSON object: its own decision, forwarded
    byte-for-byte, never re-serialised (so this dispatcher can never
    introduce a formatting difference a guard's own tests did not see).
    Only an object is forwarded; any other output is denied (see above).

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
import contextlib
import json
import subprocess
import sys
from pathlib import Path
from typing import NamedTuple

HOOKS_DIR = Path(__file__).resolve().parent
DEFAULT_GUARD_TIMEOUT_SECONDS = 8.0

# #1055: checks of forwarding / passthrough / crash / garbage behaviour use fake
# guards that terminate on their own, so their result must not depend on how
# fast a cold interpreter starts. MEASURED 2026-10-31: under heavy machine load
# a 3.0 s deadline made the dispatcher fail closed with its own timeout deny,
# which the checks misread as a forwarding bug. This backstop exists only so a
# genuinely broken test cannot hang forever; never use a tight value with it.
SELF_TEST_BACKSTOP_SECONDS = 120.0
# Only the "hangs" check uses a short deadline: its expected result (the
# dispatcher's timeout deny) is the same however slow the machine is. It may
# even expire before the interpreter has started; that is fine, because the
# expected result is the timeout deny either way.
SELF_TEST_HANG_TIMEOUT_SECONDS = 0.5

# What kind of outcome run_decision() produced, so a caller can tell "the
# dispatcher timed out" from "a guard wrote a deny" structurally, never by
# matching prose (#1055). The kind lives in-process only (see Decision): the
# bytes printed to stdout are exactly the pre-#1055 deny JSON, no extra key,
# because the hook output schema Claude Code accepts is not verified here.
KIND_PASSTHROUGH = "passthrough"  # the guard's own decision, or its own silence
KIND_MISSING = "missing"
KIND_TIMEOUT = "timeout"
KIND_START_FAILED = "start_failed"
KIND_CRASHED = "crashed"
KIND_NOT_JSON = "not_json"


class Decision(NamedTuple):
    rc: int  # always 0, the guard contract
    kind: str  # one of the KIND_* values
    stdout: str  # exactly what run() prints


def _deny_json(reason: str, system_message: str) -> str:
    return json.dumps(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            },
            "systemMessage": system_message,
        }
    )


def _deny(reason: str, system_message: str) -> None:
    sys.stdout.write(_deny_json(reason, system_message))


def _guard_path(guard_name: str, hooks_dir: Path) -> Path:
    stem = guard_name[:-3] if guard_name.endswith(".py") else guard_name
    return hooks_dir / f"{stem}.py"


def run_decision(
    guard_name: str,
    stdin_payload: str,
    timeout: float,
    hooks_dir: Path = HOOKS_DIR,
) -> Decision:
    """Run one guard under this dispatcher's own interpreter and return what to
    print (the guard's decision, or this dispatcher's own fail-closed deny)
    together with the kind of outcome. Pure: prints nothing."""
    guard_path = _guard_path(guard_name, hooks_dir)

    if not guard_path.is_file():
        return Decision(
            0,
            KIND_MISSING,
            _deny_json(
                f"The '{guard_name}' PreToolUse guard is missing: {guard_path} does not exist. "
                "No guard ran, so this tool call is refused rather than silently allowed through "
                "an unenforced guard.",
                f"Blocked: guard '{guard_name}' is missing.",
            ),
        )

    try:
        proc = subprocess.run(
            [sys.executable, str(guard_path)],
            input=stdin_payload,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return Decision(
            0,
            KIND_TIMEOUT,
            _deny_json(
                f"The '{guard_name}' guard did not finish within {timeout:g}s and was killed. "
                "A hung guard cannot have an opinion, so this tool call is refused rather than "
                "silently allowed through an unenforced guard.",
                f"Blocked: guard '{guard_name}' timed out.",
            ),
        )
    except OSError as exc:
        return Decision(
            0,
            KIND_START_FAILED,
            _deny_json(
                f"The '{guard_name}' guard could not be started under {sys.executable}: {exc}. "
                "Refused rather than silently allowed through an unenforced guard.",
                f"Blocked: guard '{guard_name}' could not be started.",
            ),
        )

    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip()
        detail = detail[:400] + ("... (truncated)" if len(detail) > 400 else "")
        reason = f"The '{guard_name}' guard exited {proc.returncode} instead of 0"
        if detail:
            reason += f": {detail}"
        reason += (
            ". A crashing guard cannot have an opinion, so this tool call is refused rather than silently allowed through an unenforced guard."
        )
        return Decision(0, KIND_CRASHED, _deny_json(reason, f"Blocked: guard '{guard_name}' crashed."))

    out = proc.stdout
    if not out.strip():
        return Decision(0, KIND_PASSTHROUGH, "")  # the guard's own "no opinion" -- stay silent, exactly as today

    snippet = out.strip()[:200]
    try:
        parsed = json.loads(out)
    except json.JSONDecodeError:
        return Decision(
            0,
            KIND_NOT_JSON,
            _deny_json(
                f"The '{guard_name}' guard exited 0 but printed something that is not valid JSON "
                f"({snippet!r}). A decision that cannot be parsed cannot be trusted, so this tool "
                "call is refused rather than silently allowed through an unenforced guard.",
                f"Blocked: guard '{guard_name}' produced an unreadable decision.",
            ),
        )

    # A decision is a JSON OBJECT. Valid JSON of any other shape ([], 3, null)
    # carries no decision, so forwarding it would silently allow the call.
    if not isinstance(parsed, dict):
        return Decision(
            0,
            KIND_NOT_JSON,
            _deny_json(
                f"The '{guard_name}' guard exited 0 but printed something that is not a JSON object "
                f"({snippet!r}). A decision that cannot be read as an object cannot be trusted, so this tool "
                "call is refused rather than silently allowed through an unenforced guard.",
                f"Blocked: guard '{guard_name}' produced an unreadable decision.",
            ),
        )

    # Deliberate: ANY object is forwarded, even one without hookSpecificOutput.
    # What an object means is the host's call, not this dispatcher's.
    return Decision(0, KIND_PASSTHROUGH, out)  # the guard's own decision, forwarded byte-for-byte


def run(
    guard_name: str,
    stdin_payload: str,
    timeout: float,
    hooks_dir: Path = HOOKS_DIR,
) -> int:
    """Run one guard (see run_decision) and print the result to stdout. Always
    returns 0 -- the return value exists so a caller can still tell the
    difference by reading what was printed, not by exit code, matching the
    guard contract this dispatcher itself is a layer over."""
    decision = run_decision(guard_name, stdin_payload, timeout, hooks_dir)
    sys.stdout.write(decision.stdout)
    return decision.rc


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
    (hooks_dir / f"{name}.py").write_text(source, encoding="utf-8", newline="\n")


def _is_deny(out: str) -> bool:
    try:
        data = json.loads(out)
    except json.JSONDecodeError:
        return False
    if not isinstance(data, dict):
        return False
    hso = data.get("hookSpecificOutput", {})
    return isinstance(hso, dict) and hso.get("permissionDecision") == "deny"


def self_test() -> bool:
    import io
    import tempfile

    ok = True

    def check(label: str, condition: bool, d: Decision, expect_timeout: bool = False) -> None:
        nonlocal ok
        if condition:
            print(f"ok    {label}")
        else:
            print(f"FAIL  {label}")
            if not expect_timeout and d.kind == KIND_TIMEOUT:
                print("      cause: the dispatcher's own timeout deny (machine load?), not a behaviour bug")
            print(f"      captured: kind={d.kind} stdout={d.stdout!r}")
            ok = False

    def capture(guard_name: str, hooks_dir: Path, timeout: float = SELF_TEST_BACKSTOP_SECONDS, stdin_payload: str = "{}") -> Decision:
        d = run_decision(guard_name, stdin_payload, timeout, hooks_dir=hooks_dir)
        assert d.rc == 0, "run_decision() must always return rc 0, matching the guard contract"
        return d

    with tempfile.TemporaryDirectory() as tmp:
        hooks_dir = Path(tmp)

        d = capture("does-not-exist", hooks_dir)
        check("a missing guard file denies", d.kind == KIND_MISSING and _is_deny(d.stdout) and "does-not-exist" in d.stdout, d)

        _write_guard(hooks_dir, "crashes", _FAKE_GUARD_CRASHES)
        d = capture("crashes", hooks_dir)
        check(
            "a guard that exits non-zero denies, with its stderr in the reason",
            d.kind == KIND_CRASHED and _is_deny(d.stdout) and "boom" in d.stdout,
            d,
        )

        _write_guard(hooks_dir, "denies", _FAKE_GUARD_DENIES)
        d = capture("denies", hooks_dir)
        check(
            "a guard's own deny decision passes through unchanged",
            d.kind == KIND_PASSTHROUGH and _is_deny(d.stdout) and "self-test guard says no" in d.stdout,
            d,
        )

        _write_guard(hooks_dir, "silent", _FAKE_GUARD_SILENT)
        d = capture("silent", hooks_dir)
        check("a guard with no opinion stays silent (no output at all)", d.kind == KIND_PASSTHROUGH and d.stdout == "", d)

        _write_guard(hooks_dir, "hangs", _FAKE_GUARD_HANGS)
        d = capture("hangs", hooks_dir, timeout=SELF_TEST_HANG_TIMEOUT_SECONDS)
        check(
            "a guard that does not finish in time gets the dispatcher's timeout deny",
            d.kind == KIND_TIMEOUT and _is_deny(d.stdout) and "hangs" in d.stdout,
            d,
            expect_timeout=True,
        )

        _write_guard(hooks_dir, "garbage", _FAKE_GUARD_GARBAGE)
        d = capture("garbage", hooks_dir)
        check(
            "a guard that exits 0 but prints unparseable output gets the not-valid-JSON deny", d.kind == KIND_NOT_JSON and _is_deny(d.stdout), d
        )

    # The print path: run() must write exactly run_decision()'s stdout (a run()
    # that computed the decision and skipped the write would silently allow).
    with tempfile.TemporaryDirectory() as tmp:
        hooks_dir = Path(tmp)
        _write_guard(hooks_dir, "denies", _FAKE_GUARD_DENIES)
        expected = run_decision("denies", "{}", SELF_TEST_BACKSTOP_SECONDS, hooks_dir=hooks_dir)
        buf = io.StringIO()
        old_stdout = sys.stdout
        sys.stdout = buf
        try:
            rc = run("denies", "{}", SELF_TEST_BACKSTOP_SECONDS, hooks_dir=hooks_dir)
        finally:
            sys.stdout = old_stdout
        check(
            "run() prints exactly the decision's stdout (the print path)",
            rc == 0 and expected.stdout != "" and buf.getvalue() == expected.stdout,
            Decision(rc, expected.kind, buf.getvalue()),
        )

    # A crashing guard's own stderr is folded into the deny reason, which is
    # also the cheapest way to prove stdin genuinely reaches the guard: make
    # it echo what it read on stdin to stderr, then exit non-zero, and look
    # for the exact payload in the resulting deny reason.
    with tempfile.TemporaryDirectory() as tmp:
        hooks_dir = Path(tmp)
        _write_guard(
            hooks_dir,
            "echo_and_fail",
            "import sys\nsys.stderr.write('got:' + sys.stdin.read())\nsys.exit(1)\n",
        )
        d = capture("echo_and_fail", hooks_dir, stdin_payload='{"marker": "xyz123"}')
        check("stdin is genuinely forwarded to the guard, not just present", d.kind == KIND_CRASHED and "xyz123" in d.stdout, d)

    print(f"\n{'PASS' if ok else 'FAIL'}: run_hook self-test")
    return ok


def _main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("guard", nargs="?", help="guard name, e.g. block_git_stash (a .py suffix is accepted)")
    ap.add_argument(
        "--timeout",
        type=float,
        default=DEFAULT_GUARD_TIMEOUT_SECONDS,
        help=f"seconds to allow the guard to run before treating it as hung (default {DEFAULT_GUARD_TIMEOUT_SECONDS:g})",
    )
    ap.add_argument(
        "--self-test", action="store_true", help="exercise every arm against synthetic fake guards, with no real PreToolUse payload, and exit"
    )
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
        # If even emitting the fallback fails there is nothing more this process can safely do.
        with contextlib.suppress(Exception):
            _deny(
                f"scripts/hooks/run_hook.py itself raised an unexpected exception: {exc!r}. "
                "Refused rather than silently allowed through an unenforced guard.",
                "Blocked: the guard dispatcher crashed unexpectedly.",
            )
        return 0


if __name__ == "__main__":
    sys.exit(main())
