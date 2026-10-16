"""Issue #694: a gene that crosses the origin of a circular molecule is written as a join.

``begin..end`` of such a feature is the OUTER span ``1..L`` (the whole molecule); only its
``segments`` say where its bases are. The GenBank and GFF3 writers used to write ``begin..end``
alone, so the gene appeared to cover the whole plasmid in SnapGene, Benchling and IGV. They now
write ``join(120..126,1..10)`` (``complement(join(...))`` on the minus strand) and, in GFF3, two
rows sharing one ``ID`` plus an ``Is_circular=true`` region row, which is what the readers of
both formats map back to the same wrapping feature.
"""

from __future__ import annotations

import re
from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.annotators.base import GeneFeature
from dna_entropy.config import RunConfig
from dna_entropy.readers.genbank import read_genbank
from dna_entropy.readers.gff3 import fit_to_sequence, read_gff3
from dna_entropy.writers.genbank import GenBankWriter
from dna_entropy.writers.gff import GffWriter

L = 126
SEQ = ("ACGT" * 32)[:L]
DATA = Path(__file__).parent / "data"


def _values() -> np.ndarray:
    v = np.zeros(L, dtype=np.float32)
    v[119:126] = 2.0  # 120..126
    v[0:10] = 2.0  # 1..10
    return v


def _wrap(strand: str = "+") -> GeneFeature:
    return GeneFeature(begin=1, end=L, strand=strand, gene_id="wrapG", segments=((1, 10), (120, 126)))


def _write_gb(tmp_path: Path, features, *, circular: bool) -> str:
    return GenBankWriter().write_multi(
        name="o",
        records=[("toy", SEQ, features, _values(), "")],
        out_dir=str(tmp_path),
        circular=[circular],
    )


# ---- GenBank -------------------------------------------------------------------------


def test_genbank_writes_a_join_for_a_wrapping_gene(tmp_path: Path) -> None:
    text = Path(_write_gb(tmp_path, [_wrap()], circular=True)).read_text(encoding="utf-8")
    assert "join(120..126,1..10)" in text
    assert not re.search(r"gene\s+1\.\.126\b", text)  # the whole-molecule span is gone
    assert " circular " in text.splitlines()[0]  # the LOCUS line keeps the topology


def test_genbank_minus_strand_wrap_is_complement_of_the_join(tmp_path: Path) -> None:
    text = Path(_write_gb(tmp_path, [_wrap("-")], circular=True)).read_text(encoding="utf-8")
    assert "complement(join(120..126,1..10))" in text


def test_genbank_wrap_mean_entropy_covers_only_the_genes_own_bases(tmp_path: Path) -> None:
    text = Path(_write_gb(tmp_path, [_wrap()], circular=True)).read_text(encoding="utf-8")
    assert "mean_entropy=2.000 bits" in text  # 17 bases of 2.0, not 126 mostly-zero bases


@pytest.mark.parametrize("strand", ["+", "-"])  # the GenBank reader has no unstranded gene
def test_genbank_round_trip_is_the_same_wrapping_gene(tmp_path: Path, strand: str) -> None:
    path = _write_gb(tmp_path, [_wrap(strand)], circular=True)
    records, notices = read_genbank(path)
    assert records[0].circular is True
    (gene,) = records[0].features
    assert (gene.begin, gene.end, gene.segments, gene.strand) == (1, L, ((1, 10), (120, 126)), strand)
    assert any("wraps the origin" in n for n in notices)


def test_genbank_segments_on_a_linear_record_keep_the_outer_span(tmp_path: Path) -> None:
    """A spliced gene on a linear molecule is unchanged: GenBank's own gene feature is the outer span."""
    spliced = GeneFeature(begin=5, end=60, strand="+", gene_id="sp", segments=((5, 20), (40, 60)))
    text = Path(_write_gb(tmp_path, [spliced], circular=False)).read_text(encoding="utf-8")
    assert re.search(r"gene\s+5\.\.60\b", text)
    assert "join(" not in text


def test_genbank_without_the_circular_argument_behaves_as_before(tmp_path: Path) -> None:
    out = GenBankWriter().write_multi(
        name="o", records=[("toy", SEQ, [_wrap()], _values(), "")], out_dir=str(tmp_path)
    )
    assert re.search(r"gene\s+1\.\.126\b", Path(out).read_text(encoding="utf-8"))


