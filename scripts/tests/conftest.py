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

import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
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
        return (
            "worker/.venv does not have the optional extras installed, so the real "
            "worker dependency list is incomplete here"
        )
    return None


@pytest.fixture(scope="session")
def real_dependency_tree() -> None:
    """Skip the test unless this machine can enumerate the real dependency tree."""
    reason = _real_tree_skip_reason()
    if reason is not None:
        pytest.skip(reason)
