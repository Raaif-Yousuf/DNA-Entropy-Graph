"""The worker subpackage's acceptance test (issue #278): a full fake job, manifest to
result.json, in a temp directory, mock predictor + LocalBlobstore, no GPU, no network.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from dna_entropy.worker.blobstore import LocalBlobstore
from dna_entropy.worker.cancel import CANCEL_PATH
from dna_entropy.worker.manifest import ManifestSchemaError
from dna_entropy.worker.runner import MANIFEST_PATH, RESULT_PATH, run_job

DATA = Path(__file__).parent / "data"


def _write_manifest(store: LocalBlobstore, **overrides) -> dict:
    manifest = {
        "schema": 1,
        "jobId": "20260918-142233-k7q2vx",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {
            "contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only",
        },
        "outputs": ["fasta", "bedgraph", "tsv"],
        "store": {"kind": "localdir", "root": "unused-by-the-test"},
    }
    manifest.update(overrides)
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    return manifest


def _seed_fasta_input(store: LocalBlobstore, path: str = "input/locus.fasta") -> None:
    store.write_text(path, ">seq\n" + "ACGT" * 40 + "\n")  # 160 nt


# --- the acceptance bar itself -----------------------------------------------------


def test_full_fake_job_manifest_to_result_json(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)

    result = run_job(store)

    assert result.status == "done"
    assert result.job_id == "20260918-142233-k7q2vx"
    assert len(result.inputs) == 1
    assert result.inputs[0].status == "done"
    assert result.inputs[0].outputs  # at least one uploaded output path

    # result.json genuinely exists on disk (the app's own terminal-state signal).
    assert store.exists(RESULT_PATH)
    result_doc = json.loads(store.read_text(RESULT_PATH))
    assert result_doc["status"] == "done"
    assert result_doc["schema"] == 1

    # status.json reflects the terminal stage.
    status_doc = json.loads(store.read_text("status.json"))
    assert status_doc["stage"] == "done"
    assert status_doc["heartbeatSeq"] >= 1

    # progress.jsonl has at least the "worker starting" notice.
    progress_lines = [json.loads(l) for l in store.read_text("progress.jsonl").splitlines() if l.strip()]
    assert any("free disk" in p["message"] for p in progress_lines)


def test_uploaded_outputs_are_real_readable_files_under_output_prefix(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)

    result = run_job(store)

    for path in result.inputs[0].outputs:
        assert path.startswith("output/locus/")
        assert store.exists(path)
    fasta_paths = [p for p in result.inputs[0].outputs if p.endswith(".fasta")]
    assert fasta_paths
    text = store.read_text(fasta_paths[0])
    assert text.startswith(">locus")


def test_multi_input_job_produces_a_result_per_input(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "multi-input-job",
        "inputs": [
            {"id": "in1", "path": "input/a.fasta", "name": "a"},
            {"id": "in2", "path": "input/b.fasta", "name": "b"},
        ],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "store": {"kind": "localdir", "root": "unused"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/a.fasta", ">a\n" + "ACGT" * 40 + "\n")
    store.write_text("input/b.fasta", ">b\n" + "TGCA" * 40 + "\n")

    result = run_job(store)

    assert result.status == "done"
    assert {r.id for r in result.inputs} == {"in1", "in2"}
    assert all(r.status == "done" for r in result.inputs)
    assert store.exists("output/a/a.fasta")
    assert store.exists("output/b/b.fasta")


# --- schema mismatch (#250) ----------------------------------------------------------


def test_unsupported_schema_refuses_before_any_status_file_is_written(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store, schema=99)
    _seed_fasta_input(store)

    with pytest.raises(ManifestSchemaError) as exc:
        run_job(store)
    assert "99" in str(exc.value)
    # Nothing was written — there is no useful heartbeat for a manifest the worker
    # cannot even read.
    assert not store.exists("status.json")
    assert not store.exists(RESULT_PATH)


# --- cancellation (docs/job_contract.md §6) --------------------------------------------


def test_cancellation_before_the_job_starts_is_seen_immediately(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)
    store.write_text(CANCEL_PATH, "")  # already cancelled before run_job() is even called

    result = run_job(store)

    assert result.status == "cancelled"
    result_doc = json.loads(store.read_text(RESULT_PATH))
    assert result_doc["status"] == "cancelled"
    status_doc = json.loads(store.read_text("status.json"))
    assert status_doc["stage"] == "cancelled"


def test_cancellation_mid_job_keeps_partial_results_for_completed_inputs(tmp_path: Path) -> None:
    """docs/job_contract.md §6: 'partial results are always kept, never discarded.'"""
    store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "cancel-mid-job",
        "inputs": [
            {"id": "in1", "path": "input/a.fasta", "name": "a"},
            {"id": "in2", "path": "input/b.fasta", "name": "b"},
        ],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "store": {"kind": "localdir", "root": "unused"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/a.fasta", ">a\n" + "ACGT" * 40 + "\n")
    store.write_text("input/b.fasta", ">b\n" + "TGCA" * 40 + "\n")

    # Cancel the instant the first input finishes, by writing control/cancel from inside
    # a wrapped upload_file call (simulating the app writing it mid-run).
    real_upload = store.upload_file
    state = {"uploads": 0}

    def _upload_then_cancel_after_first_input(local_src, path):
        real_upload(local_src, path)
        state["uploads"] += 1
        if state["uploads"] == 1:
            store.write_text(CANCEL_PATH, "")

    store.upload_file = _upload_then_cancel_after_first_input  # type: ignore[method-assign]

    result = run_job(store)

    assert result.status == "cancelled"
    # The first input's output made it into result.json/uploads despite the cancellation.
    assert store.list_prefix("output/a")
    result_doc = json.loads(store.read_text(RESULT_PATH))
    assert result_doc["status"] == "cancelled"


# --- per-input failure isolation (docs/job_contract.md §7) ----------------------------


def test_one_bad_input_fails_alone_job_still_done(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "mixed-job",
        "inputs": [
            {"id": "good", "path": "input/good.fasta", "name": "good"},
            {"id": "bad", "path": "input/bad.fasta", "name": "bad"},
        ],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "store": {"kind": "localdir", "root": "unused"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/good.fasta", ">good\n" + "ACGT" * 40 + "\n")
    store.write_text("input/bad.fasta", ">bad\nNOT-A-VALID-DNA-SEQUENCE-AT-ALL@@@\n")

    result = run_job(store)

    assert result.status == "done"  # job level: "done" even with a failed input, per §7
    by_id = {r.id: r for r in result.inputs}
    assert by_id["good"].status == "done"
    assert by_id["bad"].status == "failed"
    assert by_id["bad"].error is not None
    assert store.exists("output/good/good.fasta")


def test_missing_input_file_fails_that_input_not_the_whole_job(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store, inputs=[{"id": "ghost", "path": "input/does_not_exist.fasta", "name": "ghost"}])
    # Deliberately never seed input/does_not_exist.fasta.

    result = run_job(store)

    assert result.status == "done"
    assert result.inputs[0].status == "failed"


# --- GenBank input end to end (genes preserved) ---------------------------------------


def test_genbank_input_end_to_end(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    gb_bytes = (DATA / "sample.gb").read_bytes()
    store.write_text("input/sample.gb", gb_bytes.decode("utf-8"))
    _write_manifest(
        store,
        inputs=[{"id": "in1", "path": "input/sample.gb", "name": "sample", "genes": True}],
    )

    result = run_job(store)

    assert result.status == "done"
    assert result.inputs[0].status == "done"
    gb_outputs = [p for p in result.inputs[0].outputs if p.endswith(".gb")]
    assert gb_outputs
    assert "LOCUS" in store.read_text(gb_outputs[0])


# --- worker version / manifest.worker echoed into status.json -------------------------


def test_status_records_worker_version_and_declared_image(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store, worker={"image": "ghcr.io/x@sha256:test", "version": "9.9.9"})
    _seed_fasta_input(store)

    run_job(store)

    status_doc = json.loads(store.read_text("status.json"))
    assert status_doc["worker"]["image"] == "ghcr.io/x@sha256:test"
    assert status_doc["worker"]["version"]  # the ACTUAL running worker's own version
