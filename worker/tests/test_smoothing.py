"""Tests for the smoothed entropy tracks (issue #126): analysis/smoothing.py and its wiring
through the track writers, the pipeline, the CLI and the manifest.

The decisive observable (the issue's own): a smoothing window of 1 reproduces the raw track
bit-for-bit. Smoothing is a rolling mean over the SAME entropy track the run wrote (Hard
Rule 3); the raw track stays primary and is never replaced.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pytest
from hypothesis import given, settings
from hypothesis import strategies as st

from dna_entropy import pipeline
from dna_entropy.analysis.smoothing import (
    DEFAULT_SMOOTHING_WINDOWS,
    MAX_WINDOW,
    MAX_WINDOWS,
    rolling_mean,
    validate_smoothing_windows,
)
from dna_entropy.config import Direction, RunConfig, Topology, TrackFormat
from dna_entropy.worker.manifest import JobManifest, ManifestError

PROPERTY_SETTINGS = settings(max_examples=120, deadline=None)

_track = st.lists(
    st.floats(min_value=0.0, max_value=2.0, width=32, allow_nan=False), min_size=1, max_size=150
).map(lambda v: np.asarray(v, dtype=np.float32))


def _naive(values: np.ndarray, window: int, circular: bool) -> np.ndarray:
    """The textbook definition, one base at a time, nothing shared with the implementation."""
    n = len(values)
    h = (window - 1) // 2
    if circular:
        h = min(h, (n - 1) // 2)
    out = []
    for i in range(n):
        if circular:
            idx = [(i + k) % n for k in range(-h, h + 1)]
        else:
            idx = list(range(max(0, i - h), min(n, i + h + 1)))
        out.append(sum(float(values[j]) for j in idx) / len(idx))
    return np.asarray(out, dtype=np.float32)


# --- defaults ---------------------------------------------------------------------------------


def test_the_default_is_a_single_51_base_window() -> None:
    assert DEFAULT_SMOOTHING_WINDOWS == (51,)
    cfg = RunConfig()
    assert cfg.smoothing_windows == (51,)
    assert cfg.include_smoothed is True


# --- the decisive observable ---------------------------------------------------------------------


@PROPERTY_SETTINGS
@given(_track, st.booleans())
def test_window_one_reproduces_the_raw_track_bit_for_bit(values: np.ndarray, circular: bool) -> None:
    out = rolling_mean(values, 1, circular=circular)
    assert out.dtype == np.float32
    assert out.tobytes() == values.tobytes()


# --- the rolling mean ------------------------------------------------------------------------------


def test_interior_is_the_plain_centred_mean() -> None:
    out = rolling_mean(np.array([0, 0, 3, 0, 0], dtype=np.float32), 3, circular=False)
    assert out[2] == pytest.approx(1.0)
    assert out[1] == pytest.approx(1.0)
    assert out[3] == pytest.approx(1.0)


def test_the_window_shrinks_at_the_edges_it_does_not_pad_or_wrap() -> None:
    v = np.array([1, 2, 3, 4, 5], dtype=np.float32)
    assert rolling_mean(v, 3, circular=False).tolist() == pytest.approx([1.5, 2, 3, 4, 4.5])
    assert rolling_mean(v, 5, circular=False).tolist() == pytest.approx([2, 2.5, 3, 3.5, 4])


def test_a_window_longer_than_the_sequence_still_works_by_shrinking() -> None:
    v = np.array([1, 2, 3], dtype=np.float32)
    assert rolling_mean(v, 51, circular=False).tolist() == pytest.approx([2, 2, 2])


def test_circular_wraps_the_window_around_the_origin() -> None:
    v = np.array([4, 0, 0, 0, 0, 0, 0, 2], dtype=np.float32)
    out = rolling_mean(v, 3, circular=True)
    assert out[0] == pytest.approx((2 + 4 + 0) / 3)  # the last base is the previous neighbour
    assert out[-1] == pytest.approx((0 + 2 + 4) / 3)  # and the first is the next one
    linear = rolling_mean(v, 3, circular=False)
    assert linear[0] == pytest.approx((4 + 0) / 2)


def test_circular_window_longer_than_the_molecule_is_clamped_to_one_turn() -> None:
    v = np.array([0, 1, 2, 3, 4], dtype=np.float32)  # odd length: one turn covers each base once
    assert rolling_mean(v, 51, circular=True).tolist() == pytest.approx([2.0] * 5)


def test_output_has_the_input_length_and_float32() -> None:
    out = rolling_mean(np.zeros(7, dtype=np.float32), 5, circular=False)
    assert out.shape == (7,)
    assert out.dtype == np.float32


def test_empty_input_gives_empty_output() -> None:
    assert rolling_mean(np.zeros(0, dtype=np.float32), 5, circular=False).shape == (0,)


def test_zero_entropy_stays_exactly_non_negative() -> None:
    out = rolling_mean(np.zeros(200, dtype=np.float32), 51, circular=False)
    assert (out >= 0.0).all()
    assert out.max() == 0.0


@PROPERTY_SETTINGS
@given(_track, st.integers(min_value=0, max_value=40).map(lambda h: 2 * h + 1), st.booleans())
def test_matches_the_textbook_definition(values: np.ndarray, window: int, circular: bool) -> None:
    got = rolling_mean(values, window, circular=circular)
    assert np.allclose(got, _naive(values, window, circular), atol=1e-6)


@PROPERTY_SETTINGS
@given(_track, st.integers(min_value=0, max_value=40).map(lambda h: 2 * h + 1), st.booleans())
def test_a_mean_never_leaves_the_range_of_what_it_averages(values, window, circular) -> None:
    got = rolling_mean(values, window, circular=circular)
    assert got.min() >= values.min()
    assert got.max() <= values.max()


@PROPERTY_SETTINGS
@given(_track, st.integers(min_value=0, max_value=40).map(lambda h: 2 * h + 1), st.integers(0, 500))
def test_circular_smoothing_commutes_with_rotation(values, window, shift) -> None:
    n = len(values)
    shift %= n
    a = rolling_mean(np.roll(values, shift), window, circular=True)
    b = np.roll(rolling_mean(values, window, circular=True), shift)
    assert np.allclose(a, b, atol=1e-6)


def test_large_input_stays_accurate_enough_for_the_four_decimals_it_is_written_to() -> None:
    rng = np.random.default_rng(126)
    values = (rng.random(200_000) * 0.001).astype(np.float32)  # tiny values, long sequence
    got = rolling_mean(values, 51, circular=False)
    i = 123_456
    assert got[i] == pytest.approx(float(values[i - 25 : i + 26].astype(np.float64).mean()), abs=1e-8)


# --- option validation --------------------------------------------------------------------------------


def test_validate_returns_sorted_unique_windows() -> None:
    assert validate_smoothing_windows([201, 11, 51, 11]) == (11, 51, 201)
    assert validate_smoothing_windows(()) == ()


@pytest.mark.parametrize(
    "bad",
    [[0], [-5], [2], [50], [1.5], ["51"], [True], [MAX_WINDOW + 2], list(range(1, 2 * MAX_WINDOWS + 3, 2))],
)
def test_validate_refuses_even_nonpositive_non_integer_and_oversized(bad) -> None:
    with pytest.raises(ValueError, match="smoothing"):
        validate_smoothing_windows(bad)


def test_validate_accepts_the_largest_allowed_window() -> None:
    assert validate_smoothing_windows([MAX_WINDOW]) == (MAX_WINDOW,)


# --- wired through the pipeline -----------------------------------------------------------------------


def _out(result: pipeline.RunResult, suffix: str) -> str:
    return next(p for p in result.outputs if p.endswith(suffix))


def _data_lines(path: str) -> list[str]:
    return [ln for ln in Path(path).read_text(encoding="utf-8").splitlines() if not ln.startswith("track")]


RAW = "ATGCATGCATGCATGCATGCATGCATGCATGC" * 2


def test_default_run_writes_the_smooth51_track_next_to_the_raw_one(tmp_path: Path) -> None:
    result = pipeline.run(RunConfig(name="s", out_dir=str(tmp_path)), raw=RAW)
    names = {Path(p).name for p in result.outputs}
    assert {"s.entropy.bedgraph", "s.entropy.smooth51.bedgraph"} <= names
    assert {p.name for p in tmp_path.iterdir()} == names  # everything on disk is listed


def test_the_smoothed_bedgraph_is_the_rolling_mean_of_the_raw_track(tmp_path: Path) -> None:
    cfg = RunConfig(name="s", out_dir=str(tmp_path), smoothing_windows=(5, 11))
    result = pipeline.run(cfg, raw=RAW)
    for window in (5, 11):
        rows = [ln.split("\t") for ln in _data_lines(_out(result, f"s.entropy.smooth{window}.bedgraph"))]
        got = np.array([float(r[3]) for r in rows])
        assert np.allclose(got, rolling_mean(result.values, window, circular=False), atol=1e-4)
        assert [r[1] for r in rows[:3]] == ["0", "1", "2"]  # same 0-based half-open frame as the raw track


def test_window_one_written_file_equals_the_raw_file_line_for_line(tmp_path: Path) -> None:
    cfg = RunConfig(name="s", out_dir=str(tmp_path), smoothing_windows=(1,))
    result = pipeline.run(cfg, raw=RAW)
    assert _data_lines(_out(result, "s.entropy.smooth1.bedgraph")) == _data_lines(
        _out(result, "s.entropy.bedgraph")
    )


def test_the_smoothed_track_differs_from_raw_for_a_wider_window(tmp_path: Path) -> None:
    result = pipeline.run(RunConfig(name="s", out_dir=str(tmp_path), smoothing_windows=(11,)), raw=RAW)
    assert _data_lines(_out(result, "smooth11.bedgraph")) != _data_lines(_out(result, "s.entropy.bedgraph"))


def test_wig_runs_get_smoothed_wig_not_bedgraph(tmp_path: Path) -> None:
    cfg = RunConfig(name="s", out_dir=str(tmp_path), track_format=TrackFormat.WIG)
    names = {Path(p).name for p in pipeline.run(cfg, raw=RAW).outputs}
    assert "s.entropy.smooth51.wig" in names
    assert "s.entropy.smooth51.bedgraph" not in names


def test_include_smoothed_off_or_no_windows_writes_none(tmp_path: Path) -> None:
    a = pipeline.run(RunConfig(name="a", out_dir=str(tmp_path / "a"), include_smoothed=False), raw=RAW)
    b = pipeline.run(RunConfig(name="b", out_dir=str(tmp_path / "b"), smoothing_windows=()), raw=RAW)
    assert not any("smooth" in Path(p).name for p in a.outputs + b.outputs)


def test_a_bad_window_is_refused_before_anything_runs(tmp_path: Path) -> None:
    with pytest.raises(pipeline.PipelineError, match="smoothing"):
        pipeline.run(RunConfig(name="s", out_dir=str(tmp_path), smoothing_windows=(50,)), raw=RAW)
    assert not list(tmp_path.iterdir())


def test_genbank_input_gets_the_smoothed_track_too(tmp_path: Path) -> None:
    sample = str(Path(__file__).parent / "data" / "sample.gb")
    result = pipeline.run(RunConfig(name="toy", input_path=sample, out_dir=str(tmp_path)))
    assert "toy.entropy.smooth51.bedgraph" in {Path(p).name for p in result.outputs}


def test_multi_record_input_smooths_each_contig_on_its_own(tmp_path: Path) -> None:
    sample = str(Path(__file__).parent / "data" / "multi.gb")
    cfg = RunConfig(name="m", input_path=sample, out_dir=str(tmp_path), smoothing_windows=(3,))
    result = pipeline.run(cfg)
    rows = [ln.split("\t") for ln in _data_lines(_out(result, "m.entropy.smooth3.bedgraph"))]
    by_chrom: dict[str, list[float]] = {}
    for r in rows:
        by_chrom.setdefault(r[0], []).append(float(r[3]))
    assert len(by_chrom) == result.contigs > 1
    # the first base of every contig averages only 2 bases (its own), so a contig boundary
    # never leaks a neighbour's entropy into it
    raw_rows = [ln.split("\t") for ln in _data_lines(_out(result, "m.entropy.bedgraph"))]
    for chrom, smoothed in by_chrom.items():
        raw = [float(r[3]) for r in raw_rows if r[0] == chrom]
        assert smoothed[0] == pytest.approx((raw[0] + raw[1]) / 2, abs=1e-4)


def _plasmid_run(tmp_path: Path, topology: Topology):
    seq = "".join(np.random.default_rng(5).choice(list("ACGT"), size=300))
    cfg = RunConfig(
        name="p",
        out_dir=str(tmp_path),
        context_length=128,
        max_len=512,
        direction=Direction.FORWARD_ONLY,
        topology=topology,
        smoothing_windows=(5,),
    )
    result = pipeline.run(cfg, raw=seq)
    rows = _data_lines(_out(result, "p.entropy.smooth5.bedgraph"))
    return result, np.array([float(r.split("\t")[3]) for r in rows])


def test_a_circular_run_smooths_across_the_origin_and_a_linear_run_does_not(tmp_path: Path) -> None:
    circ, circ_smooth = _plasmid_run(tmp_path / "c", Topology.CIRCULAR)
    assert np.allclose(circ_smooth, rolling_mean(circ.values, 5, circular=True), atol=1e-4)
    lin, lin_smooth = _plasmid_run(tmp_path / "l", Topology.LINEAR)
    assert np.allclose(lin_smooth, rolling_mean(lin.values, 5, circular=False), atol=1e-4)
    # the origin is where they differ: circular averages the last two and next two bases too
    wrapped = circ.values[[-2, -1, 0, 1, 2]].astype(np.float64).mean()
    assert circ_smooth[0] == pytest.approx(wrapped, abs=1e-4)


# --- manifest -------------------------------------------------------------------------------------------


def _manifest(analysis: dict | None = None, outputs: list[str] | None = None) -> JobManifest:
    return JobManifest.parse(
        json.dumps(
            {
                "schema": 1,
                "jobId": "j1",
                "inputs": [{"id": "i1", "path": "input/x.fa", "name": "x"}],
                "analysis": analysis or {},
                "outputs": outputs or [],
                "store": {"kind": "localdir", "root": "/tmp/store"},
            }
        )
    )


def _cfg(m: JobManifest) -> RunConfig:
    return m.build_run_config(m.inputs[0], local_input_path="x.fa", local_out_dir="out")


def test_manifest_smoothing_windows_reach_the_run_config() -> None:
    assert _cfg(_manifest({"smoothingWindows": [201, 11]})).smoothing_windows == (11, 201)


def test_manifest_without_smoothing_windows_uses_the_default() -> None:
    assert _cfg(_manifest()).smoothing_windows == (51,)


def test_manifest_empty_smoothing_windows_means_none() -> None:
    assert _cfg(_manifest({"smoothingWindows": []})).smoothing_windows == ()


@pytest.mark.parametrize("bad", [[50], [0], "51", [1.5], 51])
def test_manifest_refuses_bad_smoothing_windows_at_parse_time(bad) -> None:
    with pytest.raises(ManifestError, match="smoothing"):
        _manifest({"smoothingWindows": bad})


def test_manifest_outputs_name_smoothed() -> None:
    assert _cfg(_manifest(outputs=["bedgraph", "smoothed"])).include_smoothed is True
    assert _cfg(_manifest(outputs=["bedgraph"])).include_smoothed is False
    assert _cfg(_manifest()).include_smoothed is True
