"""Issue #135: a FASTA + GFF3 pair as an annotated input (genes without Prodigal)."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from typer.testing import CliRunner

from dna_entropy import pipeline
from dna_entropy.annotators.prodigal import ProdigalAnnotator
from dna_entropy.cli import app
from dna_entropy.config import RunConfig
from dna_entropy.readers.gff3 import Gff3ReadError, read_gff3
from dna_entropy.readers.input import load_input
from dna_entropy.validation.validators import ValidationError

SEQ = (
    "atgcgtacgttagcactgaccggtattccgaaactggcgtaaccggttaaccggttaacc"
    "ggttaaccggttaaccggttaaccatgaaacgttttggcacctaaggccttaaggcctta"
    "aggcct"
).upper()  # 126 nt, the sample.gb sequence

GFF = (
    "##gff-version 3\n"
    "# a comment\n"
    "\n"
    "toy_locus\tprokka\tgene\t1\t42\t.\t+\t.\tID=g1;Name=geneA;locus_tag=TOY_1\n"
    "toy_locus\tprokka\tCDS\t1\t42\t.\t+\t0\tID=c1;Parent=g1;product=hypothetical%20protein\n"
    "toy_locus\tprokka\tgene\t85\t126\t.\t-\t.\tID=g2;Name=geneB\n"
)


def _fasta(tmp_path: Path, name: str = "toy_locus", seq: str = SEQ) -> str:
    p = tmp_path / "toy.fasta"
    p.write_text(f">{name} some description\n{seq}\n", encoding="utf-8", newline="\n")
    return str(p)


def _gff(tmp_path: Path, text: str = GFF) -> str:
    p = tmp_path / "toy.gff3"
    p.write_text(text, encoding="utf-8", newline="\n")
    return str(p)


def _cfg(tmp_path: Path, gff: str | None, **kw) -> RunConfig:
    return RunConfig(
        name="r", input_path=_fasta(tmp_path), annotation_path=gff, out_dir=str(tmp_path / "o"), **kw
    )


# ---- the reader -----------------------------------------------------------------------


def test_gene_features_win_over_cds_and_map_coordinates_and_strand(tmp_path: Path) -> None:
    by_seq, notices = read_gff3(_gff(tmp_path))
    feats = by_seq["toy_locus"]
    assert [(f.begin, f.end, f.strand, f.gene_id) for f in feats] == [
        (1, 42, "+", "geneA"),
        (85, 126, "-", "geneB"),
    ]
    assert any("2 gene feature" in n for n in notices)


def test_cds_only_file_uses_cds_and_decodes_percent_escapes(tmp_path: Path) -> None:
    text = "s\tx\tCDS\t5\t10\t.\t-\t0\tID=c1;product=a%3Bb%20c\n"
    feats = read_gff3(_gff(tmp_path, text))[0]["s"]
    assert [(f.begin, f.end, f.strand, f.gene_id) for f in feats] == [(5, 10, "-", "a;b c")]


def test_unknown_strand_is_plus_and_partial_attribute_is_kept(tmp_path: Path) -> None:
    text = "s\tx\tgene\t1\t9\t.\t.\t.\tID=a;partial=true\ns\tx\tgene\t20\t30\t.\t?\t.\tID=b\n"
    feats = read_gff3(_gff(tmp_path, text))[0]["s"]
    assert [(f.strand, f.partial) for f in feats] == [("+", True), ("+", False)]


def test_fasta_section_and_comments_are_ignored(tmp_path: Path) -> None:
    text = "s\tx\tgene\t1\t9\t.\t+\t.\tID=a\n##FASTA\n>s\nACGT\ns\tx\tgene\t1\t2\t.\t+\t.\tID=zzz\n"
    feats = read_gff3(_gff(tmp_path, text))[0]["s"]
    assert len(feats) == 1


def test_discontinuous_cds_with_shared_id_is_one_feature_with_segments(tmp_path: Path) -> None:
    text = "s\tx\tCDS\t1\t10\t.\t+\t0\tID=c\ns\tx\tCDS\t21\t30\t.\t+\t0\tID=c\n"
    feats, notices = read_gff3(_gff(tmp_path, text))
    (f,) = feats["s"]
    assert (f.begin, f.end, f.segments) == (1, 30, ((1, 10), (21, 30)))
    assert any("2 segments" in n for n in notices)


@pytest.mark.parametrize(
    "bad",
    [
        "s\tx\tgene\t1\t9\t.\t+\n",  # too few columns
        "s\tx\tgene\tone\t9\t.\t+\t.\tID=a\n",  # non-integer start
        "s\tx\tgene\t9\t1\t.\t+\t.\tID=a\n",  # start > end
        "s\tx\tgene\t0\t9\t.\t+\t.\tID=a\n",  # start < 1
    ],
)
def test_malformed_lines_are_skipped_with_one_counted_notice(tmp_path: Path, bad: str) -> None:
    text = bad + "s\tx\tgene\t1\t9\t.\t+\t.\tID=ok\n"
    feats, notices = read_gff3(_gff(tmp_path, text))
    assert len(feats["s"]) == 1
    assert any("Skipped 1 malformed GFF3 line" in n and "line 1" in n for n in notices)


def test_file_with_no_features_is_refused_with_an_action(tmp_path: Path) -> None:
    with pytest.raises(Gff3ReadError, match="no gene or CDS"):
        read_gff3(_gff(tmp_path, "##gff-version 3\n"))
    assert issubclass(Gff3ReadError, ValidationError)


def test_binary_garbage_is_a_validation_error(tmp_path: Path) -> None:
    p = tmp_path / "g.gff3"
    p.write_bytes(b"\x00\x01\x02\xff\xfe")
    with pytest.raises(ValidationError):
        read_gff3(str(p))


# ---- the pairing in load_input --------------------------------------------------------


def test_load_input_attaches_features_by_record_id_first_token(tmp_path: Path) -> None:
    loaded = load_input(_cfg(tmp_path, _gff(tmp_path)))
    assert [(f.begin, f.end) for f in loaded.contigs[0].features] == [(1, 42), (85, 126)]
    assert loaded.source_kind == "fasta"


def test_seqid_matching_no_record_is_refused(tmp_path: Path) -> None:
    gff = _gff(tmp_path, GFF.replace("toy_locus", "other"))
    with pytest.raises(ValidationError, match="None of the sequence names"):
        load_input(_cfg(tmp_path, gff))


def test_some_unmatched_seqids_are_a_counted_notice_not_an_error(tmp_path: Path) -> None:
    gff = _gff(tmp_path, GFF + "plasmid2\tx\tgene\t1\t9\t.\t+\t.\tID=p\n")
    loaded = load_input(_cfg(tmp_path, gff))
    assert any("1 sequence name(s) in the GFF3 match no FASTA record" in n for n in loaded.notices)


def test_feature_past_the_end_of_its_sequence_is_dropped_with_a_notice(tmp_path: Path) -> None:
    gff = _gff(tmp_path, GFF + "toy_locus\tx\tgene\t100\t900\t.\t+\t.\tID=far\n")
    loaded = load_input(_cfg(tmp_path, gff))
    assert len(loaded.contigs[0].features) == 2
    assert any("outside" in n and "126" in n for n in loaded.notices)


def test_gff3_with_a_non_fasta_input_is_refused(tmp_path: Path) -> None:
    gb = Path(__file__).parent / "data" / "sample.gb"
    cfg = RunConfig(name="r", input_path=str(gb), annotation_path=_gff(tmp_path), out_dir=str(tmp_path / "o"))
    with pytest.raises(ValidationError, match="FASTA"):
        load_input(cfg)


def test_no_annotation_path_leaves_fasta_features_empty(tmp_path: Path) -> None:
    assert load_input(_cfg(tmp_path, None)).contigs[0].features == []


# ---- the pipeline: genes from the file, Prodigal never runs ---------------------------


def test_pipeline_uses_the_gff3_genes_and_never_calls_prodigal(tmp_path: Path, monkeypatch) -> None:
    def boom(self, seq):
        raise AssertionError("Prodigal must not run when a GFF3 is supplied")

    monkeypatch.setattr(ProdigalAnnotator, "annotate", boom)
    cfg = _cfg(tmp_path, _gff(tmp_path), genes=True, context_length=128)  # genes=True is overridden
    result = pipeline.run(cfg)
    assert len(result.genes) == 2
    names = {Path(o).name for o in result.outputs}
    assert "r.genes.gff3" in names
    assert "r.genes.tsv" in names
    assert "r.gb" in names
    assert any("GFF3" in n for n in result.notices)
    gff_text = (tmp_path / "o" / "r.genes.gff3").read_text(encoding="utf-8")
    assert "geneA" in gff_text and "pyrodigal" not in gff_text


def test_pipeline_without_a_gff3_is_unchanged(tmp_path: Path) -> None:
    result = pipeline.run(_cfg(tmp_path, None, context_length=128))
    assert result.genes == []
    assert "r.genes.gff3" not in {Path(o).name for o in result.outputs}


# ---- the CLI --------------------------------------------------------------------------


def test_cli_gff3_option_runs_and_reports_genes(tmp_path: Path) -> None:
    result = CliRunner().invoke(
        app,
        [
            "run", "--name", "c", "--out", str(tmp_path / "out"), "-i", _fasta(tmp_path),
            "--gff3", _gff(tmp_path), "--context-length", "128",
        ],
    )  # fmt: skip
    assert result.exit_code == 0, result.output
    assert "genes: 2" in result.output
    assert json.loads((tmp_path / "out" / "c" / "provenance.json").read_text(encoding="utf-8"))
