"""Tests for worker/manifest.py: parsing, schema versioning (#250), RunConfig bridge."""

from __future__ import annotations

import json

import pytest

from dna_entropy.config import AmbiguityPolicy, Direction, PredictorKind, TrackFormat
from dna_entropy.worker.manifest import (
    CURRENT_SCHEMA_VERSION,
    SUPPORTED_SCHEMA_VERSIONS,
    JobManifest,
    ManifestError,
    ManifestSchemaError,
)

MINIMAL_MANIFEST = {
    "schema": 1,
    "jobId": "20260918-142233-k7q2vx",
    "inputs": [{"id": "in1", "path": "input/SetTnpB-Evo.gb", "name": "SetTnpB"}],
    "store": {"kind": "localdir", "root": "C:\\runs\\job1"},
}


def _manifest(**overrides) -> str:
    d = json.loads(json.dumps(MINIMAL_MANIFEST))  # deep copy
    d.update(overrides)
    return json.dumps(d)


# --- schema versioning (#250) -----------------------------------------------------


def test_current_schema_is_supported() -> None:
    assert CURRENT_SCHEMA_VERSION in SUPPORTED_SCHEMA_VERSIONS


def test_unsupported_higher_schema_is_refused_with_both_versions_named() -> None:
    with pytest.raises(ManifestSchemaError) as exc:
        JobManifest.parse(_manifest(schema=99))
    assert exc.value.code == "WORKER_VERSION_MISMATCH"
    assert exc.value.manifest_schema == 99
    assert exc.value.worker_schema == CURRENT_SCHEMA_VERSION
    msg = str(exc.value)
    assert "99" in msg
    assert str(CURRENT_SCHEMA_VERSION) in msg


def test_schema_mismatch_is_checked_before_any_other_field() -> None:
    # A schema-99 manifest with a completely wrong shape otherwise must STILL report the
    # version mismatch, not some confusing "missing field" error from a future shape.
    with pytest.raises(ManifestSchemaError):
        JobManifest.parse(json.dumps({"schema": 99, "totally": "different shape"}))


def test_missing_schema_field_is_a_manifest_error_not_a_crash() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(
            json.dumps({"jobId": "x", "inputs": [], "store": {"kind": "localdir", "root": "x"}})
        )


def test_not_json_at_all_is_a_manifest_error() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse("not json { at all")


# --- structural validation ----------------------------------------------------------


def test_missing_inputs_is_rejected() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(inputs=[]))


def test_missing_store_is_rejected() -> None:
    d = json.loads(_manifest())
    del d["store"]
    with pytest.raises(ManifestError):
        JobManifest.parse(json.dumps(d))


def test_unknown_store_kind_is_rejected() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(store={"kind": "ftp", "url": "x"}))


def test_gcs_store_requires_bucket_and_prefix() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(store={"kind": "gcs"}))


# --- parsing the full example from docs/job_contract.md -----------------------------


def test_unknown_top_level_fields_are_tolerated() -> None:
    """job_contract.md §8: 'unknown fields on an otherwise-matching schema version are
    tolerated (ignored), so a newer app talking to an older worker degrades gracefully.'"""
    m = JobManifest.parse(
        _manifest(
            aFieldFromTheFuture="some new thing this worker build has never heard of",
            anotherOne={"nested": "also unknown"},
        )
    )
    assert m.job_id == MINIMAL_MANIFEST["jobId"]  # parsed normally, unknown fields just ignored


def test_unknown_nested_fields_are_tolerated() -> None:
    m = JobManifest.parse(
        _manifest(
            predictor={"kind": "mock", "seed": 0, "futureField": "ignored"},
            analysis={"contextLength": 2048, "yetAnotherFutureField": 123},
        )
    )
    assert m.predictor.kind == "mock"
    assert m.analysis.context_length == 2048


def test_defaults_apply_when_optional_sections_are_omitted() -> None:
    """Every section except schema/jobId/inputs/store is optional at the top level; a
    minimal manifest still parses with documented defaults."""
    m = JobManifest.parse(_manifest())
    assert m.predictor.kind == "mock"
    assert m.predictor.model == "evo2_7b"
    assert m.analysis.context_length == 4096
    assert m.analysis.direction is Direction.BOTH_COMBINED
    assert m.limits.heartbeat_seconds == 30
    assert m.lifecycle.after_task == "stop"
    assert m.outputs == []


