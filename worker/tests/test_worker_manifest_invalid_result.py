"""Issue #1014: a manifest the worker cannot parse still leaves a terminal ``result.json``.

Before this, ``run_job`` let ManifestError propagate before any status.json/result.json
existed, so on a VM the app saw a vanished worker (rc 2, VM stopped) and could only say
"the VM is no longer running". The result goes to the store the worker was STARTED against
(its command line / ``DEG_JOB_URI``), which is known without the manifest.
"""

# ruff: noqa: E501  (the odd-shape manifests are one-line JSON literals)
from __future__ import annotations

import json
from pathlib import Path

import pytest

from dna_entropy.worker import __main__ as worker_main
from dna_entropy.worker import cli as worker_cli
from dna_entropy.worker.blobstore import BlobstoreError, LocalBlobstore
from dna_entropy.worker.manifest import ManifestError, ManifestSchemaError
from dna_entropy.worker.runner import MANIFEST_PATH, RESULT_PATH, run_job


def _good_manifest(**overrides) -> dict:
    manifest = {
        "schema": 1,
        "jobId": "20260918-142233-k7q2vx",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "store": {"kind": "localdir", "root": "unused"},
    }
    manifest.update(overrides)
    return manifest


def _read_result(root: Path) -> dict:
    return json.loads((root / RESULT_PATH).read_text(encoding="utf-8"))


def _assert_manifest_invalid_result(
    result: dict, *, message_has: str, code: str = "MANIFEST_INVALID"
) -> None:
    # The shape the app's WorkerResultReader accepts: schema 1, a recognised job status,
    # an inputs array, and error.code / error.message strings.
    assert result["schema"] == 1
    assert result["status"] == "failed"
    assert result["inputs"] == []
    assert result["error"]["code"] == code
    assert result["error"]["retriable"] is False
    assert message_has in result["error"]["message"]
    assert result["timing"]["startedAt"] and result["timing"]["finishedAt"]
    assert set(result["gpu"]) == {"name", "zone", "spot"}


def test_unparseable_json_writes_a_terminal_result(tmp_path: Path) -> None:
    (tmp_path / MANIFEST_PATH).write_text("{ not json", encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    result = _read_result(tmp_path)
    _assert_manifest_invalid_result(result, message_has="not valid JSON")
    assert result["jobId"] == "unknown"  # nothing readable to take it from; not invented
    assert not (tmp_path / "status.json").exists()


def test_schema_error_writes_a_terminal_result_with_the_job_id(tmp_path: Path) -> None:
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(_good_manifest(schema=99)), encoding="utf-8")
    with pytest.raises(ManifestSchemaError):
        run_job(LocalBlobstore(tmp_path))
    result = _read_result(tmp_path)
    # a schema mismatch keeps its own code (the app has dedicated copy for it)
    _assert_manifest_invalid_result(result, message_has="99", code="WORKER_VERSION_MISMATCH")
    assert result["jobId"] == "20260918-142233-k7q2vx"  # a raw best-effort read of a plain string


def test_wrong_typed_field_names_the_field_in_the_result(tmp_path: Path) -> None:
    manifest = _good_manifest(limits={"maxRunSeconds": "5"})
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    _assert_manifest_invalid_result(_read_result(tmp_path), message_has="maxRunSeconds")


def test_a_hostile_job_id_is_not_copied_into_the_result(tmp_path: Path) -> None:
    manifest = _good_manifest(jobId="../../etc/passwd", schema=99)
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    assert _read_result(tmp_path)["jobId"] == "unknown"


_SECRET = "MySecretLocus.fasta"


def _with(section: str, key: str, value) -> dict:
    manifest = _good_manifest()
    if section == "inputs":
        manifest["inputs"][0][key] = value
    elif section == "":
        manifest[key] = value
    else:
        manifest.setdefault(section, {})[key] = value
    return manifest


# (manifest, the field path the message must carry). Every value is distinctive: a wrong-typed
# or out-of-range value must never be echoed into result.json (the app shows it and the
# diagnostics zip carries it; the project never exposes a sequence or a file name).
_ECHO_CASES = [
    (_with("inputs", "name", [_SECRET]), "inputs[].name"),
    (_with("inputs", "path", {"p": _SECRET}), "inputs[].path"),
    (_with("inputs", "id", 4242.5), "inputs[].id"),
    (_with("inputs", "informat", _SECRET), "inputs[].informat"),
    (_with("inputs", "annotation", [_SECRET]), "inputs[].annotation"),
    (_with("analysis", "direction", _SECRET), "analysis.direction"),
    (_with("analysis", "gcWindow", _SECRET), "analysis.gcWindow"),
    (_with("limits", "maxRunSeconds", _SECRET), "limits.maxRunSeconds"),
    (_with("store", "kind", _SECRET), "store.kind"),
    (_with("", "outputs", _SECRET), "outputs"),
    (_with("", "preset", [_SECRET]), "preset"),
    (_with("", "inputs", [_SECRET]), "inputs"),
]


