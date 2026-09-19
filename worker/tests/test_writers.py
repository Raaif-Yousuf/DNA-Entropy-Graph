"""Tests for the IGV/Geneious output writers (bedGraph, WIG, GFF3, FASTA, summary)."""

from __future__ import annotations

from pathlib import Path

import numpy as np

from dna_entropy.writers import (
    BedGraphWriter,
    FastaWriter,
    GeneiousWriter,
    SummaryWriter,
    WigWriter,
    Writer,
)
from dna_entropy.writers.geneious import DEFAULT_MAX_PER_BASE_FEATURES

VALUES = np.array([0.0, 1.0, 2.0], dtype=np.float32)
SEQ = "ATG"


def test_writers_satisfy_protocol() -> None:
    for w in (BedGraphWriter(), WigWriter(), GeneiousWriter(), FastaWriter(), SummaryWriter()):
        assert isinstance(w, Writer)


def test_bedgraph_coordinates_and_values(tmp_path: Path) -> None:
    path = BedGraphWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0].startswith("track type=bedGraph")
    # 0-based half-open, start=1 -> first base is [0,1)
    assert lines[1] == "locus\t0\t1\t0.0000"
    assert lines[2] == "locus\t1\t2\t1.0000"
    assert lines[3] == "locus\t2\t3\t2.0000"


def test_bedgraph_respects_start_offset(tmp_path: Path) -> None:
    path = BedGraphWriter().write(name="locus", values=VALUES, seq=SEQ, start=100, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[1] == "locus\t99\t100\t0.0000"


def test_bedgraph_variant_suffix_names_the_file_fwd_rev(tmp_path: Path) -> None:
    # both-separate mode names its extra tracks <name>.entropy.fwd.bedgraph / .rev.bedgraph.
    fwd_path = BedGraphWriter().write(
        name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path), variant="fwd"
    )
    rev_path = BedGraphWriter().write(
        name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path), variant="rev"
    )
    assert fwd_path.endswith("locus.entropy.fwd.bedgraph")
    assert rev_path.endswith("locus.entropy.rev.bedgraph")
    assert Path(fwd_path).exists() and Path(rev_path).exists()
    # The default (no variant) path is unaffected and still the plain name.
    plain_path = BedGraphWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    assert plain_path.endswith("locus.entropy.bedgraph")
    assert not plain_path.endswith("fwd.bedgraph")


def test_wig_variant_suffix_names_the_file_fwd_rev(tmp_path: Path) -> None:
    fwd_path = WigWriter().write(
        name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path), variant="fwd"
    )
    assert fwd_path.endswith("locus.entropy.fwd.wig")


def test_wig_header_and_values(tmp_path: Path) -> None:
    path = WigWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0].startswith("track type=wiggle_0")
    assert lines[1] == "fixedStep chrom=locus start=1 step=1 span=1"
    assert lines[2:] == ["0.0000", "1.0000", "2.0000"]


def test_geneious_gff3_track(tmp_path: Path) -> None:
    path = GeneiousWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    assert path.endswith("locus.entropy.geneious.gff3")
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "##gff-version 3"
    assert lines[1] == "##sequence-region locus 1 3"
    # one 1 bp feature per position; entropy in the score column (6) and an entropy qualifier
    first = lines[2].split("\t")
    assert first[0] == "locus"
    assert first[2] == "entropy"
    assert first[3] == "1" and first[4] == "1"  # 1-based, inclusive
    assert first[5] == "0.0000"  # score column
    assert "entropy=0.0000" in first[8]
    assert lines[4].split("\t")[5] == "2.0000"


