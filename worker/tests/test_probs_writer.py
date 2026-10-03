"""Tests for the optional per-base probability matrix output (issue #127):
``<name>.probs.tsv.gz`` (and optionally ``<name>.probs.npy``).

The decisive observable (the issue's own): recomputing entropy from the exported matrix
reproduces the entropy track within 1e-6. The matrix is the ``(L, 4)`` array the contract guard
already validated, in the ORIGINAL strand's coordinates and base order (a reverse-complement
pass is flipped back and complemented), combined by the same rule as the entropy track; it is
serialised, never recomputed.
"""

from __future__ import annotations

import gzip
import json
from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.analysis.direction import analyze_direction
from dna_entropy.analysis.entropy import shannon_entropy
from dna_entropy.config import Direction, RunConfig, Topology
from dna_entropy.predictors.base import check_probability_matrix
from dna_entropy.predictors.mock import MockPredictor
from dna_entropy.worker.manifest import JobManifest
from dna_entropy.writers.probs import PROBS_COLUMNS, ProbsWriter

K = 128
CEILING = 512


def _seq(n: int, seed: int) -> str:
    return "".join(np.random.default_rng(seed).choice(list("ACGT"), size=n))


def _read_gz(path: str) -> list[str]:
    with gzip.open(path, "rt", encoding="utf-8", newline="") as fh:
        return fh.read().splitlines()


def _parse(lines: list[str]) -> tuple[list[str], np.ndarray, str]:
    """-> (bases, (L, 4) float32 matrix, positions-as-text), skipping header/comment lines."""
    rows = [ln.split("\t") for ln in lines if ln and not ln.startswith("#") and not ln.startswith("position")]
    probs = np.array([[float(x) for x in r[2:6]] for r in rows], dtype=np.float32)
    return [r[1] for r in rows], probs, ",".join(r[0] for r in rows)


class _Ramp:
    """Row i of every window: P(A) = (i + 1) / (len + 1), the rest split evenly. Asymmetric in
    both position and base, so a missed row flip or a missed complement cannot cancel."""

    def predict(self, window: str) -> np.ndarray:
        n = len(window)
        probs = np.empty((n, 4), dtype=np.float32)
        a = (np.arange(n, dtype=np.float64) + 1.0) / (n + 1.0)
        probs[:, 0] = a
        probs[:, 1:] = ((1.0 - a) / 3.0)[:, None]
        return probs


# --- the writer -------------------------------------------------------------------------------------


def test_columns_are_the_documented_ones() -> None:
    assert PROBS_COLUMNS == ("position", "base", "pA", "pC", "pG", "pT")


def test_writes_a_gzip_tsv_with_one_row_per_base_and_one_based_positions(tmp_path: Path) -> None:
    probs = np.array([[0.7, 0.1, 0.1, 0.1], [0.25, 0.25, 0.25, 0.25]], dtype=np.float32)
    path = ProbsWriter().write_tsv_gz(name="r", blocks=[("c", "AC", probs)], start=101, out_dir=str(tmp_path))
    assert Path(path).name == "r.probs.tsv.gz"
    lines = _read_gz(path)
    assert lines[0] == "\t".join(PROBS_COLUMNS)
    assert lines[1].split("\t")[:2] == ["101", "A"]
    assert lines[2].split("\t")[:2] == ["102", "C"]
    assert len(lines) == 3


def test_values_round_trip_float32_exactly(tmp_path: Path) -> None:
    rng = np.random.default_rng(1)
    raw = rng.random((500, 4)).astype(np.float64) ** 8  # lots of tiny probabilities
    probs = (raw / raw.sum(axis=1, keepdims=True)).astype(np.float32)
    path = ProbsWriter().write_tsv_gz(
        name="r", blocks=[("c", "A" * 500, probs)], start=1, out_dir=str(tmp_path)
    )
    _, back, _ = _parse(_read_gz(path))
    assert back.tobytes() == probs.tobytes()


def test_the_exported_entropy_matches_to_1e_6_even_with_tiny_probabilities(tmp_path: Path) -> None:
    probs = np.array([[1 - 3e-7, 1e-7, 1e-7, 1e-7], [0.5, 0.5, 0.0, 0.0]], dtype=np.float32)
    path = ProbsWriter().write_tsv_gz(name="r", blocks=[("c", "AA", probs)], start=1, out_dir=str(tmp_path))
    _, back, _ = _parse(_read_gz(path))
    assert np.abs(shannon_entropy(back) - shannon_entropy(probs)).max() < 1e-6