@pytest.mark.parametrize(("manifest", "field"), _ECHO_CASES)
def test_the_result_names_the_field_but_never_the_value(tmp_path: Path, manifest: dict, field: str) -> None:
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    text = (tmp_path / RESULT_PATH).read_text(encoding="utf-8")
    message = json.loads(text)["error"]["message"]
    assert field in message, message
    for leaked in (_SECRET, "4242.5"):
        assert leaked not in text, message


def test_the_message_is_ascii_and_bounded(tmp_path: Path) -> None:
    # json.loads of a huge key still yields only a field path; the message stays short and ASCII.
    manifest = _good_manifest(inputs=[{"id": "in1", "path": "p", "é" + "x" * 5000: 1, "name": 1}])
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    message = _read_result(tmp_path)["error"]["message"]
    assert message.isascii()
    assert len(message) <= 500


def test_truncation_never_splits_an_escape(tmp_path: Path) -> None:
    from dna_entropy.worker.runner import _record_unreadable_manifest

    store = LocalBlobstore(tmp_path)
    _record_unreadable_manifest(store, "{}", ManifestError("a" + "é" * 400))
    message = _read_result(tmp_path)["error"]["message"]
    # 497 characters: "a" plus 124 whole escapes; a fifth-hundredth-character cut would end in a broken "\x"
    assert message == "a" + "\\xe9" * 124


def test_result_file_is_utf8_with_lf_and_round_trips(tmp_path: Path) -> None:
    (tmp_path / MANIFEST_PATH).write_text("[]", encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    raw = (tmp_path / RESULT_PATH).read_bytes()
    assert b"\r" not in raw
    decoded = json.loads(raw.decode("utf-8"))  # strict UTF-8
    assert decoded["error"] == {
        "code": "MANIFEST_INVALID",
        "message": "manifest.json must be a JSON object",
        "retriable": False,
    }


@pytest.mark.parametrize(
    "text",
    [
        "[]",
        "null",
        "42",
        '"x"',
        '{"schema": 1}',
        '{"schema": 1, "jobId": "j", "inputs": "nope"}',
        '{"schema": 1, "jobId": "j", "inputs": [[]]}',
        '{"schema": 1, "jobId": "j", "inputs": [{"id": "a", "path": "p"}], "store": []}',
        '{"schema": 1, "jobId": "j", "inputs": [{"id": "a", "path": "p"}], "store": {"kind": "localdir"}, "analysis": []}',
        '{"schema": 1, "jobId": "j", "inputs": [{"id": "a", "path": "p"}], "store": {"kind": "localdir"}, '
        '"analysis": {"smoothingWindows": [null]}}',
        '{"schema": 1, "jobId": "j", "inputs": [{"id": "a", "path": "p"}], "store": {"kind": "localdir"}, '
        '"analysis": {"smoothingWindows": ["x"]}}',
        '{"schema": 1, "jobId": "j", "inputs": [{"id": "a", "path": "p"}], "store": {"kind": "localdir"}, "preset": null}',
        '{"schema": 1, "jobId": "j", "inputs": [{"id": "a", "path": "p"}], "store": {"kind": "localdir"}, "outputs": [1]}',
    ],
)
def test_odd_shapes_only_ever_raise_manifest_error(tmp_path: Path, text: str) -> None:
    (tmp_path / MANIFEST_PATH).write_text(text, encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    assert _read_result(tmp_path)["error"]["code"] == "MANIFEST_INVALID"


class _ReadOnlyStore:
    """A store whose manifest reads fine but whose writes all fail (the destination is not writable)."""

    def __init__(self, inner: LocalBlobstore) -> None:
        self._inner = inner

    def read_text(self, path: str) -> str:
        return self._inner.read_text(path)

    def write_text(self, path: str, text: str) -> None:
        raise BlobstoreError("simulated: store not writable")


def test_an_unwritable_store_still_raises_the_manifest_error_and_writes_nothing(tmp_path: Path) -> None:
    (tmp_path / MANIFEST_PATH).write_text("{ not json", encoding="utf-8")
    with pytest.raises(ManifestError, match="not valid JSON"):  # the original error, not the write failure
        run_job(_ReadOnlyStore(LocalBlobstore(tmp_path)))  # type: ignore[arg-type]
    assert not (tmp_path / RESULT_PATH).exists()


def test_entrypoint_exit_code_stays_two_with_an_error_line(tmp_path: Path, capsys) -> None:
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(_good_manifest(schema=99)), encoding="utf-8")
    rc = worker_cli.main(["run", "--store", "localdir", "--root", str(tmp_path)])
    assert rc == 2
    err = capsys.readouterr().err
    assert err.startswith("ERROR: ") and err.isascii()
    _assert_manifest_invalid_result(_read_result(tmp_path), message_has="99", code="WORKER_VERSION_MISMATCH")


def test_module_entrypoint_exit_code_stays_two(tmp_path: Path, capsys) -> None:
    manifest = _good_manifest(limits={"maxRunSeconds": "5"})
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(manifest), encoding="utf-8")
    rc = worker_main.main(["--manifest-store", "local", "--root", str(tmp_path)])
    assert rc == 2
    assert capsys.readouterr().err.startswith("ERROR: ")
    _assert_manifest_invalid_result(_read_result(tmp_path), message_has="maxRunSeconds")
