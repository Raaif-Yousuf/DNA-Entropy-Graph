"""scripts/ci_changes_gate.py is fail-CLOSED (#31), and its irrelevant-path list never hides a path a test or a CI step reads.

Skipped jobs count as passing for branch protection, so the gate may only skip when it is SURE every changed path is
irrelevant to the workflow. These tests pin: (1) the classification, exactly (a path is irrelevant only if it matches a
short known-irrelevant rule and no relevant exception); (2) any git failure runs the jobs; (3) every repo path the
workflow's tests and its own `run:` lines read is classified relevant. MEASURED 2026-10-03 (round-2 review): a prefix
check let `docs/contract/` stand in for `docs/copy_catalog.md`, so deleting the latter from the gate stayed green.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).resolve().parent.parent
REPO = SCRIPTS.parent
sys.path.insert(0, str(SCRIPTS))

import ci_changes_gate as gate  # noqa: E402

WORKFLOWS = ("ci-app", "ci-worker")


@pytest.mark.parametrize("workflow", WORKFLOWS)
@pytest.mark.parametrize(
    "path",
    ["README.md", "CLAUDE.md", "NEXT_SESSION.md", "docs/architecture.md", "docs/changelog.d/x.md", ".claude/skills/a/SKILL.md", "LICENSE"],
)
def test_known_irrelevant_paths_are_skippable(workflow, path):
    assert gate.is_relevant(workflow, path) is False, path


@pytest.mark.parametrize("workflow", WORKFLOWS)
@pytest.mark.parametrize(
    "path",
    [
        "app/src/a.cs", "worker/src/b.py", "tests/contract-fixtures/x.json", "scripts/gen_manifest_schema.py",
        ".editorconfig", ".gitattributes", "global.json", "docs/contract/error-codes.json", "docs/copy_catalog.md",
        "docs/contract/sub/new.json", "THIRD-PARTY-NOTICES.md", "something/unknown.txt", "Makefile", ".github/actions/x/action.yml",
    ],
)
def test_everything_else_and_every_exception_is_relevant(workflow, path):
    assert gate.is_relevant(workflow, path) is True, path


def test_only_the_workflows_own_file_is_relevant_among_workflow_files():
    assert gate.is_relevant("ci-app", ".github/workflows/ci-app.yml") is True
    assert gate.is_relevant("ci-app", ".github/workflows/ci-docs.yml") is False
    assert gate.is_relevant("ci-worker", ".github/workflows/ci-worker.yml") is True
    assert gate.is_relevant("ci-worker", ".github/workflows/ci-app.yml") is False


def test_decide_runs_when_any_path_is_relevant_and_skips_only_when_all_are_irrelevant():
    assert gate.decide("ci-app", ["README.md", "docs/a.md"]) is False
    assert gate.decide("ci-app", ["README.md", "app/x.cs"]) is True
    assert gate.decide("ci-app", []) is False


def _git(root: Path, *args: str) -> None:
    subprocess.run(["git", *args], cwd=root, check=True, capture_output=True)


def test_a_git_failure_runs_the_jobs_with_a_warning(tmp_path):
    out = tmp_path / "out.txt"
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS / "ci_changes_gate.py"), "--workflow", "ci-app", "--event", "pull_request",
         "--base", "0" * 40, "--root", str(tmp_path), "--output", str(out)],
        capture_output=True, text=True, check=False,
    )
    assert proc.returncode == 0, proc.stderr
    assert out.read_text(encoding="utf-8").strip() == "run=true"
    assert "::warning" in proc.stdout


def test_a_rename_out_of_app_is_seen_because_renames_are_not_collapsed(tmp_path):
    _git(tmp_path, "init", "-q")
    _git(tmp_path, "config", "user.email", "t@example.com")
    _git(tmp_path, "config", "user.name", "t")
    (tmp_path / "app").mkdir()
    (tmp_path / "app" / "a.cs").write_text("class A {}\n" * 20, encoding="utf-8")
    _git(tmp_path, "add", "-A")
    _git(tmp_path, "commit", "-q", "-m", "base")
    base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=tmp_path, capture_output=True, text=True, check=True).stdout.strip()
    (tmp_path / "docs").mkdir()
    _git(tmp_path, "mv", "app/a.cs", "docs/a.md")
    _git(tmp_path, "commit", "-q", "-m", "move")
    out = tmp_path / "out.txt"
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS / "ci_changes_gate.py"), "--workflow", "ci-app", "--event", "pull_request",
         "--base", base, "--root", str(tmp_path), "--output", str(out)],
        capture_output=True, text=True, check=False,
    )
    assert proc.returncode == 0, proc.stderr
    assert out.read_text(encoding="utf-8").strip() == "run=true", "the deleted app/a.cs half of the rename must trigger the run"


def test_a_non_pull_request_event_always_runs(tmp_path):
    out = tmp_path / "out.txt"
    subprocess.run(
        [sys.executable, str(SCRIPTS / "ci_changes_gate.py"), "--workflow", "ci-app", "--event", "workflow_dispatch",
         "--base", "", "--root", str(tmp_path), "--output", str(out)],
        check=True, capture_output=True,
    )
    assert out.read_text(encoding="utf-8").strip() == "run=true"


# --- every path the tests or the workflow's own steps read must be relevant ---------------------------------------------

_QUOTED = re.compile(r'"([^"\\]+)"')


def _literal_paths_from_cs() -> set[str]:
    found: set[str] = set()
    files = [p for p in (REPO / "app" / "tests").rglob("*.cs") if "obj" not in p.parts and "bin" not in p.parts]
    assert len(files) > 20, "vacuity guard: too few app test files"
    for path in files:
        for line in path.read_text(encoding="utf-8").splitlines():
            if "Path.Combine" in line or "Read(" in line or "File." in line:
                _add_joined(found, _QUOTED.findall(line))
    return found


def _literal_paths_from_py() -> set[str]:
    found: set[str] = set()
    files = list((REPO / "worker" / "tests").rglob("*.py"))
    assert len(files) > 20, "vacuity guard: too few worker test files"
    for path in files:
        for line in path.read_text(encoding="utf-8").splitlines():
            if "parents[2]" in line or "parent.parent.parent" in line:
                _add_joined(found, re.findall(r'/ "([^"]+)"', line))
    return found


def _add_joined(found: set[str], segments: list[str]) -> None:
    """Join literal segments (also each segment containing '/') and keep the longest prefix that exists in the repo."""
    parts: list[str] = []
    for s in segments:
        parts.extend(p for p in s.split("/") if p)
    for end in range(len(parts), 0, -1):
        for start in range(0, len(parts) - end + 1):
            candidate = "/".join(parts[start:start + end])
            target = REPO / candidate
            # A lone directory name ("docs", "tests") is noise from joining; a file or a nested directory is a real read.
            real = target.is_file() or (target.is_dir() and "/" in candidate)
            if real and not candidate.startswith(("app/", "worker/")):
                found.add(candidate)
                break


def _run_line_paths(workflow: str) -> set[str]:
    raw = (REPO / ".github" / "workflows" / f"{workflow}.yml").read_text(encoding="utf-8")
    # Comments only mention paths ("see docs/ToTest.md."); the trailing dot also made Path.exists() lie on Windows.
    text = "\n".join(line for line in raw.splitlines() if not line.lstrip().startswith("#"))
    found: set[str] = set()
    for token in re.findall(r"[\w./-]+/[\w./-]+", text):
        token = token.strip("./") if token.startswith("./") else token
        if (REPO / token).exists() and not token.startswith((".github/workflows/", "app/", "worker/")):
            found.add(token)
    return found


IMPLICIT_READS = {
    # dotnet format and -warnaserror read these, and `working-directory: app` makes dotnet read app/global.json.
    "ci-app": [".editorconfig", ".gitattributes", "app/global.json", "app/Directory.Build.props", "app/Directory.Packages.props"],
    "ci-worker": ["worker/pyproject.toml", "worker/uv.lock"],
}


@pytest.mark.parametrize("workflow", WORKFLOWS)
def test_every_path_the_tests_and_steps_read_is_classified_relevant(workflow):
    own_tests = _literal_paths_from_cs() if workflow == "ci-app" else _literal_paths_from_py()
    reads = own_tests | _run_line_paths(workflow) | set(IMPLICIT_READS[workflow])
    expected_known = {"ci-app": {"docs/contract/error-codes.json"}, "ci-worker": {"docs/copy_catalog.md"}}[workflow]
    assert expected_known <= reads, f"vacuity guard: the scanner no longer finds {expected_known - reads}; found {sorted(reads)}"
    # A directory read (a glob over docs/contract) is checked through a file that would live in it.
    probes = {p: (f"{p}/x" if (REPO / p).is_dir() else p) for p in reads}
    hidden = sorted(p for p, probe in probes.items() if not gate.is_relevant(workflow, probe))
    assert not hidden, f"{workflow} would SKIP (= pass) a PR that edits only {hidden}, which its tests or steps read"


def test_each_workflow_calls_the_gate_script_not_an_inline_pattern():
    for workflow in WORKFLOWS:
        text = (REPO / ".github" / "workflows" / f"{workflow}.yml").read_text(encoding="utf-8")
        assert f"scripts/ci_changes_gate.py --workflow {workflow}" in text, workflow
        code = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("#"))
        assert "grep -Eq" not in code, "the fail-open inline grep gate is gone"


def test_a_pull_request_with_no_base_sha_runs_the_jobs(tmp_path):
    out = tmp_path / "out.txt"
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS / "ci_changes_gate.py"), "--workflow", "ci-app", "--event", "pull_request",
         "--base", "", "--root", str(tmp_path), "--output", str(out)],
        capture_output=True, text=True, check=False,
    )
    assert proc.returncode == 0, proc.stderr
    assert out.read_text(encoding="utf-8").strip() == "run=true"
    assert "::warning" in proc.stdout


# --- the needs/if wiring: a broken gate must RUN the real jobs, never skip them (#31 round 2) -----------------------------
#
# GitHub semantics: a job whose `needs` did not succeed is SKIPPED unless its `if:` contains a status function
# (always(), cancelled(), failure(), success() explicitly); a skipped required job reads as green. So with the bare
# `if: needs.changes.outputs.run == 'true'` a failed `changes` job (outputs.run is empty) skipped every real job and the PR
# went green with nothing tested. The fix is `!cancelled() && (needs.changes.result != 'success' || ...run == 'true')`.

_STATUS_FUNCTIONS = re.compile(r"\b(always|cancelled|failure)\(\)")


def _jobs_needing_changes(workflow: str) -> dict[str, str]:
    """{job id: its `if:` expression ('' when absent)} for every top-level job with `needs: changes` (text parse, no yaml dep)."""
    lines = (REPO / ".github" / "workflows" / f"{workflow}.yml").read_text(encoding="utf-8").splitlines()
    jobs: dict[str, list[str]] = {}
    current = None
    in_jobs = False
    for line in lines:
        if re.match(r"^jobs:\s*$", line):
            in_jobs = True
        elif in_jobs and re.match(r"^\S", line):
            in_jobs = False
        elif in_jobs and (m := re.match(r"^  ([\w-]+):\s*$", line)):
            current = m.group(1)
            jobs[current] = []
        elif in_jobs and current is not None:
            jobs[current].append(line)
    found: dict[str, str] = {}
    for job, body in jobs.items():
        if any(re.match(r"^    needs:\s*(changes|\[\s*changes\s*\])\s*$", ln) for ln in body):
            expr = next((m.group(1).strip() for ln in body if (m := re.match(r"^    if:\s*(.*)$", ln))), "")
            found[job] = expr
    return found


def _evaluate(expr: str, result: str, run: str) -> bool:
    """Evaluate the small GitHub expression subset the workflows use, for a given `changes` result and `run` output."""
    body = re.sub(r"^\$\{\{\s*|\s*\}\}$", "", expr)
    py = body.replace("&&", " and ").replace("||", " or ")
    py = re.sub(r"!(?!=)", " not ", py)
    py = py.replace("needs.changes.result", repr(result)).replace("needs.changes.outputs.run", repr(run))
    py = py.replace("always()", "True").replace("cancelled()", "False").replace("failure()", "False")
    assert re.fullmatch(r"[\sA-Za-z'!=()]*", py), f"expression outside the supported subset: {expr!r} -> {py!r}"
    return bool(eval(py, {"__builtins__": {}}, {}))  # noqa: S307  (a closed-vocabulary expression from our own workflows)


@pytest.mark.parametrize("workflow", WORKFLOWS)
def test_every_job_gated_on_changes_runs_when_the_gate_itself_failed(workflow):
    jobs = _jobs_needing_changes(workflow)
    assert len(jobs) >= {"ci-app": 1, "ci-worker": 3}[workflow], f"vacuity guard: found only {sorted(jobs)}"
    for job, expr in jobs.items():
        assert _STATUS_FUNCTIONS.search(expr), (
            f"{workflow}:{job} has no status function in its if ({expr!r}), so a failed `changes` job SKIPS it and a skipped "
            "required job reads as green"
        )
        assert _evaluate(expr, "failure", "") is True, f"{workflow}:{job} is skipped when the changes job fails"
        assert _evaluate(expr, "success", "") is True, f"{workflow}:{job} is skipped when the gate output is missing or empty"
        assert _evaluate(expr, "success", "true") is True, f"{workflow}:{job} does not run when the gate says run"
        assert _evaluate(expr, "success", "false") is False, f"{workflow}:{job} runs although the gate said skip"


def test_the_gate_decision_is_case_insensitive():
    # Windows-authored paths can differ in case (Docs/Readme.md, APP/x.cs); a case miss must not turn into a skip or a run by luck.
    assert gate.is_relevant("ci-app", "Docs/Architecture.MD") is False
    assert gate.is_relevant("ci-app", "APP/src/A.cs") is True
    assert gate.is_relevant("ci-app", "Docs/Contract/x.json") is True
    assert gate.is_relevant("ci-app", "docs/COPY_CATALOG.md") is True
    assert gate.is_relevant("ci-app", "ReadMe.md") is False
    assert gate.is_relevant("ci-app", "third-party-notices.md") is True


@pytest.mark.parametrize("workflow", WORKFLOWS)
def test_the_changes_job_is_a_real_job_that_runs_the_gate_script(workflow):
    """MEASURED 2026-10-03 (cold review of 0cc0587): a replace glued `changes:` onto the end of a comment line, so the workflow
    had no changes job at all and the text-based wiring test above still passed. Pin the structure itself."""
    text = (REPO / ".github" / "workflows" / f"{workflow}.yml").read_text(encoding="utf-8")
    lines = text.splitlines()
    assert "  changes:" in lines, f"{workflow}: no line is exactly '  changes:' (a job key at 2-space indent on its own line)"
    glued = [ln for ln in lines if ln.lstrip().startswith("#") and re.search(r"\S\s{2,}[\w-]+:\s*$", ln)]
    assert not glued, f"{workflow}: a key is glued to the end of a comment line: {glued}"
    in_jobs = False
    for ln in lines:
        if re.match(r"^jobs:\s*$", ln):
            in_jobs = True
        elif in_jobs and ln.strip() and not ln.lstrip().startswith("#") and len(ln) - len(ln.lstrip()) == 2:
            assert re.match(r"^  [\w-]+:\s*$", ln), f"{workflow}: malformed job key line {ln!r}"
    start = lines.index("  changes:")
    end = next((i for i in range(start + 1, len(lines)) if re.match(r"^  [\w-]+:\s*$", lines[i])), len(lines))
    body = "\n".join(lines[start:end])
    assert f"scripts/ci_changes_gate.py --workflow {workflow}" in body, f"{workflow}: the changes job does not run the gate script"
    assert "needs: changes" in text