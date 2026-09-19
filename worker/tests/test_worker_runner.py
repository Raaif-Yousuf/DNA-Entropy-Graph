"""The worker subpackage's acceptance test (issue #278): a full fake job, manifest to
result.json, in a temp directory, mock predictor + LocalBlobstore, no GPU, no network.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from dna_entropy.worker.blobstore import BlobstoreError, LocalBlobstore
from dna_entropy.worker.cancel import CANCEL_PATH
from dna_entropy.worker.manifest import ManifestSchemaError
from dna_entropy.worker.runner import MANIFEST_PATH, RESULT_PATH, run_job

DATA = Path(__file__).parent / "data"


class _FlakyStore:
    """Wraps a real blobstore, making ``fail_method`` raise :class:`BlobstoreError` the
    first ``fail_times`` calls, then delegating normally (issues #318/#319/#320: a
    transient GCS blip, simulated without any real network)."""

    def __init__(self, inner, *, fail_method: str, fail_times: int) -> None:
        self._inner = inner
        self._fail_method = fail_method
        self._fail_times = fail_times
        self.call_count = 0

    def __getattr__(self, name: str):
        attr = getattr(self._inner, name)
        if name != self._fail_method:
            return attr

        def _flaky(*args, **kwargs):
            self.call_count += 1
            if self.call_count <= self._fail_times:
                raise BlobstoreError(f"simulated transient failure #{self.call_count}")
            return attr(*args, **kwargs)

        return _flaky


def _write_manifest(store: LocalBlobstore, **overrides) -> dict:
    manifest = {
        "schema": 1,
        "jobId": "20260918-142233-k7q2vx",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {
            "contextLength": 128,
            "window": 256,
            "stride": 128,
            "direction": "forward-only",
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
    assert result.jobId == "20260918-142233-k7q2vx"
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
    progress_lines = [
        json.loads(line) for line in store.read_text("progress.jsonl").splitlines() if line.strip()
    ]
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
        # issue #338: CancelWatcher now throttles its real store round-trip to
        # limits.cancelPollSeconds (default 10s) of wall-clock time. This test writes
        # control/cancel and expects the VERY NEXT check to see it, well under a real 10s
        # -- cancelPollSeconds=0 means "always due", the pre-#338 behavior this test
        # actually needs (it is testing cancellation propagation, not the throttle).
        "limits": {"cancelPollSeconds": 0},
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


# --- batch-level cost guardrails (issue #248) ------------------------------------------


def test_batch_exceeding_max_inputs_is_refused_before_any_processing(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "too-many-files",
        "inputs": [
            {"id": "in1", "path": "input/a.fasta", "name": "a"},
            {"id": "in2", "path": "input/b.fasta", "name": "b"},
        ],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "limits": {"maxInputs": 1},  # the batch has 2
        "store": {"kind": "localdir", "root": "unused"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/a.fasta", ">a\n" + "ACGT" * 40 + "\n")
    store.write_text("input/b.fasta", ">b\n" + "ACGT" * 40 + "\n")

    result = run_job(store)

    assert result.status == "failed"
    assert result.error is not None
    assert result.error["code"] == "BATCH_LIMIT_EXCEEDED"
    assert "2" in result.error["message"]  # the real file count, not a bare "too large"
    assert result.inputs == []  # refused before any per-input work happened
    assert not store.list_prefix("output/")  # nothing uploaded, nothing run


def test_batch_exceeding_max_total_nt_is_refused_before_any_processing(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "too-much-nt",
        "inputs": [{"id": "in1", "path": "input/a.fasta", "name": "a"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "limits": {"maxTotalNt": 10},  # the one input is 160 nt
        "store": {"kind": "localdir", "root": "unused"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/a.fasta", ">a\n" + "ACGT" * 40 + "\n")  # 160 nt

    result = run_job(store)

    assert result.status == "failed"
    assert result.error["code"] == "BATCH_LIMIT_EXCEEDED"
    assert "160" in result.error["message"]  # the real measured total, not a bare "too large"
    assert result.inputs == []
    assert not store.list_prefix("output/")


def test_batch_within_configured_limits_proceeds_normally(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "within-limits",
        "inputs": [{"id": "in1", "path": "input/a.fasta", "name": "a"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "limits": {"maxInputs": 5, "maxTotalNt": 1000},
        "store": {"kind": "localdir", "root": "unused"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/a.fasta", ">a\n" + "ACGT" * 40 + "\n")  # 160 nt, well under 1000

    result = run_job(store)

    assert result.status == "done"
    assert result.inputs[0].status == "done"


def test_default_limits_apply_when_the_manifest_omits_the_limits_section(tmp_path: Path) -> None:
    """A manifest that never mentions `limits` at all must still get the worker's own
    default cost guardrail, not an unbounded batch."""
    from dna_entropy.worker.batch_limits import DEFAULT_MAX_INPUTS, DEFAULT_MAX_TOTAL_NT
    from dna_entropy.worker.manifest import JobManifest

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)  # no "limits" key at all
    _seed_fasta_input(store)

    m = JobManifest.parse(store.read_text(MANIFEST_PATH))
    assert m.limits.max_inputs == DEFAULT_MAX_INPUTS
    assert m.limits.max_total_nt == DEFAULT_MAX_TOTAL_NT

    result = run_job(store)
    assert result.status == "done"  # a single 160 nt input is nowhere near either default


# --- partial results survive a mid-input crash, not just a mid-job cancel (#252) ------
#
# job_contract.md §6 already promises this for cancellation ("uploads whatever outputs
# were already produced ... partial results are always kept, never discarded"), and
# pipeline.run() ALREADY writes whatever contigs completed to local disk, best-effort,
# before re-raising ANY exception from its per-contig loop -- cancellation is just the
# one case that happened to be exercised. The gap: `_run_one_input` never uploaded those
# local files when pipeline.run() raised, for either cause. Both tests below use the
# SAME fix (one upload path, not two) via the two different triggers.


def _seed_three_record_fasta_input(store: LocalBlobstore, path: str = "input/locus.fasta") -> None:
    store.write_text(
        path,
        ">rec1\n" + "ACGT" * 20 + "\n>rec2\n" + "ACGT" * 20 + "\n>rec3\n" + "ACGT" * 20 + "\n",
    )


def test_crash_partway_through_a_multi_record_input_uploads_completed_contigs(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A crash on the 3rd of 3 records must not discard the first 2 records' work: their
    output files (written to local disk by pipeline.run()'s own best-effort partial
    write) must already be uploaded by the time the input's "failed" InputResult is
    returned, and result.json must point at them."""
    import dna_entropy.pipeline as pipeline_module
    from dna_entropy.predictors.mock import MockPredictor

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_three_record_fasta_input(store)

    calls = {"n": 0}

    class _CrashOnThirdRecord:
        def __init__(self, seed: int = 0) -> None:
            self._inner = MockPredictor(seed=seed)

        def predict(self, seq):
            calls["n"] += 1
            if calls["n"] == 3:
                raise RuntimeError("simulated crash analyzing the third record")
            return self._inner.predict(seq)

    monkeypatch.setattr(pipeline_module, "MockPredictor", _CrashOnThirdRecord)

    result = run_job(store)

    assert result.status == "done"  # job level: one bad input doesn't fail the whole job
    assert result.inputs[0].status == "failed"
    assert result.inputs[0].error is not None
    assert calls["n"] == 3  # proves the crash really happened on the 3rd record, not the 1st

    uploaded = store.list_prefix("output/locus")
    assert uploaded, "the 2 completed records' outputs were never uploaded"
    assert result.inputs[0].outputs, "result.json does not point at the uploaded partial outputs"
    for path in result.inputs[0].outputs:
        assert path in uploaded


