"""Every check_*.py that scans the repo must scan the repo the script lives in,
not whatever directory it happens to be launched from.

MEASURED 2026-10-02: `python <worktree>/scripts/check_app_wiring.py` run from the
shared checkout silently scanned the shared tree (default --root was the
cwd-relative "app"). Each guard here is run with cwd set to an unrelated empty
directory and must (a) behave as it does from the repo root, (b) not report that
it checked nothing, (c) not mention the foreign directory.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent

# check_*.py with no repo-root default to test, or that need dotnet + a populated venv.
SKIPPED = {
    "check_third_party_notices.py": "needs dotnet restore of app/ and worker/.venv extras (tests/conftest.py)",
}

GUARDS = sorted(p.name for p in SCRIPTS_DIR.glob("check_*.py"))

NOTHING_PHRASES = ("checked nothing", "does not exist", "not found", "not a git repository",
                   "does not look like this repo", "no .git")


def _run(script: str, cwd: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / script)],
        cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300,
    )


def test_guard_list_is_not_vacuous() -> None:
    assert len(GUARDS) >= 12, GUARDS


@pytest.mark.parametrize("script", GUARDS)
def test_guard_scans_its_own_repo_from_a_foreign_cwd(script: str, tmp_path: Path) -> None:
    if script in SKIPPED:
        pytest.skip(SKIPPED[script])
    foreign = tmp_path / "unrelated"
    foreign.mkdir()

    home = _run(script, REPO_ROOT)
    away = _run(script, foreign)

    out_away = (away.stdout + away.stderr)
    out_home = (home.stdout + home.stderr)
    low = out_away.lower()
    for phrase in NOTHING_PHRASES:
        assert phrase not in low, f"{script} from a foreign cwd says {phrase!r}:\n{out_away}"
    assert str(foreign).lower() not in low, f"{script} mentions the foreign cwd:\n{out_away}"
    assert out_away == out_home, (
        f"{script}: output differs by cwd\n--- foreign:\n{out_away}\n--- home:\n{out_home}"
    )
    assert away.returncode == home.returncode, (
        f"{script}: exit {away.returncode} from a foreign cwd vs {home.returncode} at the repo root\n"
        f"--- foreign:\n{out_away}\n--- home:\n{out_home}"
    )
