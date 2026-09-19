"""Structural checks for `tests/contract-fixtures/` (repo root).

These fixtures preserve knowledge from the prototype's `cloud/gcloud.py` (GCP error
classification, quota CSV parsing) and `cloud/keeper.py` (instance-state decisions, setup
health checks, quota pre-filtering) that must survive the gutting of `dna_entropy.cloud`
(issues #280/#277) so a future C# `ErrorCatalog`/`GpuPlanner`/`SetupHealthService`
implementation can reuse it as golden test vectors (issues #284, #213).

Deliberately does NOT import `dna_entropy.cloud` — that package is relocated to
`worker/legacy/cloud/` (not shipped) once #280/#277 land, and these fixtures must keep
being readable (and these tests must keep passing) long after that happens. Correctness
against the *live* prototype code was verified once, by hand, at extraction time (see each
fixture's `verified_against_source` field); these tests only guard the JSON's own shape.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

import pytest

# Repo root, not worker/tests: appendix A places the shared vectors at
# `tests/contract-fixtures/` (repo root) precisely so the C# suite reads the same files.
# A copy under worker/ would drift from the C# copy and the fixtures would stop
# being a contract.
FIXTURES_DIR = Path(__file__).resolve().parents[2] / "tests" / "contract-fixtures"

ERROR_BUCKETS = {
    "billing",
    "api_disabled",
    "quota",
    "stockout",
    "already_exists",
    "permission",
    "network",
    "other",
}


def _load(name: str) -> dict:
    return json.loads((FIXTURES_DIR / name).read_text(encoding="utf-8"))


def test_fixtures_dir_exists() -> None:
    assert FIXTURES_DIR.is_dir()


def test_error_classification_fixture_is_well_formed() -> None:
    data = _load("cloud_error_classification.json")
    assert set(data["buckets"]) == ERROR_BUCKETS
    cases = data["cases"]
    assert len(cases) > 0
    ids = set()
    for case in cases:
        assert case["id"] not in ids, f"duplicate case id {case['id']!r}"
        ids.add(case["id"])
        assert isinstance(case["stderr"], str) and case["stderr"]
        assert case["expected_bucket"] in ERROR_BUCKETS


def test_error_classification_fixture_covers_every_prototype_test_case() -> None:
    """Every stderr sample from the prototype's test_classify_create_error must be present."""
    data = _load("cloud_error_classification.json")
    prototype_cases = [c for c in data["cases"] if c.get("from_prototype_test")]
    # The prototype's test_cloud.py::test_classify_create_error had exactly 9 parametrized cases.
    assert len(prototype_cases) == 9
    assert {c["expected_bucket"] for c in prototype_cases} == {
        "quota",
        "stockout",
        "permission",
        "other",
        "billing",
        "api_disabled",
        "network",
    }


def test_quota_parsing_fixture_is_well_formed() -> None:
    data = _load("cloud_quota_parsing.json")
    metric_cases = data["gpu_quota_metric"]["cases"]
    assert len(metric_cases) > 0
    for case in metric_cases:
        assert isinstance(case["accelerator"], str) and case["accelerator"]
        assert isinstance(case["expected_metric"], str) and case["expected_metric"]

    region_cases = data["list_region_gpu_quota"]["cases"]
    assert len(region_cases) == 3
    by_id = {c["id"]: c for c in region_cases}
    assert by_id["parses_available"]["expected"] == {
        "us-central1": {"NVIDIA_L4_GPUS": 6.0},
        "europe-west4": {"NVIDIA_A100_GPUS": 0.0},
    }
    assert by_id["returns_none_on_query_failure"]["expected"] is None
    assert by_id["empty_metrics_is_empty_without_a_query"]["expected"] == {}