def test_cancellation_partway_through_one_inputs_records_uploads_completed_contigs_first(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Cancellation seen BETWEEN two records of the SAME input (not between two whole
    inputs, which #278/§6's own test already covers) must still upload the record(s) that
    finished first, and record this input as "cancelled" -- not silently drop it out of
    result.json, which would orphan the very files this test proves get uploaded."""
    import dna_entropy.pipeline as pipeline_module
    from dna_entropy.predictors.mock import MockPredictor

    store = LocalBlobstore(tmp_path)
    # issue #338: cancelPollSeconds=0 -- see the sibling cancellation test above for why
    # this test needs "always due" rather than the manifest's real 10s default throttle.
    _write_manifest(store, limits={"cancelPollSeconds": 0})
    _seed_three_record_fasta_input(store)

    calls = {"n": 0}

    class _CancelOnSecondCall:
        def __init__(self, seed: int = 0) -> None:
            self._inner = MockPredictor(seed=seed)

        def predict(self, seq):
            calls["n"] += 1
            if calls["n"] == 2:
                store.write_text(CANCEL_PATH, "")
            return self._inner.predict(seq)  # 2nd record's own prediction still "succeeds"...

    monkeypatch.setattr(pipeline_module, "MockPredictor", _CancelOnSecondCall)

    result = run_job(store)

    assert result.status == "cancelled"
    assert len(result.inputs) == 1  # the input is NOT silently missing from result.json
    assert result.inputs[0].status == "cancelled"
    # ...but the cancellation is seen right after, so only the 1st record's work survives.
    assert calls["n"] == 2

    uploaded = store.list_prefix("output/locus")
    assert uploaded, "the 1 completed record's outputs were never uploaded"
    assert result.inputs[0].outputs
    for path in result.inputs[0].outputs:
        assert path in uploaded


# --- a status-write blip must never fail real work (issues #318/#319/#320) ------------


def test_a_transient_status_write_blip_during_processing_does_not_fail_a_good_input(
    tmp_path: Path,
) -> None:
    """issue #319: status.update()/notice() run synchronously inside pipeline.run()'s
    per-contig hook (_on_contig), inside _run_one_input's catch-all `except Exception`.
    Before the fix, a transient status-write blip there was indistinguishable from a
    real pipeline failure, and correctly computed work was discarded as "failed" /
    INPUT_INVALID because telling someone about it didn't work."""
    real_store = LocalBlobstore(tmp_path)
    _write_manifest(real_store)
    _seed_three_record_fasta_input(real_store)

    # write_text underlies status.json/progress.jsonl only (input staging uses
    # download_file/upload_file, not write_text) -- fails the first 2 status-ish writes,
    # landing squarely inside the per-record processing window, not job setup (which
    # already happened above, against the real store).
    flaky = _FlakyStore(real_store, fail_method="write_text", fail_times=2)

    result = run_job(flaky)

    assert result.status == "done"
    assert result.inputs[0].status == "done"  # NOT "failed" -- the blip must not count
    assert result.inputs[0].error is None
    assert flaky.call_count >= 2, "the test never actually exercised the injected failure"


def test_result_json_write_failure_still_stops_status_and_applies_lifecycle(
    tmp_path: Path,
) -> None:
    """issue #320: write_json(RESULT_PATH), status.stop(), and apply_lifecycle() used to
    run unguarded in sequence -- a BlobstoreError writing result.json skipped both the
    status stop and the lifecycle call. Here the store is "gcs"-kind so apply_lifecycle
    would be attempted; lifecycle.py's own metadata-server call fails immediately in this
    sandbox (no real VM), which must be caught and logged, not left to crash run_job or
    to silently skip status.stop()."""
    real_store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "result-write-blip",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "lifecycle": {"afterTask": "stop"},
        "store": {"kind": "gcs", "bucket": "fake-bucket", "prefix": "jobs/x/"},
    }
    real_store.write_text(MANIFEST_PATH, json.dumps(manifest))
    real_store.write_text("input/locus.fasta", ">seq\n" + "ACGT" * 40 + "\n")

    # Only RESULT_PATH's own write is made to fail (targeted, not every write_text call --
    # a real StatusWriter write failure is already covered by the #318/#319 tests above).
    real_write_text = real_store.write_text

    def _fail_only_result_json(path, text):
        if path == RESULT_PATH:
            raise BlobstoreError("simulated failure writing result.json")
        return real_write_text(path, text)

    real_store.write_text = _fail_only_result_json  # type: ignore[method-assign]

    result = run_job(real_store)  # must not raise despite the injected failure

    assert result.status == "done"  # the in-memory result still reflects the real outcome
    # status.json's LAST write still happened (status.stop() was not skipped): its own
    # write_text call is for "status.json", not RESULT_PATH, so it went through the real
    # (non-failing) path above and is readable.
    real_store.write_text = real_write_text  # restore before reading, for clarity
    status_doc = json.loads(real_store.read_text("status.json"))
    assert status_doc["stage"] == "done"


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
        # issue #304: outputs is now honored literally, and _write_manifest's own default
        # ("fasta", "bedgraph", "tsv") does not include "genbank" -- this test wants the
        # .gb file, so it must ask for it explicitly, same as any other suppressible output.
        outputs=["genbank", "fasta", "bedgraph", "tsv"],
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


# --- generated schema validates the REAL output (issue #39's whole point) -------------


def test_real_result_json_validates_against_the_generated_schema(tmp_path: Path) -> None:
    jsonschema = pytest.importorskip("jsonschema")
    from dna_entropy.worker.runner import JobResult
    from dna_entropy.worker.schema_gen import top_level_schema

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)
    run_job(store)

    schema = top_level_schema(JobResult, schema_id="x", title="JobResult")
    result_doc = json.loads(store.read_text(RESULT_PATH))
    jsonschema.Draft202012Validator(schema).validate(result_doc)


