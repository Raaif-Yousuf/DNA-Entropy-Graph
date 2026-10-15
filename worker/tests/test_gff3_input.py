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
    by_seq, notices, circular = read_gff3(_gff(tmp_path))
    assert circular == set()
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


def test_unknown_strand_stays_unstranded_and_partial_attribute_is_kept(tmp_path: Path) -> None:
    """The old reader turned '.' and '?' into '+', which invented a strand. Now they stay
    unstranded ('.') and only a real '+' / '-' is a strand."""
    text = (
        "s\tx\tgene\t1\t9\t.\t.\t.\tID=a;partial=true\n"
        "s\tx\tgene\t20\t30\t.\t?\t.\tID=b\n"
        "s\tx\tgene\t40\t50\t.\t+\t.\tID=c\n"
        "s\tx\tgene\t60\t70\t.\t-\t.\tID=d\n"
    )
    feats = read_gff3(_gff(tmp_path, text))[0]["s"]
    assert [(f.strand, f.partial) for f in feats] == [(".", True), (".", False), ("+", False), ("-", False)]


def test_unstranded_gene_is_written_unstranded_in_every_output(tmp_path: Path) -> None:
    from Bio import SeqIO

    gff = _gff(tmp_path, "toy_locus\tx\tgene\t1\t42\t.\t.\t.\tID=g1;Name=geneA\n")
    out = tmp_path / "o"
    pipeline.run(_cfg(tmp_path, gff, context_length=128))
    row = (out / "r.genes.gff3").read_text(encoding="utf-8").splitlines()
    (gene_line,) = [ln for ln in row if "\tgene\t" in ln]
    assert gene_line.split("\t")[6] == "."
    tsv = (out / "r.genes.tsv").read_text(encoding="utf-8").splitlines()
    assert tsv[1].split("\t")[tsv[0].split("\t").index("strand")] == "."
    # GenBank has no unstranded location (a plain 1..42 reads back as +), but it must not be
    # written as complement(...), which the old writer did for anything that was not "+".
    gb_text = (out / "r.gb").read_text(encoding="utf-8")
    assert "complement(" not in gb_text
    rec = next(SeqIO.parse(str(out / "r.gb"), "genbank"))
    (feat,) = [f for f in rec.features if f.type == "gene"]
    assert (int(feat.location.start), int(feat.location.end)) == (0, 42)


def test_seqid_is_percent_decoded(tmp_path: Path) -> None:
    """GFF3 escapes reserved characters in column 1 (a '|' in an NCBI-style id is %7C)."""
    text = "gnl%7Ctoy%20one\tx\tgene\t1\t9\t.\t+\t.\tID=a\n"
    assert list(read_gff3(_gff(tmp_path, text))[0]) == ["gnl|toy one"]


def test_percent_encoded_seqid_pairs_with_the_fasta_record(tmp_path: Path) -> None:
    fasta = tmp_path / "toy.fasta"
    fasta.write_text(f">gnl|toy_1 desc\n{SEQ}\n", encoding="utf-8", newline="\n")
    gff = _gff(tmp_path, "gnl%7Ctoy_1\tx\tgene\t1\t42\t.\t+\t.\tID=a;Name=geneA\n")
    cfg = RunConfig(name="r", input_path=str(fasta), annotation_path=gff, out_dir=str(tmp_path / "o"))
    assert [(f.begin, f.end) for f in load_input(cfg).contigs[0].features] == [(1, 42)]


def test_fasta_section_and_comments_are_ignored(tmp_path: Path) -> None:
    text = "s\tx\tgene\t1\t9\t.\t+\t.\tID=a\n##FASTA\n>s\nACGT\ns\tx\tgene\t1\t2\t.\t+\t.\tID=zzz\n"
    feats = read_gff3(_gff(tmp_path, text))[0]["s"]
    assert len(feats) == 1


