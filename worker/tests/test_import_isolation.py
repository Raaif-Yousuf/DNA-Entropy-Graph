"""#937: pytest in a checkout imports THAT checkout's dna_entropy, whatever editable install the venv carries.

The fake layout: two "checkouts" (`mine`, `other`), each with its own `worker/src/dna_entropy`.
A sitecustomize on PYTHONPATH appends `other/worker/src` to sys.path, the same shape as an
editable install made from another checkout. `mine` carries a verbatim copy of the real
worker/tests/conftest.py, run in a real pytest subprocess.
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest

REAL_CONFTEST = Path(__file__).resolve().parent / "conftest.py"

INSIDE_TEST = """
import os
import subprocess
import sys
from pathlib import Path

import dna_entropy

MINE = Path(__file__).resolve().parents[1] / "src"


def _inside(path: str, root: Path) -> bool:
    return Path(path).resolve().is_relative_to(root.resolve())


def test_in_process_import_is_mine():
    assert dna_entropy.MARK == "mine", dna_entropy.__file__
    assert _inside(dna_entropy.__file__, MINE)


def test_a_spawned_python_imports_mine_too():
    out = subprocess.run(
        [sys.executable, "-c", "import dna_entropy; print(dna_entropy.__file__)"],
        capture_output=True, text=True, check=True,
    ).stdout.strip()
    assert _inside(out, MINE), out


def test_pythonpath_starts_with_my_src():
    assert os.environ["PYTHONPATH"].split(os.pathsep)[0] == str(MINE.resolve())
"""


def _pkg(root: Path, mark: str) -> Path:
    pkg = root / "worker" / "src" / "dna_entropy"
    pkg.mkdir(parents=True)
    (pkg / "__init__.py").write_text(f'MARK = "{mark}"\n', encoding="utf-8", newline="\n")
    return root / "worker" / "src"


@pytest.fixture
def layout(tmp_path: Path) -> dict[str, Path]:
    mine, other = tmp_path / "mine", tmp_path / "other"
    _pkg(mine, "mine")
    other_src = _pkg(other, "other")
    tests = mine / "worker" / "tests"
    tests.mkdir(parents=True)
    (tests / "conftest.py").write_text(
        REAL_CONFTEST.read_text(encoding="utf-8"), encoding="utf-8", newline="\n"
    )
    (tests / "test_inside.py").write_text(INSIDE_TEST, encoding="utf-8", newline="\n")
    site = tmp_path / "site"
    site.mkdir()
    # Drop the venv's real editable entry (the real dna_entropy), then append `other` the way a .pth does.
    hook = (
        "import os, sys\n"
        "def _real(p):\n"
        "    f = os.path.join(p, 'dna_entropy', '__init__.py')\n"
        "    return os.path.isfile(f) and 'MARK' not in open(f, encoding='utf-8').read()\n"
        "sys.path[:] = [p for p in sys.path if not _real(p)]\n"
        f"sys.path.append({str(other_src)!r})\n"
    )
    (site / "sitecustomize.py").write_text(hook, encoding="utf-8", newline="\n")
    return {"mine": mine, "other": other, "site": site, "tests": tests}


def _pytest(layout: dict[str, Path], *args: str) -> subprocess.CompletedProcess[str]:
    env = {k: v for k, v in os.environ.items() if k != "PYTHONPATH"}
    env["PYTHONPATH"] = str(layout["site"])
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    return subprocess.run(
        [sys.executable, "-m", "pytest", "-q", "-p", "no:cacheprovider", *args],
        cwd=layout["mine"] / "worker",
        env=env,
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )


def test_the_fake_editable_install_alone_resolves_to_the_other_checkout(layout: dict[str, Path]) -> None:
    """Harness check: with no conftest the site hook really imports `other` (else the next proves nothing)."""
    done = subprocess.run(
        [sys.executable, "-c", "import dna_entropy; print(dna_entropy.MARK)"],
        env={**os.environ, "PYTHONPATH": str(layout["site"])},
        capture_output=True,
        text=True,
        check=True,
    )
    assert done.stdout.strip() == "other"


def test_pytest_imports_this_checkouts_package_not_the_editable_installs(layout: dict[str, Path]) -> None:
    done = _pytest(layout)
    assert done.returncode == 0, done.stdout + done.stderr
    assert "3 passed" in done.stdout


def test_a_plugin_that_imported_the_other_package_first_fails_loudly(layout: dict[str, Path]) -> None:
    (layout["site"] / "early_plugin.py").write_text("import dna_entropy\n", encoding="utf-8", newline="\n")
    done = _pytest(layout, "-p", "early_plugin")
    out = done.stdout + done.stderr
    assert done.returncode != 0
    assert "dna_entropy" in out and layout["other"].name in out
    assert "passed" not in out


def test_the_guard_fires_when_this_checkout_has_no_package(layout: dict[str, Path]) -> None:
    """My own src has no dna_entropy: the only importable one is the other checkout's. Must fail, not pass."""
    pkg = layout["mine"] / "worker" / "src" / "dna_entropy"
    (pkg / "__init__.py").unlink()
    pkg.rmdir()
    done = _pytest(layout)
    out = done.stdout + done.stderr
    assert done.returncode != 0
    assert "passed" not in out
