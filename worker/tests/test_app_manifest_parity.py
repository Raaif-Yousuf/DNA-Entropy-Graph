"""Issue #476: every manifest the C# ``WorkerManifestBuilder`` emits (golden files under
``tests/contract-fixtures/app_manifests/``, owned by ``WorkerManifestParityFixtureTests``) must
validate against ``docs/contract/manifest.schema.json`` AND parse with ``JobManifest.parse``.
The C# test fails when the builder drifts from the files; this one fails when the files drift
from the worker."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from dna_entropy.worker.manifest import JobManifest

REPO = Path(__file__).resolve().parents[2]
FIXTURE_DIR = REPO / "tests" / "contract-fixtures" / "app_manifests"
FIXTURES = sorted(FIXTURE_DIR.glob("*.json"))
SCHEMA = json.loads((REPO / "docs" / "contract" / "manifest.schema.json").read_text(encoding="utf-8"))


def _parse(name: str) -> JobManifest:
    return JobManifest.parse((FIXTURE_DIR / f"{name}.json").read_text(encoding="utf-8"))


def test_the_fixture_set_is_not_vacuous() -> None:
    names = {p.stem for p in FIXTURES}
    assert len(FIXTURES) >= 20
    assert {"lifecycle_keepalive_stop", "lifecycle_keepalive_delete", "topology_circular"} <= names


@pytest.mark.parametrize("path", FIXTURES, ids=lambda p: p.stem)
def test_app_manifest_validates_against_the_schema(path: Path) -> None:
    # Required, never skipped: jsonschema is in the dev extra and a skipped check is a vacuous pass.
    import jsonschema

    jsonschema.Draft202012Validator(SCHEMA).validate(json.loads(path.read_text(encoding="utf-8")))


@pytest.mark.parametrize("path", FIXTURES, ids=lambda p: p.stem)
def test_app_manifest_parses_with_the_worker(path: Path) -> None:
    JobManifest.parse(path.read_text(encoding="utf-8"))


def test_keep_alive_survives_the_worker_parse() -> None:
    m = _parse("lifecycle_keepalive_delete")
    assert m.lifecycle.keep_alive_minutes == 45
    assert m.lifecycle.after_task == "keep"
    assert m.lifecycle.after_keep_alive == "delete"


@pytest.mark.parametrize(
    ("name", "value"),
    [("topology_circular", "circular"), ("topology_linear", "linear"), ("topology_auto", "auto")],
)
def test_topology_survives_the_worker_parse(name: str, value: str) -> None:
    assert _parse(name).analysis.topology.value == value


# Written for people and support (docs/job_contract.md), never read by the worker, so undeclared.
INFORMATIONAL_ROOT_KEYS = {"createdAt", "createdBy"}


def _undeclared_keys(node: object, schema: dict, path: str = "") -> list[str]:
    """Keys in ``node`` that ``schema`` does not declare under ``properties`` (objects and arrays).

    The schema allows additional properties, so a key the C# builder renamed would still validate and
    the worker would silently ignore it. Every key an app manifest carries must be declared.
    """
    found: list[str] = []
    if isinstance(node, dict) and isinstance(schema.get("properties"), dict):
        declared = schema["properties"]
        for key, value in node.items():
            if key not in declared:
                if not path and key in INFORMATIONAL_ROOT_KEYS:
                    continue
                found.append(f"{path}/{key}")
            else:
                found.extend(_undeclared_keys(value, declared[key], f"{path}/{key}"))
    elif isinstance(node, list) and isinstance(schema.get("items"), dict):
        for index, item in enumerate(node):
            found.extend(_undeclared_keys(item, schema["items"], f"{path}/{index}"))
    return found


@pytest.mark.parametrize("path", FIXTURES, ids=lambda p: p.stem)
def test_every_key_in_an_app_manifest_is_one_the_worker_declares(path: Path) -> None:
    assert _undeclared_keys(json.loads(path.read_text(encoding="utf-8")), SCHEMA) == []


def test_the_declared_key_check_sees_a_renamed_key() -> None:
    manifest = json.loads((FIXTURE_DIR / "lifecycle_keepalive_delete.json").read_text(encoding="utf-8"))
    manifest["lifecycle"]["keepAliveMins"] = manifest["lifecycle"].pop("keepAliveMinutes")
    assert _undeclared_keys(manifest, SCHEMA) == ["/lifecycle/keepAliveMins"]
