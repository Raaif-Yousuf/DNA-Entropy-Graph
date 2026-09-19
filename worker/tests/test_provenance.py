"""Tests for writers/provenance.py (issue #82): provenance.json's shape and invariants."""

from __future__ import annotations

import json
from pathlib import Path

from dna_entropy import __version__
from dna_entropy.writers.provenance import (
    GENERATED_AT_KEY,
    WALL_TIME_KEY,
    ProvenanceWriter,
    build_run_provenance,
    contig_provenance,
)


def _contig(**overrides):
    base = dict(
        name="locus",
        length=100,
        context_length=50,
        window=100,
        stride=50,
        ceiling=8192,
        direction="both-combined",
        seam=50,
        reduced_context_count=0,
    )
    base.update(overrides)
    return contig_provenance(**base)


def _run(**overrides):
    base = dict(
        name="demo",
        predictor_kind="mock",
        model="evo2_7b",
        device="cuda",
        seed=0,
        direction="both-combined",
        ambiguity_policy="keep",
        rna=False,
        contigs=[_contig()],
        wall_time_seconds=1.23,
    )
    base.update(overrides)
    return build_run_provenance(**base)


def test_top_level_shape_has_the_expected_keys() -> None:
    data = _run()
    assert data["schema"] == 1
    assert data["worker_version"] == __version__
    assert GENERATED_AT_KEY in data
    assert WALL_TIME_KEY in data
    assert data["run"] == {
        "name": "demo",
        "direction": "both-combined",
        "ambiguity_policy": "keep",
        "rna": False,
    }
    assert data["predictor"] == {"kind": "mock", "model": "evo2_7b", "device": "cuda", "seed": 0}
    assert len(data["contigs"]) == 1


# --- issue #82's own Observable: two runs differ ONLY in generated_at/wall_time --------


def test_two_builds_of_the_same_config_differ_only_in_generated_at_and_wall_time() -> None:
    a = _run(generated_at="2026-01-01T00:00:00+00:00", wall_time_seconds=1.0)
    b = _run(generated_at="2026-01-01T00:00:01+00:00", wall_time_seconds=2.0)
    a_stripped = {k: v for k, v in a.items() if k not in (GENERATED_AT_KEY, WALL_TIME_KEY)}
    b_stripped = {k: v for k, v in b.items() if k not in (GENERATED_AT_KEY, WALL_TIME_KEY)}
    assert a_stripped == b_stripped
    assert a[GENERATED_AT_KEY] != b[GENERATED_AT_KEY]
    assert a[WALL_TIME_KEY] != b[WALL_TIME_KEY]


def test_generated_at_defaults_to_now_when_not_given() -> None:
    data = _run()
    # ISO 8601 with a UTC offset -- just check it parses, not an exact value (the clock).
    import datetime

    datetime.datetime.fromisoformat(data[GENERATED_AT_KEY])


# --- issue #81: an OOM-halving retry's FINAL W/S, robust to a stale context_length -----


def test_k_used_derives_from_window_minus_stride_not_from_context_length() -> None:
    """MEASURED 2026-09-19: DirectionResult.context_length is NOT updated after an
    OOM-halving retry that shrinks K, but .window/.stride ARE the final, post-halving
    values. contig_provenance must report the ACTUALLY-USED K (window - stride), not the
    possibly-stale context_length -- this test plants exactly that disagreement."""
    c = _contig(context_length=4096, window=8, stride=4)  # a halved pass: K was really 4
    assert c["k_used"] == 4
    assert c["k_used"] != c["context_length"]  # the disagreement this field exists to survive


def test_k_used_matches_context_length_in_the_ordinary_non_halved_case() -> None:
    c = _contig(context_length=50, window=100, stride=50)
    assert c["k_used"] == 50 == c["context_length"]


# --- worker-layer-only fields: None by default, overlaid by extra ----------------------


def test_worker_layer_fields_default_to_none() -> None:
    data = _run()
    assert data["gpu"] is None
    assert data["versions"] == {"torch": None, "evo2": None, "flash_attn": None}
    assert data["image_digest"] is None
    assert data["input_sha256"] is None


def test_extra_overlays_worker_layer_fields() -> None:
    data = _run(
        extra={
            "gpu": {"name": "NVIDIA L4", "driver": "550.54.15"},
            "versions": {"torch": "2.9.0", "evo2": "0.2.1", "flash_attn": "2.7.0"},
            "image_digest": "sha256:deadbeef",
            "input_sha256": "sha256:cafef00d",
        }
    )
    assert data["gpu"] == {"name": "NVIDIA L4", "driver": "550.54.15"}
    assert data["versions"]["torch"] == "2.9.0"
    assert data["image_digest"] == "sha256:deadbeef"
    assert data["input_sha256"] == "sha256:cafef00d"


def test_extra_is_additive_never_required() -> None:
    # No `extra` at all (pipeline.py's own default call shape) must not raise.
    data = build_run_provenance(
        name="n",
        predictor_kind="mock",
        model="m",
        device="cpu",
        seed=0,
        direction="forward-only",
        ambiguity_policy="keep",
        rna=False,
        contigs=[],
        wall_time_seconds=0.0,
    )
    assert data["contigs"] == []


# --- ProvenanceWriter: valid JSON, UTF-8, LF ---------------------------------------------


def test_writer_writes_parseable_json_named_provenance_json(tmp_path: Path) -> None:
    data = _run()
    path = ProvenanceWriter().write(out_dir=str(tmp_path), data=data)
    assert Path(path).name == "provenance.json"
    with open(path, "rb") as fh:
        raw = fh.read()
    assert b"\r\n" not in raw  # LF only, never CRLF (Hard Rule 5)
    parsed = json.loads(raw.decode("utf-8"))
    assert parsed == data


def test_writer_output_is_deterministic_key_order(tmp_path: Path) -> None:
    data = _run()
    path_a = ProvenanceWriter().write(out_dir=str(tmp_path), data=data)
    text_a = Path(path_a).read_text(encoding="utf-8")
    path_b = ProvenanceWriter().write(out_dir=str(tmp_path), data=data)
    text_b = Path(path_b).read_text(encoding="utf-8")
    assert text_a == text_b  # sort_keys=True: byte-identical for byte-identical input