def test_discontinuous_cds_with_shared_id_is_one_feature_with_segments(tmp_path: Path) -> None:
    text = "s\tx\tCDS\t1\t10\t.\t+\t0\tID=c\ns\tx\tCDS\t21\t30\t.\t+\t0\tID=c\n"
    feats, _, _ = read_gff3(_gff(tmp_path, text))
    (f,) = feats["s"]
    assert (f.begin, f.end, f.segments) == (1, 30, ((1, 10), (21, 30)))


def test_gene_rows_sharing_an_id_are_one_feature_too(tmp_path: Path) -> None:
    text = "s\tx\tgene\t1\t10\t.\t-\t.\tID=g\ns\tx\tgene\t120\t126\t.\t-\t.\tID=g\n"
    feats, _, _ = read_gff3(_gff(tmp_path, text))
    (f,) = feats["s"]
    assert (f.begin, f.end, f.strand, f.segments) == (1, 126, "-", ((1, 10), (120, 126)))


def test_is_circular_attribute_on_any_row_marks_the_sequence_circular(tmp_path: Path) -> None:
    text = (
        "toy_locus\tx\tregion\t1\t126\t.\t+\t.\tID=toy_locus;Is_circular=true\n"
        "toy_locus\tx\tgene\t1\t9\t.\t+\t.\tID=a\n"
        "other\tx\tregion\t1\t50\t.\t+\t.\tID=other;Is_circular=false\n"
        "other\tx\tgene\t1\t9\t.\t+\t.\tID=b\n"
    )
    assert read_gff3(_gff(tmp_path, text))[2] == {"toy_locus"}


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
    feats, notices, _ = read_gff3(_gff(tmp_path, text))
    assert len(feats["s"]) == 1
    assert any("Skipped 1 malformed GFF3 line" in n and "line 1" in n for n in notices)


def test_file_with_no_features_is_refused_with_an_action(tmp_path: Path) -> None:
    with pytest.raises(Gff3ReadError, match="no gene or CDS features") as exc:
        read_gff3(_gff(tmp_path, "##gff-version 3\n"))
    assert "Check it is the annotation for this FASTA" in str(exc.value)
    assert issubclass(Gff3ReadError, ValidationError)


def test_binary_garbage_is_a_validation_error(tmp_path: Path) -> None:
    p = tmp_path / "g.gff3"
    p.write_bytes(b"\x00\x01\x02\xff\xfe")
    with pytest.raises(Gff3ReadError, match="no gene or CDS features"):
        read_gff3(str(p))


# ---- the pairing in load_input --------------------------------------------------------


def test_load_input_attaches_features_by_record_id_first_token(tmp_path: Path) -> None:
    loaded = load_input(_cfg(tmp_path, _gff(tmp_path)))
    assert [(f.begin, f.end) for f in loaded.contigs[0].features] == [(1, 42), (85, 126)]
    assert loaded.source_kind == "fasta"


def test_seqid_matching_no_record_is_refused(tmp_path: Path) -> None:
    gff = _gff(tmp_path, GFF.replace("toy_locus", "other"))
    with pytest.raises(
        ValidationError, match="None of the sequence names in the GFF3 match a FASTA record"
    ) as exc:
        load_input(_cfg(tmp_path, gff))
    assert "Make the GFF3's first column match the FASTA header names" in str(exc.value)


def test_some_unmatched_seqids_are_a_counted_notice_not_an_error(tmp_path: Path) -> None:
    gff = _gff(tmp_path, GFF + "plasmid2\tx\tgene\t1\t9\t.\t+\t.\tID=p\n")
    loaded = load_input(_cfg(tmp_path, gff))
    assert any("1 sequence name(s) in the GFF3 match no FASTA record" in n for n in loaded.notices)


