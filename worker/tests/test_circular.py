"""Tests for circular sequence mode (issue #128): wrap-around context for plasmids.

The mock predictor seeds on window CONTENT, so it cannot show rotation invariance. The stub
here is a pure function of the 3 bases before each position (and uniform at a window's row 0,
like the real model with no BOS token): window-independent for every row after the first
three, which is exactly the property a correct wrap-around must preserve.
"""

from __future__ import annotations

import json
import zlib
from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.analysis.direction import analyze_direction
from dna_entropy.config import Direction, RunConfig, Topology
from dna_entropy.readers.genbank import read_genbank

K = 16
CEILING = 64  # W = 32, S = 16: several windows over the padded sequence

_rng = np.random.default_rng(128)
SEQ = "".join(_rng.choice(list("ACGT"), size=200))
LONG = "".join(_rng.choice(list("ACGT"), size=300))  # >= 2K for the pipeline tests (K >= 128)


class _Local:
    """Probabilities depend only on the 3 preceding bases; row 0 is uniform (2.0 bits)."""

    def __init__(self) -> None:
        self.windows: list[int] = []

    def predict(self, window: str) -> np.ndarray:
        self.windows.append(len(window))
        probs = np.full((len(window), 4), 0.25, dtype=np.float32)
        for i in range(1, len(window)):
            seed = zlib.crc32(window[max(0, i - 3) : i].encode("ascii"))
            logits = np.random.default_rng(seed).standard_normal(4)
            e = np.exp(logits - logits.max())
            probs[i] = (e / e.sum()).astype(np.float32)
        return probs


def _run(seq: str, direction: Direction, *, circular: bool, predictor=None, k: int = K):
    return analyze_direction(
        predictor or _Local(), seq, context_length=k, ceiling=CEILING, direction=direction, circular=circular
    )


# --- the observable: position 0 gets real context --------------------------------------


def test_linear_forward_only_row_zero_is_the_uniform_two_bits() -> None:
    assert _run(SEQ, Direction.FORWARD_ONLY, circular=False).values[0] == pytest.approx(2.0)


def test_circular_forward_only_row_zero_has_wrapped_context_and_is_not_uniform() -> None:
    values = _run(SEQ, Direction.FORWARD_ONLY, circular=True).values
    assert values[0] < 2.0 - 1e-3
    assert len(values) == len(SEQ)


def test_circular_forward_only_row_zero_equals_what_the_last_bases_predict() -> None:
    """Position 0's context is the molecule's own tail: the stub's value there is a function
    of SEQ[-3:], so it must equal the entropy of that stub row computed by hand."""
    stub = _Local()
    probs = stub.predict(SEQ[-3:] + SEQ[0])[3]
    expected = float(-(probs * np.log2(probs)).sum())
    assert _run(SEQ, Direction.FORWARD_ONLY, circular=True).values[0] == pytest.approx(expected, abs=1e-5)


# --- rotation invariance (the issue's named test) ---------------------------------------


@pytest.mark.parametrize(
    "direction",
    [
        Direction.FORWARD_ONLY,
        Direction.REVERSE_ONLY,
        Direction.BOTH_COMBINED,
        Direction.BOTH_AVERAGED,
        Direction.BOTH_SEPARATE,
    ],
)
@pytest.mark.parametrize("shift", [1, 37, 199])
def test_rotating_a_circular_input_rotates_every_output(direction: Direction, shift: int) -> None:
    rotated = SEQ[shift:] + SEQ[:shift]
    a = _run(SEQ, direction, circular=True)
    b = _run(rotated, direction, circular=True)
    assert np.allclose(b.values, np.roll(a.values, -shift), atol=1e-6)
    assert np.allclose(b.surprisal_values, np.roll(a.surprisal_values, -shift), atol=1e-6)
    if direction is Direction.BOTH_SEPARATE:
        assert np.allclose(b.forward_values, np.roll(a.forward_values, -shift), atol=1e-6)
        assert np.allclose(b.reverse_values, np.roll(a.reverse_values, -shift), atol=1e-6)


def test_a_linear_input_is_not_rotation_invariant_so_the_test_above_can_fail() -> None:
    rotated = SEQ[37:] + SEQ[:37]
    a = _run(SEQ, Direction.FORWARD_ONLY, circular=False)
    b = _run(rotated, Direction.FORWARD_ONLY, circular=False)
    assert not np.allclose(b.values, np.roll(a.values, -37), atol=1e-6)


# --- combined mode: no seam, no reduced context -----------------------------------------


@pytest.mark.parametrize("direction", [Direction.BOTH_COMBINED, Direction.BOTH_AVERAGED])
def test_circular_combined_has_no_seam_and_no_reduced_context(direction: Direction) -> None:
    result = _run(SEQ, direction, circular=True)
    assert result.seam is None
    assert result.reduced_context_count == 0
    assert result.reduced_context_range is None
    assert result.circular is True


def test_linear_combined_still_has_its_seam() -> None:
    result = _run(SEQ, Direction.BOTH_COMBINED, circular=False)
    assert result.seam == K
    assert result.circular is False


def test_circular_combined_is_forward_everywhere_so_it_equals_forward_only() -> None:
    comb = _run(SEQ, Direction.BOTH_COMBINED, circular=True)
    fwd = _run(SEQ, Direction.FORWARD_ONLY, circular=True)
    assert np.allclose(comb.values, fwd.values, atol=1e-6)


# --- a molecule shorter than K wraps more than once -------------------------------------


