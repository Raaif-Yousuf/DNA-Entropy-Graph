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
import os
import sys
from pathlib import Path

import pytest

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
    where = Path(getattr(module, "__file__", None) or "").resolve()
    if not where.is_relative_to(_SRC):
        raise pytest.UsageError(
            f"dna_entropy was imported from {where}, outside this checkout's worker/src ({_SRC}). "
            "Another checkout's editable install (or an early plugin) won the import; "
            "the tests would exercise the wrong code. "
            "Remove the early import, or run pytest from a clean interpreter."
        )


_prefer_own_src()
_assert_imports_from_own_src()


@pytest.fixture
def sample_seq() -> str:
    """A short, valid uppercase A/C/G/T sequence."""
    return "ATGCGTACGTTAGCAACGTACGATCGATCGTAGCTAGCTAGCAT"
