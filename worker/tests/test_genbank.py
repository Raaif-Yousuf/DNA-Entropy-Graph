"""Tests for GenBank/FASTA input routing, reading, writing, and the pipeline branches."""

from __future__ import annotations

from pathlib import Path

import pytest

from dna_entropy import pipeline
from dna_entropy.config import RunConfig, TrackFormat
from dna_entropy.readers import detect
from dna_entropy.readers.fasta import read_fasta
from dna_entropy.readers.genbank import GenBankReadError, read_genbank
from dna_entropy.readers.input import load_input
from dna_entropy.validation.validators import ValidationError, validate_sequence
from dna_entropy.writers.genbank import GenBankWriter

DATA = Path(__file__).parent / "data"
SAMPLE_GB = str(DATA / "sample.gb")
MULTI_GB = str(DATA / "multi.gb")
SAMPLE_FA = str(DATA / "sample.fasta")
SPLICED_GB = str(DATA / "spliced.gb")
OUT_OF_RANGE_GB = str(DATA / "out_of_range.gb")
MALFORMED_DIR = DATA / "malformed"


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


def test_detect_by_content_sniff_ignores_a_utf8_bom(tmp_path: Path) -> None:
    """#330: a UTF-8 BOM must not defeat the '>'/'LOCUS' content sniff for an unknown
    extension -- the BOM decodes to a literal U+FEFF character that str.startswith(">")
    does not see through on its own."""
    gb = tmp_path / "mystery_bom.dat"
    gb.write_bytes(b"\xef\xbb\xbfLOCUS       foo    10 bp\n//\n")
    assert detect.detect_kind(str(gb)) == detect.GENBANK
    fa = tmp_path / "mystery2_bom.dat"
    fa.write_bytes(b"\xef\xbb\xbf>seq1\nACGT\n")
    assert detect.detect_kind(str(fa)) == detect.FASTA


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


def test_read_genbank_zero_feature_summary_notice_is_grammatical(tmp_path: Path) -> None:
    """#352: a record with no gene/CDS features at all is a real, unremarkable shape
    (source-only annotations, or a tool that never calls genes) -- the summary notice
    must read as ordinary English, not '0 no gene feature(s)'."""
    from Bio import SeqIO
    from Bio.Seq import Seq
    from Bio.SeqRecord import SeqRecord

    rec = SeqRecord(Seq("ACGTACGTACGT"), id="nofeat", name="nofeat", description="no features")
    rec.annotations["molecule_type"] = "DNA"
    p = tmp_path / "nofeat.gb"
    with open(p, "w", encoding="utf-8", newline="\n") as fh:
        SeqIO.write(rec, fh, "genbank")

    records, notices = read_genbank(str(p))
    assert records[0].features == []
    assert any("0 gene feature(s)" in n for n in notices)
    assert not any("no gene feature" in n for n in notices)


def test_read_genbank_wraps_a_missing_origin_block_as_a_clean_error(tmp_path: Path) -> None:
    """#349: a FEATURES table with no ORIGIN block at all (ends straight at '//') must
    raise a clean GenBankReadError naming an action, not Biopython's own raw
    'ValueError: Premature end of features table' traceback."""
    p = tmp_path / "no_origin.gb"
    p.write_text(
        "LOCUS       nosource    0 bp    DNA\n"
        "DEFINITION  no origin block at all.\n"
        "FEATURES             Location/Qualifiers\n"
        '     gene            1..5\n                     /gene="x"\n'
        "//\n",
        encoding="utf-8",
    )
    with pytest.raises(GenBankReadError):
        read_genbank(str(p))