# ---- GFF3 ----------------------------------------------------------------------------


def _write_gff(tmp_path: Path, features, *, circular: bool, start: int = 1) -> str:
    return GffWriter().write_multi(
        name="o",
        blocks=[("toy", features, L)],
        start=start,
        out_dir=str(tmp_path),
        circular=[circular],
    )


def _rows(path: str) -> list[list[str]]:
    return [ln.split("\t") for ln in Path(path).read_text(encoding="utf-8").splitlines() if ln[:1] != "#"]


def test_gff3_wrapping_gene_is_two_rows_with_one_id_and_a_circular_region(tmp_path: Path) -> None:
    rows = _rows(_write_gff(tmp_path, [_wrap("-")], circular=True))
    genes = [r for r in rows if r[2] == "gene"]
    assert [(r[3], r[4], r[6]) for r in genes] == [("120", "126", "-"), ("1", "10", "-")]
    assert {r[8] for r in genes} == {"ID=wrapG;Name=wrapG"}
    (region,) = [r for r in rows if r[2] == "region"]
    assert (region[0], region[3], region[4]) == ("toy", "1", str(L))
    assert "Is_circular=true" in region[8]


def test_gff3_round_trip_is_the_same_wrapping_gene(tmp_path: Path) -> None:
    path = _write_gff(tmp_path, [_wrap()], circular=True)
    result, _notices, circular = read_gff3(path)
    assert circular == {"toy"}
    kept, notices = fit_to_sequence(result["toy"], L, True)
    (gene,) = kept
    assert (gene.begin, gene.end, gene.segments) == (1, L, ((1, 10), (120, 126)))
    assert any("crosses the origin" in n for n in notices)


def test_gff3_start_offset_moves_every_wrapping_row(tmp_path: Path) -> None:
    rows = _rows(_write_gff(tmp_path, [_wrap()], circular=True, start=101))
    genes = [(r[3], r[4]) for r in rows if r[2] == "gene"]
    assert genes == [("220", "226"), ("101", "110")]


def test_gff3_linear_record_is_unchanged_one_row_no_region(tmp_path: Path) -> None:
    rows = _rows(_write_gff(tmp_path, [_wrap()], circular=False))
    assert [r[2] for r in rows] == ["gene"]
    assert (rows[0][3], rows[0][4]) == ("1", str(L))


def test_gff3_circular_record_with_plain_genes_gets_the_region_row_and_one_row_per_gene(
    tmp_path: Path,
) -> None:
    plain = GeneFeature(begin=5, end=40, strand="+", gene_id="p")
    rows = _rows(_write_gff(tmp_path, [plain], circular=True))
    assert [r[2] for r in rows] == ["region", "gene"]


# ---- through the pipeline ------------------------------------------------------------


def _wrapping_gb(tmp_path: Path) -> str:
    text = (DATA / "sample.gb").read_text(encoding="utf-8")
    text = text.replace("DNA              UNK", "DNA     circular UNK", 1)
    text = text.replace("     gene            1..42\n", "     gene            join(120..126,1..10)\n", 1)
    p = tmp_path / "wrap.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    return str(p)


def test_a_circular_genbank_run_writes_the_wrap_as_a_join_and_two_gff3_rows(tmp_path: Path) -> None:
    out = tmp_path / "out"
    cfg = RunConfig(name="r", input_path=_wrapping_gb(tmp_path), out_dir=str(out))
    pipeline.run(cfg)
    gb = (out / "r.gb").read_text(encoding="utf-8")
    assert "join(120..126,1..10)" in gb
    gff = _rows(str(out / "r.genes.gff3"))
    wrap_rows = [(r[3], r[4]) for r in gff if r[2] == "gene" and r[8].startswith("ID=geneA")]
    assert wrap_rows == [("120", "126"), ("1", "10")]
    # and the written .gb reads back as the same wrapping gene
    records, _ = read_genbank(str(out / "r.gb"))
    gene = next(f for f in records[0].features if f.gene_id == "geneA")
    assert gene.segments == ((1, 10), (120, 126))