def test_parses_the_full_documented_example() -> None:
    full = {
        "schema": 1,
        "jobId": "20260918-142233-k7q2vx",
        "createdAt": "2026-09-18T14:22:33Z",
        "createdBy": {"installationId": "01H9X5G6K7M8N9P0Q1R2S3T4U5", "appVersion": "1.0.0"},
        "worker": {"image": "ghcr.io/x@sha256:abc", "version": "1.0.0"},
        "inputs": [
            {
                "id": "in1",
                "path": "input/SetTnpB-Evo.gb",
                "name": "SetTnpB",
                "informat": "auto",
                "start": 1,
                "rna": False,
                "genes": True,
                "ambiguityPolicy": "mask",
                "fastaRecords": "all",
            }
        ],
        "predictor": {"kind": "evo", "model": "evo2_7b", "precision": "bf16", "device": "cuda", "seed": 0},
        "analysis": {
            "contextLength": 4096,
            "window": 8192,
            "stride": 4096,
            "direction": "both-combined",
            "format": "bedgraph",
        },
        "outputs": ["genbank", "fasta", "bedgraph", "wig", "geneious_gff3", "genes_gff3", "stats", "tsv"],
        "limits": {"maxRunSeconds": 14400, "cancelPollSeconds": 10, "heartbeatSeconds": 30},
        "lifecycle": {"afterTask": "stop", "keepAliveMinutes": 30, "afterKeepAlive": "stop"},
        "store": {"kind": "gcs", "bucket": "deg-123-abc", "prefix": "jobs/20260918-142233-k7q2vx/"},
    }
    m = JobManifest.parse(json.dumps(full))
    assert m.job_id == "20260918-142233-k7q2vx"
    assert len(m.inputs) == 1
    assert m.inputs[0].name == "SetTnpB"
    assert m.inputs[0].genes is True
    assert m.inputs[0].ambiguity_policy is AmbiguityPolicy.MASK
    assert m.predictor.kind == "evo"
    assert m.analysis.context_length == 4096
    assert m.analysis.window == 8192
    assert m.analysis.direction is Direction.BOTH_COMBINED
    assert m.outputs == ["genbank", "fasta", "bedgraph", "wig", "geneious_gff3", "genes_gff3", "stats", "tsv"]
    assert m.limits.heartbeat_seconds == 30
    assert m.lifecycle.after_task == "stop"
    assert m.store.kind == "gcs"
    assert m.store.bucket == "deg-123-abc"
    assert m.worker.image == "ghcr.io/x@sha256:abc"
    assert m.worker.version == "1.0.0"


def test_parses_a_localdir_store_manifest() -> None:
    m = JobManifest.parse(_manifest())
    assert m.store.kind == "localdir"
    assert m.store.root == "C:\\runs\\job1"


# --- direction spelling: accepts job_contract.md's alias, prefers the shipped enum ----


@pytest.mark.parametrize(
    "spelling, expected",
    [
        ("forward-only", Direction.FORWARD_ONLY),
        ("reverse-only", Direction.REVERSE_ONLY),
        ("both-combined", Direction.BOTH_COMBINED),
        ("both-averaged", Direction.BOTH_AVERAGED),
        ("both-separate", Direction.BOTH_SEPARATE),
    ],
)
def test_direction_canonical_spellings(spelling: str, expected: Direction) -> None:
    m = JobManifest.parse(_manifest(analysis={"direction": spelling}))
    assert m.analysis.direction is expected


def test_unknown_direction_spelling_is_rejected() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(analysis={"direction": "sideways"}))


def test_the_old_forward_reverse_aliases_are_no_longer_accepted() -> None:
    """Naming resolved (issue #254 follow-up): docs/job_contract.md's old "forward"/
    "reverse" spelling is not the canonical one — see manifest.py's module docstring.
    A manifest using the old spelling must now fail loudly, not be silently tolerated."""
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(analysis={"direction": "forward"}))
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(analysis={"direction": "reverse"}))


