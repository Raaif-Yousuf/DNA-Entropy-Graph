"""Tests for GenBank/FASTA input routing, reading, writing, and the pipeline branches."""

from __future__ import annotations

from pathlib import Path

import pytest

from dna_entropy import pipeline
from dna_entropy.config import RunConfig
from dna_entropy.readers import detect
from dna_entropy.readers.fasta import read_fasta
from dna_entropy.readers.genbank import read_genbank
from dna_entropy.readers.input import load_input
from dna_entropy.validation.validators import ValidationError, validate_sequence
from dna_entropy.writers.genbank import GenBankWriter

DATA = Path(__file__).parent / "data"
SAMPLE_GB = str(DATA / "sample.gb")
MULTI_GB = str(DATA / "multi.gb")
SAMPLE_FA = str(DATA / "sample.fasta")
SPLICED_GB = str(DATA / "spliced.gb")


# --- detection ----------------------------------------------------------------------


@pytest.mark.parametrize(
    "path, expected",
    [
        (None, detect.PASTE),
        ("x.gb", detect.GENBANK),
        ("x.gbk", detect.GENBANK),
        ("x.genbank", detect.GENBANK),
        ("x.fasta", detect.FASTA),
        ("x.fa", detect.FASTA),
        ("x.fna", detect.FASTA),
    ],
)
def test_detect_by_extension(path, expected) -> None:
    assert detect.detect_kind(path) == expected


def test_detect_by_content_sniff(tmp_path: Path) -> None:
    gb = tmp_path / "mystery.dat"
    gb.write_text("LOCUS       foo    10 bp\n//\n", encoding="utf-8")
    assert detect.detect_kind(str(gb)) == detect.GENBANK
    fa = tmp_path / "mystery2.dat"
    fa.write_text(">seq1\nACGT\n", encoding="utf-8")
    assert detect.detect_kind(str(fa)) == detect.FASTA
    plain = tmp_path / "mystery3.dat"
    plain.write_text("ACGTACGT\n", encoding="utf-8")
    assert detect.detect_kind(str(plain)) == detect.PASTE


# --- GenBank reader (genes preserved, gene preferred over CDS) -----------------------


def test_read_genbank_extracts_seq_and_genes() -> None:
    records, notices = read_genbank(SAMPLE_GB)
    assert len(records) == 1
    rec = records[0]
    assert len(rec.seq) == 126
    assert len(rec.features) == 2  # two 'gene' features; the duplicate CDS is ignored
    a, b = rec.features
    assert (a.begin, a.end, a.strand, a.gene_id) == (1, 42, "+", "geneA")
    assert (b.begin, b.end, b.strand, b.gene_id) == (85, 126, "-", "geneB")
    assert any("not re-annotated" in n for n in notices)


def test_read_genbank_flags_compound_locations_instead_of_collapsing_silently() -> None:
    """#295: a join(...)/complement(join(...)) gene must not silently collapse to its
    bounding box — the reader must say so, loudly, and name the real exon segments."""
    records, notices = read_genbank(SPLICED_GB)
    assert len(records) == 1
    rec = records[0]
    by_id = {f.gene_id: f for f in rec.features}

    # Three GeneFeatures still come back (no cardinality change downstream) — the two
    # compound ones keep their outer bounding box as begin/end...
    assert (by_id["splicedA"].begin, by_id["splicedA"].end, by_id["splicedA"].strand) == (1, 130, "+")
    assert (by_id["splicedB"].begin, by_id["splicedB"].end, by_id["splicedB"].strand) == (151, 200, "-")
    assert (by_id["plainC"].begin, by_id["plainC"].end, by_id["plainC"].strand) == (40, 60, "+")

    # ...but each compound one is now flagged with a notice naming its real segments, and
    # the plain (non-compound) gene is never mentioned by one.
    compound_notices = [n for n in notices if "compound" in n.lower()]
    assert len(compound_notices) == 2
    assert any("splicedA" in n and "1..30, 101..130" in n for n in compound_notices)
    assert any("splicedB" in n and "151..170, 181..200" in n for n in compound_notices)
    assert not any("plainC" in n for n in compound_notices)


# --- FASTA reader -------------------------------------------------------------------


def test_read_fasta_single_record() -> None:
    records, notices = read_fasta(SAMPLE_FA)
    assert len(records) == 1
    assert records[0].seq and set(records[0].seq.upper()) <= set("ACGTN")


def test_read_fasta_multi_record_returns_all_records(tmp_path: Path) -> None:
    """D14/#283: FASTA processes ALL records, matching GenBank — not just the first."""
    p = tmp_path / "multi.fasta"
    p.write_text(">one\nACGT\nACGT\n>two\nTTTT\n", encoding="utf-8")
    records, notices = read_fasta(str(p))
    assert len(records) == 2
    assert records[0].header == "one" and records[0].seq == "ACGTACGT"
    assert records[1].header == "two" and records[1].seq == "TTTT"
    assert any("2 record" in n for n in notices)