def test_real_status_json_validates_against_the_generated_schema(tmp_path: Path) -> None:
    jsonschema = pytest.importorskip("jsonschema")
    from dna_entropy.worker.schema_gen import top_level_schema
    from dna_entropy.worker.status import StatusDocument

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)
    run_job(store)

    schema = top_level_schema(StatusDocument, schema_id="x", title="StatusDocument")
    status_doc = json.loads(store.read_text("status.json"))
    jsonschema.Draft202012Validator(schema).validate(status_doc)


def test_job_result_to_dict_matches_its_own_dataclass_shape() -> None:
    """Regression guard for the exact bug this refactor fixed: a hand-written to_dict()
    that nests timing/gpu without those being real dataclass fields, silently drifting
    from what the schema generator (which only sees real fields) would produce."""
    import dataclasses

    from dna_entropy.worker.result import ResultGpu
    from dna_entropy.worker.runner import JobResult, ResultTiming

    result = JobResult(
        schema=1,
        jobId="x",
        status="done",
        inputs=[],
        timing=ResultTiming(startedAt="a", finishedAt="b"),
        gpu=ResultGpu(),
    )
    assert set(result.to_dict().keys()) == {f.name for f in dataclasses.fields(JobResult)}


# --- error-code fidelity (issue #254): a specific exception's own .code must survive ---
# all the way into the InputResult, never be flattened to the generic INPUT_INVALID.