@pytest.mark.parametrize("direction", [Direction.FORWARD_ONLY, Direction.BOTH_COMBINED])
def test_a_circular_input_shorter_than_k_still_gets_full_context(direction: Direction) -> None:
    short = SEQ[:12]
    result = _run(short, direction, circular=True)
    assert len(result.values) == 12
    assert result.values[0] < 2.0 - 1e-3
    assert result.reduced_context_count == 0
    rotated = short[5:] + short[:5]
    assert np.allclose(_run(rotated, direction, circular=True).values, np.roll(result.values, -5), atol=1e-6)


# --- cost: still one forward pass per window, padded by exactly K each side -------------


def test_circular_runs_windows_over_the_k_padded_sequence_never_per_base() -> None:
    stub = _Local()
    _run(SEQ, Direction.FORWARD_ONLY, circular=True, predictor=stub)
    assert all(w <= CEILING for w in stub.windows)
    covered_lengths = len(SEQ) + 2 * K
    assert 1 < len(stub.windows) < covered_lengths // 4
    linear = _Local()
    _run(SEQ, Direction.FORWARD_ONLY, circular=False, predictor=linear)
    assert len(stub.windows) > len(linear.windows)


# --- topology resolution through the real pipeline entry point --------------------------

_PLASMID = (
    "LOCUS       pDEMO                {n:>11} bp    DNA     {topo:<8} SYN 01-JAN-2000\n"
    "DEFINITION  demo.\nACCESSION   pDEMO\nFEATURES             Location/Qualifiers\n"
    "ORIGIN\n{origin}\n//\n"
)


def _genbank(tmp_path: Path, topo: str, seq: str = LONG) -> str:
    lines = []
    for i in range(0, len(seq), 60):
        chunk = seq[i : i + 60].lower()
        groups = " ".join(chunk[j : j + 10] for j in range(0, len(chunk), 10))
        lines.append(f"{i + 1:>9} {groups}")
    path = tmp_path / f"{topo}.gb"
    path.write_text(
        _PLASMID.format(n=len(seq), topo=topo, origin="\n".join(lines)), encoding="utf-8", newline="\n"
    )
    return str(path)


def test_the_reader_reports_the_locus_line_topology(tmp_path: Path) -> None:
    circ, _ = read_genbank(_genbank(tmp_path, "circular"))
    lin, _ = read_genbank(_genbank(tmp_path, "linear"))
    assert circ[0].circular is True
    assert lin[0].circular is False


def _provenance_contig(out: Path) -> dict:
    return json.loads((out / "provenance.json").read_text(encoding="utf-8"))["contigs"][0]


@pytest.mark.parametrize(
    ("topology", "locus", "expect_circular"),
    [
        (Topology.AUTO, "circular", True),
        (Topology.AUTO, "linear", False),
        (Topology.LINEAR, "circular", False),
        (Topology.CIRCULAR, "linear", True),
    ],
)
@pytest.mark.parametrize("direction", [Direction.FORWARD_ONLY, Direction.BOTH_COMBINED])
def test_topology_option_and_locus_line_resolve_through_pipeline_run(
    tmp_path: Path, topology: Topology, locus: str, expect_circular: bool, direction: Direction
) -> None:
    out = tmp_path / "out"
    cfg = RunConfig(
        name="plasmid",
        input_path=_genbank(tmp_path, locus),
        out_dir=str(out),
        context_length=128,
        max_len=512,
        direction=direction,
        topology=topology,
        seed=1,
    )
    result = pipeline.run(cfg, predictor=_Local())
    contig = _provenance_contig(out)
    assert contig["topology"] == ("circular" if expect_circular else "linear")
    if direction is Direction.BOTH_COMBINED:
        assert (contig["seam"] is None) == expect_circular
        assert result.seam is None if expect_circular else result.seam == 128
    run = json.loads((out / "provenance.json").read_text(encoding="utf-8"))["run"]
    assert run["topology"] == topology.value
    if direction is Direction.FORWARD_ONLY:
        # The observable: row 0 is the uniform 2.0 bits only when the molecule is read as
        # linear (combined mode already takes a linear molecule's head from the reverse pass).
        assert (result.values[0] < 2.0 - 1e-3) == expect_circular


def test_a_circular_run_writes_exactly_the_input_length_per_track(tmp_path: Path) -> None:
    out = tmp_path / "out"
    cfg = RunConfig(
        name="plasmid",
        input_path=_genbank(tmp_path, "circular"),
        out_dir=str(out),
        context_length=128,
        max_len=512,
        topology=Topology.AUTO,
        seed=1,
    )
    result = pipeline.run(cfg, predictor=_Local())
    assert len(result.values) == len(LONG)
    tsv = (out / "plasmid.entropy.tsv").read_text(encoding="utf-8").splitlines()
    assert len(tsv) == len(LONG) + 1  # header + one row per base, no padding rows


def test_fasta_and_paste_inputs_are_linear_under_auto_and_circular_when_asked(tmp_path: Path) -> None:
    auto = RunConfig(name="p1", out_dir=str(tmp_path / "a"), context_length=128, max_len=512, seed=1)
    pipeline.run(auto, raw=SEQ, predictor=_Local())
    assert _provenance_contig(tmp_path / "a")["topology"] == "linear"
    forced = RunConfig(
        name="p2",
        out_dir=str(tmp_path / "b"),
        context_length=128,
        max_len=512,
        topology=Topology.CIRCULAR,
        seed=1,
    )
    result = pipeline.run(forced, raw=SEQ, predictor=_Local())
    assert _provenance_contig(tmp_path / "b")["topology"] == "circular"
    assert result.values[0] < 2.0 - 1e-3


def test_a_circular_run_says_so_in_the_notices(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="p3", out_dir=str(tmp_path), context_length=128, max_len=512, topology=Topology.CIRCULAR, seed=1
    )
    result = pipeline.run(cfg, raw=SEQ, predictor=_Local())
    assert any("circular" in n.lower() for n in result.notices)
