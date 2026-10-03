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
    text = (REPO / ".github" / "workflows" / f"{workflow}.yml").read_text(encoding="utf-8")
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
