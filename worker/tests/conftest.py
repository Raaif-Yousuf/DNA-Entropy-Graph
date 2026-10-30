"""Shared pytest fixtures, and the import-isolation guard (#937).

The shared worker venv carries an editable install of `dna_entropy` made from ONE checkout (the
primary one). Run pytest from an agent worktree with that venv and, without this block, the tests
import the primary checkout's worker code: false failures, and worse, a worker change that is
broken here can test green against main's code.

So, before any test module imports `dna_entropy`: put THIS repo's `worker/src` first on sys.path
and first on PYTHONPATH (worker subprocesses that tests spawn inherit it), then fail the whole
session loudly if `dna_entropy` still resolves anywhere else (an earlier plugin or `-p` import of
another checkout's copy, or no package here).
"""

from __future__ import annotations

import importlib
import importlib.metadata
import json
import os
import sys
from pathlib import Path

import pytest

_DIST = "dna-entropy"  # [project] name in worker/pyproject.toml
_SRC = (Path(__file__).resolve().parent.parent / "src").resolve()


def _same(a: str, b: str) -> bool:
    return os.path.normcase(os.path.abspath(a)) == os.path.normcase(os.path.abspath(b))


def _prefer_own_src() -> None:
    src = str(_SRC)
    sys.path[:] = [p for p in sys.path if not _same(p, src)]
    sys.path.insert(0, src)
    rest = [p for p in os.environ.get("PYTHONPATH", "").split(os.pathsep) if p and not _same(p, src)]
    os.environ["PYTHONPATH"] = os.pathsep.join([src, *rest])


def _assert_imports_from_own_src() -> None:
    try:
        module = importlib.import_module("dna_entropy")
    except ImportError as err:
        raise pytest.UsageError(
            f"dna_entropy is not importable from this checkout's worker/src ({_SRC}): {err}"
        ) from err
    file = getattr(module, "__file__", None)
    if file is None:
        raise pytest.UsageError(
            "dna_entropy resolved to a namespace package (no __init__.py; paths "
            f"{list(getattr(module, '__path__', []))}), "
            f"not to a regular package under this checkout's worker/src ({_SRC}). "
            "Is the source tree incomplete?"
        )
    where = Path(file).resolve()
    if not where.is_relative_to(_SRC):
        raise pytest.UsageError(
            f"dna_entropy was imported from {where}, outside this checkout's worker/src ({_SRC}). "
            "Another checkout's editable install (or an early plugin) won the import; "
            "the tests would exercise the wrong code. "
            "Remove the early import, or run pytest from a clean interpreter."
        )


def _dev_layout() -> bool:
    """True when this is a source checkout to test: the dist is absent or an EDITABLE install
    (direct_url.json dir_info.editable). A normal wheel install (the GPU image: Dockerfile.cuda,
    non-editable) is tested AS INSTALLED, so a mis-packaged wheel cannot pass. DEG_TEST_INSTALLED=1
    (anything but empty or 0) forces that mode."""
    if os.environ.get("DEG_TEST_INSTALLED", "") not in ("", "0"):
        return False
    try:
        dist = importlib.metadata.distribution(_DIST)
    except importlib.metadata.PackageNotFoundError:
        return True
    try:
        info = json.loads(dist.read_text("direct_url.json") or "{}")
    except ValueError:
        return False
    return bool(info.get("dir_info", {}).get("editable"))


_DEV_LAYOUT = _dev_layout()
if _DEV_LAYOUT:
    _prefer_own_src()
    _assert_imports_from_own_src()


def pytest_report_header(config: pytest.Config) -> str:
    mode = (
        "this checkout's worker/src (development layout)"
        if _DEV_LAYOUT
        else "the installed package (as installed)"
    )
    return f"dna_entropy tested from: {mode}"


@pytest.fixture
def sample_seq() -> str:
    """A short, valid uppercase A/C/G/T sequence."""
    return "ATGCGTACGTTAGCAACGTACGATCGATCGTAGCTAGCTAGCAT"