# --- ambiguity policy: keep / mask / error, per input (issue #249) --------------------


def test_ambiguity_policy_defaults_to_keep_when_omitted() -> None:
    m = JobManifest.parse(_manifest())
    assert m.inputs[0].ambiguity_policy is AmbiguityPolicy.KEEP


@pytest.mark.parametrize(
    "spelling, expected",
    [
        ("keep", AmbiguityPolicy.KEEP),
        ("mask", AmbiguityPolicy.MASK),
        ("error", AmbiguityPolicy.ERROR),
    ],
)
def test_ambiguity_policy_spellings(spelling: str, expected: AmbiguityPolicy) -> None:
    m = JobManifest.parse(
        _manifest(inputs=[{"id": "in1", "path": "input/x", "name": "x", "ambiguityPolicy": spelling}])
    )
    assert m.inputs[0].ambiguity_policy is expected


def test_unknown_ambiguity_policy_spelling_is_rejected() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(
            _manifest(inputs=[{"id": "in1", "path": "input/x", "name": "x", "ambiguityPolicy": "sideways"}])
        )


def test_the_old_allow_ambiguity_boolean_field_is_tolerated_but_no_longer_read() -> None:
    """issue #249: allowAmbiguity was parsed but never actually consulted anywhere
    downstream before this issue (a real "wired to nothing" gap, not just a rename). An
    old manifest still sending it must not error (unknown fields are tolerated,
    job_contract.md §8) -- it just has no effect any more; ambiguityPolicy is what
    matters now."""
    m = JobManifest.parse(
        _manifest(inputs=[{"id": "in1", "path": "input/x", "name": "x", "allowAmbiguity": False}])
    )
    assert m.inputs[0].ambiguity_policy is AmbiguityPolicy.KEEP  # the real default, not affected


# --- build_run_config: the manifest -> RunConfig bridge -------------------------------


def test_build_run_config_maps_core_fields() -> None:
    m = JobManifest.parse(
        _manifest(
            predictor={"kind": "mock", "seed": 7},
            analysis={"contextLength": 2048, "window": 4096, "stride": 2048, "direction": "both-averaged"},
        )
    )
    cfg = m.build_run_config(m.inputs[0], local_input_path="/tmp/staged/in1.gb", local_out_dir="/tmp/out/in1")
    assert cfg.name == "SetTnpB"
    assert cfg.input_path == "/tmp/staged/in1.gb"
    assert cfg.out_dir == "/tmp/out/in1"
    assert cfg.predictor is PredictorKind.MOCK
    assert cfg.seed == 7
    assert cfg.context_length == 2048
    assert cfg.max_len == 4096  # the app-derived window becomes the worker's ceiling
    assert cfg.direction is Direction.BOTH_AVERAGED


def test_build_run_config_maps_ambiguity_policy_per_input() -> None:
    m = JobManifest.parse(
        _manifest(inputs=[{"id": "in1", "path": "input/x", "name": "x", "ambiguityPolicy": "error"}])
    )
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.ambiguity_policy is AmbiguityPolicy.ERROR


def test_build_run_config_window_reproduces_itself_when_recomputed() -> None:
    """The worker RE-derives W=min(2K, ceiling) from (context_length, max_len) rather than
    trusting analysis.window literally (see manifest.py's build_run_config docstring for
    why this is mathematically identical either way: W is always <= 2K by construction)."""
    from dna_entropy.analysis.windowing import plan_windows

    m = JobManifest.parse(
        _manifest(
            analysis={"contextLength": 3000, "window": 6000, "stride": 3000, "direction": "both-combined"},
        )
    )
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    plan = plan_windows(length=100_000, context_length=cfg.context_length, ceiling=cfg.max_len)
    assert plan.window == 6000  # matches analysis.window exactly


def test_build_run_config_track_format_from_outputs() -> None:
    m = JobManifest.parse(_manifest(outputs=["wig", "fasta"]))
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.track_format is TrackFormat.WIG