def test_read_fasta_skips_a_record_with_no_sequence_lines(tmp_path: Path) -> None:
    p = tmp_path / "empty_record.fasta"
    p.write_text(">has_seq\nACGT\n>empty\n>also_has_seq\nTTTT\n", encoding="utf-8")
    records, notices = read_fasta(str(p))
    assert [r.header for r in records] == ["has_seq", "also_has_seq"]
    # issue #253: the notice must name WHICH record was skipped without ever echoing the
    # header text itself back (a header is user-typed free text, same privacy class as a
    # sequence or a file name).
    assert any("Skipped FASTA record 2" in n for n in notices)
    assert not any("empty" in n for n in notices)  # the header text must not leak


def test_read_fasta_flags_missing_header() -> None:
    import tempfile

    with tempfile.NamedTemporaryFile(mode="w", suffix=".fasta", delete=False, encoding="utf-8") as f:
        f.write(">\nACGT\n>named\nTTTT\n")
        path = f.name
    records, notices = read_fasta(path)
    assert len(records) == 2
    assert records[0].header == ""
    assert any("empty header" in n.lower() for n in notices)


def test_read_fasta_flags_duplicate_headers(tmp_path: Path) -> None:
    p = tmp_path / "dupes.fasta"
    p.write_text(">same\nACGT\n>same\nTTTT\n", encoding="utf-8")
    records, notices = read_fasta(str(p))
    assert len(records) == 2  # both kept — duplicates are flagged, not fatal
    assert any("repeat across records" in n for n in notices)


# --- ambiguity policy: keep / mask / error (issue #249) ------------------------------


def test_validate_keep_policy_preserves_the_original_codes() -> None:
    v = validate_sequence("ACGTNNNACGT", ambiguity_policy="keep")
    assert v.seq == "ACGTNNNACGT"
    assert any("kept" in n.lower() and "ambiguity" in n.lower() for n in v.notices)


def test_validate_mask_policy_normalizes_every_code_to_n() -> None:
    v = validate_sequence("ACGTRYSACGT", ambiguity_policy="mask")
    assert v.seq == "ACGTNNNACGT"  # R, Y, S all become N; ACGT bases untouched
    assert any("masked" in n.lower() and "ambiguity" in n.lower() for n in v.notices)


def test_validate_mask_policy_leaves_n_itself_unchanged() -> None:
    v = validate_sequence("ACGTNNNACGT", ambiguity_policy="mask")
    assert v.seq == "ACGTNNNACGT"


def test_validate_rejects_n_by_default() -> None:
    with pytest.raises(ValidationError):
        validate_sequence("ACGTNNNACGT")


def test_validate_error_policy_rejects_any_ambiguity_code_explicitly() -> None:
    with pytest.raises(ValidationError, match="ambiguity"):
        validate_sequence("ACGTNNNACGT", ambiguity_policy="error")


def test_validate_still_rejects_true_garbage_under_every_policy() -> None:
    for policy in ("keep", "mask", "error"):
        with pytest.raises(ValidationError):
            validate_sequence("ACGT@@@ACGT", ambiguity_policy=policy)


def test_validate_rejects_an_unknown_ambiguity_policy_value() -> None:
    with pytest.raises(ValidationError):
        validate_sequence("ACGTNNNACGT", ambiguity_policy="sideways")


# --- GenBank writer round-trip ------------------------------------------------------


def test_genbank_writer_embeds_mean_entropy(tmp_path: Path) -> None:
    import numpy as np

    from dna_entropy.readers.genbank import read_genbank

    records, _ = read_genbank(SAMPLE_GB)
    rec = records[0]
    seq, features = rec.seq, rec.features
    values = np.linspace(0.0, 2.0, len(seq), dtype="float32")
    out = GenBankWriter().write(
        name="tl", values=values, seq=seq, start=1, features=features, out_dir=str(tmp_path)
    )
    text = Path(out).read_text(encoding="utf-8")
    assert out.endswith("tl.gb")
    assert "mean_entropy=" in text
    assert "geneA" in text and "geneB" in text


# --- pipeline: GenBank input -> genbank + wig + stats, genes preserved ---------------


