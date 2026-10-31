"""scripts/tests must test THIS checkout's worker code, not another checkout's editable install.

The shared worker venv carries an editable install of `dna_entropy` from the PRIMARY checkout.
worker/tests/conftest.py already guards that (#937), but a pytest run that only collects
scripts/tests never loaded that conftest, so from an agent worktree `dna_entropy` (and any worker
subprocess a script test spawns) resolved to the primary checkout. MEASURED 2026-10-31: from a
worktree, `pytest scripts/tests/<file>` printed the primary checkout's worker/src for both the
in-process import and a child `sys.executable` import.

The in-process arm needs no test here: the guard aborts the whole session when it fails. The child
process is the real check, because only PYTHONPATH (not sys.path) reaches it.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
OWN_SRC = (REPO_ROOT / "worker" / "src").resolve()


def test_child_python_dna_entropy_comes_from_this_checkout() -> None:
    guard = sys.modules.get("_deg_worker_import_guard")
    assert guard is not None, "scripts/tests/conftest.py did not load the worker import guard"
    if not guard._dev_layout():  # pyright: ignore[reportPrivateUsage]
        pytest.skip("installed layout (DEG_TEST_INSTALLED or a non-editable install): tested as installed")
    result = subprocess.run(
        [sys.executable, "-c", "import dna_entropy; print(dna_entropy.__file__)"],
        capture_output=True,
        text=True,
        check=True,
    )
    assert Path(result.stdout.strip()).resolve().is_relative_to(OWN_SRC), result.stdout