def test_read_genbank_names_a_reason_when_biopython_raises_a_bare_assertion() -> None:
    """#403: Biopython's GenBank scanner has several bare `assert` statements with no
    message; `str(AssertionError())` is empty, so a naive wrap left
    'Could not parse the GenBank file: . Check...' -- a fact-free gap where the reason
    should be. The fixture below (issue #368's own Hypothesis-found regression) drives
    exactly one of those bare asserts (a feature qualifier continuation line missing its
    leading '/'): the wrapped message must name that specific, checkable cause instead of
    leaving the gap empty."""
    path = MALFORMED_DIR / "genbank_qualifier_missing_slash.gb"
    with pytest.raises(GenBankReadError) as excinfo:
        read_genbank(str(path))
    message = str(excinfo.value)
    assert "Could not parse the GenBank file: . " not in message, (
        f"the reason slot is still empty: {message!r}"
    )
    assert "leading '/'" in message or "leading slash" in message, message


def test_describe_bare_assertion_falls_back_to_naming_the_internal_check_when_unrecognized() -> None:
    """#403's fallback branch: Biopython has several bare `assert` sites, not just the
    qualifier-continuation one; a source line this reader does not specifically recognize
    must still produce a concrete, non-empty description (the raw internal check that
    failed), never silently falling through to an empty string. Exercised directly against
    `_describe_bare_assertion`, using a real (non-Biopython) bare assertion so the test
    does not depend on finding another obscure, possibly-unreachable Biopython assert site
    to trigger through the full `read_genbank` call."""
    from dna_entropy.readers.genbank import _describe_bare_assertion

    some_unrelated_value = 1
    try:
        assert some_unrelated_value == 2  # noqa: PT015 - deliberately triggering a bare assert
    except AssertionError as exc:
        reason = _describe_bare_assertion(exc)

    assert reason, "the fallback produced an empty description"
    assert "qualifiers" not in reason, "should not misapply the qualifier-specific guess here"


def test_read_genbank_handles_lone_cr_line_endings(tmp_path: Path) -> None:
    """#349: classic Mac / some sequencing instruments still emit lone '\\r' line
    endings. readers/fasta.py's text.splitlines() already tolerates this; GenBank must
    agree, not raise Biopython's raw 'ValueError: Premature end of line'."""
    raw = Path(SAMPLE_GB).read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r")
    p = tmp_path / "lone_cr.gb"
    p.write_bytes(raw)
    records, notices = read_genbank(str(p))
    assert len(records) == 1
    assert len(records[0].seq) == 126
    assert len(records[0].features) == 2


def test_read_genbank_strips_a_utf8_bom(tmp_path: Path) -> None:
    """#330: a BOM-prefixed GenBank file must parse identically to its BOM-free twin,
    not raise "No GenBank records found" because Biopython's own file-reading never saw
    a real 'LOCUS' line."""
    raw = Path(SAMPLE_GB).read_bytes()
    p = tmp_path / "bom_sample.gb"
    p.write_bytes(b"\xef\xbb\xbf" + raw)
    records, notices = read_genbank(str(p))
    assert len(records) == 1
    assert len(records[0].seq) == 126
    assert len(records[0].features) == 2


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


def test_read_genbank_drops_a_feature_whose_coordinates_exceed_the_record_length() -> None:
    """#331: a gene whose recorded end is beyond its own record's ORIGIN length (a
    feature-table/ORIGIN mismatch -- truncation, hand-editing, corruption) must not be
    silently kept with a wrong span. It is dropped, loudly, and the rest of the record's
    genes are unaffected."""
    records, notices = read_genbank(OUT_OF_RANGE_GB)
    assert len(records) == 1
    rec = records[0]
    by_id = {f.gene_id: f for f in rec.features}

    assert "badgene" not in by_id  # dropped: 40..500 on a 60 nt record
    assert "goodgene" in by_id  # unaffected: still reported normally
    assert (by_id["goodgene"].begin, by_id["goodgene"].end) == (10, 30)

    dropped_notices = [n for n in notices if "badgene" in n]
    assert len(dropped_notices) == 1
    assert "60 nt" in dropped_notices[0]
    assert "40..500" in dropped_notices[0]


