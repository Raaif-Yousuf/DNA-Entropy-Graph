"""scripts/tests must test THIS checkout's worker code, not another checkout's editable install.

The shared worker venv carries an editable install of `dna_entropy` from the PRIMARY checkout.
worker/tests/conftest.py already guards that (#937), but a pytest run that only collects
scripts/tests never loaded that conftest, so from an agent worktree `dna_entropy` (and any worker
subprocess a script test spawns) resolved to the primary checkout. MEASURED 2026-10-31: from a
worktree, `pytest scripts/tests/<file>` printed the primary checkout's worker/src for both the
in-process import and a child `sys.executable` import.
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
OWN_SRC = (REPO_ROOT / "worker" / "src").resolve()


def test_in_process_dna_entropy_comes_from_this_checkout() -> None:
    import dna_entropy

    assert dna_entropy.__file__ is not None
    assert Path(dna_entropy.__file__).resolve().is_relative_to(OWN_SRC), dna_entropy.__file__


def test_child_python_dna_entropy_comes_from_this_checkout() -> None:
    result = subprocess.run(
        [sys.executable, "-c", "import dna_entropy; print(dna_entropy.__file__)"],
        capture_output=True,
        text=True,
        check=True,
    )
    assert Path(result.stdout.strip()).resolve().is_relative_to(OWN_SRC), result.stdout


def test_pythonpath_leads_with_this_checkouts_worker_src() -> None:
    first = os.environ.get("PYTHONPATH", "").split(os.pathsep)[0]
    assert first, "PYTHONPATH is empty: worker subprocesses would use the editable install"
    assert Path(first).resolve() == OWN_SRC