def test_build_run_config_defaults_to_bedgraph_when_unspecified() -> None:
    m = JobManifest.parse(_manifest())  # no "outputs" key at all
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.track_format is TrackFormat.BEDGRAPH


def test_build_run_config_tsv_toggle_from_outputs() -> None:
    with_tsv = JobManifest.parse(_manifest(outputs=["tsv"]))
    without_tsv = JobManifest.parse(_manifest(outputs=["fasta"]))
    cfg_with = with_tsv.build_run_config(with_tsv.inputs[0], local_input_path="x", local_out_dir="y")
    cfg_without = without_tsv.build_run_config(without_tsv.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg_with.include_tsv is True
    assert cfg_without.include_tsv is False


def test_build_run_config_genes_from_input_spec_or_outputs() -> None:
    m = JobManifest.parse(
        _manifest(
            inputs=[{"id": "in1", "path": "x", "name": "n", "genes": False}],
            outputs=["genes_gff3"],
        )
    )
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.genes is True  # outputs asked for it even though the input spec did not


# --- outputs -> per-writer suppression flags (issue #304) -----------------------------


def test_build_run_config_outputs_unspecified_defaults_every_include_flag_true() -> None:
    m = JobManifest.parse(_manifest())  # no "outputs" key at all
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.include_fasta is True
    assert cfg.include_track is True
    assert cfg.include_geneious is True
    assert cfg.include_stats is True
    assert cfg.include_genbank is True
    assert cfg.include_genes_gff3 is True


def test_build_run_config_outputs_bedgraph_only_suppresses_every_other_flag() -> None:
    m = JobManifest.parse(_manifest(outputs=["bedgraph"]))
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.include_track is True
    assert cfg.include_fasta is False
    assert cfg.include_geneious is False
    assert cfg.include_stats is False
    assert cfg.include_genbank is False
    assert cfg.include_genes_gff3 is False
    assert cfg.include_tsv is False


def test_build_run_config_outputs_neither_bedgraph_nor_wig_turns_track_off() -> None:
    m = JobManifest.parse(_manifest(outputs=["fasta"]))
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.include_track is False
    assert cfg.include_fasta is True


def test_build_run_config_outputs_geneious_and_stats_flags() -> None:
    m = JobManifest.parse(_manifest(outputs=["geneious_gff3", "stats"]))
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.include_geneious is True
    assert cfg.include_stats is True
    assert cfg.include_fasta is False
    assert cfg.include_track is False


def test_build_run_config_outputs_genbank_flag() -> None:
    with_gb = JobManifest.parse(_manifest(outputs=["genbank"]))
    without_gb = JobManifest.parse(_manifest(outputs=["fasta"]))
    cfg_with = with_gb.build_run_config(with_gb.inputs[0], local_input_path="x", local_out_dir="y")
    cfg_without = without_gb.build_run_config(without_gb.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg_with.include_genbank is True
    assert cfg_without.include_genbank is False


def test_build_run_config_genes_computation_trigger_is_unaffected_by_outputs_being_unspecified() -> None:
    """The empty-outputs 'everything on' fallback must NOT also turn genes (a real Prodigal
    compute cost, opt-in only) on by itself -- only an explicit input.genes or an explicit
    'genes_gff3' entry in a non-empty outputs list may do that (regression guard: an
    earlier draft of this fix conflated the two and silently ran Prodigal on every input)."""
    m = JobManifest.parse(_manifest(inputs=[{"id": "in1", "path": "x", "name": "n", "genes": False}]))
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.genes is False
    assert cfg.include_genes_gff3 is True  # the FILE toggle still defaults on; only the trigger is guarded


# --- fastaRecords -> RunConfig.fasta_records (issue #306) ------------------------------


def test_build_run_config_passes_fasta_records_through() -> None:
    m = JobManifest.parse(
        _manifest(inputs=[{"id": "in1", "path": "x", "name": "n", "fastaRecords": "first"}])
    )
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.fasta_records == "first"


def test_build_run_config_fasta_records_defaults_to_all() -> None:
    m = JobManifest.parse(_manifest())
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.fasta_records == "all"


def test_unknown_fasta_records_spelling_is_rejected() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(
            _manifest(inputs=[{"id": "in1", "path": "input/x", "name": "x", "fastaRecords": "middle"}])
        )


# --- analysis.format -> RunConfig.track_format (found during the #304 audit) -----------
#
# AnalysisSpec.track_format (wire field analysis.format) was parsed and stored but never
# consulted by build_run_config, which derived the format purely from `outputs` membership
# and otherwise always fell back to bedgraph -- a manifest declaring analysis.format="wig"
# with no `outputs` key at all silently got bedgraph anyway. Fixed alongside #304 since it
# is the same build_run_config method and the same class of bug (a manifest field with no
# consumer).


def test_analysis_format_is_validated_at_parse_time() -> None:
    m = JobManifest.parse(_manifest(analysis={"format": "wig"}))
    assert m.analysis.track_format is TrackFormat.WIG


def test_unknown_analysis_format_is_rejected() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(analysis={"format": "svg"}))


def test_build_run_config_honors_analysis_format_when_outputs_is_unspecified() -> None:
    m = JobManifest.parse(_manifest(analysis={"format": "wig"}))  # no "outputs" key at all
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.track_format is TrackFormat.WIG


def test_build_run_config_outputs_still_wins_over_analysis_format_when_specified() -> None:
    # A non-empty `outputs` array is the literal, per-file suppression list (issue #304);
    # analysis.format is only the fallback when outputs never says anything at all.
    m = JobManifest.parse(_manifest(analysis={"format": "wig"}, outputs=["bedgraph"]))
    cfg = m.build_run_config(m.inputs[0], local_input_path="x", local_out_dir="y")
    assert cfg.track_format is TrackFormat.BEDGRAPH


# --- JobManifest.raw (issue #361) -------------------------------------------------------


def test_job_manifest_has_no_raw_field() -> None:
    """Issue #361: JobManifest.raw held the original parsed dict but nothing ever read
    it -- not this package, not scripts/, not even a test. job_contract.md section 8's
    own documented behaviour for an unknown field is "tolerated (ignored)", not
    "preserved for later re-emission" -- there is no forward-compatibility feature this
    field was actually supporting. Deleted rather than given a consumer."""
    m = JobManifest.parse(_manifest())
    assert not hasattr(m, "raw")


# --- predictor.precision cross-checked against the model (issue #343) ------------------


def test_precision_matching_the_model_parses_fine() -> None:
    m = JobManifest.parse(_manifest(predictor={"kind": "evo", "model": "evo2_7b", "precision": "bf16"}))
    assert m.predictor.precision == "bf16"


def test_precision_disagreeing_with_a_known_model_is_rejected() -> None:
    # evo2_7b is bf16-only (predictors/hardware.py's own MODEL_REQUIREMENTS) -- fp8 here
    # can never be honored, since nothing reads predictor.precision to configure the
    # predictor at all; the model id alone determines the real precision used.
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(predictor={"kind": "evo", "model": "evo2_7b", "precision": "fp8"}))


def test_precision_disagreeing_with_a_hopper_model_is_rejected() -> None:
    with pytest.raises(ManifestError):
        JobManifest.parse(_manifest(predictor={"kind": "evo", "model": "evo2_20b", "precision": "bf16"}))


def test_precision_is_not_cross_checked_for_the_mock_predictor() -> None:
    # The mock predictor ignores precision entirely -- a manifest built for local/dev
    # testing should not have to know evo2_7b's real hardware requirement.
    m = JobManifest.parse(_manifest(predictor={"kind": "mock", "model": "evo2_7b", "precision": "fp8"}))
    assert m.predictor.precision == "fp8"


def test_precision_is_not_cross_checked_for_an_unrecognized_model() -> None:
    # An unrecognized model id is predictors.hardware's OWN refusal (UnknownModelError,
    # MODEL_UNKNOWN, issue #346), raised later at predictor-construction time with a
    # clearer, dedicated message -- this manifest-level check must not preempt it with a
    # confusing "precision mismatch" for a model it does not actually recognize.
    m = JobManifest.parse(
        _manifest(predictor={"kind": "evo", "model": "not_a_real_model", "precision": "fp8"})
    )
    assert m.predictor.model == "not_a_real_model"