def test_multi_contig_blocks_get_contig_comment_lines_and_restart_numbering(tmp_path: Path) -> None:
    p = np.full((2, 4), 0.25, dtype=np.float32)
    path = ProbsWriter().write_tsv_gz(
        name="r", blocks=[("a", "AC", p), ("b", "GT", p)], start=1, out_dir=str(tmp_path)
    )
    lines = _read_gz(path)
    assert [ln for ln in lines if ln.startswith("#")] == ["# contig: a", "# contig: b"]
    assert [ln.split("\t")[0] for ln in lines if ln and ln[0].isdigit()] == ["1", "2", "1", "2"]


def test_a_single_block_writes_no_contig_comment(tmp_path: Path) -> None:
    path = ProbsWriter().write_tsv_gz(
        name="r",
        blocks=[("a", "AC", np.full((2, 4), 0.25, dtype=np.float32))],
        start=1,
        out_dir=str(tmp_path),
    )
    assert not any(ln.startswith("#") for ln in _read_gz(path))


def test_the_gzip_is_deterministic_and_the_text_is_lf_utf8(tmp_path: Path) -> None:
    p = np.full((3, 4), 0.25, dtype=np.float32)
    a = ProbsWriter().write_tsv_gz(name="a", blocks=[("c", "ACG", p)], start=1, out_dir=str(tmp_path))
    b = ProbsWriter().write_tsv_gz(name="b", blocks=[("c", "ACG", p)], start=1, out_dir=str(tmp_path))
    assert Path(a).read_bytes() == Path(b).read_bytes()  # no timestamp, no file name inside
    header = Path(a).read_bytes()[:10]
    assert header[3] & 0x08 == 0  # FNAME flag clear: no original file name stored
    assert (
        header[4:8] == b"\x00\x00\x00\x00"
    )  # MTIME is zero, not "now" (two writes in one second agree anyway)
    raw = gzip.decompress(Path(a).read_bytes())
    assert b"\r" not in raw
    assert raw.endswith(b"\n")
    raw.decode("utf-8")


def test_npy_is_the_concatenated_float32_matrix(tmp_path: Path) -> None:
    rng = np.random.default_rng(2)
    a = rng.dirichlet(np.ones(4), size=5).astype(np.float32)
    b = rng.dirichlet(np.ones(4), size=3).astype(np.float32)
    path = ProbsWriter().write_npy(
        name="r", blocks=[("a", "A" * 5, a), ("b", "C" * 3, b)], out_dir=str(tmp_path)
    )
    assert Path(path).name == "r.probs.npy"
    loaded = np.load(path)
    assert loaded.dtype == np.float32
    assert loaded.shape == (8, 4)
    assert loaded.tobytes() == np.concatenate([a, b]).tobytes()


# --- the matrix out of the direction analysis -----------------------------------------------------------


def _analyze(direction: Direction, seq: str, predictor=None, *, keep: bool = True, circular: bool = False):
    return analyze_direction(
        predictor or MockPredictor(seed=3),
        seq,
        context_length=K,
        ceiling=CEILING,
        direction=direction,
        circular=circular,
        keep_probs=keep,
    )


def test_probs_are_not_kept_unless_asked() -> None:
    assert _analyze(Direction.FORWARD_ONLY, _seq(300, 1), keep=False).probs is None


@pytest.mark.parametrize("direction", list(Direction))
@pytest.mark.parametrize("circular", [False, True])
def test_the_kept_matrix_passes_the_contract_and_reproduces_the_entropy_track(direction, circular) -> None:
    seq = _seq(700, 4)
    dr = _analyze(direction, seq, circular=circular)
    check_probability_matrix(dr.probs, len(seq))
    if direction is Direction.BOTH_AVERAGED:
        # entropy of a mean is >= the mean of the entropies (concavity): exact equality is
        # impossible, the track is only bounded by the matrix
        assert (shannon_entropy(dr.probs) >= dr.values - 1e-5).all()
    else:
        assert np.abs(shannon_entropy(dr.probs) - dr.values).max() < 1e-6


def test_forward_only_matrix_is_the_predictors_own_output() -> None:
    seq = _seq(100, 5)  # one window
    dr = _analyze(Direction.FORWARD_ONLY, seq, _Ramp())
    assert np.array_equal(dr.probs, _Ramp().predict(seq))