def test_read_genbank_keeps_a_partial_feature_whose_end_legitimately_exceeds_the_record() -> None:
    """#331: a GenBank `>` partial-end marker is the one legitimate reason a feature's
    given end coordinate sits past the record's own sequence length -- it is GenBank's
    own way of saying the feature is known to continue beyond what was given. This must
    NOT be dropped or flagged as a mismatch the way an unmarked overrun is."""
    records, notices = read_genbank(OUT_OF_RANGE_GB)
    rec = records[0]
    by_id = {f.gene_id: f for f in rec.features}

    assert "partialgene" in by_id
    assert by_id["partialgene"].partial is True
    assert (by_id["partialgene"].begin, by_id["partialgene"].end) == (45, 1000)
    assert not any("partialgene" in n for n in notices)


# --- FASTA reader -------------------------------------------------------------------


def test_read_fasta_single_record() -> None:
    records, notices = read_fasta(SAMPLE_FA)
    assert len(records) == 1
    assert records[0].seq and set(records[0].seq.upper()) <= set("ACGTN")


def test_read_fasta_strips_a_utf8_bom(tmp_path: Path) -> None:
    """#330: a BOM-prefixed FASTA is still a valid FASTA -- it must not be rejected
    with "No FASTA records found" just because the header line starts with a BOM."""
    p = tmp_path / "bom.fasta"
    p.write_bytes(b"\xef\xbb\xbf>seq1\nACGTACGT\n")
    records, notices = read_fasta(str(p))
    assert len(records) == 1
    assert records[0].header == "seq1"
    assert records[0].seq == "ACGTACGT"


def test_read_fasta_decodes_utf16_bom(tmp_path: Path) -> None:
    """#330: a UTF-16 FASTA (Windows Notepad's "Unicode" save option) must decode in
    full, not turn every non-ASCII-looking byte pair into U+FFFD."""
    p = tmp_path / "utf16.fasta"
    p.write_bytes(">seq1\nACGTACGT\n".encode("utf-16"))
    records, notices = read_fasta(str(p))
    assert len(records) == 1
    assert records[0].header == "seq1"
    assert records[0].seq == "ACGTACGT"


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


def test_read_fasta_flags_duplicate_ids_even_with_different_descriptions(tmp_path: Path) -> None:
    """#351: the ID is the first whitespace-delimited token (BLAST/samtools/IGV
    convention) — two records sharing an ID but with different free-text descriptions
    are a genuine ID collision and must be flagged, not silently missed because the
    FULL header lines happen to differ."""
    p = tmp_path / "dup_ids.fasta"
    p.write_text(">seq1 first description\nACGT\n>seq1 second description\nTTTT\n", encoding="utf-8")
    records, notices = read_fasta(str(p))
    assert len(records) == 2
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
        "tl.entropy.geneious.gff3",
        "tl.entropy.tsv",
        "tl.genes.gff3",
        "tl.regions.bed",  # issue #125
        "tl.regions.gff3",
        "tl.surprisal.bedgraph",
        "tl.surprisal.geneious.gff3",
        "provenance.json",
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
        "mt.entropy.geneious.gff3",
        "mt.entropy.tsv",
        "mt.genes.gff3",
        "mt.regions.bed",  # issue #125
        "mt.regions.gff3",
        "mt.surprisal.bedgraph",
        "mt.surprisal.geneious.gff3",
        "provenance.json",
        "stats.txt",
    }
    assert result.contigs == 2
    assert len(result.genes) == 3  # 2 + 1 genes preserved across both records

    # One GenBank file holds BOTH records.
    from Bio import SeqIO

    gb_path = next(p for p in result.outputs if p.endswith(".gb"))
    recs = list(SeqIO.parse(gb_path, "genbank"))
    assert len(recs) == 2

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


# --- #412: a GenBank input honours track_format exactly like every other input ------------


