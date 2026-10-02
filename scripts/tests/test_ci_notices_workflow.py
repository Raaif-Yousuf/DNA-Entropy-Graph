"""Structure test for .github/workflows/ci-notices.yml (#416).

The job cannot be run locally (it needs a windows-latest runner with dotnet), so this pins what
makes it a REAL check rather than a vacuous one: both prerequisites are set up before the
checker runs, the venv is the locked one, and the checker runs on that venv's python.
"""

from __future__ import annotations

from pathlib import Path

import pytest

yaml = pytest.importorskip("yaml")

WORKFLOW = Path(__file__).resolve().parent.parent.parent / ".github" / "workflows" / "ci-notices.yml"


@pytest.fixture(scope="module")
def job():
    data = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
    assert list(data["jobs"]) == ["notices"]
    return data["jobs"]["notices"]


def _steps_text(job) -> list[str]:
    return [str(step.get("run", "")) + " " + str(step.get("uses", "")) for step in job["steps"]]


def _index(job, needle: str) -> int:
    for i, text in enumerate(_steps_text(job)):
        if needle in text:
            return i
    raise AssertionError(f"no step mentions {needle!r}")


def test_runs_on_windows_because_app_targets_windows(job):
    assert job["runs-on"] == "windows-latest"


def test_dotnet_comes_from_global_json(job):
    setup = next(s for s in job["steps"] if str(s.get("uses", "")).startswith("actions/setup-dotnet"))
    assert setup["with"]["global-json-file"] == "app/global.json"


def test_worker_venv_is_the_locked_one_with_the_dev_boxs_extras(job):
    step = job["steps"][_index(job, "uv sync")]
    run = step["run"]
    assert "--frozen" in run and "--extra dev" in run and "--extra genes" in run and "--extra evo" not in run
    assert step["working-directory"] == "worker"


def test_app_is_restored_inside_app(job):
    step = job["steps"][_index(job, "dotnet restore")]
    assert step["working-directory"] == "app"


def test_checker_runs_last_on_the_venv_python_after_both_prerequisites(job):
    check = _index(job, "check_third_party_notices.py\n") if False else len(job["steps"]) - 1
    last = job["steps"][check]["run"]
    assert "worker\\.venv\\Scripts\\python.exe" in last
    assert last.strip().endswith("check_third_party_notices.py")
    assert _index(job, "uv sync") < check
    assert _index(job, "dotnet restore") < check


def test_it_guards_against_the_vacuous_pass_and_runs_the_self_test(job):
    assert _index(job, "THIRD-PARTY-NOTICES.md") < len(job["steps"]) - 1
    assert "--self-test" in job["steps"][-2]["run"]


def test_path_filter_covers_every_input_of_the_notices():
    data = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
    paths = data[True]["pull_request"]["paths"] if True in data else data["on"]["pull_request"]["paths"]
    for needed in ("worker/uv.lock", "worker/pyproject.toml", "app/Directory.Packages.props",
                   "THIRD-PARTY-NOTICES.md", "scripts/gen_third_party_notices.py"):
        assert needed in paths
