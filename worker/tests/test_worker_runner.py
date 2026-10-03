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
from dna_entropy.worker.runner import MANIFEST_PATH, RESULT_PATH, run_job, run_job_outcome

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


def _keep_store(tmp_path: Path, job_id: str, lifecycle: dict) -> LocalBlobstore:
    store = LocalBlobstore(tmp_path)
    manifest = {
        "schema": 1,
        "jobId": job_id,
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "lifecycle": lifecycle,
        "store": {"kind": "gcs", "bucket": "fake-bucket", "prefix": "jobs/x/"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/locus.fasta", ">seq\n" + "ACGT" * 40 + "\n")
    return store


def _messages(store: LocalBlobstore) -> list[str]:
    return [json.loads(ln)["message"] for ln in store.read_text("progress.jsonl").splitlines() if ln.strip()]


@pytest.fixture
def lifecycle_calls(monkeypatch: pytest.MonkeyPatch) -> list[str]:
    calls: list[str] = []
    monkeypatch.setattr(
        "dna_entropy.worker.runner.apply_lifecycle", lambda action, **_: calls.append(action) or {}
    )
    return calls


def test_keep_with_a_window_is_left_to_startup_sh_which_enforces_the_expiry(
    tmp_path: Path, lifecycle_calls: list[str]
) -> None:
    """Issue #464: a keep-alive run used to be stopped by the worker the moment the job
    ended (keep degraded to afterKeepAlive at once), so the user's window never happened.
    With keepAliveMinutes > 0 the worker now makes NO Compute call and reports no
    lifecycle applied (so `dna-entropy-worker run` exits 0/2/3, not 10/11); worker/vm/
    startup.sh holds the VM for the window and then applies afterKeepAlive."""
    store = _keep_store(
        tmp_path, "keep-job", {"afterTask": "keep", "keepAliveMinutes": 30, "afterKeepAlive": "delete"}
    )

    outcome = run_job_outcome(store)

    assert outcome.result.status == "done"
    assert lifecycle_calls == []
    assert outcome.lifecycle_applied is None
    assert any("30 minutes" in m and "'delete'" in m and "startup.sh" in m for m in _messages(store))


def test_keep_with_no_window_degrades_to_after_keep_alive_immediately(
    tmp_path: Path, lifecycle_calls: list[str]
) -> None:
    """Hard Rule 11: with no window there is nothing to hold the VM for, so "keep" must
    not be an unbounded no-op; it applies afterKeepAlive now, with a notice."""
    store = _keep_store(tmp_path, "keep-0", {"afterTask": "keep", "afterKeepAlive": "delete"})

    outcome = run_job_outcome(store)

    assert lifecycle_calls == ["delete"]
    assert outcome.lifecycle_applied == "delete"
    assert any("afterTask='keep'" in m and "afterKeepAlive='delete'" in m for m in _messages(store))


def test_keep_with_after_keep_alive_also_keep_and_no_window_falls_back_to_stop(
    tmp_path: Path, lifecycle_calls: list[str]
) -> None:
    """A malformed manifest asking for "keep" both ways must not truly no-op: the worker
    forces a terminating default."""
    store = _keep_store(tmp_path, "double-keep", {"afterTask": "keep", "afterKeepAlive": "keep"})

    run_job_outcome(store)

    assert lifecycle_calls == ["stop"]


@pytest.mark.parametrize("task", ["stop", "delete"])
def test_stop_and_delete_are_still_applied_by_the_worker_itself(
    tmp_path: Path, lifecycle_calls: list[str], task: str
) -> None:
    """Neighbour: only keep-with-a-window moved to the script."""
    store = _keep_store(tmp_path, f"plain-{task}", {"afterTask": task, "keepAliveMinutes": 30})

    outcome = run_job_outcome(store)

    assert lifecycle_calls == [task]
    assert outcome.lifecycle_applied == task


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


# --- manifest.worker.version surfaced on mismatch (issue #344) -------------------------


def test_mismatched_declared_worker_version_produces_a_notice(tmp_path: Path) -> None:
    """manifest.worker.version is the app's DECLARED expectation, not necessarily the
    actually-running build's own version (WorkerRef's own docstring) -- but before this
    fix nothing ever compared the two, so a real mismatch (an app talking to a stale or
    newer worker image than it expects) was invisible. Visibility only, per the issue's
    own scope -- this must not refuse or fail the job, only surface the disagreement."""
    store = LocalBlobstore(tmp_path)
    _write_manifest(store, worker={"image": "ghcr.io/x@sha256:test", "version": "9.9.9"})
    _seed_fasta_input(store)

    result = run_job(store, worker_version="1.2.3")

    assert result.status == "done"
    progress_lines = [
        json.loads(line) for line in store.read_text("progress.jsonl").splitlines() if line.strip()
    ]
    messages = [p["message"] for p in progress_lines]
    assert any("9.9.9" in m and "1.2.3" in m for m in messages)


def test_matching_declared_worker_version_produces_no_mismatch_notice(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store, worker={"image": "ghcr.io/x@sha256:test", "version": "1.2.3"})
    _seed_fasta_input(store)

    result = run_job(store, worker_version="1.2.3")

    assert result.status == "done"
    progress_lines = [
        json.loads(line) for line in store.read_text("progress.jsonl").splitlines() if line.strip()
    ]
    messages = [p["message"] for p in progress_lines]
    assert not any("version" in m.lower() and "1.2.3" in m and "mismatch" in m.lower() for m in messages)


def test_no_declared_worker_version_produces_no_mismatch_notice(tmp_path: Path) -> None:
    # WorkerRef.version defaults to "" when the manifest's "worker" section omits it --
    # an undeclared expectation is not a mismatch against anything.
    store = LocalBlobstore(tmp_path)
    _write_manifest(store)  # no "worker" section at all
    _seed_fasta_input(store)

    result = run_job(store, worker_version="1.2.3")

    assert result.status == "done"
    progress_lines = [
        json.loads(line) for line in store.read_text("progress.jsonl").splitlines() if line.strip()
    ]
    messages = [p["message"] for p in progress_lines]
    assert not any("mismatch" in m.lower() for m in messages)


# --- result.json local fallback on a persistent store outage (issue #341, DECISION) ----


def test_result_json_write_failure_writes_a_local_fallback_copy(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The sharp end of #341: a run that finishes successfully but cannot write
    result.json (the store is down at exactly the wrong moment) must not lose its own
    outcome. A local-disk fallback copy is written independent of the broken store, so a
    human who later inspects this VM's disk (a snapshot, or before instanceTerminationAction
    deletes it) can still recover what actually happened."""
    from dna_entropy.worker import status as status_module

    fallback_dir = tmp_path / "fallback"
    monkeypatch.setattr(status_module, "LOCAL_FALLBACK_DIR", fallback_dir)

    real_store = LocalBlobstore(tmp_path / "store")
    _write_manifest(real_store)
    _seed_fasta_input(real_store)

    real_write_text = real_store.write_text

    def _fail_only_result_json(path, text):
        if path == RESULT_PATH:
            raise BlobstoreError("simulated failure writing result.json")
        return real_write_text(path, text)

    real_store.write_text = _fail_only_result_json  # type: ignore[method-assign]

    result = run_job(real_store)  # must not raise despite the injected failure

    assert result.status == "done"
    fallback_path = fallback_dir / "20260918-142233-k7q2vx-result.json"
    assert fallback_path.exists()
    doc = json.loads(fallback_path.read_text(encoding="utf-8"))
    assert doc["status"] == "done"
    assert doc["jobId"] == "20260918-142233-k7q2vx"


def test_result_json_success_never_writes_a_local_fallback(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from dna_entropy.worker import status as status_module

    fallback_dir = tmp_path / "fallback"
    monkeypatch.setattr(status_module, "LOCAL_FALLBACK_DIR", fallback_dir)

    store = LocalBlobstore(tmp_path / "store")
    _write_manifest(store)
    _seed_fasta_input(store)

    run_job(store)

    assert not fallback_dir.exists()


# --- issue #41: one predictor per batch; result.json carries stats, notices, sha256 ------


def _three_input_store(tmp_path: Path, **overrides) -> LocalBlobstore:
    store = LocalBlobstore(tmp_path)
    _write_manifest(
        store,
        inputs=[{"id": f"in{i}", "path": f"input/s{i}.fasta", "name": f"s{i}"} for i in (1, 2, 3)],
        **overrides,
    )
    for i in (1, 2, 3):
        store.write_text(f"input/s{i}.fasta", f">seq{i}" + chr(10) + "ACGT" * (40 + i) + chr(10))
    return store


def test_one_predictor_instance_serves_the_whole_batch(tmp_path: Path) -> None:
    from dna_entropy.predictors.mock import MockPredictor

    store = _three_input_store(tmp_path)
    built: list[object] = []
    predicted_lengths: list[int] = []

    class _Recording(MockPredictor):
        def predict(self, seq):
            predicted_lengths.append(len(seq))
            return super().predict(seq)

    def _factory(cfg):
        built.append(cfg)
        return _Recording(seed=cfg.seed)

    result = run_job(store, predictor_factory=_factory)

    assert [i.status for i in result.inputs] == ["done", "done", "done"]
    assert len(built) == 1, "the model must be loaded once per batch, not once per input"
    # The wired-to-nothing check: the ONE instance actually served every input's windows
    # (each input is a single window of its own length: 4 * (40 + i) nt).
    assert predicted_lengths == [164, 168, 172]


def test_batch_with_a_shared_predictor_matches_per_input_fresh_predictors(tmp_path: Path) -> None:
    """Sharing the predictor must not change a single output byte (mock is stateless)."""
    shared = _three_input_store(tmp_path / "shared")
    run_job(shared)
    for i in (1, 2, 3):
        solo_dir = tmp_path / f"solo{i}"
        solo = LocalBlobstore(solo_dir)
        _write_manifest(solo, inputs=[{"id": "in1", "path": "input/x.fasta", "name": f"s{i}"}])
        solo.write_text("input/x.fasta", f">seq{i}" + chr(10) + "ACGT" * (40 + i) + chr(10))
        run_job(solo)
        a = shared.read_text(f"output/s{i}/s{i}.entropy.bedgraph")
        b = solo.read_text(f"output/s{i}/s{i}.entropy.bedgraph")
        assert a == b


def test_a_predictor_that_fails_to_build_is_built_once_and_fails_every_input(tmp_path: Path) -> None:
    from dna_entropy.predictors.base import PredictorError

    store = _three_input_store(tmp_path)
    calls = 0

    def _factory(cfg):
        nonlocal calls
        calls += 1
        raise PredictorError("no weights")

    result = run_job(store, predictor_factory=_factory)

    assert calls == 1, "a failed model load must not be retried once per remaining input"
    assert [i.status for i in result.inputs] == ["failed", "failed", "failed"]
    assert all(i.error and "no weights" in i.error["message"] for i in result.inputs)


def test_result_json_lists_each_output_file_with_its_sha256_and_size(tmp_path: Path) -> None:
    import hashlib

    store = _three_input_store(tmp_path)
    run_job(store)
    doc = json.loads(store.read_text(RESULT_PATH))
    for entry in doc["inputs"]:
        assert entry["files"], entry
        assert [f["path"] for f in entry["files"]] == entry["outputs"]
        for f in entry["files"]:
            data = (tmp_path / f["path"]).read_bytes()
            assert f["sha256"] == hashlib.sha256(data).hexdigest()
            assert f["bytes"] == len(data)


def test_result_json_carries_per_input_stats_and_notices(tmp_path: Path) -> None:
    store = _three_input_store(tmp_path)
    run_job(store)
    doc = json.loads(store.read_text(RESULT_PATH))
    first = doc["inputs"][0]
    assert first["stats"]["contigs"] == 1
    assert first["stats"]["totalNt"] == 4 * 41
    assert (
        0.0
        <= first["stats"]["minEntropy"]
        <= first["stats"]["meanEntropy"]
        <= first["stats"]["maxEntropy"]
        <= 2.0
    )
    assert isinstance(first["notices"], list)


def test_input_notices_are_recorded_per_input_in_result_json(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _write_manifest(store)
    store.write_text("input/locus.fasta", ">seq" + chr(10) + "ACGT" * 4 + chr(10))  # 16 nt, K=128
    run_job(store)
    entry = json.loads(store.read_text(RESULT_PATH))["inputs"][0]
    assert entry["status"] == "done"
    assert any("shorter than the context length" in n for n in entry["notices"])


def test_a_failed_input_has_no_stats_but_still_has_the_fields(tmp_path: Path) -> None:
    store = _three_input_store(tmp_path)
    (tmp_path / "input" / "s2.fasta").unlink()
    run_job(store)
    second = json.loads(store.read_text(RESULT_PATH))["inputs"][1]
    assert second["status"] == "failed"
    assert second["stats"] is None
    assert second["files"] == []


def test_result_json_is_written_after_every_uploaded_output(tmp_path: Path) -> None:
    store = _three_input_store(tmp_path)
    run_job(store)
    result_mtime = (tmp_path / RESULT_PATH).stat().st_mtime_ns
    outputs = [p for p in (tmp_path / "output").rglob("*") if p.is_file()]
    assert outputs
    assert all(p.stat().st_mtime_ns <= result_mtime for p in outputs)


def test_result_json_is_the_newest_object_in_the_whole_store(tmp_path: Path) -> None:
    """Issue #41's observable: with a 3-input manifest, result.json is the newest file of
    ALL of them - not only newer than the outputs. status.json's and progress.jsonl's final
    snapshot (``StatusWriter.stop()``) used to be written AFTER result.json, so a reader
    listing the store by age saw a non-terminal-looking object as the latest write.

    Scope, stated honestly: this is a ``localdir`` run, which has no lifecycle step and no
    result-write failure, so it checks outputs plus the final status snapshot only. On a
    ``gcs`` job the lifecycle's own progress.jsonl notices are written after result.json
    (docs/job_contract.md section 7); this test does not claim otherwise."""
    store = _three_input_store(tmp_path)
    run_job(store)
    result_mtime = (tmp_path / RESULT_PATH).stat().st_mtime_ns
    others = {
        p.name: p.stat().st_mtime_ns for p in tmp_path.rglob("*") if p.is_file() and p.name != RESULT_PATH
    }
    assert "status.json" in others and "progress.jsonl" in others  # vacuity: the files exist
    newer = sorted(name for name, m in others.items() if m > result_mtime)
    assert newer == [], f"written after result.json: {newer}"


def test_provenance_records_the_input_sha256(tmp_path: Path) -> None:
    import hashlib

    store = _three_input_store(tmp_path)
    run_job(store)
    prov = json.loads((tmp_path / "output" / "s1" / "provenance.json").read_text(encoding="utf-8"))
    expected = hashlib.sha256((tmp_path / "input" / "s1.fasta").read_bytes()).hexdigest()
    assert prov["input_sha256"] == expected


# --- issue #455: a user-correctable input problem is INPUT_INVALID, never a crash ----------


def _one_input_job(tmp_path: Path, *, context_length: int, seq: str, name: str = "locus") -> dict:
    store = LocalBlobstore(tmp_path)
    _write_manifest(
        store,
        inputs=[{"id": "in1", "path": "input/locus.fasta", "name": name}],
        analysis={
            "contextLength": context_length,
            "window": 2 * context_length,
            "stride": context_length,
            "direction": "forward-only",
        },
    )
    store.write_text("input/locus.fasta", ">seq" + chr(10) + seq + chr(10))
    return run_job(store).to_dict()["inputs"][0]


@pytest.mark.parametrize(
    ("context_length", "seq", "must_name"),
    [
        (64, "ACGT" * 100, "128"),  # K below the minimum: the exact threshold is named
        (128, "ACGTACGTA", "10"),  # input below the 10 nt minimum
    ],
)
def test_windowing_refusals_are_input_invalid_with_no_traceback(
    tmp_path: Path, context_length: int, seq: str, must_name: str
) -> None:
    entry = _one_input_job(tmp_path, context_length=context_length, seq=seq)
    assert entry["status"] == "failed"
    assert entry["error"]["code"] == "INPUT_INVALID"
    assert entry["error"]["retriable"] is False
    assert "detail" not in entry["error"], "a user-correctable problem must not carry a traceback"
    assert must_name in entry["error"]["message"]


def test_windowing_refusal_messages_name_an_action() -> None:
    """Hard Rule 13: every error names one action the user can take."""
    from dna_entropy.analysis.windowing import WindowingError, validate_context

    for kwargs in (
        {"context_length": 64, "ceiling": 8192, "seq_len": 1000},
        {"context_length": 128, "ceiling": 8192, "seq_len": 5},
    ):
        with pytest.raises(WindowingError) as exc:
            validate_context(**kwargs)
        assert any(verb in str(exc.value) for verb in ("Choose", "Use ", "Provide")), str(exc.value)


def test_an_unusable_run_name_is_input_invalid_and_the_worker_does_not_report_a_crash(tmp_path: Path) -> None:
    entry = _one_input_job(tmp_path, context_length=128, seq="ACGT" * 40, name="...")
    assert entry["error"]["code"] == "INPUT_INVALID"
    assert "detail" not in entry["error"]
    assert "Use letters" in entry["error"]["message"]


def test_a_user_correctable_failure_does_not_raise_a_worker_crashed_notice(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    _one_input_job(tmp_path, context_length=64, seq="ACGT" * 100)
    progress = store.read_text("progress.jsonl")
    assert "worker crashed" not in progress


# --- issue #448: manifest store.bucket/prefix are cross-checked against the live GCS store ---


def _gcs_job(bucket_in_manifest: str, prefix_in_manifest: str, *, store_bucket: str = "bkt-b"):
    from fake_gcs import FakeGcs

    from dna_entropy.worker.blobstore import GcsBlobstore

    fake = FakeGcs(store_bucket)
    store = GcsBlobstore(store_bucket, "jobs/j1/", opener=fake, sleep=lambda _s: None)
    manifest = {
        "schema": 1,
        "jobId": "20260918-142233-k7q2vx",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "lifecycle": {"afterTask": "stop"},
        "store": {"kind": "gcs", "bucket": bucket_in_manifest, "prefix": prefix_in_manifest},
    }
    store.write_text("manifest.json", json.dumps(manifest))
    store.write_text("input/locus.fasta", ">seq" + chr(10) + "ACGT" * 40 + chr(10))
    return store, fake


def test_a_manifest_naming_another_bucket_is_refused_before_any_work(monkeypatch) -> None:
    from dna_entropy.worker import runner
    from dna_entropy.worker.manifest import ManifestError

    monkeypatch.setattr(runner, "apply_lifecycle", lambda *_a, **_k: {})
    store, fake = _gcs_job("bkt-a", "jobs/j1/")
    with pytest.raises(ManifestError) as exc:
        run_job(store)
    msg = str(exc.value)
    assert "bkt-a" in msg and "bkt-b" in msg
    assert "Start the job again" in msg  # Hard Rule 13: names one action
    assert sorted(k for k in fake.objects) == ["jobs/j1/input/locus.fasta", "jobs/j1/manifest.json"], (
        "nothing may be written (no status, no outputs, no result) once the cross-check fails"
    )


def test_a_manifest_naming_another_prefix_is_refused(monkeypatch) -> None:
    from dna_entropy.worker import runner
    from dna_entropy.worker.manifest import ManifestError

    monkeypatch.setattr(runner, "apply_lifecycle", lambda *_a, **_k: {})
    store, _fake = _gcs_job("bkt-b", "jobs/other/")
    with pytest.raises(ManifestError, match="jobs/other"):
        run_job(store)


@pytest.mark.parametrize(
    "prefix",
    [
        "jobs/j1/",
        "jobs/j1",
        "",
    ],
)
def test_a_matching_or_undeclared_bucket_and_prefix_runs_normally(monkeypatch, prefix: str) -> None:
    from dna_entropy.worker import runner

    monkeypatch.setattr(runner, "apply_lifecycle", lambda *_a, **_k: {"name": "op"})
    bucket = "bkt-b" if prefix else ""  # an empty declaration means "not declared"
    store, fake = _gcs_job(bucket, prefix)
    result = run_job(store)
    assert result.status == "done"
    assert "jobs/j1/result.json" in fake.objects


# --- issue #75: the model-weights cache is wired into the run, not just defined ----------


def _evo_manifest_store(tmp_path: Path) -> LocalBlobstore:
    store = LocalBlobstore(tmp_path / "job")
    _write_manifest(store, predictor={"kind": "evo", "model": "evo2_7b"})
    _seed_fasta_input(store)
    return store


def _progress(store: LocalBlobstore) -> list[dict]:
    return [json.loads(ln) for ln in store.read_text("progress.jsonl").splitlines() if ln.strip()]


def _downloading_factory(hf: Path, built: list):
    """Stands in for evo2/huggingface_hub: 'downloads' files into the HF cache dir."""
    from dna_entropy.predictors.mock import MockPredictor

    def _factory(cfg):
        built.append(cfg)
        (hf / "models--arcinstitute--evo2_7b" / "blobs").mkdir(parents=True, exist_ok=True)
        (hf / "models--arcinstitute--evo2_7b" / "blobs" / "sha1").write_bytes(b"weights")
        return MockPredictor(seed=0)

    return _factory


def test_first_job_downloads_then_mirrors_and_second_job_restores(tmp_path: Path) -> None:
    from dna_entropy.worker.weights import is_cached

    cache = LocalBlobstore(tmp_path / "bucket-root")
    hf1, hf2 = tmp_path / "vm1-hf", tmp_path / "vm2-hf"

    store1 = _evo_manifest_store(tmp_path / "a")
    built1: list = []
    r1 = run_job(
        store1, predictor_factory=_downloading_factory(hf1, built1), cache_store=cache, hf_cache_dir=hf1
    )
    assert r1.status == "done"
    assert is_cached(cache, "evo2_7b") is True  # mirrored after the download
    msgs1 = [p["message"] for p in _progress(store1)]
    assert any("mirrored" in m for m in msgs1), msgs1
    assert not any("restored from the cache" in m for m in msgs1)

    # a second FRESH VM (empty HF dir): restores BEFORE the loader runs
    store2 = _evo_manifest_store(tmp_path / "b")
    seen_at_load: list[bool] = []
    from dna_entropy.predictors.mock import MockPredictor

    def _factory2(cfg):
        seen_at_load.append((hf2 / "models--arcinstitute--evo2_7b" / "blobs" / "sha1").exists())
        return MockPredictor(seed=0)

    r2 = run_job(store2, predictor_factory=_factory2, cache_store=cache, hf_cache_dir=hf2)
    assert r2.status == "done"
    assert seen_at_load == [True], "weights must be in the HF dir before the predictor is built"
    prog2 = _progress(store2)
    stages = [p["stage"] for p in prog2]
    assert "restoring-cache" in stages
    assert any("restored from the cache" in p["message"] for p in prog2)
    assert not any("mirrored" in p["message"] for p in prog2)  # nothing new downloaded


def test_mirror_failure_is_a_notice_and_never_fails_the_job(tmp_path: Path) -> None:
    class _BrokenCache(LocalBlobstore):
        def upload_file(self, local_src, path):
            raise BlobstoreError("bucket unreachable")

    cache = _BrokenCache(tmp_path / "bucket-root")
    hf = tmp_path / "hf"
    store = _evo_manifest_store(tmp_path)
    result = run_job(
        store, predictor_factory=_downloading_factory(hf, []), cache_store=cache, hf_cache_dir=hf
    )
    assert result.status == "done"
    assert [i.status for i in result.inputs] == ["done"]
    failed = [p for p in _progress(store) if "could not be mirrored" in p["message"]]
    assert failed
    # non-fatal: contract levels are info/notice/error and "error" reads as a failure
    assert failed[0]["level"] == "notice"


def test_partial_cache_is_ignored_and_the_loader_downloads(tmp_path: Path) -> None:
    cache = LocalBlobstore(tmp_path / "bucket-root")
    cache.write_text("cache/models/evo2_7b/blobs/sha1", "half")  # no marker
    hf = tmp_path / "hf"
    built: list = []
    store = _evo_manifest_store(tmp_path)
    result = run_job(
        store, predictor_factory=_downloading_factory(hf, built), cache_store=cache, hf_cache_dir=hf
    )
    assert result.status == "done"
    assert len(built) == 1
    assert (hf / "models--arcinstitute--evo2_7b" / "blobs" / "sha1").read_bytes() == b"weights"
    assert not any("restored from the cache" in p["message"] for p in _progress(store))


def test_corrupt_cache_restore_failure_falls_back_to_download(tmp_path: Path) -> None:
    from dna_entropy.worker.weights import save_to_cache

    cache = LocalBlobstore(tmp_path / "bucket-root")
    src = tmp_path / "src"
    (src / "blobs").mkdir(parents=True)
    (src / "blobs" / "sha1").write_bytes(b"weights")
    save_to_cache(cache, "evo2_7b", src)
    cache.write_text("cache/models/evo2_7b/blobs/sha1", "bad")
    hf = tmp_path / "hf"
    store = _evo_manifest_store(tmp_path / "j")
    result = run_job(
        store, predictor_factory=_downloading_factory(hf, []), cache_store=cache, hf_cache_dir=hf
    )
    assert result.status == "done"
    assert any("could not be restored" in p["message"] for p in _progress(store))


def test_mock_predictor_never_touches_the_weights_cache(tmp_path: Path) -> None:
    cache = LocalBlobstore(tmp_path / "bucket-root")
    store = LocalBlobstore(tmp_path / "job")
    _write_manifest(store)  # mock
    _seed_fasta_input(store)
    result = run_job(store, cache_store=cache, hf_cache_dir=tmp_path / "hf")
    assert result.status == "done"
    assert "restoring-cache" not in [p["stage"] for p in _progress(store)]
    assert cache.list_prefix("cache/") == []


def test_gcs_job_mirrors_to_the_bucket_root_not_under_the_job_prefix(tmp_path: Path) -> None:
    """The default wiring: with no explicit cache_store, a GCS job store derives the
    bucket-root cache. Under jobs/<id>/ the cache would never be shared across jobs."""
    from fake_gcs import FakeGcs

    from dna_entropy.worker.blobstore import GcsBlobstore

    fake = FakeGcs("deg-bucket")
    job = GcsBlobstore("deg-bucket", "jobs/j1/", opener=fake)
    _write_manifest(job, predictor={"kind": "evo", "model": "evo2_7b"})
    _seed_fasta_input(job)
    hf = tmp_path / "hf"

    result = run_job(job, predictor_factory=_downloading_factory(hf, []), hf_cache_dir=hf)

    assert result.status == "done"
    assert "cache/models/evo2_7b/_COMPLETE.json" in fake.objects
    assert "cache/models/evo2_7b/models--arcinstitute--evo2_7b/blobs/sha1" in fake.objects
    assert not [n for n in fake.objects if n.startswith("jobs/j1/cache/")]


def test_mirror_is_announced_before_it_starts(tmp_path: Path) -> None:
    cache = LocalBlobstore(tmp_path / "bucket-root")
    hf = tmp_path / "hf"
    store = _evo_manifest_store(tmp_path)
    run_job(store, predictor_factory=_downloading_factory(hf, []), cache_store=cache, hf_cache_dir=hf)
    msgs = [p["message"] for p in _progress(store)]
    start = [i for i, m in enumerate(msgs) if "mirroring model weights" in m]
    done = [i for i, m in enumerate(msgs) if "mirrored to the bucket cache" in m]
    assert start and done and start[0] < done[0]


def test_a_non_empty_local_cache_is_never_mirrored_as_a_complete_set(tmp_path: Path) -> None:
    """Marker would list only part of the set: skip the mirror and say why."""
    cache = LocalBlobstore(tmp_path / "bucket-root")
    hf = tmp_path / "hf"
    (hf / "models--other--model" / "blobs").mkdir(parents=True)
    (hf / "models--other--model" / "blobs" / "old").write_bytes(b"old")
    store = _evo_manifest_store(tmp_path)
    result = run_job(
        store, predictor_factory=_downloading_factory(hf, []), cache_store=cache, hf_cache_dir=hf
    )
    assert result.status == "done"
    assert cache.list_prefix("cache/") == []
    assert any("not mirrored: the local cache was not empty" in p["message"] for p in _progress(store))


def test_an_oserror_scanning_the_hf_dir_after_a_good_load_does_not_fail_the_job(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import dna_entropy.worker.runner as runner_mod

    real = runner_mod.snapshot_tree
    calls = 0

    def _flaky(path):
        nonlocal calls
        calls += 1
        if calls >= 2:  # the post-load scan
            raise OSError("scan failed")
        return real(path)

    monkeypatch.setattr(runner_mod, "snapshot_tree", _flaky)
    cache = LocalBlobstore(tmp_path / "bucket-root")
    hf = tmp_path / "hf"
    store = _evo_manifest_store(tmp_path)
    result = run_job(
        store, predictor_factory=_downloading_factory(hf, []), cache_store=cache, hf_cache_dir=hf
    )
    assert result.status == "done"
    assert any("could not be mirrored" in p["message"] for p in _progress(store))


def test_cancel_during_the_mirror_stops_it_without_a_marker(tmp_path: Path) -> None:
    from dna_entropy.predictors.mock import MockPredictor
    from dna_entropy.worker.weights import is_cached

    cache = LocalBlobstore(tmp_path / "bucket-root")
    hf = tmp_path / "hf"
    store = LocalBlobstore(tmp_path / "job")
    _write_manifest(store, predictor={"kind": "evo", "model": "evo2_7b"}, limits={"cancelPollSeconds": 0})
    _seed_fasta_input(store)

    def _factory(cfg):
        d = hf / "models--arcinstitute--evo2_7b" / "blobs"
        d.mkdir(parents=True)
        (d / "a").write_bytes(b"a")
        (d / "b").write_bytes(b"b")
        store.write_text(CANCEL_PATH, "")  # the user cancels while weights are downloading
        return MockPredictor(seed=0)

    run_job(store, predictor_factory=_factory, cache_store=cache, hf_cache_dir=hf)
    assert is_cached(cache, "evo2_7b") is False
    assert any("mirror stopped" in p["message"] for p in _progress(store))
