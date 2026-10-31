"""Tests for scripts/check_ruff_pin.py (#1027).

One ruff version is named in scripts/ruff.toml (`required-version`, which ruff itself enforces) and
every other place that runs ruff must agree: worker/pyproject.toml, the premerge gates, the CI
workflows and the docs' command blocks. An unpinned `uvx ruff` is a finding.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_ruff_pin as crp  # noqa: E402

V = "0.17.3"


def _tree(tmp_path: Path, **overrides: str | None) -> Path:
    """A minimal repo where every site agrees on V; an override replaces a file's text (None deletes it)."""
    files = {
        "scripts/ruff.toml": f'required-version = "=={V}"\nline-length = 145\n',
        "worker/pyproject.toml": f'[tool.ruff]\nrequired-version = "=={V}"\nline-length = 110\n',
        "scripts/premerge.py": f'G = ["uvx", "ruff@{V}", "check", "worker"]\n' * 4,
        ".github/workflows/ci-docs.yml": f"        run: uvx ruff@{V} check scripts\n" * 2,
        ".github/workflows/ci-worker.yml": f"        run: uvx ruff@{V} check .\n" * 2,
        "docs/dev_commands.md": f"uvx ruff@{V} check worker\n",
    }
    for key, value in overrides.items():
        files[key.replace("__", "/").replace("_dot_", ".")] = value  # type: ignore[assignment]
    for rel, text in files.items():
        if text is None:
            continue
        path = tmp_path / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")
    return tmp_path


def test_a_consistent_tree_has_no_problems(tmp_path: Path) -> None:
    assert crp.check_repo(_tree(tmp_path)) == []


def test_the_canonical_version_is_read_from_scripts_ruff_toml(tmp_path: Path) -> None:
    assert crp.canonical_version(_tree(tmp_path)) == V


def test_a_worker_config_pin_that_disagrees_is_named(tmp_path: Path) -> None:
    root = _tree(tmp_path, worker__pyproject_dot_toml='[tool.ruff]\nrequired-version = "==0.16.0"\n')
    problems = crp.check_repo(root)
    assert len(problems) == 1
    assert "worker/pyproject.toml" in problems[0] and "0.16.0" in problems[0] and V in problems[0]


def test_a_missing_required_version_in_a_config_is_a_problem(tmp_path: Path) -> None:
    root = _tree(tmp_path, worker__pyproject_dot_toml="[tool.ruff]\nline-length = 110\n")
    assert any("worker/pyproject.toml" in p and "required-version" in p for p in crp.check_repo(root))


def test_an_unpinned_uvx_ruff_in_a_workflow_is_a_problem(tmp_path: Path) -> None:
    root = _tree(tmp_path, **{".github__workflows__ci-worker_dot_yml": "        run: uvx ruff check .\n" * 2})
    problems = crp.check_repo(root)
    assert any("ci-worker.yml" in p and "unpinned" in p for p in problems)


def test_an_unpinned_premerge_argv_is_a_problem(tmp_path: Path) -> None:
    root = _tree(tmp_path, scripts__premerge_dot_py='G = ["uvx", "ruff", "check", "worker"]\n' + f'H = ["uvx", "ruff@{V}", "x"]\n' * 3)
    assert any("premerge.py" in p and "unpinned" in p for p in crp.check_repo(root))


def test_a_pin_in_the_docs_that_drifted_is_named_with_its_line(tmp_path: Path) -> None:
    root = _tree(tmp_path, docs__dev_commands_dot_md=f"ok uvx ruff@{V} check\nold uvx ruff@0.15.0 check\n")
    problems = crp.check_repo(root)
    assert len(problems) == 1 and "docs/dev_commands.md:2" in problems[0] and "0.15.0" in problems[0]


def test_a_missing_canonical_file_fails_loudly(tmp_path: Path) -> None:
    root = _tree(tmp_path, scripts__ruff_dot_toml=None)
    problems = crp.check_repo(root)
    assert problems and "scripts/ruff.toml" in problems[0]


def test_a_tree_with_no_pins_anywhere_is_not_a_clean_pass(tmp_path: Path) -> None:
    """Vacuity: a scanner that matches nothing must not report a clean tree."""
    root = _tree(
        tmp_path,
        scripts__premerge_dot_py="nothing\n",
        **{".github__workflows__ci-docs_dot_yml": "nothing\n", ".github__workflows__ci-worker_dot_yml": "nothing\n"},
    )
    assert any("expected" in p for p in crp.check_repo(root))