@pytest.mark.parametrize(
    "filename",
    [
        "cloud_error_classification.json",
        "cloud_quota_parsing.json",
        "keeper_next_action.json",
        "keeper_setup_diagnosis.json",
        "keeper_quota_prefilter.json",
        "cli_validate_parity.json",
    ],
)
def test_fixture_is_valid_json(filename: str) -> None:
    # Round-trips through json.loads/dumps without raising; that's the whole contract for
    # "readable by both pytest and a future xUnit test" (JSON is language-neutral).
    data = _load(filename)
    json.dumps(data)  # must be serializable back out


# --- keeper.py fixtures (issue #213): state machine, setup diagnosis, quota prefilter --


def test_keeper_next_action_fixture_covers_every_prototype_case() -> None:
    data = _load("keeper_next_action.json")
    cases = data["cases"]
    # tests/test_keeper.py::test_next_action had exactly 7 parametrized cases.
    assert len(cases) == 7
    ids = {c["id"] for c in cases}
    assert len(ids) == 7, "duplicate case id"
    for case in cases:
        assert case["action"] in data["actions"]
        if case["action"] == "acquire":
            assert case["existing"] is None
        else:
            assert isinstance(case["existing"], list) and len(case["existing"]) == 2


def test_keeper_setup_diagnosis_fixture_is_well_formed_and_no_timestamps_leaked() -> None:
    data = _load("keeper_setup_diagnosis.json")
    cases = data["cases"]
    # tests/test_keeper.py's setup-doctor section had exactly 7 scenarios.
    assert len(cases) == 7
    ids = {c["id"] for c in cases}
    assert len(ids) == 7, "duplicate case id"
    for case in cases:
        assert isinstance(case["ok"], bool)
        assert case["expected_substrings"], f"{case['id']} has no substrings to check against"
        for s in case["expected_substrings"]:
            assert isinstance(s, str) and s
            # The raw captured prototype stdout this fixture was built from carries a
            # wall-clock timestamp per line; the fixture's own warning says only
            # substrings were kept, never the full timestamped lines. Guard that no
            # timestamp-shaped text snuck into a frozen substring by accident.
            assert not re.search(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", s), (
                f"{case['id']!r} substring looks like it contains a timestamp: {s!r}"
            )


def test_keeper_quota_prefilter_fixture_is_well_formed() -> None:
    data = _load("keeper_quota_prefilter.json")
    cases = data["cases"]
    assert len(cases) == 3
    by_id = {c["id"]: c for c in cases}
    assert by_id["narrows_to_quota_regions"]["result_zones"] == ["us-central1-a", "us-central1-b"]
    assert by_id["keeps_all_when_query_unknown"]["quota_query_result"] is None
    assert by_id["keeps_all_when_zero_quota_everywhere"]["quota_query_result"] == {}
    # Same None-vs-{} distinction as cloud_quota_parsing.json -- both "unknown" and
    # "genuinely zero" keep every zone, but for different, worth-preserving reasons.
    assert (
        by_id["keeps_all_when_query_unknown"]["result_zones"]
        == by_id["keeps_all_when_zero_quota_everywhere"]["result_zones"]
    )


# --- CLI parity fixture (issue #212) ----------------------------------------------------


def test_cli_validate_parity_fixture_is_well_formed() -> None:
    data = _load("cli_validate_parity.json")
    cases = data["cases"]
    assert len(cases) == 5
    ids = {c["id"] for c in cases}
    assert len(ids) == 5, "duplicate case id"
    for case in cases:
        assert isinstance(case["args"], list) and case["args"][0] == "validate"
        assert isinstance(case["exit_code"], int)
        assert isinstance(case["output"], str)
    by_id = {c["id"]: c for c in cases}
    # The one case that is NOT a success -- flagged in its own `note`, and worth a
    # structural guard so a future edit can't quietly turn it into a success case
    # without someone noticing the fixture's own claim about it needs updating too.
    assert by_id["genbank_file_is_not_genbank_aware"]["exit_code"] == 1
    assert "note" in by_id["genbank_file_is_not_genbank_aware"]