def test_model_needs_hopper_reports_its_own_code_not_input_invalid(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from dna_entropy.predictors.hardware import ModelNeedsHopperError
    from dna_entropy.worker import runner as runner_module

    def _raise_hopper(*args, **kwargs):
        raise ModelNeedsHopperError("evo2_40b needs an H100-class GPU; use evo2_7b instead.")

    monkeypatch.setattr(runner_module.pipeline, "run", _raise_hopper)

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)

    result = run_job(store)

    assert result.status == "done"  # job level: one bad input, still "done" per §7
    assert result.inputs[0].status == "failed"
    assert result.inputs[0].error["code"] == "MODEL_NEEDS_HOPPER"
    assert result.inputs[0].error["retriable"] is False


def test_second_oom_reports_model_oom_not_input_invalid(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from dna_entropy.predictors.base import PredictorOOMError
    from dna_entropy.worker import runner as runner_module

    def _raise_oom(*args, **kwargs):
        raise PredictorOOMError("out of memory (simulated second OOM)")

    monkeypatch.setattr(runner_module.pipeline, "run", _raise_oom)

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)

    result = run_job(store)

    assert result.inputs[0].error["code"] == "MODEL_OOM"
    assert result.inputs[0].error["retriable"] is True  # per the errors.py registry


def test_plain_validation_error_still_reports_input_invalid(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The fallback path (no .code on the exception) must still work correctly."""
    from dna_entropy.validation.validators import ValidationError
    from dna_entropy.worker import runner as runner_module

    def _raise_validation(*args, **kwargs):
        raise ValidationError("bad sequence")

    monkeypatch.setattr(runner_module.pipeline, "run", _raise_validation)

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)

    result = run_job(store)

    assert result.inputs[0].error["code"] == "INPUT_INVALID"
    assert result.inputs[0].error["retriable"] is False


def test_unrecognized_exception_falls_back_to_worker_crash(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from dna_entropy.worker import runner as runner_module

    def _raise_odd(*args, **kwargs):
        raise RuntimeError("something nobody registered a code for")

    monkeypatch.setattr(runner_module.pipeline, "run", _raise_odd)

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    _seed_fasta_input(store)

    result = run_job(store)

    assert result.inputs[0].error["code"] == "WORKER_CRASH"
    assert "detail" in result.inputs[0].error  # traceback attached for WORKER_CRASH only


# --- lifecycle.afterTask == "keep" must never be an unbounded no-op (Hard Rule 11) -----


def test_after_task_keep_applies_after_keep_alive_instead_of_running_forever(
    tmp_path: Path,
) -> None:
    """Found auditing #304/#306: manifest.lifecycle.keepAliveMinutes/afterKeepAlive were
    parsed but never consumed anywhere, and run_job's own lifecycle block skipped
    apply_lifecycle ENTIRELY whenever afterTask == "keep" -- meaning a manifest asking
    for "keep" left the VM running with literally zero worker-side enforcement of any
    kind, violating Hard Rule 11 ("keep alive always has an expiry, never indefinitely").
    The real keep-alive queue/idle-timer feature is issue #93 and is not built here;
    until it is, "keep" must safely degrade to afterKeepAlive (default "stop") rather
    than a true no-op, with a loud notice explaining why."""
    real_store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "keep-job",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "lifecycle": {"afterTask": "keep", "keepAliveMinutes": 30, "afterKeepAlive": "delete"},
        "store": {"kind": "gcs", "bucket": "fake-bucket", "prefix": "jobs/x/"},
    }
    real_store.write_text(MANIFEST_PATH, json.dumps(manifest))
    real_store.write_text("input/locus.fasta", ">seq\n" + "ACGT" * 40 + "\n")

    result = run_job(real_store)  # no real metadata server in this sandbox

    assert result.status == "done"
    progress_lines = [
        json.loads(line) for line in real_store.read_text("progress.jsonl").splitlines() if line.strip()
    ]
    messages = [p["message"] for p in progress_lines]
    # The substitution actually happened (not skipped): a notice names it...
    assert any("afterTask='keep'" in m and "afterKeepAlive='delete'" in m for m in messages)
    # ...and apply_lifecycle was actually ATTEMPTED with the substituted action (there is
    # no real metadata server in this sandbox, so it fails -- proving it was called at
    # all, which the old "skip lifecycle entirely for keep" behavior never did).
    assert any("lifecycle apply failed" in m for m in messages)


def test_after_task_keep_with_after_keep_alive_also_keep_falls_back_to_stop(
    tmp_path: Path,
) -> None:
    """A malformed manifest asking for "keep" both ways must not loop or truly no-op --
    the worker forces a safe, terminating default ("stop") rather than propagate a second
    "keep"."""
    real_store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": "double-keep-job",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "lifecycle": {"afterTask": "keep", "afterKeepAlive": "keep"},
        "store": {"kind": "gcs", "bucket": "fake-bucket", "prefix": "jobs/x/"},
    }
    real_store.write_text(MANIFEST_PATH, json.dumps(manifest))
    real_store.write_text("input/locus.fasta", ">seq\n" + "ACGT" * 40 + "\n")

    result = run_job(real_store)

    assert result.status == "done"
    progress_lines = [
        json.loads(line) for line in real_store.read_text("progress.jsonl").splitlines() if line.strip()
    ]
    messages = [p["message"] for p in progress_lines]
    assert any("lifecycle apply failed" in m for m in messages)  # stop was attempted, not skipped


# --- limits.heartbeatSeconds must actually reach StatusWriter's tick interval ----------


def test_heartbeat_seconds_from_manifest_reaches_status_writer_interval(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Found auditing #304/#306: manifest.limits.heartbeatSeconds was parsed but
    run_job() built StatusWriter with no interval_seconds at all, so it always used
    status.py's own hardcoded DEFAULT_INTERVAL_SECONDS regardless of what the manifest
    declared -- CLAUDE.md calls the status.json heartbeat the ONLY health signal an app
    has, so the app and the worker silently disagreeing about the intended cadence is a
    real, not cosmetic, gap."""
    from dna_entropy.worker import runner as runner_module
    from dna_entropy.worker.status import DEFAULT_INTERVAL_SECONDS, StatusWriter

    captured: dict = {}
    real_init = StatusWriter.__init__

    def _spy_init(self, *args, **kwargs):
        captured["interval_seconds"] = kwargs.get("interval_seconds")
        real_init(self, *args, **kwargs)

    monkeypatch.setattr(StatusWriter, "__init__", _spy_init)

    store = LocalBlobstore(tmp_path)
    _write_manifest(store, limits={"heartbeatSeconds": 2})
    _seed_fasta_input(store)

    runner_module.run_job(store)

    # Never SLOWER than the manifest's own declared cadence -- and never faster than
    # necessary either, so this is a real wiring check, not a "some number or other" one.
    assert captured["interval_seconds"] == min(2.0, DEFAULT_INTERVAL_SECONDS)


def test_heartbeat_seconds_default_still_matches_pre_existing_behavior(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from dna_entropy.worker import runner as runner_module
    from dna_entropy.worker.status import DEFAULT_INTERVAL_SECONDS, StatusWriter

    captured: dict = {}
    real_init = StatusWriter.__init__

    def _spy_init(self, *args, **kwargs):
        captured["interval_seconds"] = kwargs.get("interval_seconds")
        real_init(self, *args, **kwargs)

    monkeypatch.setattr(StatusWriter, "__init__", _spy_init)

    store = LocalBlobstore(tmp_path)
    _write_manifest(store)  # no "limits" override -- heartbeatSeconds defaults to 30
    _seed_fasta_input(store)

    runner_module.run_job(store)

    assert captured["interval_seconds"] == min(30.0, DEFAULT_INTERVAL_SECONDS)
