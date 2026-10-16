"""Issue #129: compare two runs (or two sequences). B minus A, by position or by an offset.

The inputs are two runs' ``<name>.entropy.tsv`` files (1-based positions, a ``# contig:`` line per
record). The difference track is written as bedGraph so IGV and the app's viewer can stack it under
A and B; the largest deviations are listed in a small TSV and returned for the console.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
from typer.testing import CliRunner

from dna_entropy import pipeline
from dna_entropy.analysis.compare import CompareError, difference, largest_deviations, read_entropy_tsv
from dna_entropy.cli import app
from dna_entropy.config import RunConfig
from dna_entropy.writers.compare import write_compare

SEQ = "ACGTTGCAAGGCTTAACCGGTATCGATCGGATCCATGCAAT" * 3


def _run(tmp_path: Path, name: str, seq: str = SEQ, seed: int = 0) -> Path:
    fasta = tmp_path / f"{name}.fasta"
    fasta.write_text(f">{name}\n{seq}\n", encoding="utf-8", newline="\n")
    out = tmp_path / name
    pipeline.run(RunConfig(name=name, input_path=str(fasta), out_dir=str(out), seed=seed))
    return out / f"{name}.entropy.tsv"


def test_read_entropy_tsv_returns_start_and_values_per_contig(tmp_path: Path) -> None:
    tsv = _run(tmp_path, "a")
    blocks = read_entropy_tsv(str(tsv))
    (name, (start, values)) = next(iter(blocks.items()))
    assert (
        start == 1
        and values.shape == (len(SEQ),)
        and 0.0 <= float(values.min()) <= float(values.max()) <= 2.0
    )


def test_a_run_compared_with_itself_is_an_all_zero_difference(tmp_path: Path) -> None:
    tsv = str(_run(tmp_path, "a"))
    pos, diff = difference(read_entropy_tsv(tsv)["a"], read_entropy_tsv(tsv)["a"])
    assert len(diff) == len(SEQ) and not diff.any()
    assert pos[0] == 1 and pos[-1] == len(SEQ)


def test_difference_is_b_minus_a() -> None:
    a = (1, np.array([1.0, 1.0, 1.0], dtype=np.float32))
    b = (1, np.array([1.5, 0.5, 1.0], dtype=np.float32))
    pos, diff = difference(a, b)
    assert pos.tolist() == [1, 2, 3] and diff.tolist() == [0.5, -0.5, 0.0]


def test_different_lengths_without_an_offset_are_refused_with_the_action() -> None:
    a = (1, np.zeros(10, dtype=np.float32))
    b = (1, np.zeros(8, dtype=np.float32))
    with pytest.raises(CompareError, match="--offset"):
        difference(a, b)


def test_an_offset_aligns_b_position_p_with_a_position_p_plus_offset() -> None:
    a = (1, np.arange(10, dtype=np.float32))  # a[pos] = pos - 1
    b = (1, np.arange(4, dtype=np.float32) + 2)  # b at 1..4 = 2,3,4,5 ; offset 2 -> a at 3..6 = 2,3,4,5
    pos, diff = difference(a, b, offset=2)
    assert pos.tolist() == [3, 4, 5, 6] and not diff.any()


def test_an_offset_with_no_overlap_is_refused() -> None:
    a = (1, np.zeros(5, dtype=np.float32))
    b = (1, np.zeros(5, dtype=np.float32))
    with pytest.raises(CompareError, match="overlap"):
        difference(a, b, offset=50)


def test_largest_deviations_are_ranked_by_absolute_difference() -> None:
    pos = np.arange(1, 6)
    diff = np.array([0.1, -0.9, 0.0, 0.5, 0.2], dtype=np.float32)
    top = largest_deviations(pos, diff, top=2)
    assert [(p, round(d, 2)) for p, d in top] == [(2, -0.9), (4, 0.5)]


def test_two_different_sequences_differ_and_the_files_are_written(tmp_path: Path) -> None:
    a = read_entropy_tsv(str(_run(tmp_path, "a")))["a"]
    other = SEQ[:60] + "TTTTTTTTTTTT" + SEQ[72:]
    b = read_entropy_tsv(str(_run(tmp_path, "b", seq=other, seed=0)))["b"]
    pos, diff = difference(a, b)
    out = write_compare(name="cmp", contig="a", positions=pos, diff=diff, out_dir=str(tmp_path / "o"), top=5)
    bed = Path(out.bedgraph).read_text(encoding="utf-8").splitlines()
    assert bed[0].startswith("track type=bedGraph") and len(bed) == 1 + len(SEQ)
    assert bed[1].split("\t")[:3] == ["a", "0", "1"]
    rows = Path(out.deviations).read_text(encoding="utf-8").splitlines()
    assert rows[0] == "position\tdifference_bits" and len(rows) == 1 + 5


def test_cli_compare_writes_outputs_and_says_where(tmp_path: Path) -> None:
    tsv = str(_run(tmp_path, "a"))
    res = CliRunner().invoke(app, ["compare", tsv, tsv, "--name", "self", "--out-dir", str(tmp_path / "o")])
    assert res.exit_code == 0, res.output
    assert "OK:" in res.output and (tmp_path / "o" / "self.compare.bedgraph").exists()
    values = [
        float(ln.split("\t")[3])
        for ln in (tmp_path / "o" / "self.compare.bedgraph").read_text().splitlines()[1:]
    ]
    assert set(values) == {0.0}


def test_cli_compare_length_mismatch_is_an_error_naming_offset(tmp_path: Path) -> None:
    a = _run(tmp_path, "a")
    b = _run(tmp_path, "b", seq=SEQ[:50])
    res = CliRunner().invoke(app, ["compare", str(a), str(b), "--out-dir", str(tmp_path / "o")])
    assert res.exit_code == 1 and "ERROR:" in res.output and "--offset" in res.output
