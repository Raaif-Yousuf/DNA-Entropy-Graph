"""Issue #1014: a manifest the worker cannot parse still leaves a terminal ``result.json``.

Before this, ``run_job`` let ManifestError propagate before any status.json/result.json
existed, so on a VM the app saw a vanished worker (rc 2, VM stopped) and could only say
"the VM is no longer running". The result goes to the store the worker was STARTED against
(its command line / ``DEG_JOB_URI``), which is known without the manifest.
"""

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


def _assert_manifest_invalid_result(result: dict, *, message_has: str) -> None:
    # The shape the app's WorkerResultReader accepts: schema 1, a recognised job status,
    # an inputs array, and error.code / error.message strings.
    assert result["schema"] == 1
    assert result["status"] == "failed"
    assert result["inputs"] == []
    assert result["error"]["code"] == "MANIFEST_INVALID"
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
    _assert_manifest_invalid_result(result, message_has="99")
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


def test_the_message_is_ascii_and_bounded(tmp_path: Path) -> None:
    manifest = _good_manifest(inputs=["é" + "x" * 5000])
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    message = _read_result(tmp_path)["error"]["message"]
    assert message.isascii()
    assert len(message) <= 600


def test_result_file_is_utf8_with_lf(tmp_path: Path) -> None:
    (tmp_path / MANIFEST_PATH).write_text("[]", encoding="utf-8")
    with pytest.raises(ManifestError):
        run_job(LocalBlobstore(tmp_path))
    raw = (tmp_path / RESULT_PATH).read_bytes()
    assert b"\r" not in raw


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
    _assert_manifest_invalid_result(_read_result(tmp_path), message_has="99")


def test_module_entrypoint_exit_code_stays_two(tmp_path: Path, capsys) -> None:
    manifest = _good_manifest(limits={"maxRunSeconds": "5"})
    (tmp_path / MANIFEST_PATH).write_text(json.dumps(manifest), encoding="utf-8")
    rc = worker_main.main(["--manifest-store", "local", "--root", str(tmp_path)])
    assert rc == 2
    assert capsys.readouterr().err.startswith("ERROR: ")
    _assert_manifest_invalid_result(_read_result(tmp_path), message_has="maxRunSeconds")