def test_feature_past_the_end_of_a_linear_sequence_is_dropped_with_an_accurate_notice(tmp_path: Path) -> None:
    """The old notice only said 'check the GFF3 belongs to this FASTA', which is wrong advice for a
    feature that legitimately crosses the origin of a circular molecule."""
    gff = _gff(tmp_path, GFF + "toy_locus\tx\tgene\t100\t900\t.\t+\t.\tID=far\n")
    loaded = load_input(_cfg(tmp_path, gff))
    assert len(loaded.contigs[0].features) == 2
    (notice,) = [n for n in loaded.notices if "past the end" in n]
    assert "1 GFF3 feature(s) run past the end of their sequence (126 nt)" in notice
    assert "Is_circular=true" in notice and "--topology circular" in notice
    assert "belongs to this FASTA" in notice


CIRCULAR_GFF = (
    "toy_locus\tx\tregion\t1\t126\t.\t+\t.\tID=toy_locus;Is_circular=true\n"
    "toy_locus\tx\tgene\t1\t42\t.\t+\t.\tID=g1;Name=geneA\n"
)


def test_gff3_is_circular_makes_the_contig_circular_and_says_so(tmp_path: Path) -> None:
    loaded = load_input(_cfg(tmp_path, _gff(tmp_path, CIRCULAR_GFF)))
    assert loaded.contigs[0].circular is True
    assert any("Is_circular=true" in n and "analysed as circular" in n for n in loaded.notices)


def test_topology_linear_still_overrides_a_declared_circular_gff3(tmp_path: Path) -> None:
    from dna_entropy.config import Topology

    cfg = _cfg(tmp_path, _gff(tmp_path, CIRCULAR_GFF), topology=Topology.LINEAR)
    assert load_input(cfg).contigs[0].circular is True  # what the input declared
    assert cfg.topology.resolve(True) is False  # the run's own switch decides


def test_end_past_length_on_a_circular_sequence_is_a_wrapping_feature(tmp_path: Path) -> None:
    import numpy as np

    from dna_entropy.analysis.gene_summary import summarize_genes

    gff = _gff(tmp_path, CIRCULAR_GFF + "toy_locus\tx\tgene\t120\t130\t.\t+\t.\tID=w;Name=wrap\n")
    loaded = load_input(_cfg(tmp_path, gff))
    contig = loaded.contigs[0]
    wrap = next(f for f in contig.features if f.gene_id == "wrap")
    assert (wrap.begin, wrap.end, wrap.segments) == (1, 126, ((1, 4), (120, 126)))
    assert any("crosses the origin" in n for n in loaded.notices)
    assert not any("past the end" in n for n in loaded.notices)
    row = next(
        r
        for r in summarize_genes("c", contig.features, np.ones(126), circular=True, start=1)
        if r.gene_id == "wrap"
    )
    assert (row.begin, row.end, row.wraps_origin, row.length) == (120, 4, True, 11)


def test_topology_circular_flag_alone_also_lets_end_past_length_wrap(tmp_path: Path) -> None:
    from dna_entropy.config import Topology

    gff = _gff(tmp_path, "toy_locus\tx\tgene\t120\t130\t.\t+\t.\tID=w\n")
    loaded = load_input(_cfg(tmp_path, gff, topology=Topology.CIRCULAR))
    assert loaded.contigs[0].features[0].segments == ((1, 4), (120, 126))


def test_a_feature_longer_than_the_circular_molecule_is_dropped_not_wrapped(tmp_path: Path) -> None:
    gff = _gff(tmp_path, CIRCULAR_GFF + "toy_locus\tx\tgene\t10\t200\t.\t+\t.\tID=long\n")
    loaded = load_input(_cfg(tmp_path, gff))
    assert [f.gene_id for f in loaded.contigs[0].features] == ["geneA"]
    assert any("1 GFF3 feature(s) run past the end" in n for n in loaded.notices)


