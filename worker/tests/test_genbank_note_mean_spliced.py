"""Issue #563: the GenBank ``mean_entropy`` note averages over a gene's own segments.

A spliced gene (``join(a..b,c..d)``) is written as its outer-span ``gene`` feature (GenBank's own
convention), but its note must be the mean over the joined exon bases, the same number the gene
table (``analysis/gene_summary.py``, #124) reports, so the two outputs never disagree.
"""

from __future__ import annotations

import re
from pathlib import Path

import numpy as np
import pytest

from dna_entropy.analysis.gene_summary import summarize_genes
from dna_entropy.annotators.base import GeneFeature
from dna_entropy.writers.genbank import GenBankWriter

L = 126
SEQ = ("ACGT" * 32)[:L]


def _values() -> np.ndarray:
    v = np.zeros(L, dtype=np.float32)
    v[4:20] = 2.0  # exon 5..20
    v[39:60] = 1.0  # exon 40..60; the intron 21..39 stays 0.0
    v[119:126] = 2.0  # 120..126
    v[0:10] = 1.0  # 1..10
    return v


def _note(tmp_path: Path, feature: GeneFeature, *, circular: bool) -> float:
    out = GenBankWriter().write_multi(
        name="o", records=[("toy", SEQ, [feature], _values(), "")], out_dir=str(tmp_path), circular=[circular]
    )
    match = re.search(r"mean_entropy=([0-9.]+) bits", Path(out).read_text(encoding="utf-8"))
    assert match, "no mean_entropy note written"
    return float(match.group(1))


def _table(feature: GeneFeature, *, circular: bool) -> float:
    (row,) = summarize_genes("toy", [feature], _values(), circular=circular, start=1)
    return row.mean_entropy


def test_spliced_gene_note_averages_the_exons_not_the_intron(tmp_path: Path) -> None:
    spliced = GeneFeature(begin=5, end=60, strand="+", gene_id="sp", segments=((5, 20), (40, 60)))
    expected = (6 * 1.0 + 10 * 2.0 + 21 * 1.0) / 37  # 5..10 is overwritten with 1.0 below
    assert _note(tmp_path, spliced, circular=False) == pytest.approx(expected, abs=5e-4)


@pytest.mark.parametrize("strand", ["+", "-"])
def test_spliced_gene_note_equals_the_gene_table(tmp_path: Path, strand: str) -> None:
    spliced = GeneFeature(begin=5, end=60, strand=strand, gene_id="sp", segments=((5, 20), (40, 60)))
    assert _note(tmp_path, spliced, circular=False) == pytest.approx(
        _table(spliced, circular=False), abs=5e-4
    )


def test_origin_wrapping_gene_note_equals_the_gene_table(tmp_path: Path) -> None:
    wrap = GeneFeature(begin=1, end=L, strand="+", gene_id="w", segments=((1, 10), (120, 126)))
    assert _note(tmp_path, wrap, circular=True) == pytest.approx(_table(wrap, circular=True), abs=5e-4)


def test_wrap_shaped_gene_on_a_linear_record_averages_its_segments_too(tmp_path: Path) -> None:
    wrap = GeneFeature(begin=1, end=L, strand="+", gene_id="w", segments=((1, 10), (120, 126)))
    assert _note(tmp_path, wrap, circular=False) == pytest.approx(_table(wrap, circular=False), abs=5e-4)


def test_plain_gene_without_segments_is_unchanged(tmp_path: Path) -> None:
    plain = GeneFeature(begin=11, end=20, strand="+", gene_id="p")
    assert _note(tmp_path, plain, circular=False) == pytest.approx(2.0, abs=5e-4)
    assert _note(tmp_path, plain, circular=False) == pytest.approx(_table(plain, circular=False), abs=5e-4)


def _write(tmp_path: Path, feature: GeneFeature, values: np.ndarray, *, circular: bool = False) -> str:
    out = GenBankWriter().write_multi(
        name="o", records=[("toy", SEQ, [feature], values, "")], out_dir=str(tmp_path), circular=[circular]
    )
    return Path(out).read_text(encoding="utf-8")


def test_segments_all_past_the_track_write_the_gene_without_a_note(tmp_path: Path) -> None:
    """Nothing to average (summarize_genes skips the row): no note, and no crash."""
    ghost = GeneFeature(begin=5, end=60, strand="+", gene_id="ghost", segments=((200, 210), (220, 230)))
    text = _write(tmp_path, ghost, _values())
    assert "mean_entropy" not in text
    assert "ghost" in text


def test_a_track_shorter_than_the_sequence_does_not_raise(tmp_path: Path) -> None:
    short = _values()[:50]
    spliced = GeneFeature(begin=5, end=60, strand="+", gene_id="sp", segments=((5, 20), (40, 60)))
    text = _write(tmp_path, spliced, short)
    expected = (6 * 1.0 + 10 * 2.0 + 11 * 1.0) / 27  # exon 2 is clipped to 40..50
    assert float(re.search(r"mean_entropy=([0-9.]+) bits", text).group(1)) == pytest.approx(
        expected, abs=5e-4
    )


def test_a_wrapping_gene_on_a_short_track_does_not_raise(tmp_path: Path) -> None:
    wrap = GeneFeature(begin=1, end=L, strand="+", gene_id="w", segments=((1, 10), (120, 126)))
    text = _write(tmp_path, wrap, _values()[:50], circular=True)
    assert float(re.search(r"mean_entropy=([0-9.]+) bits", text).group(1)) == pytest.approx(1.0, abs=5e-4)