def test_genbank_wig_format_writes_wig_and_no_bedgraph_for_entropy_and_surprisal(tmp_path: Path) -> None:
    cfg = RunConfig(name="mt", input_path=MULTI_GB, out_dir=str(tmp_path), track_format=TrackFormat.WIG)
    result = pipeline.run(cfg)
    names = {Path(p).name for p in result.outputs}
    assert "mt.entropy.wig" in names and "mt.surprisal.wig" in names
    assert not any(n.endswith(".bedgraph") for n in names)
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert not any(n.endswith(".bedgraph") for n in on_disk)

    # The WIG has a fixedStep block per record (chrom = each contig name).
    wig_text = Path(next(p for p in result.outputs if p.endswith("mt.entropy.wig"))).read_text()
    assert wig_text.count("fixedStep") == 2
    assert "chrom=mt_1" in wig_text and "chrom=mt_2" in wig_text


def test_genbank_bedgraph_format_writes_no_wig(tmp_path: Path) -> None:
    cfg = RunConfig(name="mt", input_path=MULTI_GB, out_dir=str(tmp_path), track_format=TrackFormat.BEDGRAPH)
    result = pipeline.run(cfg)
    assert not any(Path(p).name.endswith(".wig") for p in result.outputs)
    assert not any(p.suffix == ".wig" for p in tmp_path.iterdir())


def test_genbank_and_fasta_inputs_write_the_same_track_files_for_the_same_format(tmp_path: Path) -> None:
    for fmt, ext in ((TrackFormat.BEDGRAPH, ".bedgraph"), (TrackFormat.WIG, ".wig")):
        gb_dir, fa_dir = tmp_path / f"gb{ext}", tmp_path / f"fa{ext}"
        gb = pipeline.run(RunConfig(name="x", input_path=SAMPLE_GB, out_dir=str(gb_dir), track_format=fmt))
        fa = pipeline.run(RunConfig(name="x", input_path=SAMPLE_FA, out_dir=str(fa_dir), track_format=fmt))
        gb_tracks = {Path(p).name for p in gb.outputs if p.endswith((".bedgraph", ".wig"))}
        fa_tracks = {Path(p).name for p in fa.outputs if p.endswith((".bedgraph", ".wig"))}
        assert gb_tracks == fa_tracks
        assert gb_tracks and all(n.endswith(ext) for n in gb_tracks)


def test_genbank_both_separate_fwd_rev_tracks_follow_the_track_format(tmp_path: Path) -> None:
    from dna_entropy.config import Direction

    cfg = RunConfig(
        name="sep",
        input_path=SAMPLE_GB,
        out_dir=str(tmp_path),
        track_format=TrackFormat.WIG,
        direction=Direction.BOTH_SEPARATE,
    )
    names = {Path(p).name for p in pipeline.run(cfg).outputs}
    assert "sep.entropy.fwd.wig" in names and "sep.entropy.rev.wig" in names
    assert not any(n.endswith(".bedgraph") for n in names)


# --- issue #162: a truncated multi-record file names the record it could not read -----------


def test_a_truncated_multi_record_genbank_names_the_record_that_failed(tmp_path: Path) -> None:
    text = Path(MULTI_GB).read_text(encoding="utf-8")
    cut = tmp_path / "trunc.gb"
    cut.write_text(text[: len(text) - 200], encoding="utf-8", newline="\n")  # chops record 2 mid-ORIGIN
    with pytest.raises(GenBankReadError) as exc:
        read_genbank(cut)
    msg = str(exc.value)
    assert "record 2" in msg, msg
    assert "truncated" in msg  # the action stays: check the file was not truncated


def test_a_malformed_first_record_is_named_record_1() -> None:
    with pytest.raises(GenBankReadError, match="record 1"):
        read_genbank(MALFORMED_DIR / "genbank_qualifier_missing_slash.gb")


# --- issue #536: a truncated file (a LOCUS length, no ORIGIN sequence) is the reader's own error ---


