"""Tests for the low/high-entropy region caller (issue #125): analysis/regions.py, the
writers/regions.py BED + GFF3 writer, and their wiring through the pipeline, CLI and manifest.

The decisive observable (the issue's own): on a synthetic sequence with a planted 50-base
zero-entropy stretch, the BED contains exactly that interval, 0-based half-open.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pytest
from hypothesis import given, settings
from hypothesis import strategies as st

from dna_entropy import pipeline
from dna_entropy.analysis.regions import (
    DEFAULT_MERGE_GAP,
    DEFAULT_MIN_LENGTH,
    DEFAULT_THRESHOLD_BITS,
    Region,
    call_regions,
    validate_region_options,
)
from dna_entropy.config import Direction, RunConfig, Topology
from dna_entropy.worker.manifest import JobManifest, ManifestError
from dna_entropy.writers.regions import RegionWriter

PROPERTY_SETTINGS = settings(max_examples=150, deadline=None)


def _low(values, **kw):
    return call_regions(np.asarray(values, dtype=np.float32), kind="low", **{**_defaults(), **kw})


def _high(values, **kw):
    return call_regions(np.asarray(values, dtype=np.float32), kind="high", **{**_defaults(), **kw})


def _defaults() -> dict:
    return {"threshold": 0.5, "min_length": 3, "merge_gap": 0, "circular": False}


def _spans(regions: list[Region]) -> list[tuple[int, int]]:
    return [(r.begin, r.end) for r in regions]


# --- defaults are the issue's ----------------------------------------------------------------


def test_defaults_are_the_documented_ones() -> None:
    assert DEFAULT_THRESHOLD_BITS == 0.5
    assert DEFAULT_MIN_LENGTH == 20
    assert DEFAULT_MERGE_GAP == 5
    cfg = RunConfig()
    assert (cfg.region_threshold, cfg.region_min_length, cfg.region_merge_gap) == (0.5, 20, 5)
    assert cfg.include_regions is True


# --- the caller: examples --------------------------------------------------------------------


def test_a_planted_stretch_is_called_exactly() -> None:
    values = np.full(200, 2.0)
    values[70:120] = 0.0
    (r,) = call_regions(values, kind="low", threshold=0.5, min_length=20, merge_gap=5, circular=False)
    assert (r.begin, r.end) == (70, 120)
    assert r.kind == "low"
    assert r.mean_entropy == pytest.approx(0.0)


def test_below_threshold_is_strict() -> None:
    assert _spans(_low([2, 0.5, 0.5, 0.5, 2])) == []  # exactly the threshold is not "below"
    assert _spans(_low([2, 0.4999, 0.4999, 0.4999, 2])) == [(1, 4)]


def test_a_run_shorter_than_min_length_is_dropped_and_exactly_min_length_is_kept() -> None:
    assert _spans(_low([2, 0, 0, 2], min_length=3)) == []
    assert _spans(_low([2, 0, 0, 0, 2], min_length=3)) == [(1, 4)]


def test_runs_separated_by_at_most_merge_gap_are_merged_and_include_the_gap() -> None:
    values = [0, 0, 0, 2, 2, 0, 0, 0]
    assert _spans(_low(values, merge_gap=2)) == [(0, 8)]  # gap of 2 bridged
    assert _spans(_low(values, merge_gap=1)) == [(0, 3), (5, 8)]  # gap of 2 > 1 stays split


def test_merging_happens_before_the_length_filter() -> None:
    # two 2-base runs are each too short, but bridged they make a 5-base region
    assert _spans(_low([0, 0, 2, 0, 0], min_length=5, merge_gap=1)) == [(0, 5)]
    assert _spans(_low([0, 0, 2, 0, 0], min_length=5, merge_gap=0)) == []


def test_the_high_caller_mirrors_it() -> None:
    values = [0.0, 2.0, 2.0, 2.0, 0.0]
    (r,) = _high(values, threshold=1.5)
    assert (r.kind, r.begin, r.end) == ("high", 1, 4)
    assert _spans(_high([0, 1.5, 1.5, 1.5, 0], threshold=1.5)) == []  # strict


def test_no_values_and_no_hits_give_no_regions() -> None:
    assert _low([]) == []
    assert _low([2.0] * 10) == []


def test_a_region_at_either_end_is_called_linear() -> None:
    assert _spans(_low([0, 0, 0, 2, 2, 0, 0, 0])) == [(0, 3), (5, 8)]


def test_the_whole_sequence_below_threshold_is_one_region() -> None:
    assert _spans(_low([0.1] * 30)) == [(0, 30)]


# --- circular topology (#128) ---------------------------------------------------------------


def test_circular_a_run_at_the_end_and_one_at_the_start_are_one_region_across_the_origin() -> None:
    values = np.full(100, 2.0)
    values[70:100] = 0.0
    values[0:20] = 0.0
    (r,) = call_regions(values, kind="low", threshold=0.5, min_length=20, merge_gap=0, circular=True)
    assert (r.begin, r.end) == (70, 120)  # end > length: it wraps
    assert r.length == 50
    assert r.segments(100) == [(70, 100), (0, 20)]
    assert r.mean_entropy == pytest.approx(0.0)


def test_linear_the_same_values_are_two_regions() -> None:
    values = np.full(100, 2.0)
    values[70:100] = 0.0
    values[0:20] = 0.0
    spans = _spans(
        call_regions(values, kind="low", threshold=0.5, min_length=20, merge_gap=0, circular=False)
    )
    assert spans == [(0, 20), (70, 100)]


def test_circular_merge_gap_is_bridged_across_the_origin() -> None:
    values = np.full(100, 2.0)
    values[0:20] = 0.0
    values[96:100] = 0.0  # touches the end, so it joins 0..19 across the origin (gap 0)
    values[90:94] = 0.0  # 94 and 95 are 2 high bases: bridged to 96..99 by merge_gap=2
    spans = _spans(call_regions(values, kind="low", threshold=0.5, min_length=10, merge_gap=2, circular=True))
    assert spans == [(90, 120)]


def test_circular_gap_across_the_origin_wider_than_merge_gap_is_not_bridged() -> None:
    values = np.full(100, 2.0)
    values[0:10] = 0.0
    values[85:95] = 0.0  # 95..99 are 5 high bases between this run and the origin
    spans = _spans(call_regions(values, kind="low", threshold=0.5, min_length=10, merge_gap=4, circular=True))
    assert spans == [(0, 10), (85, 95)]
    spans = _spans(call_regions(values, kind="low", threshold=0.5, min_length=10, merge_gap=5, circular=True))
    assert spans == [(85, 110)]


def test_circular_everything_low_is_one_region_covering_the_molecule_not_a_wrap() -> None:
    (r,) = call_regions(np.zeros(40), kind="low", threshold=0.5, min_length=5, merge_gap=0, circular=True)
    assert (r.begin, r.end) == (0, 40)
    assert r.segments(40) == [(0, 40)]


def test_circular_nearly_all_low_with_a_small_gap_is_the_whole_molecule() -> None:
    values = np.zeros(40)
    values[0:2] = 2.0  # hits are 2..39; the 2 non-hits sit across the origin, within merge_gap
    (r,) = call_regions(values, kind="low", threshold=0.5, min_length=5, merge_gap=2, circular=True)
    assert r.length == 40
    assert r.segments(40) == [(0, 40)]


# --- properties -------------------------------------------------------------------------------


def _reference(values: np.ndarray, kind: str, threshold: float, min_length: int, merge_gap: int):
    """A deliberately different, naive implementation: hit/gap state machine over a Python list."""
    hit = [bool(v < threshold) if kind == "low" else bool(v > threshold) for v in values]
    runs: list[list[int]] = []
    i = 0
    while i < len(hit):
        if hit[i]:
            j = i
            while j < len(hit) and hit[j]:
                j += 1
            if runs and i - runs[-1][1] <= merge_gap:
                runs[-1][1] = j
            else:
                runs.append([i, j])
            i = j
        else:
            i += 1
    return [(a, b) for a, b in runs if b - a >= min_length]


_values = st.lists(st.sampled_from([0.0, 0.2, 0.5, 0.8, 1.5, 1.8, 2.0]), min_size=0, max_size=120)


@PROPERTY_SETTINGS
@given(
    _values,
    st.sampled_from(["low", "high"]),
    st.integers(min_value=1, max_value=12),
    st.integers(min_value=0, max_value=8),
)
def test_linear_matches_a_naive_reference(values, kind, min_length, merge_gap) -> None:
    arr = np.asarray(values, dtype=np.float32)
    threshold = 0.5 if kind == "low" else 1.5
    got = _spans(
        call_regions(
            arr, kind=kind, threshold=threshold, min_length=min_length, merge_gap=merge_gap, circular=False
        )
    )
    assert got == _reference(arr, kind, threshold, min_length, merge_gap)


@PROPERTY_SETTINGS
@given(_values, st.integers(min_value=1, max_value=12), st.integers(min_value=0, max_value=8))
def test_linear_invariants(values, min_length, merge_gap) -> None:
    arr = np.asarray(values, dtype=np.float32)
    regions = call_regions(
        arr, kind="low", threshold=0.5, min_length=min_length, merge_gap=merge_gap, circular=False
    )
    for r in regions:
        assert r.length >= min_length
        assert 0 <= r.begin < r.end <= len(arr)
        assert arr[r.begin] < 0.5 and arr[r.end - 1] < 0.5  # a region starts and ends on a hit
        assert r.mean_entropy == pytest.approx(float(arr[r.begin : r.end].mean()), abs=1e-6)
    for a, b in zip(regions, regions[1:], strict=False):
        assert b.begin - a.end > merge_gap  # otherwise they would have been merged


@PROPERTY_SETTINGS
@given(
    _values.filter(lambda v: len(v) >= 2),
    st.integers(min_value=0, max_value=1000),
    st.integers(1, 10),
    st.integers(0, 6),
)
def test_circular_rotating_the_input_rotates_the_called_bases(values, shift, min_length, merge_gap) -> None:
    arr = np.asarray(values, dtype=np.float32)
    n = len(arr)

    def covered(a: np.ndarray) -> set[int]:
        out: set[int] = set()
        for r in call_regions(
            a, kind="low", threshold=0.5, min_length=min_length, merge_gap=merge_gap, circular=True
        ):
            for s, e in r.segments(n):
                out.update(range(s, e))
        return out

    shift %= n
    assert covered(np.roll(arr, shift)) == {(i + shift) % n for i in covered(arr)}


# --- option validation ------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("threshold", "min_length", "merge_gap"),
    [(0.0, 20, 5), (-0.1, 20, 5), (1.01, 20, 5), (float("nan"), 20, 5), (0.5, 0, 5), (0.5, 20, -1)],
)
def test_validate_region_options_refuses_nonsense(threshold, min_length, merge_gap) -> None:
    with pytest.raises(ValueError, match="region"):
        validate_region_options(threshold=threshold, min_length=min_length, merge_gap=merge_gap)


def test_validate_region_options_accepts_the_defaults_and_the_mirror_boundary() -> None:
    validate_region_options(threshold=0.5, min_length=20, merge_gap=5)
    validate_region_options(threshold=1.0, min_length=1, merge_gap=0)  # low and high just meet


# --- the writer ---------------------------------------------------------------------------------


def _low_region(begin: int, end: int, mean: float = 0.1) -> Region:
    return Region(kind="low", begin=begin, end=end, mean_entropy=mean)


def test_bed_is_zero_based_half_open_with_the_start_offset(tmp_path: Path) -> None:
    bed, _ = RegionWriter().write_multi(
        name="r", blocks=[("chr1", 200, [_low_region(10, 30)])], start=101, out_dir=str(tmp_path)
    )
    assert Path(bed).name == "r.regions.bed"
    assert Path(bed).read_text(encoding="utf-8").splitlines() == ["chr1\t110\t130\tlow_entropy_1"]


def test_gff3_is_one_based_inclusive_with_attributes(tmp_path: Path) -> None:
    _, gff = RegionWriter().write_multi(
        name="r", blocks=[("chr1", 200, [_low_region(10, 30, 0.25)])], start=1, out_dir=str(tmp_path)
    )
    assert Path(gff).name == "r.regions.gff3"
    lines = Path(gff).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "##gff-version 3"
    assert lines[1] == "##sequence-region chr1 1 200"
    cols = lines[2].split("\t")
    assert cols[:8] == ["chr1", "dna-entropy", "region", "11", "30", ".", ".", "."]
    assert cols[8] == "ID=low_entropy_1;Name=low_entropy_1;kind=low_entropy;length=20;mean_entropy=0.2500"


def test_a_wrapping_region_is_two_bed_lines_and_two_gff3_lines_sharing_one_id(tmp_path: Path) -> None:
    wrap = Region(kind="low", begin=90, end=110, mean_entropy=0.0)  # 100 nt molecule: 90..99 + 0..9
    bed, gff = RegionWriter().write_multi(
        name="r", blocks=[("p", 100, [wrap])], start=1, out_dir=str(tmp_path)
    )
    assert Path(bed).read_text(encoding="utf-8").splitlines() == [
        "p\t90\t100\tlow_entropy_1",
        "p\t0\t10\tlow_entropy_1",
    ]
    rows = [
        line.split("\t")
        for line in Path(gff).read_text(encoding="utf-8").splitlines()
        if not line.startswith("#")
    ]
    assert [(r[3], r[4]) for r in rows] == [("91", "100"), ("1", "10")]
    assert all(r[8].startswith("ID=low_entropy_1;") and "wraps_origin=true" in r[8] for r in rows)


def test_ids_count_per_kind_across_contigs_and_high_regions_are_named_high(tmp_path: Path) -> None:
    high = Region(kind="high", begin=0, end=25, mean_entropy=1.9)
    bed, _ = RegionWriter().write_multi(
        name="r",
        blocks=[("a", 100, [_low_region(5, 30), high]), ("b", 100, [_low_region(40, 70)])],
        start=1,
        out_dir=str(tmp_path),
    )
    names = [line.split("\t")[3] for line in Path(bed).read_text(encoding="utf-8").splitlines()]
    assert names == ["low_entropy_1", "high_entropy_1", "low_entropy_2"]


def test_no_regions_gives_an_empty_bed_and_a_header_only_gff3(tmp_path: Path) -> None:
    bed, gff = RegionWriter().write_multi(name="r", blocks=[("c", 50, [])], start=1, out_dir=str(tmp_path))
    assert Path(bed).read_bytes() == b""
    assert Path(gff).read_text(encoding="utf-8").splitlines() == [
        "##gff-version 3",
        "##sequence-region c 1 50",
    ]


def test_region_files_are_lf_only_utf8(tmp_path: Path) -> None:
    for path in RegionWriter().write_multi(
        name="r", blocks=[("c", 50, [_low_region(1, 30)])], start=1, out_dir=str(tmp_path)
    ):
        raw = Path(path).read_bytes()
        assert b"\r" not in raw
        raw.decode("utf-8")


# --- wired through the pipeline: the issue's observable ----------------------------------------


class _PlantedPredictor:
    """One-hot (zero entropy) on every A or T, uniform on C/G. A or T, not just A, so the
    reverse-complement pass (where a planted A-run reads as T) agrees with the forward pass."""

    def predict(self, window: str) -> np.ndarray:
        probs = np.full((len(window), 4), 0.25, dtype=np.float32)
        for i, base in enumerate(window):
            if base in "AT":
                probs[i] = [1.0, 0.0, 0.0, 0.0] if base == "A" else [0.0, 0.0, 0.0, 1.0]
        return probs


def _cg(n: int, seed: int) -> str:
    return "".join(np.random.default_rng(seed).choice(list("CG"), size=n))


def _run(tmp_path: Path, seq: str, **kw):
    cfg = RunConfig(
        name="pl",
        out_dir=str(tmp_path),
        context_length=128,
        max_len=512,
        direction=Direction.FORWARD_ONLY,
        **kw,
    )
    return pipeline.run(cfg, raw=seq, predictor=_PlantedPredictor())


def _bed_rows(result) -> list[list[str]]:
    path = next(p for p in result.outputs if p.endswith(".regions.bed"))
    return [line.split("\t") for line in Path(path).read_text(encoding="utf-8").splitlines()]


def test_planted_50_base_zero_entropy_stretch_is_exactly_the_bed_interval(tmp_path: Path) -> None:
    seq = _cg(150, 1) + "A" * 50 + _cg(200, 2)
    result = _run(tmp_path, seq)
    # no other high/low regions: C/G are uniform (2.0 bits, a high region!) -> filter to low
    low = [r for r in _bed_rows(result) if r[3].startswith("low_entropy")]
    assert [(r[0], r[1], r[2]) for r in low] == [("pl", "150", "200")]


def test_the_same_run_also_calls_the_high_entropy_stretches(tmp_path: Path) -> None:
    seq = _cg(150, 1) + "A" * 50 + _cg(200, 2)
    result = _run(tmp_path, seq)
    high = [(r[1], r[2]) for r in _bed_rows(result) if r[3].startswith("high_entropy")]
    # the flanks are uniform (2.0 bits); the forward-only first row is 2.0 too
    assert high == [("0", "150"), ("200", "400")]


def test_region_files_are_listed_in_the_outputs_and_exist(tmp_path: Path) -> None:
    result = _run(tmp_path, _cg(150, 1) + "A" * 50 + _cg(200, 2))
    names = {Path(p).name for p in result.outputs}
    assert {"pl.regions.bed", "pl.regions.gff3"} <= names
    assert {p.name for p in tmp_path.iterdir()} == names


def test_region_options_are_read_min_length_threshold_and_gap(tmp_path: Path) -> None:
    seq = _cg(150, 1) + "A" * 50 + _cg(200, 2)
    # min_length above the planted stretch drops it
    low = [r for r in _bed_rows(_run(tmp_path / "a", seq, region_min_length=51)) if r[3].startswith("low")]
    assert low == []
    # a threshold above 1.0 bit would make the low and high callers overlap: refused up front
    with pytest.raises(pipeline.PipelineError, match="region"):
        _run(tmp_path / "b", seq, region_threshold=1.5)


def test_include_regions_off_writes_no_region_files(tmp_path: Path) -> None:
    result = _run(tmp_path, _cg(150, 1) + "A" * 50 + _cg(200, 2), include_regions=False)
    assert not any(".regions." in p for p in result.outputs)


def test_start_offset_moves_the_bed_not_the_called_bases(tmp_path: Path) -> None:
    seq = _cg(150, 1) + "A" * 50 + _cg(200, 2)
    result = _run(tmp_path, seq, start=1001)
    low = [r for r in _bed_rows(result) if r[3].startswith("low")]
    assert [(r[1], r[2]) for r in low] == [("1150", "1200")]


def test_circular_stretch_across_the_origin_is_one_region_in_two_bed_lines(tmp_path: Path) -> None:
    seq = "A" * 20 + _cg(300, 3) + "A" * 30  # 20 at the start + 30 at the end
    result = _run(tmp_path, seq, topology=Topology.CIRCULAR)
    low = [r for r in _bed_rows(result) if r[3].startswith("low")]
    assert [(r[1], r[2], r[3]) for r in low] == [
        ("320", "350", "low_entropy_1"),
        ("0", "20", "low_entropy_1"),
    ]
    # the same molecule read as linear: two separate regions (20 and 30 bases)
    result = _run(tmp_path / "lin", seq, topology=Topology.LINEAR)
    low = [r for r in _bed_rows(result) if r[3].startswith("low")]
    assert [(r[1], r[2], r[3]) for r in low] == [
        ("0", "20", "low_entropy_1"),
        ("320", "350", "low_entropy_2"),
    ]


# --- manifest: options and the `regions` output name ---------------------------------------------


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


def test_manifest_region_options_reach_the_run_config() -> None:
    cfg = _cfg(_manifest({"regionThreshold": 0.3, "regionMinLength": 40, "regionMergeGap": 9}))
    assert (cfg.region_threshold, cfg.region_min_length, cfg.region_merge_gap) == (0.3, 40, 9)


def test_manifest_without_region_options_uses_the_defaults() -> None:
    cfg = _cfg(_manifest())
    assert (cfg.region_threshold, cfg.region_min_length, cfg.region_merge_gap) == (0.5, 20, 5)


@pytest.mark.parametrize(
    "analysis",
    [{"regionThreshold": 0}, {"regionThreshold": 1.5}, {"regionMinLength": 0}, {"regionMergeGap": -1}],
)
def test_manifest_refuses_bad_region_options_at_parse_time(analysis: dict) -> None:
    with pytest.raises(ManifestError, match="region"):
        _manifest(analysis)


@pytest.mark.parametrize(
    "analysis",
    [
        {"regionMinLength": 20.9},
        {"regionMinLength": True},
        {"regionMinLength": "20"},
        {"regionMergeGap": 5.5},
        {"regionMergeGap": False},
        {"regionMergeGap": "5"},
        {"regionThreshold": True},
        {"regionThreshold": "0.5"},
        {"regionThreshold": None},
    ],
)
def test_manifest_refuses_wrongly_typed_region_options_without_coercing(analysis: dict) -> None:
    """A float, bool or string is refused, never silently truncated (20.9 -> 20, true -> 1)."""
    with pytest.raises(ManifestError, match="region"):
        _manifest(analysis)


def test_manifest_accepts_an_integer_valued_float_threshold_and_int_threshold() -> None:
    assert _cfg(_manifest({"regionThreshold": 1})).region_threshold == 1.0
    assert _cfg(_manifest({"regionThreshold": 0.25})).region_threshold == 0.25


def test_manifest_outputs_name_regions() -> None:
    assert _cfg(_manifest(outputs=["bedgraph", "regions"])).include_regions is True
    assert _cfg(_manifest(outputs=["bedgraph"])).include_regions is False
    assert _cfg(_manifest()).include_regions is True  # unspecified outputs means everything


def test_genbank_input_writes_the_region_files_too(tmp_path: Path) -> None:
    sample = str(Path(__file__).parent / "data" / "sample.gb")
    result = pipeline.run(RunConfig(name="toy", input_path=sample, out_dir=str(tmp_path)))
    names = {Path(p).name for p in result.outputs}
    assert {"toy.regions.bed", "toy.regions.gff3"} <= names


# --- end to end through the worker: manifest -> run_job -> result.json ---------------------------------


def _job(tmp_path: Path, outputs: list[str], analysis_extra: dict | None = None) -> tuple[set[str], str]:
    from dna_entropy.worker.blobstore import LocalBlobstore
    from dna_entropy.worker.runner import MANIFEST_PATH, RESULT_PATH, run_job

    store = LocalBlobstore(tmp_path)
    analysis = {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"}
    manifest = {
        "schema": 1,
        "jobId": "regions-job",
        "inputs": [{"id": "in1", "path": "input/a.fasta", "name": "a"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {**analysis, **(analysis_extra or {})},
        "outputs": outputs,
        "store": {"kind": "localdir", "root": "unused"},
    }
    store.write_text(MANIFEST_PATH, json.dumps(manifest))
    store.write_text("input/a.fasta", ">a\n" + "ACGT" * 75 + "\n")
    run_job(store)
    doc = json.loads(store.read_text(RESULT_PATH))
    files = doc["inputs"][0]["files"]
    gff = next((f["path"] for f in files if f["path"].endswith(".regions.gff3")), "")
    return {Path(f["path"]).name for f in files}, gff


def test_a_job_lists_the_region_files_in_result_json_and_the_option_reaches_the_caller(
    tmp_path: Path,
) -> None:
    listed, gff = _job(tmp_path / "a", [])
    assert {"a.regions.bed", "a.regions.gff3"} <= listed
    default_rows = [
        ln for ln in (tmp_path / "a" / gff).read_text(encoding="utf-8").splitlines() if "\t" in ln
    ]
    # mock entropy over a 300 nt periodic sequence sits near 2 bits: with the mirrored default
    # threshold (1.5) that is a long high-entropy stretch; with a tiny threshold (mirror 1.99) it
    # is a different call, so the manifest option demonstrably changes what is written
    _, gff2 = _job(tmp_path / "b", [], {"regionThreshold": 0.01})
    tight_rows = [ln for ln in (tmp_path / "b" / gff2).read_text(encoding="utf-8").splitlines() if "\t" in ln]
    assert default_rows != tight_rows


def test_a_job_with_outputs_that_omit_regions_writes_no_region_file(tmp_path: Path) -> None:
    listed, _ = _job(tmp_path, ["bedgraph"])
    assert not any("regions" in n for n in listed)