def test_reverse_only_matrix_is_flipped_and_complemented_back_to_the_original_strand() -> None:
    seq = _seq(100, 6)
    n = len(seq)
    dr = _analyze(Direction.REVERSE_ONLY, seq, _Ramp())
    ramp = _Ramp().predict(seq)  # the reverse pass sees a window of the same length
    # original base j is rc position n-1-j; its distribution over {A,C,G,T} is the rc base's
    # distribution over {T,G,C,A}
    expected = ramp[::-1][:, ::-1]
    assert np.array_equal(dr.probs, expected)
    assert dr.probs[0, 3] == pytest.approx(n / (n + 1), abs=1e-6)  # the ramp's largest P(A) is P(T) here


@pytest.mark.parametrize(
    "direction", [Direction.FORWARD_ONLY, Direction.REVERSE_ONLY, Direction.BOTH_COMBINED]
)
def test_surprisal_recomputed_from_the_exported_matrix_and_the_sequence_matches(direction) -> None:
    """Orientation check the entropy cannot make (entropy ignores column order): the
    probability the matrix gives the ACTUAL base is exactly what the surprisal track used."""
    seq = _seq(700, 7)
    dr = _analyze(direction, seq)
    cols = np.array(["ACGT".index(b) for b in seq])
    p_actual = dr.probs[np.arange(len(seq)), cols].astype(np.float64)
    recomputed = -np.log2(np.clip(p_actual, 1e-30, None))
    assert np.abs(recomputed - dr.surprisal_values).max() < 1e-4


def test_both_averaged_matrix_is_the_mean_of_the_two_passes_where_both_have_full_context() -> None:
    seq = _seq(700, 8)
    fwd = _analyze(Direction.FORWARD_ONLY, seq)
    rev = _analyze(Direction.REVERSE_ONLY, seq)
    avg = _analyze(Direction.BOTH_AVERAGED, seq)
    middle = slice(K, len(seq) - K)  # both directions have >= K context here
    assert np.allclose(avg.probs[middle], (fwd.probs[middle] + rev.probs[middle]) / 2, atol=1e-6)
    assert any("averaged" in n and "probabilit" in n for n in avg.notices)


def test_both_separate_matrix_is_the_combined_track_not_either_pass() -> None:
    seq = _seq(700, 9)
    sep = _analyze(Direction.BOTH_SEPARATE, seq)
    comb = _analyze(Direction.BOTH_COMBINED, seq)
    assert np.array_equal(sep.probs, comb.probs)


def test_circular_matrix_is_trimmed_to_the_molecule_not_the_padded_window() -> None:
    seq = _seq(300, 10)
    dr = _analyze(Direction.BOTH_COMBINED, seq, circular=True)
    assert dr.probs.shape == (300, 4)


# --- wired through the pipeline: the issue's observable ---------------------------------------------------


def _run(tmp_path: Path, seq: str, **kw) -> pipeline.RunResult:
    cfg = RunConfig(name="pm", out_dir=str(tmp_path), context_length=K, max_len=CEILING, seed=4, **kw)
    return pipeline.run(cfg, raw=seq)


def test_default_run_writes_no_probability_files(tmp_path: Path) -> None:
    result = _run(tmp_path, _seq(300, 11))
    assert not any("probs" in Path(p).name for p in result.outputs)
    assert not any("probs" in p.name for p in tmp_path.iterdir())


def test_defaults_are_off() -> None:
    cfg = RunConfig()
    assert (cfg.include_probs, cfg.include_probs_npy) == (False, False)


@pytest.mark.parametrize(
    "direction", [Direction.FORWARD_ONLY, Direction.REVERSE_ONLY, Direction.BOTH_COMBINED]
)
@pytest.mark.parametrize("topology", [Topology.LINEAR, Topology.CIRCULAR])
def test_entropy_recomputed_from_the_exported_file_reproduces_the_track(
    tmp_path: Path, direction, topology
) -> None:
    result = _run(tmp_path, _seq(400, 12), include_probs=True, direction=direction, topology=topology)
    path = next(p for p in result.outputs if p.endswith(".probs.tsv.gz"))
    bases, probs, _ = _parse(_read_gz(path))
    assert "".join(bases) == result.seq
    assert np.abs(shannon_entropy(probs) - result.values).max() < 1e-6