def _truncated_after_origin() -> str:
    text = Path(SAMPLE_GB).read_text(encoding="utf-8")
    return text[: text.index("ORIGIN\n") + len("ORIGIN\n ")]  # the exact shape the fuzz test found


def test_truncated_after_origin_raises_the_readers_own_error_naming_an_action(tmp_path: Path) -> None:
    path = tmp_path / "truncated.gb"
    path.write_text(_truncated_after_origin(), encoding="utf-8", newline="\n")
    with pytest.raises(GenBankReadError) as exc:
        read_genbank(str(path))
    assert "Re-export" in str(exc.value)
    assert isinstance(exc.value, ValidationError)  # INPUT_INVALID at the worker boundary


def test_truncated_after_origin_is_a_validation_error_through_load_input(tmp_path: Path) -> None:
    path = tmp_path / "truncated.gb"
    path.write_text(_truncated_after_origin(), encoding="utf-8", newline="\n")
    with pytest.raises(ValidationError):
        load_input(RunConfig(name="t", input_path=str(path), informat="genbank", out_dir=str(tmp_path)))


def test_a_truncated_record_among_good_ones_is_skipped_with_a_notice(tmp_path: Path) -> None:
    good = Path(SAMPLE_GB).read_text(encoding="utf-8")
    path = tmp_path / "mixed.gb"
    path.write_text(good + _truncated_after_origin() + "\n//\n", encoding="utf-8", newline="\n")
    records, notices = read_genbank(str(path))
    assert len(records) == 1
    assert any("Skipped GenBank record 2" in n and "no nucleotide sequence" in n for n in notices)


def test_every_prefix_of_a_valid_genbank_file_reads_or_raises_the_readers_own_error(tmp_path: Path) -> None:
    """A deterministic sweep of the truncation shape the random fuzz test found once."""
    data = Path(SAMPLE_GB).read_bytes()
    path = tmp_path / "prefix.gb"
    outcomes = {"read": 0, "refused": 0}
    for cut in range(0, len(data) + 1, 3):
        path.write_bytes(data[:cut])
        try:
            read_genbank(str(path))
            outcomes["read"] += 1
        except GenBankReadError:
            outcomes["refused"] += 1
    assert outcomes["read"] > 0 and outcomes["refused"] > 0, "the sweep must see both outcomes"


# --- issue #536 audit: every other exception type Biopython's scanner can raise ---


def test_a_feature_line_shorter_than_the_qualifier_indent_is_the_readers_own_error() -> None:
    """MEASURED 2026-10-03 (a seeded mutation sweep of sample.gb, 18000 files): a feature
    line shorter than Biopython's qualifier column raised a raw ``IndexError`` out of
    ``Bio.GenBank.Scanner.parse_features``, which the parse try-block did not catch."""
    with pytest.raises(GenBankReadError, match="record 1") as exc:
        read_genbank(MALFORMED_DIR / "genbank_feature_line_shorter_than_qualifier_indent.gb")
    assert "Check" in str(exc.value)  # names the action
    assert "string index out of range" not in str(exc.value)  # a Python-internal phrase, not a fact


# --- issue #492: a non-ASCII letter in ORIGIN is refused before Biopython can fold it into ASCII ---


def _gb_with_origin(*origin_lines: str, locus_len: int = 16) -> str:
    return (
        f"LOCUS       toy    {locus_len} bp    DNA              UNK 01-JAN-1980\n"
        "FEATURES             Location/Qualifiers\n"
        "     gene            1..4\n"
        '                     /gene="g"\n'
        "ORIGIN\n" + "".join(line + "\n" for line in origin_lines) + "//\n"
    )


