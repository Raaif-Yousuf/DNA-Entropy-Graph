"""Structural checks for `tests/contract-fixtures/` (repo root).

These fixtures preserve knowledge from the prototype's `cloud/gcloud.py` (GCP error
classification, quota CSV parsing) that must survive the gutting of `dna_entropy.cloud`
(issues #280/#277) so a future C# `ErrorCatalog`/`GpuPlanner` implementation can reuse it
as golden test vectors (issue #284).

Deliberately does NOT import `dna_entropy.cloud` — that package is relocated to
`worker/legacy/cloud/` (not shipped) once #280/#277 land, and these fixtures must keep
being readable (and these tests must keep passing) long after that happens. Correctness
against the *live* prototype code was verified once, by hand, at extraction time (see each
fixture's `verified_against_source` field); these tests only guard the JSON's own shape.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

# Repo root, not worker/tests: appendix A places the shared vectors at
# `tests/contract-fixtures/` (repo root) precisely so the C# suite reads the same files.
# A copy under worker/ would drift from the C# copy and the fixtures would stop
# being a contract.
FIXTURES_DIR = Path(__file__).resolve().parents[2] / "tests" / "contract-fixtures"

ERROR_BUCKETS = {
    "billing", "api_disabled", "quota", "stockout",
    "already_exists", "permission", "network", "other",
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
        "quota", "stockout", "permission", "other", "billing", "api_disabled", "network",
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


@pytest.mark.parametrize("filename", ["cloud_error_classification.json", "cloud_quota_parsing.json"])
def test_fixture_is_valid_json(filename: str) -> None:
    # Round-trips through json.loads/dumps without raising; that's the whole contract for
    # "readable by both pytest and a future xUnit test" (JSON is language-neutral).
    data = _load(filename)
    json.dumps(data)  # must be serializable back out
