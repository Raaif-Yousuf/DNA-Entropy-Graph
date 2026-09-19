"""Tests for worker/errors.py (issue #254): the worker's error-code taxonomy.

The mechanical guard issue #254 asks for — "worker raises only listed codes" — is the
source-scanning tests below: they grep every ``.py`` file under ``worker/src`` and
``worker/vm/startup.sh`` for a ``code = "..."``/``"code": "..."`` literal and assert every
one found is registered in ``WORKER_ERROR_CODES``. This is mechanical, not a convention: a
new exception with an unregistered code, or a new hardcoded error dict, fails this test
the moment it is added, with no reliance on anyone remembering to update a second list by
hand.
"""

from __future__ import annotations

import re
from pathlib import Path

from dna_entropy.worker.errors import (
    WORKER_ERROR_CODE_SET,
    WORKER_ERROR_CODES,
    is_retriable,
)

WORKER_SRC = Path(__file__).parent.parent / "src" / "dna_entropy"
STARTUP_SH = Path(__file__).parent.parent / "vm" / "startup.sh"
COPY_CATALOG = Path(__file__).parent.parent.parent / "docs" / "copy_catalog.md"

# A code literal is ALL_CAPS_WITH_UNDERSCORES, matching every code in the registry.
_CODE_PATTERN = re.compile(r'code["\']?\s*[:=]\s*["\'](?P<code>[A-Z][A-Z0-9_]*)["\']')


def _codes_in_python_source() -> set[str]:
    found: set[str] = set()
    for path in WORKER_SRC.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        for m in _CODE_PATTERN.finditer(text):
            found.add(m.group("code"))
    return found


def _codes_in_startup_script() -> set[str]:
    text = STARTUP_SH.read_text(encoding="utf-8")
    return {m.group("code") for m in _CODE_PATTERN.finditer(text)}


# --- registry self-consistency -------------------------------------------------------


def test_registry_has_no_duplicate_codes() -> None:
    codes = [spec.code for spec in WORKER_ERROR_CODES]
    assert len(codes) == len(set(codes))


def test_every_code_is_shouty_snake_case() -> None:
    for spec in WORKER_ERROR_CODES:
        assert spec.code == spec.code.upper()
        assert " " not in spec.code
        assert spec.code.replace("_", "").isalnum()


def test_is_retriable_looks_up_the_registry() -> None:
    for spec in WORKER_ERROR_CODES:
        assert is_retriable(spec.code) is spec.retriable


def test_is_retriable_fails_closed_for_an_unknown_code() -> None:
    assert is_retriable("SOMETHING_NOBODY_REGISTERED") is False


# --- the mechanical guard: "worker raises only listed codes" --------------------------


def test_every_code_literal_in_worker_python_source_is_registered() -> None:
    found = _codes_in_python_source()
    # A handful of test fixtures/docstrings use example codes that are not necessarily
    # this package's own — restrict the scan to worker/src (production code) only, which
    # WORKER_SRC.rglob already does; nothing further to exclude.
    unregistered = found - WORKER_ERROR_CODE_SET
    assert not unregistered, f"code(s) used in worker/src but not registered in errors.py: {unregistered}"


def test_every_code_literal_in_startup_script_is_registered() -> None:
    found = _codes_in_startup_script()
    unregistered = found - WORKER_ERROR_CODE_SET
    assert not unregistered, f"code(s) used in startup.sh but not registered in errors.py: {unregistered}"


def test_predictor_oom_error_code_is_registered() -> None:
    from dna_entropy.predictors.base import PredictorOOMError

    assert PredictorOOMError.code in WORKER_ERROR_CODE_SET
    assert PredictorOOMError.code == "MODEL_OOM"


def test_model_needs_hopper_error_code_is_registered() -> None:
    from dna_entropy.predictors.hardware import ModelNeedsHopperError

    assert ModelNeedsHopperError.code in WORKER_ERROR_CODE_SET


def test_manifest_schema_error_code_is_registered() -> None:
    from dna_entropy.worker.manifest import ManifestError, ManifestSchemaError

    assert ManifestError.code in WORKER_ERROR_CODE_SET
    assert ManifestSchemaError.code in WORKER_ERROR_CODE_SET


# --- cross-check against docs/copy_catalog.md's 34-code app catalog -------------------


def _copy_catalog_codes() -> set[str]:
    if not COPY_CATALOG.is_file():
        return set()
    text = COPY_CATALOG.read_text(encoding="utf-8")
    # Table rows look like: | `CODE_NAME` | `PascalKey` | Title | Body | Actions |
    return set(re.findall(r"^\|\s*`([A-Z][A-Z0-9_]*)`\s*\|", text, re.MULTILINE))


def test_copy_catalog_is_readable_and_has_codes() -> None:
    # docs/ is outside this package's own paths (read-only reference here), but the file
    # is expected to exist once issue #227-family docs work lands; skip gracefully if it
    # doesn't rather than failing a worker test on a docs-repo-layout assumption.
    codes = _copy_catalog_codes()
    if not COPY_CATALOG.is_file():
        import pytest

        pytest.skip("docs/copy_catalog.md not present in this checkout")
    assert len(codes) >= 30  # the 34-code catalog, generously bounded


def test_every_worker_code_is_in_copy_catalog_or_has_a_documented_gap_note() -> None:
    """Every WORKER_ERROR_CODES entry must either (a) appear in docs/copy_catalog.md's
    table, proving the worker and the app-facing catalog agree, or (b) carry a non-empty
    `note` explaining exactly why not — so a genuine drift can never hide silently behind
    "eh, probably fine". At the time this was written, exactly two codes fall into (b):
    MANIFEST_INVALID and WORKER_VERSION_MISMATCH — see errors.py's module docstring and
    issue #254's closing comment for the full disposition of each.
    """
    import pytest

    catalog_codes = _copy_catalog_codes()
    if not catalog_codes:
        pytest.skip("docs/copy_catalog.md not present in this checkout")

    undocumented_without_a_reason = [
        spec.code for spec in WORKER_ERROR_CODES if spec.code not in catalog_codes and not spec.note
    ]
    assert undocumented_without_a_reason == [], (
        f"these worker codes are missing from docs/copy_catalog.md AND have no `note` "
        f"explaining why: {undocumented_without_a_reason}"
    )