NON_ASCII_ORIGIN_LETTERS = [
    ("\u017f", "U+017F"),  # long s: str.upper() gives "S", a valid IUPAC code (the #492 probe)
    ("\u00df", "U+00DF"),  # sharp s: str.upper() gives "SS"
    ("\u0131", "U+0131"),  # dotless i: str.upper() gives "I"
    ("\u00e9", "U+00E9"),  # e acute: Biopython's own ascii encode used to fail with an opaque codec error
    ("\u00a0", "U+00A0"),  # no-break space: str.split() would silently treat it as whitespace
    ("\u0663", "U+0663"),  # Arabic-Indic digit three: str.isdigit() is True, but it is not a position number
]


def test_non_ascii_origin_letter_table_is_not_vacuous() -> None:
    assert len(NON_ASCII_ORIGIN_LETTERS) >= 6
    assert all(not ch.isascii() for ch, _ in NON_ASCII_ORIGIN_LETTERS)


@pytest.mark.parametrize(("char", "code_point"), NON_ASCII_ORIGIN_LETTERS)
def test_a_non_ascii_origin_letter_is_refused_naming_its_code_point_and_position(
    tmp_path: Path, char: str, code_point: str
) -> None:
    path = tmp_path / "non_ascii.gb"
    path.write_text(_gb_with_origin(f"        1 acgtacgt{char}acgtacg"), encoding="utf-8", newline="\n")
    with pytest.raises(GenBankReadError) as exc:
        read_genbank(str(path))
    message = str(exc.value)
    assert f"Invalid character {code_point} at position 9" in message
    assert "record 1" in message
    assert message.isascii()  # Hard Rule 5: the console line never carries the lookalike glyph


def test_the_issue_492_probe_is_refused_through_load_input_naming_u017f_at_position_9(tmp_path: Path) -> None:
    path = tmp_path / "long_s.gb"
    path.write_text(_gb_with_origin("        1 acgtacgt\u017facgtacg"), encoding="utf-8", newline="\n")
    cfg = RunConfig(name="t", input_path=str(path), informat="genbank", out_dir=str(tmp_path))
    with pytest.raises(ValidationError, match=r"U\+017F at position 9"):
        load_input(cfg)


def test_the_non_ascii_position_counts_bases_across_origin_lines_not_digits_or_spaces(tmp_path: Path) -> None:
    path = tmp_path / "second_line.gb"
    path.write_text(
        _gb_with_origin("        1 acgtacgtac gtacgtacgt", "       21 acgt\u017facgtac", locus_len=30),
        encoding="utf-8",
        newline="\n",
    )
    with pytest.raises(GenBankReadError, match=r"U\+017F at position 25"):
        read_genbank(str(path))


def test_the_non_ascii_refusal_names_the_record_and_counts_from_its_own_origin(tmp_path: Path) -> None:
    good = _gb_with_origin("        1 acgtacgtacgtacgt")
    bad = _gb_with_origin("        1 acg\u017facgtacgtacgt")
    path = tmp_path / "second_record.gb"
    path.write_text(good + bad, encoding="utf-8", newline="\n")
    with pytest.raises(
        GenBankReadError, match=r"U\+017F at position 4 .*record 2|record 2.*U\+017F at position 4"
    ):
        read_genbank(str(path))


def test_non_ascii_outside_the_origin_block_is_still_read(tmp_path: Path) -> None:
    text = _gb_with_origin("        1 acgtacgtacgtacgt").replace(
        '/gene="g"', '/gene="g\u00e9ne"\n                     /note="\u017f"'
    )
    path = tmp_path / "qualifier.gb"
    path.write_text(text, encoding="utf-8", newline="\n")
    records, _ = read_genbank(str(path))
    assert records[0].seq == "ACGTACGTACGTACGT"
    assert records[0].features[0].gene_id == "g\u00e9ne"


def test_an_ascii_lowercase_origin_is_still_uppercased(tmp_path: Path) -> None:
    path = tmp_path / "lower.gb"
    path.write_text(_gb_with_origin("        1 acgtacgtrykmacgt"), encoding="utf-8", newline="\n")
    records, _ = read_genbank(str(path))
    assert records[0].seq == "ACGTACGTRYKMACGT"
