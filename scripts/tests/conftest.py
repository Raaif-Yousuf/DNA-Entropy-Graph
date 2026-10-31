"""Shared fixtures for scripts/tests.

The only thing in here so far is the guard for the three THIRD-PARTY-NOTICES
tests that run against this repo's REAL dependency tree rather than a
synthetic fixture. Those three are the valuable ones (they are what caught
the pre-#34 permanent no-op), and they are also the only tests in this
directory that need something the checkout alone does not provide:

  - a `dotnet` CLI that can restore `app/`, which on a Linux runner it
    cannot, because the app is WinUI 3 and its packages are Windows-only;
  - a populated `worker/.venv` with the optional extras installed, which is
    gitignored and so is never present in a fresh checkout.

That is the same gap `ci-docs.yml` already documents for the SCRIPT itself
(issue #416: check_third_party_notices.py has no CI job that can actually
run it, because it needs dotnet and worker/.venv together). The script was
excluded from that job's loop; its tests were not, so they ran on a runner
that could not satisfy them and failed for a reason that has nothing to do
with the change under test.

Skipping is the honest answer rather than deleting the tests or weakening
their assertions: on a machine that HAS both prerequisites, all three run
for real and assert exactly what they asserted before. The skip reason says
which prerequisite was missing, so a skip is never silent.
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent


def _apply_worker_import_isolation() -> None:
    """Run worker/tests/conftest.py's import-isolation guard (#937) for a scripts-only session.

    The shared venv's editable install points at the PRIMARY checkout; without this, `pytest
    scripts/tests/...` from an agent worktree imports (and spawns children against) the primary
    checkout's `dna_entropy`. Loaded by path under a private name (not as `conftest`) so there is one
    implementation, not a copy that can drift; importing it runs the guard at module level.
    """
    path = SCRIPTS_DIR.parent / "worker" / "tests" / "conftest.py"
    spec = importlib.util.spec_from_file_location("_deg_worker_import_guard", path)
    assert spec is not None and spec.loader is not None, path
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)


_apply_worker_import_isolation()

if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import gen_third_party_notices as gtpn  # noqa: E402

_UNINSTALLED_MARKER = "not installed in worker/.venv"


def _real_tree_skip_reason() -> str | None:
    """None when this machine can enumerate the real dependency tree, else why not."""
    repo_root = SCRIPTS_DIR.parent
    try:
        text, _clean = gtpn.generate(repo_root / "app", repo_root / "worker")
    except gtpn.NoticesGenerationError as error:
        return f"cannot enumerate the real dependency tree here: {error}"
    if _UNINSTALLED_MARKER in text:
        return "worker/.venv does not have the optional extras installed, so the real worker dependency list is incomplete here"
    return None


@pytest.fixture(scope="session")
def real_dependency_tree() -> None:
    """Skip the test unless this machine can enumerate the real dependency tree."""
    reason = _real_tree_skip_reason()
    if reason is not None:
        pytest.skip(reason)