def test_geneious_gff3_respects_start_offset(tmp_path: Path) -> None:
    path = GeneiousWriter().write(name="locus", values=VALUES, seq=SEQ, start=100, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[1] == "##sequence-region locus 100 102"
    assert lines[2].split("\t")[3] == "100"


# --- large-sequence binning (issue #296) -----------------------------------------------
#
# MEASURED 2026-09-19: an unbinned 1,000,000-position track (via the ORIGINAL, one-line-
# per-base writer) is 87.78 MB and takes 1.587s to write; a 10,000-position track is
# 0.82 MB / 0.012s. The 1 Mb file is not slow to WRITE, but it is a poor size for
# Geneious to import and a poor density for its Heatmap view. GeneiousWriter therefore
# switches from one 1 bp feature per base to fixed-size mean-entropy bins once the
# combined track exceeds `DEFAULT_MAX_PER_BASE_FEATURES` positions; `max_per_base_features`
# lets a caller force full resolution (`None`) or a tighter/looser cap.


def test_geneious_stays_per_base_below_the_threshold(tmp_path: Path) -> None:
    # Unaffected by the fix: default threshold is far above any test-sized sequence.
    path = GeneiousWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    feature_lines = [line for line in lines if not line.startswith("#")]
    assert len(feature_lines) == len(VALUES)


def test_geneious_bins_when_over_the_threshold(tmp_path: Path) -> None:
    values = np.arange(12, dtype=np.float32) * 0.1  # 0.0 .. 1.1, distinct per position
    path = GeneiousWriter().write(
        name="locus", values=values, seq="A" * 12, start=1, out_dir=str(tmp_path), max_per_base_features=3
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    feature_lines = [line for line in lines if not line.startswith("#") and line != "##gff-version 3"]
    # bin_size = ceil(12 / 3) = 4 -> 3 bins covering [1,4], [5,8], [9,12].
    assert len(feature_lines) == 3
    first = feature_lines[0].split("\t")
    assert first[3] == "1" and first[4] == "4"
    expected_mean = f"{values[0:4].mean():.4f}"
    assert first[5] == expected_mean
    last = feature_lines[-1].split("\t")
    assert last[3] == "9" and last[4] == "12"


def test_geneious_binning_notice_is_recorded_in_the_file(tmp_path: Path) -> None:
    values = np.zeros(12, dtype=np.float32)
    path = GeneiousWriter().write(
        name="locus", values=values, seq="A" * 12, start=1, out_dir=str(tmp_path), max_per_base_features=3
    )
    text = Path(path).read_text(encoding="utf-8")
    assert "# NOTE" in text
    assert "binned" in text


def test_geneious_max_per_base_features_none_forces_full_resolution(tmp_path: Path) -> None:
    values = np.arange(12, dtype=np.float32)
    path = GeneiousWriter().write(
        name="locus", values=values, seq="A" * 12, start=1, out_dir=str(tmp_path), max_per_base_features=None
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    feature_lines = [line for line in lines if not line.startswith("#")]
    assert len(feature_lines) == 12  # never binned, regardless of length
    assert "# NOTE" not in Path(path).read_text(encoding="utf-8")


def test_geneious_default_threshold_bounds_file_size_for_a_long_sequence(tmp_path: Path) -> None:
    # Exercises the REAL default threshold (not an injected override) with a sequence
    # just over it, so the test stays fast while still proving the shipped default
    # actually bounds output size -- the point of issue #296. MEASURED 2026-09-19 at full
    # scale (see geneious.py's module docstring): unbinned 1,000,000 positions is 87.78 MB;
    # with this fix, 1,000,000 AND 10,000,000 positions both land at ~22-23 MB, because
    # the feature COUNT is capped at max_per_base_features regardless of L.
    length = DEFAULT_MAX_PER_BASE_FEATURES + 50_000
    rng = np.random.default_rng(0)
    values = rng.uniform(0.0, 2.0, size=length).astype(np.float32)
    path = GeneiousWriter().write(
        name="locus", values=values, seq="A" * length, start=1, out_dir=str(tmp_path)
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    feature_lines = [line for line in lines if not line.startswith("#") and line != "##gff-version 3"]
    assert len(feature_lines) <= DEFAULT_MAX_PER_BASE_FEATURES
    assert any(line.startswith("# NOTE") for line in lines)


def test_geneious_binned_last_bin_can_be_shorter(tmp_path: Path) -> None:
    values = np.arange(10, dtype=np.float32)  # 10 positions, bin_size = ceil(10/3) = 4
    path = GeneiousWriter().write(
        name="locus", values=values, seq="A" * 10, start=1, out_dir=str(tmp_path), max_per_base_features=3
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    feature_lines = [line for line in lines if not line.startswith("#") and line != "##gff-version 3"]
    # bins: [1,4], [5,8], [9,10] (last bin only 2 positions wide)
    assert len(feature_lines) == 3
    last = feature_lines[-1].split("\t")
    assert last[3] == "9" and last[4] == "10"
    assert last[5] == f"{values[8:10].mean():.4f}"


def test_fasta_roundtrip_and_wrapping(tmp_path: Path) -> None:
    seq = "ACGT" * 40  # 160 nt -> wraps at 60
    path = FastaWriter().write(
        name="locus",
        values=np.zeros(len(seq), dtype=np.float32),
        seq=seq,
        start=1,
        out_dir=str(tmp_path),
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == ">locus"
    assert all(len(line) <= 60 for line in lines[1:])
    assert "".join(lines[1:]) == seq


def test_summary_contains_stats(tmp_path: Path) -> None:
    path = SummaryWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    text = Path(path).read_text(encoding="utf-8")
    assert "length:" in text
    assert "3 nt" in text
    assert "entropy mean:" in text


def test_summary_records_windowing_and_direction_provenance(tmp_path: Path) -> None:
    from dna_entropy.analysis.direction import DirectionResult
    from dna_entropy.config import Direction

    dr = DirectionResult(
        values=VALUES,
        direction=Direction.BOTH_COMBINED,
        context_length=128,
        window=256,
        stride=128,
        seam=128,
        reduced_context_count=0,
    )
    path = SummaryWriter().write(
        name="locus",
        values=VALUES,
        seq=SEQ,
        start=1,
        out_dir=str(tmp_path),
        provenance=dr,
    )
    text = Path(path).read_text(encoding="utf-8")
    assert "context length (K):" in text and "128" in text
    assert "window (W):" in text and "256" in text
    assert "stride (S):" in text
    assert "direction:" in text and "both-combined" in text
    assert "seam:" in text and "128" in text


def test_summary_records_the_ceiling_used(tmp_path: Path) -> None:
    from dna_entropy.analysis.direction import DirectionResult
    from dna_entropy.config import Direction

    dr = DirectionResult(
        values=VALUES,
        direction=Direction.BOTH_COMBINED,
        context_length=128,
        window=256,
        stride=128,
        seam=128,
        reduced_context_count=0,
        ceiling=8192,
    )
    path = SummaryWriter().write(
        name="locus",
        values=VALUES,
        seq=SEQ,
        start=1,
        out_dir=str(tmp_path),
        provenance=dr,
    )
    text = Path(path).read_text(encoding="utf-8")
    assert "ceiling" in text and "8192" in text


def test_summary_records_reduced_context_note_when_present(tmp_path: Path) -> None:
    from dna_entropy.analysis.direction import DirectionResult
    from dna_entropy.config import Direction

    dr = DirectionResult(
        values=VALUES,
        direction=Direction.BOTH_COMBINED,
        context_length=100,
        window=200,
        stride=100,
        seam=None,
        reduced_context_count=7,
    )
    path = SummaryWriter().write(
        name="locus",
        values=VALUES,
        seq=SEQ,
        start=1,
        out_dir=str(tmp_path),
        provenance=dr,
    )
    text = Path(path).read_text(encoding="utf-8")
    assert "reduced-context positions: 7" in text
    seam_line = next(line for line in text.splitlines() if line.strip().startswith("seam:"))
    assert seam_line.strip() == "seam:               n/a"


# --- GenBankWriter: UTF-8/LF, never CRLF (Hard Rule 5) ---------------------------------
#
# MEASURED 2026-09-19: GenBankWriter._build_record's SeqIO.write(seq_records, str(path),
# "genbank") lets Biopython open the file itself, in platform-default text mode -- on
# Windows that means CRLF line endings, unlike every other writer here, which routes
# through writers/base.write_text_lf (encoding="utf-8", newline="\n"). A run's GenBank
# output landing with CRLF endings is invisible in Windows Notepad/most viewers but is a
# real Hard Rule 5 violation (and can trip a downstream tool's naive line-based parser).


def test_genbank_writer_uses_lf_not_crlf(tmp_path: Path) -> None:
    from dna_entropy.annotators.base import GeneFeature
    from dna_entropy.writers.genbank import GenBankWriter

    seq = "ACGT" * 20
    features = [GeneFeature(begin=1, end=10, strand="+", gene_id="gene_1")]
    path = GenBankWriter().write(
        name="locus",
        values=np.zeros(len(seq), dtype=np.float32),
        seq=seq,
        start=1,
        features=features,
        out_dir=str(tmp_path),
    )
    raw = Path(path).read_bytes()
    assert b"\r\n" not in raw, "GenBankWriter must write LF newlines, never CRLF (Hard Rule 5)"
    assert b"\n" in raw
