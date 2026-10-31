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
    made = {"mine": mine, "other": other, "site": site, "tests": tests}
    # Hermetic default: an editable install made from another checkout, whatever the real venv looks like.
    _dist_info(made, '{"url": "file:///elsewhere/worker", "dir_info": {"editable": true}}')
    return made


def _dist_info(layout: dict[str, Path], direct_url: str | None) -> None:
    """A fake `dna-entropy` dist on the site dir; `direct_url` is its direct_url.json text (None: no file)."""
    info = layout["site"] / "dna_entropy-0.0.1.dist-info"
    info.mkdir(exist_ok=True)
    (info / "METADATA").write_text(
        "Metadata-Version: 2.1\nName: dna-entropy\nVersion: 0.0.1\n", encoding="utf-8", newline="\n"
    )
    target = info / "direct_url.json"
    if direct_url is None:
        target.unlink(missing_ok=True)
    else:
        target.write_text(direct_url, encoding="utf-8", newline="\n")


def _pytest(
    layout: dict[str, Path], *args: str, env_extra: dict[str, str] | None = None
) -> subprocess.CompletedProcess[str]:
    env = {k: v for k, v in os.environ.items() if k not in ("PYTHONPATH", "DEG_TEST_INSTALLED")}
    env["PYTHONPATH"] = str(layout["site"])
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    env.update(env_extra or {})
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


INSTALLED_TEST = """
import os
import sys
from pathlib import Path

import dna_entropy

MINE = (Path(__file__).resolve().parents[1] / "src").resolve()


def test_the_installed_copy_is_what_is_tested():
    assert dna_entropy.MARK == "other", dna_entropy.__file__


def test_path_and_pythonpath_were_left_alone():
    assert str(MINE) not in [str(Path(p).resolve()) for p in sys.path if p]
    assert str(MINE) not in os.environ["PYTHONPATH"]
"""


def _installed_layout(layout: dict[str, Path]) -> None:
    (layout["tests"] / "test_inside.py").write_text(INSTALLED_TEST, encoding="utf-8", newline="\n")


def test_a_non_editable_install_is_tested_as_installed_and_nothing_is_touched(
    layout: dict[str, Path],
) -> None:
    """The GPU image shape: a wheel installed beside /opt/worker/src. The tests must run against the wheel."""
    _dist_info(layout, '{"url": "file:///build/worker", "dir_info": {}}')
    _installed_layout(layout)
    done = _pytest(layout)
    assert done.returncode == 0, done.stdout + done.stderr
    assert "2 passed" in done.stdout


def test_an_index_install_with_no_direct_url_is_also_tested_as_installed(layout: dict[str, Path]) -> None:
    _dist_info(layout, None)
    _installed_layout(layout)
    done = _pytest(layout)
    assert done.returncode == 0, done.stdout + done.stderr


def test_an_editable_install_from_another_checkout_still_loses_to_this_checkouts_src(
    layout: dict[str, Path],
) -> None:
    _dist_info(layout, '{"url": "file:///elsewhere/worker", "dir_info": {"editable": true}}')
    done = _pytest(layout)
    assert done.returncode == 0, done.stdout + done.stderr
    assert "3 passed" in done.stdout


def test_the_opt_out_variable_tests_the_installed_package_even_with_no_install_metadata(
    layout: dict[str, Path],
) -> None:
    _installed_layout(layout)
    off = _pytest(layout, env_extra={"DEG_TEST_INSTALLED": "1"})
    assert off.returncode == 0, off.stdout + off.stderr
    zero = _pytest(
        layout, env_extra={"DEG_TEST_INSTALLED": "0"}
    )  # "0" is not an opt-out: this checkout's src wins
    assert zero.returncode != 0, zero.stdout


@pytest.mark.parametrize(
    "direct_url",
    ["[]", '"x"', "7", "null", '{"dir_info": null}', '{"dir_info": []}', '{"dir_info": {"editable": "yes"}}'],
)
def test_a_malformed_direct_url_is_not_editable_and_does_not_crash_conftest(
    layout: dict[str, Path], direct_url: str
) -> None:
    _dist_info(layout, direct_url)
    _installed_layout(layout)
    done = _pytest(layout)
    assert done.returncode == 0, done.stdout + done.stderr
    assert "AttributeError" not in done.stdout + done.stderr


def test_the_header_names_the_mode_and_the_resolved_file(layout: dict[str, Path]) -> None:
    done = _pytest(layout, "-v")
    mine_pkg = str((layout["mine"] / "worker" / "src" / "dna_entropy" / "__init__.py").resolve())
    line = next(ln for ln in done.stdout.splitlines() if ln.startswith("dna_entropy tested from"))
    assert "development layout" in line and mine_pkg in line
    _dist_info(layout, '{"dir_info": {}}')
    _installed_layout(layout)
    other_pkg = str((layout["other"] / "worker" / "src" / "dna_entropy" / "__init__.py").resolve())
    line = next(
        ln for ln in _pytest(layout, "-v").stdout.splitlines() if ln.startswith("dna_entropy tested from")
    )
    assert "as installed" in line and other_pkg in line


def test_a_namespace_package_is_named_not_a_traceback_about_none(layout: dict[str, Path]) -> None:
    for root in (layout["mine"], layout["other"]):
        (
            root / "worker" / "src" / "dna_entropy" / "__init__.py"
        ).unlink()  # leaves a bare directory: a namespace package
    done = _pytest(layout)
    out = done.stdout + done.stderr
    assert done.returncode != 0
    assert "resolved to a namespace package" in out and "NoneType" not in out and "TypeError" not in out


def test_the_guard_fires_when_this_checkout_has_no_package(layout: dict[str, Path]) -> None:
    """My own src has no dna_entropy: the only importable one is the other checkout's. Must fail, not pass."""
    pkg = layout["mine"] / "worker" / "src" / "dna_entropy"
    (pkg / "__init__.py").unlink()
    pkg.rmdir()
    done = _pytest(layout)
    out = done.stdout + done.stderr
    assert done.returncode != 0
    assert "passed" not in out