def test_pipeline_genbank_input(tmp_path: Path) -> None:
    cfg = RunConfig(name="tl", input_path=SAMPLE_GB, out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    names = {Path(p).name for p in result.outputs}
    assert names == {
        "tl.gb",
        "tl.fasta",
        "tl.entropy.bedgraph",
        "tl.entropy.wig",
        "tl.entropy.geneious.gff3",
        "tl.entropy.tsv",
        "tl.genes.gff3",
        "stats.txt",
    }
    assert len(result.genes) == 2  # preserved from the input, not re-called

    # The genes track comes from the GenBank (source column "genbank"), not Prodigal.
    genes_gff = Path(next(p for p in result.outputs if p.endswith(".genes.gff3"))).read_text()
    assert "\tgenbank\tgene\t" in genes_gff
    assert "\tpyrodigal\t" not in genes_gff


def test_pipeline_fasta_input_adds_genbank(tmp_path: Path) -> None:
    cfg = RunConfig(name="fa", input_path=SAMPLE_FA, out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    names = {Path(p).name for p in result.outputs}
    assert "fa.fasta" in names
    assert "fa.entropy.bedgraph" in names
    assert "fa.summary.txt" in names
    assert "fa.gb" in names  # bonus GenBank on the FASTA path


def test_load_input_paste_stays_strict(tmp_path: Path) -> None:
    cfg = RunConfig(name="p", input_path=None)
    loaded = load_input(cfg, raw="ACGTACGTACGT")
    assert loaded.source_kind == detect.PASTE
    assert loaded.features == []


# --- multi-record GenBank: ALL records processed ------------------------------------


def test_load_input_enforces_max_total_len_across_all_genbank_records_not_per_record() -> None:
    """#314: MULTI_GB has two records (58 bp + 57 bp = 115 bp total). Each is well under
    a 100-bp cap on its own, so a per-record-only check would let the 115-bp file through
    a documented "outer sanity bound on total input length" of 100."""
    cfg = RunConfig(name="mt", input_path=MULTI_GB, max_total_len=100)
    with pytest.raises(ValidationError) as exc:
        load_input(cfg)
    msg = str(exc.value)
    assert "100" in msg
    assert "115" in msg


def test_read_genbank_returns_all_records() -> None:
    records, notices = read_genbank(MULTI_GB)
    assert len(records) == 2
    assert [len(r.features) for r in records] == [2, 1]
    assert any("2 record" in n for n in notices)


def test_load_input_multi_record_contigs() -> None:
    cfg = RunConfig(name="mt", input_path=MULTI_GB)
    loaded = load_input(cfg)
    assert len(loaded.contigs) == 2
    # Geneious-style ids must be sanitized to safe, indexed contig names.
    assert [c.name for c in loaded.contigs] == ["mt_1", "mt_2"]
    assert all(
        set(c.name) <= set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")
        for c in loaded.contigs
    )


def test_pipeline_multi_record_genbank(tmp_path: Path) -> None:
    cfg = RunConfig(name="mt", input_path=MULTI_GB, out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    names = {Path(p).name for p in result.outputs}
    assert names == {
        "mt.gb",
        "mt.fasta",
        "mt.entropy.bedgraph",
        "mt.entropy.wig",
        "mt.entropy.geneious.gff3",
        "mt.entropy.tsv",
        "mt.genes.gff3",
        "stats.txt",
    }
    assert result.contigs == 2
    assert len(result.genes) == 3  # 2 + 1 genes preserved across both records

    # One GenBank file holds BOTH records.
    from Bio import SeqIO

    gb_path = next(p for p in result.outputs if p.endswith(".gb"))
    recs = list(SeqIO.parse(gb_path, "genbank"))
    assert len(recs) == 2

    # The WIG has a fixedStep block per record (chrom = each contig name).
    wig_text = Path(next(p for p in result.outputs if p.endswith(".wig"))).read_text()
    assert wig_text.count("fixedStep") == 2
    assert "chrom=mt_1" in wig_text and "chrom=mt_2" in wig_text

    # The FASTA (for loading as an IGV genome) holds both contigs, named to match the track.
    fasta_text = Path(next(p for p in result.outputs if p.endswith(".fasta"))).read_text()
    assert ">mt_1" in fasta_text and ">mt_2" in fasta_text

    # The bedGraph carries one row set per contig, keyed by the same chrom names.
    bg_text = Path(next(p for p in result.outputs if p.endswith(".bedgraph"))).read_text()
    assert bg_text.count("track type=bedGraph") == 1  # single header covers both records
    assert "mt_1\t" in bg_text and "mt_2\t" in bg_text

    # The genes track (from the GenBank) places each record's genes on its own contig.
    genes_gff = Path(next(p for p in result.outputs if p.endswith(".genes.gff3"))).read_text()
    assert "mt_1\tgenbank\tgene\t" in genes_gff and "mt_2\tgenbank\tgene\t" in genes_gff

    # stats.txt reports each record.
    stats = Path(next(p for p in result.outputs if p.endswith("stats.txt"))).read_text()
    assert "records:            2" in stats
    assert "[mt_1]" in stats and "[mt_2]" in stats