def test_two_rows_sharing_an_id_across_the_origin_are_one_wrapping_feature(tmp_path: Path) -> None:
    gff = _gff(
        tmp_path,
        CIRCULAR_GFF
        + "toy_locus\tx\tgene\t120\t126\t.\t+\t.\tID=w;Name=wrap\n"
        + "toy_locus\tx\tgene\t1\t4\t.\t+\t.\tID=w;Name=wrap\n",
    )
    loaded = load_input(_cfg(tmp_path, gff))
    wrap = next(f for f in loaded.contigs[0].features if f.gene_id == "wrap")
    assert (wrap.begin, wrap.end, wrap.segments) == (1, 126, ((1, 4), (120, 126)))
    assert any("crosses the origin" in n for n in loaded.notices)
    assert not any("includes the sequence between segments" in n for n in loaded.notices)


def test_two_rows_sharing_an_id_on_a_linear_sequence_say_spliced_not_wrapping(tmp_path: Path) -> None:
    gff = _gff(
        tmp_path,
        "toy_locus\tx\tgene\t120\t126\t.\t+\t.\tID=w\ntoy_locus\tx\tgene\t1\t4\t.\t+\t.\tID=w\n",
    )
    loaded = load_input(_cfg(tmp_path, gff))
    assert any("includes the sequence between segments" in n for n in loaded.notices)
    assert not any("crosses the origin" in n for n in loaded.notices)


def test_gff3_coordinates_count_sequence_letters_not_fasta_lines(tmp_path: Path) -> None:
    """Coordinates are 1-based over the sequence letters, so FASTA line wrapping and case do not
    shift them; the 126 nt length used for the bounds check is the validated sequence's."""
    fasta = tmp_path / "wrapped.fasta"
    wrapped = "\n".join(SEQ.lower()[i : i + 60] for i in range(0, len(SEQ), 60))
    fasta.write_text(f">toy_locus\n{wrapped}\n", encoding="utf-8", newline="\n")
    cfg = RunConfig(
        name="r", input_path=str(fasta), annotation_path=_gff(tmp_path), out_dir=str(tmp_path / "o")
    )
    loaded = load_input(cfg)
    assert [(f.begin, f.end) for f in loaded.contigs[0].features] == [(1, 42), (85, 126)]
    assert not any("past the end" in n for n in loaded.notices)


def test_gff3_with_a_non_fasta_input_is_refused(tmp_path: Path) -> None:
    gb = Path(__file__).parent / "data" / "sample.gb"
    cfg = RunConfig(name="r", input_path=str(gb), annotation_path=_gff(tmp_path), out_dir=str(tmp_path / "o"))
    with pytest.raises(
        ValidationError, match="A GFF3 annotation can only be paired with a FASTA file"
    ) as exc:
        load_input(cfg)
    assert "Remove the GFF3, or add the sequence as a FASTA file" in str(exc.value)


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


def test_multi_record_fasta_with_gff3_rows_for_one_record_gives_the_other_no_genes_and_no_prodigal(
    tmp_path: Path, monkeypatch
) -> None:
    def boom(self, seq):
        raise AssertionError("Prodigal must not run for ANY record when a GFF3 is supplied")

    monkeypatch.setattr(ProdigalAnnotator, "annotate", boom)
    fasta = tmp_path / "two.fasta"
    fasta.write_text(f">toy_locus first\n{SEQ}\n>other second\n{SEQ}\n", encoding="utf-8", newline="\n")
    cfg = RunConfig(
        name="r",
        input_path=str(fasta),
        annotation_path=_gff(tmp_path),
        out_dir=str(tmp_path / "o"),
        context_length=128,
        genes=True,
    )
    loaded = load_input(cfg)
    assert [len(c.features) for c in loaded.contigs] == [2, 0]
    result = pipeline.run(cfg)
    assert len(result.genes) == 2  # both from the first record, none for the second
    chroms = {
        ln.split("\t")[0]
        for ln in (tmp_path / "o" / "r.genes.gff3").read_text(encoding="utf-8").splitlines()
        if ln and not ln.startswith("#")
    }
    assert chroms == {"r_1"}  # the second record (r_2) has no gene rows


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
