"""scripts/premerge.py -- one command that runs every gate before `gh pr create` (#431).

    worker\\.venv\\Scripts\\python.exe scripts\\premerge.py --fast     # per lane: guards + lint + schema + fast dotnet
    worker\\.venv\\Scripts\\python.exe scripts\\premerge.py            # full: also the three big suites
    worker\\.venv\\Scripts\\python.exe scripts\\premerge.py --list     # what would run, and why
    worker\\.venv\\Scripts\\python.exe scripts\\premerge.py --only check_docs_index --only ruff-worker-check
    worker\\.venv\\Scripts\\python.exe scripts\\premerge.py --self-test

WHY (MEASURED 2026-09-19): landing a change meant ~13 separate commands over two
languages and two working directories. An orchestrator ran the guard set before one
lane merged and not after, and a literal user-home path sat on main for several PRs.
CI is on demand only here, so nothing else checks a green-looking PR.

THE TRAPS THIS HANDLES SO THE CALLER DOES NOT
---------------------------------------------
- `dotnet test` / `dotnet format` only work with `app/` as the working directory (the
  repo root fails with a misleading VSTest error). Every dotnet gate runs with cwd=app/.
- Python gates run on `worker/.venv`'s interpreter, never the PATH python (an MSYS2 build
  with no wheels). The runner may itself be started by any python; it never lends its own
  interpreter to a gate unless that interpreter IS the venv one. A missing venv makes
  those gates ERROR with the one command that creates it, never a silent fallback.
- The scripts test suite needs pyyaml, which the venv does not have; it runs through
  `uv run --with pytest --with pyyaml` exactly as ci-docs.yml does.

CANNOT SILENTLY LEAVE A GATE OUT
--------------------------------
- Every `scripts/check_*.py` is DISCOVERED by glob (like ci-docs.yml's loop), never listed.
- Every `app/tests/*/*.csproj` is discovered too; the one exempt project is named, with a reason.
- `audit()` fails the run when a workflow references a `scripts/*.py|ps1` that no gate runs and
  that is not in NOT_A_GATE with a reason. Adding a gate to CI without adding it here goes red.
- Selecting zero gates (a `--only` typo, an empty tier) is a usage error, never a green run.

STATUSES
--------
PASS (exit 0), FAIL (the gate ran and said no), ERROR (the gate could not run or finish:
tool missing, timeout, or pytest dying after its tests), SKIP (only for an optional tool that is
not installed; always printed), KNOWN (a failure fully explained by a named known issue, see
below; always printed with the issue number, never silent).
FAIL and ERROR both make the run fail, but are labelled differently so "the guard caught
something" is never confused with "the guard did not run".

PYTEST GATES (MEASURED 2026-10-02)
----------------------------------
The first full run reported worker-pytest and scripts-tests as FAIL although every test passed:
both crashed afterwards in pytest's own `cleanup_dead_symlinks` on the SHARED default basetemp
(`%TEMP%\\pytest-of-<user>\\pytest-current`, a race with any other pytest running). So every pytest gate
gets its own private `--basetemp` (under `--log-dir` when given, else a temp dir premerge creates
and removes) plus `-p no:cacheprovider`, and its verdict comes from the TEST OUTCOME, not the exit
code alone (`classify_pytest`): failed or errored tests are FAIL; a run that printed only passes
and then exited non-zero is an ERROR naming that (an infrastructure death, not a test failure,
and not a PASS either); no summary at all is an ERROR.

KNOWN ISSUES
------------
`dotnet format --verify-no-changes` reports ENDOFLINE for every line of a CRLF working tree
(core.autocrlf=true on Windows, `.gitattributes` says eol=lf; #462). Only when git reports
autocrlf=true, and only when EVERY parsed diagnostic is ENDOFLINE, the gate is KNOWN instead of
FAIL. Any other diagnostic id, an unparseable failure, or autocrlf off stays FAIL, so a real
formatting regression cannot hide behind it. THEORY (unverified, dotnet could not be run when this
was written): the diagnostic line shape is `path(line,col): error ENDOFLINE: ...`; if it differs
the parse finds nothing and the gate fails safe.

Exit codes: 0 every selected gate passed, 1 a gate FAILED or ERRORED, 2 bad usage.
Console output is ASCII-only.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path

PASS, FAIL, ERROR, SKIP, KNOWN = "PASS", "FAIL", "ERROR", "SKIP", "KNOWN"

# Scripts a workflow may reference that are deliberately not pre-merge gates.
NOT_A_GATE: dict[str, str] = {
    "scripts/cloud_gpu_test.ps1": "creates billed cloud resources; never a pre-merge step",
    "scripts/gen_third_party_notices.py": "generator; its output is verified by check_third_party_notices.py",
    "scripts/compile_sprint_log.py": "folds fragments at merge time; only its --check arm is a gate",
}

# Test projects that are not run here, with the reason (ci-app.yml excludes the same one).
EXEMPT_TEST_PROJECTS: dict[str, str] = {
    "DnaEntropyGraph.App.UiTests": "WinUI-app-hosted; hangs under CI (ci-app.yml), needs an interactive desktop",
}

# Gates that need more than the default timeout.
LONG_TIMEOUT = 1800.0
DEFAULT_TIMEOUT = 900.0
DEFAULT_BASE = "origin/main"

SCRIPT_REF = re.compile(r"scripts/[A-Za-z0-9_./-]+\.(?:py|ps1)")


@dataclass
class Gate:
    name: str
    argv: list[str]
    cwd: Path
    tier: str = "fast"  # "fast" gates run in both modes, "full" gates only in full mode
    optional: bool = False  # missing executable -> SKIP instead of ERROR
    timeout: float = DEFAULT_TIMEOUT
    why: str = ""
    pytest: bool = False  # gets a private --basetemp and an outcome-based verdict
    forgive: Callable[[str, int, Path], str | None] | None = None  # (output, rc, cwd) -> note or None


@dataclass
class Result:
    gate: Gate
    status: str
    seconds: float
    output: str = ""
    detail: str = ""


# ---------------------------------------------------------------------------
# Building the gate list
# ---------------------------------------------------------------------------


def find_venv_python(root: Path) -> Path | None:
    for rel in ("worker/.venv/Scripts/python.exe", "worker/.venv/bin/python"):
        candidate = root / rel
        if candidate.is_file():
            return candidate
    return None


def venv_python_or_marker(root: Path, override: str | None) -> str:
    """The interpreter every Python gate uses. When there is no venv the returned path does
    not exist, so each gate ERRORs naming the fix instead of quietly using another python."""
    if override:
        return override
    found = find_venv_python(root)
    return str(found) if found else str(root / "worker" / ".venv" / "Scripts" / "python.exe")


def discover_check_scripts(root: Path) -> list[Path]:
    return sorted((root / "scripts").glob("check_*.py"))


def discover_test_projects(root: Path) -> list[Path]:
    return sorted((root / "app" / "tests").glob("*/*.csproj"))


def build_gates(root: Path, python: str | None = None, base: str = DEFAULT_BASE) -> list[Gate]:
    py = venv_python_or_marker(root, python)
    gates: list[Gate] = []

    # 1. Every check_*.py, discovered.
    for script in discover_check_scripts(root):
        name = script.stem
        tier = "full" if script.name == "check_third_party_notices.py" else "fast"
        gates.append(
            Gate(
                name=name,
                argv=[py, f"scripts/{script.name}"],
                cwd=root,
                tier=tier,
                why="repo guard (discovered)" + ("; needs dotnet and the worker venv" if tier == "full" else ""),
            )
        )

    # 2. The non-check_ gates CI runs.
    gates += [
        Gate("compile-sprint-log-check", [py, "scripts/compile_sprint_log.py", "--check"], root,
             why="changelog fragment shape"),
        Gate("changelog-fragment-present", [py, "scripts/check_changelog_fragments.py", "--base", base], root,
             why=f"behaviour changed since merge-base({base}) means a docs/changelog.d fragment exists (Hard Rule 16)"),
        Gate("manifest-schema-drift", [py, "scripts/gen_manifest_schema.py", "--check"], root,
             why="docs/contract/*.json match the worker dataclasses"),
        Gate("triage-schema", [py, "scripts/triage_diagnostics.py", "--check-schema"], root,
             why="triage SCHEMA_FIELDS match the generated schema"),
        Gate("ruff-worker-check", ["uvx", "ruff", "check", "worker"], root, why="worker lint"),
        Gate("ruff-worker-format", ["uvx", "ruff", "format", "--check", "worker"], root, why="worker format"),
        Gate("ruff-scripts-check", ["uvx", "ruff", "check", "scripts"], root, why="scripts lint"),
        Gate("lychee-links", ["lychee", "--offline", "--no-progress", "--include-fragments",
                              "--exclude-path", "legacy", "--exclude-path", "worker/legacy",
                              "--exclude-path", "worker/docs-legacy",
                              "--exclude-path", "docs/superpowers/specs",
                              "--exclude-path", "worker/CLAUDE.legacy.md",
                              "--exclude-path", "worker/README.legacy.md", "./**/*.md"],
             root, optional=True, why="markdown links (CI parity; skipped when lychee is not installed)"),
    ]

    # 3. The three big suites (full mode only).
    gates += [
        Gate("scripts-tests",
             ["uv", "run", "--with", "pytest", "--with", "pyyaml", "python", "-m", "pytest", "scripts/tests", "-q"],
             root, tier="full", pytest=True, why="the guards' own tests; pyyaml is not in the worker venv"),
        Gate("worker-pytest", [py, "-m", "pytest", "worker/tests", "-m", "not gpu", "-q"], root, tier="full",
             timeout=LONG_TIMEOUT, pytest=True, why="worker suite, no GPU"),
    ]

    # 4. dotnet: always with app/ as the working directory.
    app = root / "app"
    if app.is_dir():
        gates.append(Gate("dotnet-format", ["dotnet", "format", "--verify-no-changes", "--no-restore"], app,
                          forgive=_dotnet_format_forgive,
                          why="C# formatting (cwd=app/); ENDOFLINE-only failures on an autocrlf checkout are KNOWN (#462)"))
        for project in discover_test_projects(root):
            project_name = project.parent.name
            if project_name in EXEMPT_TEST_PROJECTS:
                continue
            rel = project.relative_to(app).as_posix()
            short = project_name.removeprefix("DnaEntropyGraph.")
            is_guards = short == "Guards.Tests"
            gates.append(Gate(f"dotnet-test-{short}", ["dotnet", "test", rel], app,
                              tier="fast" if is_guards else "full", timeout=LONG_TIMEOUT,
                              why="mechanical C# guards" if is_guards else "C# suite (cwd=app/)"))
    return gates


def select(gates: Sequence[Gate], full: bool, only: Sequence[str], skip: Sequence[str]) -> list[Gate]:
    names = {g.name for g in gates}
    unknown = [n for n in [*only, *skip] if n not in names]
    if unknown:
        raise ValueError(f"unknown gate name(s): {', '.join(unknown)} (see --list)")
    chosen = [g for g in gates if (full or g.tier == "fast")]
    if only:
        chosen = [g for g in gates if g.name in only]
    chosen = [g for g in chosen if g.name not in skip]
    if not chosen:
        raise ValueError("no gates selected; refusing to report a pass for running nothing")
    return chosen


# ---------------------------------------------------------------------------
# Audit: a gate added to CI but not here must go red
# ---------------------------------------------------------------------------


def covered_scripts(gates: Sequence[Gate]) -> set[str]:
    covered: set[str] = set()
    for gate in gates:
        for arg in gate.argv:
            for ref in SCRIPT_REF.findall(arg.replace("\\", "/")):
                covered.add(ref)
    return covered


def audit(root: Path, gates: Sequence[Gate]) -> list[str]:
    covered = covered_scripts(gates)
    problems: list[str] = []
    workflows = root / ".github" / "workflows"
    seen: dict[str, str] = {}
    if workflows.is_dir():
        for wf in sorted(workflows.glob("*.yml")):
            # Full-line YAML comments are prose, not steps: a comment naming a script is not a gate.
            code = "\n".join(
                line for line in wf.read_text(encoding="utf-8").splitlines() if not line.lstrip().startswith("#")
            )
            for ref in SCRIPT_REF.findall(code):
                seen.setdefault(ref, wf.name)
    for ref, wf_name in sorted(seen.items()):
        if not (root / ref).is_file():
            problems.append(f"{wf_name} references {ref}, which does not exist")
        elif ref not in covered and ref not in NOT_A_GATE:
            problems.append(
                f"{wf_name} runs {ref}, but premerge.py does not: add a gate in build_gates() "
                f"or an entry with a reason in NOT_A_GATE"
            )
    return problems


# ---------------------------------------------------------------------------
# Running
# ---------------------------------------------------------------------------


def _resolve_exe(exe: str) -> str | None:
    if os.path.isabs(exe) or "/" in exe or "\\" in exe:
        return exe if Path(exe).is_file() else None
    return shutil.which(exe)


_PYTEST_COUNTS = re.compile(r"(\d+) (failed|errors?)\b")
_PYTEST_PASSED = re.compile(r"(\d+) passed\b")
_PYTEST_FAILURE_LINE = re.compile(r"^(FAILED|ERROR) \S", re.MULTILINE)
_PYTEST_PROGRESS_DONE = re.compile(r"\[100%\]")


def classify_pytest(returncode: int, output: str) -> tuple[str, str]:
    """(status, detail) for a finished pytest process. The verdict follows the test outcome, so a
    crash after green tests is never reported as a failing test, and never as a pass."""
    counts = _PYTEST_COUNTS.search(output)
    failed_tests = bool(counts) or bool(_PYTEST_FAILURE_LINE.search(output))
    if returncode == 0:
        return PASS, ""
    if failed_tests:
        return FAIL, counts.group(0) if counts else f"test failures (pytest exit {returncode})"
    saw_passes = bool(_PYTEST_PASSED.search(output)) or bool(_PYTEST_PROGRESS_DONE.search(output))
    if saw_passes:
        return ERROR, (
            f"pytest reported only passing tests but exited {returncode} afterwards (died in teardown or an "
            "internal error); NOT a test failure, NOT a pass: read the log tail"
        )
    if returncode == 5:
        return ERROR, "pytest collected no tests (exit 5)"
    return ERROR, f"pytest exited {returncode} without a test summary (could not run or crashed before finishing)"


_DOTNET_DIAGNOSTIC = re.compile(r"\(\d+,\d+\)\s*:\s*(?:(?:error|warning)\s+)?([A-Za-z][A-Za-z0-9_]*)\s*:")
ISSUE_ENDOFLINE = 462


def _autocrlf_is_true(cwd: Path) -> bool:
    try:
        proc = subprocess.run(["git", "config", "--get", "core.autocrlf"], cwd=cwd, capture_output=True, check=False,
                              timeout=15)
    except (OSError, subprocess.SubprocessError):
        return False
    return proc.returncode == 0 and proc.stdout.decode("utf-8", errors="replace").strip().lower() == "true"


def forgive_endofline(output: str, returncode: int, autocrlf_true: bool) -> str | None:
    """A note when the whole failure is ENDOFLINE on an autocrlf checkout (#462), else None."""
    if returncode == 0 or not autocrlf_true:
        return None
    ids = _DOTNET_DIAGNOSTIC.findall(output)
    if ids and set(ids) == {"ENDOFLINE"}:
        return f"{len(ids)} ENDOFLINE finding(s) ignored: core.autocrlf=true on this checkout (#{ISSUE_ENDOFLINE})"
    return None


def _dotnet_format_forgive(output: str, returncode: int, cwd: Path) -> str | None:
    return forgive_endofline(output, returncode, _autocrlf_is_true(cwd))


def run_gate(gate: Gate, env: dict[str, str] | None = None, basetemp_root: Path | None = None) -> Result:
    started = time.monotonic()
    resolved = _resolve_exe(gate.argv[0])
    if resolved is None:
        if gate.optional:
            return Result(gate, SKIP, 0.0, detail=f"{gate.argv[0]} is not installed; this gate did NOT run")
        hint = ""
        if "python" in Path(gate.argv[0]).name.lower():
            hint = " (create it: cd worker; uv venv --python 3.12; uv pip install -e \".[dev,genes]\")"
        return Result(gate, ERROR, 0.0, detail=f"cannot run: {gate.argv[0]} not found{hint}")
    child_env = dict(os.environ if env is None else env)
    child_env.setdefault("PYTHONUTF8", "1")
    child_env.setdefault("PYTHONIOENCODING", "utf-8")
    argv = [resolved, *gate.argv[1:]]
    if gate.pytest and basetemp_root is not None:
        argv += ["--basetemp", str(basetemp_root / gate.name), "-p", "no:cacheprovider"]
    try:
        proc = subprocess.run(
            argv,
            cwd=gate.cwd,
            capture_output=True,
            timeout=gate.timeout,
            env=child_env,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        partial = (exc.stdout or b"") + (exc.stderr or b"")
        text = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) else str(partial)
        return Result(gate, ERROR, time.monotonic() - started, text, f"timed out after {gate.timeout:.0f}s")
    except OSError as exc:
        return Result(gate, ERROR, time.monotonic() - started, detail=f"could not start: {exc}")
    text = (proc.stdout + proc.stderr).decode("utf-8", errors="replace")
    seconds = time.monotonic() - started
    if gate.pytest:
        status, detail = classify_pytest(proc.returncode, text)
        return Result(gate, status, seconds, text, detail)
    if proc.returncode == 0:
        return Result(gate, PASS, seconds, text)
    if gate.forgive is not None:
        note = gate.forgive(text, proc.returncode, gate.cwd)
        if note:
            return Result(gate, KNOWN, seconds, text, note)
    return Result(gate, FAIL, seconds, text, f"exit code {proc.returncode}")


def _ascii(text: str) -> str:
    return text.encode("ascii", errors="replace").decode("ascii")


def render_summary(results: Sequence[Result], tail_lines: int = 25) -> tuple[str, int]:
    bad = [r for r in results if r.status in (FAIL, ERROR)]
    skipped = [r for r in results if r.status == SKIP]
    known = [r for r in results if r.status == KNOWN]
    lines: list[str] = []
    for r in bad:
        lines.append("")
        lines.append(f"--- {r.status}: {r.gate.name} ({r.detail}) ---")
        lines.append(f"    run: (cd {r.gate.cwd}) {' '.join(r.gate.argv)}")
        for out_line in r.output.splitlines()[-tail_lines:]:
            lines.append(f"    | {out_line}")
    lines.append("")
    lines.append("=" * 72)
    for r in results:
        lines.append(f"  {r.status:<5} {r.gate.name:<34} {r.seconds:6.1f}s  {r.detail if r.status != PASS else ''}".rstrip())
    lines.append("=" * 72)
    passed = sum(1 for r in results if r.status == PASS)
    if skipped:
        lines.append("SKIPPED (did not run): " + ", ".join(r.gate.name for r in skipped))
    for r in known:
        lines.append(f"KNOWN ISSUE (forgiven, not clean): {r.gate.name}: {r.detail}")
    if bad:
        failed = [r.gate.name for r in bad if r.status == FAIL]
        errored = [r.gate.name for r in bad if r.status == ERROR]
        if failed:
            lines.append("FAILED gates: " + ", ".join(failed))
        if errored:
            lines.append("ERRORED gates (could not run): " + ", ".join(errored))
        lines.append(f"PREMERGE: FAIL - {len(bad)} of {len(results)} gate(s) not green, {passed} passed")
        return _ascii("\n".join(lines)), 1
    extras = (f", {len(skipped)} skipped" if skipped else "") + (f", {len(known)} known issue(s)" if known else "")
    lines.append(f"PREMERGE: PASS - {passed} gate(s) green{extras}")
    return _ascii("\n".join(lines)), 0


def run_all(gates: Sequence[Gate], log_dir: Path | None = None,
            runner: Callable[[Gate], Result] | None = None, emit: Callable[[str], None] = print
            ) -> tuple[list[Result], str, int]:
    """Run the gates in order. Pytest gates each get a private --basetemp: under `log_dir` when given,
    else a temp dir created here and removed at the end (the shared default basetemp raced; see the
    module docstring)."""
    own_tmp: Path | None = None
    if runner is None:
        if log_dir is not None:
            basetemp_root = log_dir / "basetemp"
        else:
            own_tmp = Path(tempfile.mkdtemp(prefix="premerge-basetemp-"))
            basetemp_root = own_tmp
        basetemp_root.mkdir(parents=True, exist_ok=True)

        def runner(gate: Gate) -> Result:  # noqa: F811 - the default runner closes over the basetemp root
            return run_gate(gate, basetemp_root=basetemp_root)

    results: list[Result] = []
    try:
        results = _run_each(gates, runner, emit, log_dir)
    finally:
        if own_tmp is not None:
            shutil.rmtree(own_tmp, ignore_errors=True)
    summary, code = render_summary(results)
    if log_dir is not None:
        (log_dir / "summary.txt").write_text(summary + "\n", encoding="utf-8", newline="\n")
    return results, summary, code


def _run_each(gates: Sequence[Gate], runner: Callable[[Gate], Result], emit: Callable[[str], None],
              log_dir: Path | None) -> list[Result]:
    results: list[Result] = []
    for index, gate in enumerate(gates, 1):
        emit(f"[{index}/{len(gates)}] {gate.name} ...")
        result = runner(gate)
        results.append(result)
        emit(_ascii(f"      -> {result.status} ({result.seconds:.1f}s) {result.detail}".rstrip()))
        if log_dir is not None:
            log_dir.mkdir(parents=True, exist_ok=True)
            (log_dir / f"{gate.name}.log").write_text(result.output, encoding="utf-8", newline="\n")
    return results


# ---------------------------------------------------------------------------
# Self-test: stub gates only, so it never touches the real suites
# ---------------------------------------------------------------------------


def _stub(name: str, code: str, *, optional: bool = False, timeout: float = 30.0, exe: str | None = None) -> Gate:
    argv = [exe] if exe else [sys.executable, "-c", code]
    return Gate(name, argv, Path.cwd(), optional=optional, timeout=timeout)


def self_test() -> bool:
    ok = True

    def expect(label: str, condition: bool, extra: str = "") -> None:
        nonlocal ok
        if condition:
            print(f"ok    {label}")
        else:
            print(f"FAIL: {label} {extra}")
            ok = False

    passing = _stub("stub-pass", "import sys; sys.exit(0)")
    failing = _stub("stub-fail", "import sys; print('guard says no: planted'); sys.exit(3)")
    missing = _stub("stub-error", "", exe="definitely-not-a-real-executable-xyz")
    slow = _stub("stub-timeout", "import time; time.sleep(30)", timeout=0.5)
    optional_missing = _stub("stub-optional", "", optional=True, exe="definitely-not-a-real-executable-xyz")

    silent: Callable[[str], None] = lambda _msg: None  # noqa: E731
    results, summary, code = run_all([passing, failing, missing, slow, optional_missing], emit=silent)
    status = {r.gate.name: r.status for r in results}
    expect("a gate exiting 0 is PASS", status["stub-pass"] == PASS, str(status))
    expect("a gate that ran and exited nonzero is FAIL, not ERROR", status["stub-fail"] == FAIL, str(status))
    expect("a gate whose tool is missing is ERROR, not FAIL", status["stub-error"] == ERROR, str(status))
    expect("a gate that times out is ERROR", status["stub-timeout"] == ERROR, str(status))
    expect("a missing optional tool is SKIP", status["stub-optional"] == SKIP, str(status))
    expect("any FAIL or ERROR makes the exit code 1", code == 1)
    expect("the summary names the failed gate", "FAILED gates: stub-fail" in summary, summary)
    expect("the summary names the errored gate separately", "stub-error" in summary and "ERRORED" in summary)
    expect("the failing gate's own output is shown", "guard says no: planted" in summary)
    expect("a skipped gate is announced, never silent", "SKIPPED (did not run): stub-optional" in summary)

    _, ok_summary, ok_code = run_all([passing], emit=silent)
    expect("all gates green exits 0 and says PASS", ok_code == 0 and "PREMERGE: PASS" in ok_summary)

    expect("an ERROR is not reported as FAILED", "FAILED gates: stub-fail" in summary and "FAILED gates: stub-fail, stub-error" not in summary)

    try:
        select([passing], full=True, only=["typo"], skip=[])
        expect("an unknown --only name is refused", False)
    except ValueError:
        expect("an unknown --only name is refused", True)
    try:
        select([passing], full=False, only=[], skip=["stub-pass"])
        expect("selecting zero gates is refused", False)
    except ValueError:
        expect("selecting zero gates is refused", True)

    # Pytest gates: the verdict follows the test outcome (the 2026-10-02 shared-basetemp teardown crash).
    def pytest_stub(name: str, body: str) -> Gate:
        return Gate(name, [sys.executable, "-c", body], Path.cwd(), pytest=True, timeout=30.0)

    crash_after_green = pytest_stub(
        "pt-crash",
        "import sys\nprint('.' * 10 + ' [100%]')\nprint('Traceback (most recent call last):', file=sys.stderr)\n"
        "print('PermissionError: [WinError 5] pytest-current', file=sys.stderr)\nsys.exit(1)",
    )
    real_failure = pytest_stub(
        "pt-fail", "import sys\nprint('FAILED tests/test_x.py::test_y - assert 1 == 2')\nprint('1 failed, 9 passed in 0.5s')\nsys.exit(1)"
    )
    clean = pytest_stub("pt-pass", "print('10 passed in 0.5s')")
    no_summary = pytest_stub("pt-nosummary", "import sys\nprint('INTERNALERROR> boom')\nsys.exit(3)")
    echo_argv = pytest_stub("pt-argv", "import sys\nprint('ARGV', ' '.join(sys.argv[1:]))")
    other_echo = Gate("pt-argv2", [sys.executable, "-c", "import sys\nprint('ARGV', ' '.join(sys.argv[1:]))"], Path.cwd(),
                      pytest=True, timeout=30.0)
    results, summary, code = run_all([crash_after_green, real_failure, clean, no_summary], emit=silent)
    pstatus = {r.gate.name: r.status for r in results}
    expect("a pytest that printed 100% then died is ERROR, not FAIL", pstatus["pt-crash"] == ERROR, str(pstatus))
    expect("... and not PASS either (exit code is 1)", code == 1 and pstatus["pt-crash"] != PASS)
    expect("... and the reason says it was not a test failure", "NOT a test failure" in summary, summary)
    expect("a pytest with a failed test is FAIL", pstatus["pt-fail"] == FAIL, str(pstatus))
    expect("a pytest with passes only and exit 0 is PASS", pstatus["pt-pass"] == PASS, str(pstatus))
    expect("a pytest with no summary at all is ERROR", pstatus["pt-nosummary"] == ERROR, str(pstatus))
    with tempfile.TemporaryDirectory() as logs:
        results, _, _ = run_all([echo_argv, other_echo], log_dir=Path(logs), emit=silent)
        seen = [next(line for line in r.output.splitlines() if line.startswith("ARGV")) for r in results]
        bases = [line.split("--basetemp ")[1].split(" -p")[0] for line in seen]
        expect("each pytest gate gets its own --basetemp", len(set(bases)) == 2 and all(b for b in bases), str(seen))
        expect("... under the log dir when one is given", all(Path(b).parent == Path(logs) / "basetemp" for b in bases), str(bases))
        expect("... with the cache provider off", all("no:cacheprovider" in s for s in seen))

    eol_only = (
        "src/A.cs(1,1): error ENDOFLINE: Fix end of line marker. Replace 2 characters with '\\n'. [x.csproj]\n"
        "src/B.cs(9,1): error ENDOFLINE: Fix end of line marker. [x.csproj]\n"
    )
    mixed = eol_only + "src/C.cs(4,5): error IDE0055: Fix formatting. [x.csproj]\n"
    expect("ENDOFLINE-only on an autocrlf checkout is forgiven by name",
           forgive_endofline(eol_only, 2, True) is not None and "#462" in (forgive_endofline(eol_only, 2, True) or ""))
    expect("ENDOFLINE-only with autocrlf off is NOT forgiven", forgive_endofline(eol_only, 2, False) is None)
    expect("ENDOFLINE mixed with a real finding is NOT forgiven", forgive_endofline(mixed, 2, True) is None)
    expect("an unparseable failure is NOT forgiven", forgive_endofline("Unhandled exception: boom", 1, True) is None)
    expect("a clean run needs no forgiveness", forgive_endofline("", 0, True) is None)
    known_gate = Gate("stub-known", [sys.executable, "-c", f"import sys; sys.stdout.write({eol_only!r}); sys.exit(2)"],
                      Path.cwd(), forgive=lambda out, rc, cwd: forgive_endofline(out, rc, True), timeout=30.0)
    results, ksummary, kcode = run_all([known_gate], emit=silent)
    expect("a forgiven gate is KNOWN, exits 0 and is printed with its issue",
           results[0].status == KNOWN and kcode == 0 and "KNOWN ISSUE" in ksummary and "#462" in ksummary, ksummary)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "scripts").mkdir()
        (root / ".github" / "workflows").mkdir(parents=True)
        (root / "app" / "tests" / "DnaEntropyGraph.Fake.Tests").mkdir(parents=True)
        (root / "app" / "tests" / "DnaEntropyGraph.Fake.Tests" / "DnaEntropyGraph.Fake.Tests.csproj").write_text(
            "<Project/>", encoding="utf-8", newline="\n")
        (root / "scripts" / "check_new_guard.py").write_text("print('x')\n", encoding="utf-8", newline="\n")
        (root / "scripts" / "unlisted_tool.py").write_text("print('x')\n", encoding="utf-8", newline="\n")
        (root / ".github" / "workflows" / "ci.yml").write_text(
            "run: python scripts/unlisted_tool.py\nrun: python scripts/check_new_guard.py\n",
            encoding="utf-8", newline="\n")
        gates = build_gates(root, python="py-stub")
        names = {g.name for g in gates}
        expect("a newly added check_*.py is picked up with no edit here", "check_new_guard" in names, str(names))
        expect("a newly added dotnet test project is picked up", "dotnet-test-Fake.Tests" in names, str(names))
        expect("every dotnet gate runs with cwd=app/",
               all(g.cwd == root / "app" for g in gates if g.argv[0] == "dotnet"))
        expect("python gates use the interpreter given, never sys.executable",
               all(g.argv[0] == "py-stub" for g in gates if g.name.startswith("check_")))
        problems = audit(root, gates)
        expect("audit flags a workflow script the runner does not run",
               any("unlisted_tool.py" in p for p in problems), str(problems))
        expect("audit accepts a discovered check_ script", not any("check_new_guard" in p for p in problems))

    print(f"\n{'PASS' if ok else 'FAIL'}: premerge self-test")
    return ok


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def _write_stamp(root: Path, mode: str) -> None:
    """Record a green, UNFILTERED run for the require_premerge_before_pr hook (#451). A partial run
    (--only/--skip) proves nothing about the rest, so main() never calls this for one."""
    import premerge_stamp  # lazy: --self-test and --list need nothing from it

    try:
        print(f"stamp: {premerge_stamp.write_stamp(root, mode)}")
    except (RuntimeError, OSError, ValueError) as exc:
        print(f"NOTE: could not write the premerge stamp ({exc}); the PR hook will not see this run")


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Run every pre-merge gate and print one pass/fail summary.")
    ap.add_argument("--fast", action="store_true", help="skip the big suites (scripts tests, worker pytest, C# suites)")
    ap.add_argument("--list", action="store_true", help="list the gates that would run and exit")
    ap.add_argument("--only", action="append", default=[], metavar="GATE", help="run only this gate (repeatable)")
    ap.add_argument("--skip", action="append", default=[], metavar="GATE", help="skip this gate (repeatable)")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent, help="repo root")
    ap.add_argument("--base", default=DEFAULT_BASE, help="ref the changelog-fragment presence check compares against")
    ap.add_argument("--python", default=None, help="interpreter for Python gates (default: worker/.venv)")
    ap.add_argument("--log-dir", type=Path, default=None, help="also write each gate's full output and summary.txt here")
    ap.add_argument("--self-test", action="store_true", help="prove the runner reports PASS/FAIL/ERROR correctly")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    root = args.root.resolve()
    if not (root / "scripts").is_dir():
        print(f"ERROR: {root} does not look like the repo root (no scripts/).", file=sys.stderr)
        return 2
    gates = build_gates(root, args.python, args.base)
    problems = audit(root, gates)
    try:
        chosen = select(gates, full=not args.fast, only=args.only, skip=args.skip)
    except ValueError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    if args.list:
        for gate in gates:
            mark = "*" if gate in chosen else " "
            print(f"{mark} {gate.tier:<4} {gate.name:<34} {gate.why}")
        print("\n* = selected for this invocation.")
        for ref, why in NOT_A_GATE.items():
            print(f"  not a gate: {ref} ({why})")
        for proj, why in EXEMPT_TEST_PROJECTS.items():
            print(f"  exempt test project: {proj} ({why})")
        for problem in problems:
            print(f"AUDIT: {problem}")
        return 1 if problems else 0

    if not (root / "app").is_dir():
        print("NOTE: app/ is absent, so no dotnet gate is registered.")
    for problem in problems:
        print(f"AUDIT: {problem}")
    mode = "fast" if args.fast else "full"
    print(f"premerge ({mode}): {len(chosen)} gate(s), python for guards: {venv_python_or_marker(root, args.python)}")
    _, summary, code = run_all(chosen, log_dir=args.log_dir)
    print(summary)
    if problems:
        print(f"PREMERGE: AUDIT FAILED - {len(problems)} workflow script(s) not covered by this runner")
        return 1
    if code == 0 and not args.only and not args.skip:
        _write_stamp(root, mode)
    return code


if __name__ == "__main__":
    sys.exit(main())