def test_the_real_repo_agrees_and_the_scanner_saw_enough_sites() -> None:
    assert crp.check_repo(REPO_ROOT) == []
    assert crp.count_sites(REPO_ROOT) >= 10  # 2 configs + 4 premerge + 2 ci-docs + 2 ci-worker (+ docs)


def test_the_pin_is_the_ruff_that_ships_with_the_gates_today() -> None:
    """The canonical pin is a real, released ruff (a typo would make every gate fail to resolve)."""
    version = crp.canonical_version(REPO_ROOT)
    assert version is not None
    parts = version.split(".")
    assert len(parts) == 3 and all(p.isdigit() for p in parts)


def test_main_exit_codes_and_ascii_output(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    assert crp.main(["--root", str(_tree(tmp_path))]) == 0
    assert capsys.readouterr().out.startswith("OK:")
    bad = _tree(tmp_path / "bad", worker__pyproject_dot_toml='[tool.ruff]\nrequired-version = "==1.0.0"\n')
    assert crp.main(["--root", str(bad)]) == 1
    err = capsys.readouterr().err
    assert err.startswith("ERROR:") and err.isascii()


def test_self_test_passes() -> None:
    proc = subprocess.run([sys.executable, str(SCRIPTS_DIR / "check_ruff_pin.py"), "--self-test"], capture_output=True, text=True, check=False)
    assert proc.returncode == 0, proc.stderr


def test_a_ruff_running_the_wrong_version_refuses_via_required_version(tmp_path: Path) -> None:
    """The enforcement half: ruff itself rejects a config whose required-version it does not match."""
    (tmp_path / "ruff.toml").write_text('required-version = "==0.0.1"\n', encoding="utf-8", newline="\n")
    (tmp_path / "a.py").write_text("x = 1\n", encoding="utf-8", newline="\n")
    proc = subprocess.run(["uvx", f"ruff@{V}", "check", "a.py"], cwd=tmp_path, capture_output=True, text=True, check=False)
    assert proc.returncode != 0
    assert "required-version" in (proc.stderr + proc.stdout).lower() or "0.0.1" in (proc.stderr + proc.stdout)


@pytest.mark.parametrize("spec", ["latest", "0.17", "0.17.3rc1", "^0.17.3", ">=0.17.3"])
def test_a_ruff_at_something_other_than_the_exact_pin_is_a_problem(tmp_path: Path, spec: str) -> None:
    """#1027 review: `ruff@latest` / a partial version matched neither the pinned nor the unpinned pattern."""
    root = _tree(
        tmp_path, **{".github__workflows__ci-worker_dot_yml": f"        run: uvx ruff@{spec} check .\n" + f"        run: uvx ruff@{V} x\n" * 2}
    )
    problems = crp.check_repo(root)
    assert any("ci-worker.yml:1" in p and f"ruff@{spec}" in p for p in problems), problems


def test_a_quoted_premerge_argv_with_ruff_at_latest_is_a_problem(tmp_path: Path) -> None:
    root = _tree(tmp_path, scripts__premerge_dot_py='G = ["uvx", "ruff@latest", "check"]\n' + f'H = ["uvx", "ruff@{V}", "x"]\n' * 4)
    assert any("premerge.py:1" in p and "ruff@latest" in p for p in crp.check_repo(root))


def test_an_exact_pin_followed_by_punctuation_is_still_accepted(tmp_path: Path) -> None:
    root = _tree(tmp_path, docs__dev_commands_dot_md=f"run `uvx ruff@{V}`, then `uvx ruff@{V}.`\n")
    assert crp.check_repo(root) == []


@pytest.mark.parametrize("cmd", ["uv run ruff check .", "uvx --from ruff ruff check .", "python -m ruff check ."])
def test_other_unversioned_ways_to_run_ruff_are_problems(tmp_path: Path, cmd: str) -> None:
    root = _tree(tmp_path, docs__dev_commands_dot_md=f"run {cmd}\n")
    assert any("docs/dev_commands.md:1" in p and "unpinned" in p for p in crp.check_repo(root)), cmd


def test_from_ruff_with_an_exact_version_is_accepted(tmp_path: Path) -> None:
    assert crp.check_repo(_tree(tmp_path, docs__dev_commands_dot_md=f"run uvx --from ruff=={V} ruff check .\n")) == []