def test_the_files_are_listed_in_the_outputs_and_exist(tmp_path: Path) -> None:
    result = _run(tmp_path, _seq(300, 13), include_probs=True, include_probs_npy=True)
    names = {Path(p).name for p in result.outputs}
    assert {"pm.probs.tsv.gz", "pm.probs.npy"} <= names
    assert {p.name for p in tmp_path.iterdir()} == names


def test_npy_matches_the_tsv_matrix(tmp_path: Path) -> None:
    result = _run(tmp_path, _seq(300, 14), include_probs=True, include_probs_npy=True)
    tsv = next(p for p in result.outputs if p.endswith(".probs.tsv.gz"))
    npy = next(p for p in result.outputs if p.endswith(".probs.npy"))
    _, from_tsv, _ = _parse(_read_gz(tsv))
    assert np.array_equal(np.load(npy), from_tsv)


def test_npy_only_does_not_write_the_tsv(tmp_path: Path) -> None:
    result = _run(tmp_path, _seq(300, 15), include_probs_npy=True)
    names = {Path(p).name for p in result.outputs}
    assert "pm.probs.npy" in names
    assert "pm.probs.tsv.gz" not in names


def test_start_offset_labels_positions_not_values(tmp_path: Path) -> None:
    result = _run(tmp_path, _seq(300, 16), include_probs=True, start=1001)
    path = next(p for p in result.outputs if p.endswith(".probs.tsv.gz"))
    assert _read_gz(path)[1].split("\t")[0] == "1001"


def test_genbank_multi_record_input_gets_one_block_per_contig(tmp_path: Path) -> None:
    sample = str(Path(__file__).parent / "data" / "multi.gb")
    cfg = RunConfig(
        name="m", input_path=sample, out_dir=str(tmp_path), include_probs=True, include_probs_npy=True
    )
    result = pipeline.run(cfg)
    tsv = next(p for p in result.outputs if p.endswith(".probs.tsv.gz"))
    assert [ln for ln in _read_gz(tsv) if ln.startswith("# contig:")] and result.contigs > 1
    assert np.load(next(p for p in result.outputs if p.endswith(".probs.npy"))).shape == (result.total_nt, 4)


def test_probs_are_only_retained_when_a_probs_output_was_asked_for(tmp_path: Path, monkeypatch) -> None:
    seen: list[bool] = []
    real = pipeline.analyze_direction

    def spy(*args, **kwargs):
        seen.append(kwargs.get("keep_probs", False))
        return real(*args, **kwargs)

    monkeypatch.setattr(pipeline, "analyze_direction", spy)
    _run(tmp_path / "a", _seq(300, 17))
    _run(tmp_path / "b", _seq(300, 17), include_probs_npy=True)
    assert seen == [False, True]


# --- manifest: probs is OFF unless named, even when outputs is unspecified --------------------------------


def _cfg(outputs: list[str]) -> RunConfig:
    m = JobManifest.parse(
        json.dumps(
            {
                "schema": 1,
                "jobId": "j1",
                "inputs": [{"id": "i1", "path": "input/x.fa", "name": "x"}],
                "outputs": outputs,
                "store": {"kind": "localdir", "root": "/tmp/store"},
            }
        )
    )
    return m.build_run_config(m.inputs[0], local_input_path="x.fa", local_out_dir="out")


def test_an_unspecified_outputs_list_means_everything_except_the_matrix() -> None:
    cfg = _cfg([])
    assert (cfg.include_probs, cfg.include_probs_npy) == (False, False)


def test_naming_probs_or_probs_npy_turns_each_on() -> None:
    assert (_cfg(["bedgraph", "probs"]).include_probs, _cfg(["bedgraph", "probs"]).include_probs_npy) == (
        True,
        False,
    )
    assert (_cfg(["probs_npy"]).include_probs, _cfg(["probs_npy"]).include_probs_npy) == (False, True)
    both = _cfg(["probs", "probs_npy"])
    assert (both.include_probs, both.include_probs_npy) == (True, True)


def test_the_file_is_a_real_gzip_stream(tmp_path: Path) -> None:
    path = ProbsWriter().write_tsv_gz(
        name="r", blocks=[("c", "A", np.full((1, 4), 0.25, dtype=np.float32))], start=1, out_dir=str(tmp_path)
    )
    with gzip.GzipFile(path, "rb") as fh:
        assert fh.read().startswith(b"position\t")
    assert Path(path).read_bytes()[:2] == b"\x1f\x8b"
