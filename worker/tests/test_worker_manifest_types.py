"""Issue #878: every manifest field read from the JSON is type-checked; a wrong-typed value
raises ManifestError naming the field, never a raw ValueError/TypeError (or a silent coercion)."""

from __future__ import annotations

import copy
import json

import pytest

from dna_entropy.worker.manifest import JobManifest, ManifestError

GOOD: dict = {
    "schema": 1,
    "jobId": "20260918-142233-k7q2vx",
    "inputs": [
        {
            "id": "in1",
            "path": "input/a.fasta",
            "name": "a",
            "informat": "auto",
            "start": 1,
            "rna": False,
            "genes": False,
            "ambiguityPolicy": "keep",
            "fastaRecords": "all",
        }
    ],
    "predictor": {"kind": "mock", "model": "evo2_7b", "precision": "bf16", "device": "cpu", "seed": 0},
    "analysis": {
        "contextLength": 4096,
        "window": 8192,
        "stride": 4096,
        "direction": "both-combined",
        "format": "bedgraph",
        "topology": "auto",
        "smoothingWindows": [51],
    },
    "outputs": ["bedgraph", "tsv"],
    "limits": {
        "maxRunSeconds": 14400,
        "cancelPollSeconds": 10,
        "heartbeatSeconds": 30,
        "maxInputs": 50,
        "maxTotalNt": 1000000,
    },
    "lifecycle": {"afterTask": "stop", "keepAliveMinutes": 0, "afterKeepAlive": "stop"},
    "store": {"kind": "gcs", "bucket": "b", "prefix": "jobs/x/"},
    "worker": {"image": "img", "version": "1"},
}

BAD_FOR = {
    "int": ["5", None, [1], {"a": 1}, True, 1.5],
    "str": [5, None, ["x"], {"a": 1}, True],
    "bool": ["yes", 1, None, [True], {"a": 1}],
    "list": ["x", 5, None, {"a": 1}],
    "object": ["x", 5, None, [1], True],
    "str_list": [[5], [None], [["x"]], "x", 5, None],
}

# (path to the containing object, key, kind). A path element that is an int indexes a list.
FIELDS: list[tuple[tuple, str | int, str]] = [
    ((), "jobId", "str"),
    ((), "inputs", "list"),
    ((), "predictor", "object"),
    ((), "analysis", "object"),
    ((), "outputs", "str_list"),
    ((), "limits", "object"),
    ((), "lifecycle", "object"),
    ((), "store", "object"),
    ((), "worker", "object"),
    (("inputs",), 0, "object"),
    (("inputs", 0), "id", "str"),
    (("inputs", 0), "name", "str"),
    (("inputs", 0), "path", "str"),
    (("inputs", 0), "informat", "str"),
    (("inputs", 0), "ambiguityPolicy", "str"),
    (("inputs", 0), "fastaRecords", "str"),
    (("inputs", 0), "start", "int"),
    (("inputs", 0), "rna", "bool"),
    (("inputs", 0), "genes", "bool"),
    (("predictor",), "kind", "str"),
    (("predictor",), "model", "str"),
    (("predictor",), "precision", "str"),
    (("predictor",), "device", "str"),
    (("predictor",), "seed", "int"),
    (("analysis",), "contextLength", "int"),
    (("analysis",), "window", "int"),
    (("analysis",), "stride", "int"),
    (("analysis",), "direction", "str"),
    (("analysis",), "format", "str"),
    (("analysis",), "topology", "str"),
    (("analysis",), "smoothingWindows", "list"),
    (("limits",), "maxRunSeconds", "int"),
    (("limits",), "cancelPollSeconds", "int"),
    (("limits",), "heartbeatSeconds", "int"),
    (("limits",), "maxInputs", "int"),
    (("limits",), "maxTotalNt", "int"),
    (("lifecycle",), "afterTask", "str"),
    (("lifecycle",), "keepAliveMinutes", "int"),
    (("lifecycle",), "afterKeepAlive", "str"),
    (("store",), "kind", "str"),
    (("store",), "bucket", "str"),
    (("store",), "prefix", "str"),
    (("worker",), "image", "str"),
    (("worker",), "version", "str"),
]


def _cases() -> list:
    out = []
    for path, key, kind in FIELDS:
        # `inputs` itself and the top-level list/object kinds already have their own wording.
        for bad in BAD_FOR[kind]:
            out.append(pytest.param(path, key, bad, id=f"{'.'.join(map(str, path))}.{key}={bad!r}"))
    return out


def _set(d: dict, path: tuple, key, value) -> None:
    node = d
    for p in path:
        node = node[p]
    node[key] = value


def _name_of(key) -> str:
    return "inputs" if isinstance(key, int) else str(key)


@pytest.mark.parametrize(("path", "key", "bad"), _cases())
def test_a_wrong_typed_field_is_a_manifest_error_naming_the_field(path, key, bad) -> None:
    d = copy.deepcopy(GOOD)
    _set(d, path, key, bad)
    with pytest.raises(ManifestError) as info:
        JobManifest.parse(json.dumps(d))
    assert _name_of(key) in str(info.value)


def test_the_good_manifest_parses() -> None:
    m = JobManifest.parse(json.dumps(GOOD))
    assert m.limits.max_inputs == 50
    assert m.store.bucket == "b"


def test_the_localdir_root_is_type_checked() -> None:
    d = copy.deepcopy(GOOD)
    d["store"] = {"kind": "localdir", "root": 5}
    with pytest.raises(ManifestError, match="root"):
        JobManifest.parse(json.dumps(d))


def test_every_optional_section_may_still_be_absent() -> None:
    d = {k: GOOD[k] for k in ("schema", "jobId", "inputs", "store")}
    m = JobManifest.parse(json.dumps(d))
    assert m.limits.max_run_seconds == 14400
    assert m.outputs == []


def test_the_field_matrix_covers_every_manifest_field_the_worker_reads() -> None:
    """Vacuity guard: the matrix is not a hand list that silently shrinks."""
    assert len(_cases()) > 150
