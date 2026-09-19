"""Tests for scripts/check_guard_drift.py (issue #309)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_guard_drift as cgd  # noqa: E402


def _run(*args, cwd):
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


def _make_repo(tmp_path: Path) -> Path:
    repo = tmp_path / "repo"
    repo.mkdir()
    _run("init", "-q", cwd=repo)
    _run("config", "user.email", "test@example.com", cwd=repo)
    _run("config", "user.name", "Test", cwd=repo)
    return repo


def _commit_all(repo: Path, message: str) -> None:
    _run("add", "-A", cwd=repo)
    _run("commit", "-q", "-m", message, cwd=repo)


_SOFT_WORKFLOW = (
    "steps:\n"
    "  - name: guarded thing\n"
    "    run: |\n"
    "      if [ -f fixture/guarded.txt ]; then\n"
    "        echo real check\n"
    "      else\n"
    "        echo '::notice title=skipped::fixture/guarded.txt does not exist yet.'\n"
    "      fi\n"
)

_HARDENED_WORKFLOW = (
    "steps:\n"
    "  - name: guarded thing\n"
    "    run: echo real check unconditionally\n"
)

_FIXTURE_GUARD = cgd.GuardEntry(
    name="fixture guard",
    issue=0,
    guarded_file="fixture/guarded.txt",
    workflow_file=".github/workflows/fixture.yml",
    notice_marker="if [ -f fixture/guarded.txt ]",
)


def _write_workflow(repo: Path, text: str) -> None:
    workflows = repo / ".github" / "workflows"
    workflows.mkdir(parents=True, exist_ok=True)
    (workflows / "fixture.yml").write_text(text, encoding="utf-8")


def test_no_drift_when_guarded_file_not_yet_committed(tmp_path):
    repo = _make_repo(tmp_path)
    _write_workflow(repo, _SOFT_WORKFLOW)
    _commit_all(repo, "workflow only")
    assert cgd.check_drift(repo, guards=(_FIXTURE_GUARD,)) == []


def test_drift_when_guarded_file_lands_and_workflow_still_soft_gates(tmp_path):
    repo = _make_repo(tmp_path)
    _write_workflow(repo, _SOFT_WORKFLOW)
    (repo / "fixture").mkdir()
    (repo / "fixture" / "guarded.txt").write_text("here now\n", encoding="utf-8")
    _commit_all(repo, "guarded file lands, workflow not hardened")
    assert cgd.check_drift(repo, guards=(_FIXTURE_GUARD,)) == [_FIXTURE_GUARD]
    assert cgd.check(repo, guards=(_FIXTURE_GUARD,)) == 1


def test_no_drift_once_workflow_is_hardened_past_the_marker(tmp_path):
    repo = _make_repo(tmp_path)
    _write_workflow(repo, _HARDENED_WORKFLOW)
    (repo / "fixture").mkdir()
    (repo / "fixture" / "guarded.txt").write_text("here now\n", encoding="utf-8")
    _commit_all(repo, "guarded file lands, workflow already hardened")
    assert cgd.check_drift(repo, guards=(_FIXTURE_GUARD,)) == []
    assert cgd.check(repo, guards=(_FIXTURE_GUARD,)) == 0


def test_uncommitted_guarded_file_does_not_count_as_landed(tmp_path):
    """A file sitting on disk but never committed has not landed for this
    check's purposes -- it reads HEAD's tree, not the working tree, the same
    distinction issue_precheck.py's own scan draws for a different guard."""
    repo = _make_repo(tmp_path)
    _write_workflow(repo, _SOFT_WORKFLOW)
    _commit_all(repo, "workflow only")
    (repo / "fixture").mkdir()
    (repo / "fixture" / "guarded.txt").write_text("uncommitted\n", encoding="utf-8")
    assert cgd.check_drift(repo, guards=(_FIXTURE_GUARD,)) == []


def test_missing_workflow_file_is_not_a_crash_and_not_a_finding(tmp_path):
    repo = _make_repo(tmp_path)
    (repo / "fixture").mkdir()
    (repo / "fixture" / "guarded.txt").write_text("here now\n", encoding="utf-8")
    _commit_all(repo, "guarded file lands, no workflow file at all")
    assert cgd.check_drift(repo, guards=(_FIXTURE_GUARD,)) == []


def test_self_test_flag_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_guard_drift.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_guard_drift.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_cli_exits_2_when_root_is_not_a_repo(tmp_path):
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_guard_drift.py"), "--root", str(tmp_path)],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2


def test_real_manifest_against_this_repo_does_not_crash():
    """A smoke check on the real GUARDS manifest, not an assertion on
    today's live findings -- those change as issues #39/#45/#36/#61 close.
    As of the session that filed issue #309, three of the four real guards
    (schema, shellcheck, docker) were already committed with their workflow
    step still textually soft-gated, so this legitimately can (and, as of
    that session, does) return non-empty -- that is the check working, not
    the check being broken."""
    result = cgd.check_drift(REPO_ROOT)
    assert isinstance(result, list)
    assert all(isinstance(entry, cgd.GuardEntry) for entry in result)


def test_ruff_gate_is_not_in_the_manifest():
    """The ruff gate is a permanent "is [tool.ruff] configured" content
    check, not a one-time "has this file landed yet" gate, and as of this
    session worker/pyproject.toml already carries a real [tool.ruff] table
    with lint live -- see the module docstring's "WHAT IS DELIBERATELY NOT
    IN SCOPE" section for why it does not belong in GUARDS."""
    assert not any("ruff" in entry.name.lower() for entry in cgd.GUARDS)
