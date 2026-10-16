"""Issue #192: batch outlier QC. Flag the input whose entropy profile deviates from the batch.

The statistic is a robust z-score on each input's mean entropy: ``0.6745 * (x - median) / MAD``
(median absolute deviation), so one outlier cannot hide itself by inflating the spread the way it
does for a plain standard deviation. Below ``MIN_BATCH`` inputs nothing is flagged: there is no
"batch" to deviate from.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.analysis.batch_qc import MIN_BATCH, Z_THRESHOLD, batch_qc
from dna_entropy.config import RunConfig
from dna_entropy.writers.summary import SummaryWriter


def _tracks(means: list[float]) -> list[tuple[str, np.ndarray]]:
    rng = np.random.default_rng(0)
    return [(f"c{i}", (m + rng.normal(0, 0.01, 200)).astype(np.float32)) for i, m in enumerate(means)]


def test_one_planted_outlier_in_near_identical_inputs_is_the_only_flag() -> None:
    rows = batch_qc(_tracks([1.80, 1.81, 1.79, 1.80, 1.82, 1.20, 1.80, 1.81]))
    assert [r.name for r in rows if r.flagged] == ["c5"]
    assert rows[5].z_score < -Z_THRESHOLD


def test_identical_inputs_flag_nothing_and_have_zero_z() -> None:
    rows = batch_qc([(f"c{i}", np.full(50, 1.5, dtype=np.float32)) for i in range(5)])
    assert not any(r.flagged for r in rows)
    assert all(r.z_score == 0.0 for r in rows)


def test_a_lone_outlier_among_exactly_identical_inputs_is_flagged_not_a_division_by_zero() -> None:
    tracks = [(f"c{i}", np.full(50, 1.5, dtype=np.float32)) for i in range(5)]
    tracks.append(("odd", np.full(50, 0.4, dtype=np.float32)))
    rows = batch_qc(tracks)
    assert [r.name for r in rows if r.flagged] == ["odd"]
    assert np.isfinite(rows[-1].z_score)


def test_a_batch_smaller_than_the_minimum_flags_nothing() -> None:
    rows = batch_qc(_tracks([1.8, 0.2])[: MIN_BATCH - 1])
    assert len(rows) == MIN_BATCH - 1 and not any(r.flagged for r in rows)


def test_row_carries_mean_and_median_entropy() -> None:
    rows = batch_qc([("a", np.array([0.0, 2.0, 2.0], dtype=np.float32)), *_tracks([1.0, 1.0, 1.0])])
    assert rows[0].mean == pytest.approx(4 / 3, abs=1e-4) and rows[0].median == pytest.approx(2.0)


def test_summary_file_lists_batch_qc_for_a_batch_and_marks_the_outlier(tmp_path: Path) -> None:
    sections = _tracks([1.8, 1.8, 1.81, 1.79, 1.2])
    path = SummaryWriter().write_multi(name="b", sections=sections, start=1, out_dir=str(tmp_path))
    text = Path(path).read_text(encoding="utf-8")
    assert "batch QC flagged:   1 of 5" in text
    block = text.split("[c4]")[1]
    assert "batch QC flag:    OUTLIER" in block
    assert "batch QC flag:    OUTLIER" not in text.split("[c4]")[0]
    # keys the app's summary reader takes as the track's numbers are never shadowed
    assert text.count("entropy mean:") == 5


def test_summary_of_one_or_two_inputs_has_no_batch_qc_lines(tmp_path: Path) -> None:
    path = SummaryWriter().write_multi(name="b", sections=_tracks([1.8, 1.2]), start=1, out_dir=str(tmp_path))
    assert "batch QC" not in Path(path).read_text(encoding="utf-8")


def test_a_real_run_of_a_multi_record_fasta_writes_batch_qc(tmp_path: Path) -> None:
    fasta = tmp_path / "b.fasta"
    rec = "ACGTTGCAAGGCTTAACCGGTATCGATCGGATCCATGCAAT" * 3
    fasta.write_text("".join(f">r{i}\n{rec}\n" for i in range(4)), encoding="utf-8", newline="\n")
    out = tmp_path / "o"
    pipeline.run(RunConfig(name="b", input_path=str(fasta), out_dir=str(out)))
    text = (out / "b.summary.txt").read_text(encoding="utf-8")
    assert "batch QC flagged:" in text and text.count("batch QC flag:") == 4
