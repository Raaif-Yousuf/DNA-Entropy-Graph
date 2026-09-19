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
