"""Tests for writers/tsv.py (issue #281/#46): position, base, entropy per row."""

from __future__ import annotations

from pathlib import Path

import numpy as np

from dna_entropy.writers import TsvWriter, Writer

VALUES = np.array([0.0, 1.0, 2.0], dtype=np.float32)
SEQ = "ATG"


def test_tsv_writer_satisfies_the_writer_protocol() -> None:
    assert isinstance(TsvWriter(), Writer)


def test_tsv_has_exactly_three_columns_with_a_header_row(tmp_path: Path) -> None:
    path = TsvWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    assert path.endswith("locus.entropy.tsv")
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_bits"
    assert len(lines) == 1 + len(VALUES)  # header + one row per base, no extra rows
    for line in lines[1:]:
        assert len(line.split("\t")) == 3


def test_tsv_position_is_one_based_matching_genbank(tmp_path: Path) -> None:
    path = TsvWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[1] == "1\tA\t0.0000"
    assert lines[2] == "2\tT\t1.0000"
    assert lines[3] == "3\tG\t2.0000"


def test_tsv_respects_start_offset(tmp_path: Path) -> None:
    path = TsvWriter().write(name="locus", values=VALUES, seq=SEQ, start=100, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[1] == "100\tA\t0.0000"
    assert lines[2] == "101\tT\t1.0000"


def test_tsv_entropy_is_formatted_to_four_decimal_places(tmp_path: Path) -> None:
    values = np.array([1.0 / 3.0], dtype=np.float32)
    path = TsvWriter().write(name="locus", values=values, seq="A", start=1, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    position, base, entropy = lines[1].split("\t")
    assert entropy == f"{float(values[0]):.4f}"
    assert len(entropy.split(".")[1]) == 4


def test_tsv_write_multi_separates_contigs_with_a_comment_and_still_three_columns(
    tmp_path: Path,
) -> None:
    path = TsvWriter().write_multi(
        name="multi",
        blocks=[
            ("chrom_1", "AT", np.array([0.0, 1.0], dtype=np.float32)),
            ("chrom_2", "GC", np.array([2.0, 0.5], dtype=np.float32)),
        ],
        start=1,
        out_dir=str(tmp_path),
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_bits"
    assert "# contig: chrom_1" in lines
    assert "# contig: chrom_2" in lines
    data_lines = [ln for ln in lines[1:] if not ln.startswith("#")]
    assert len(data_lines) == 4  # 2 bases x 2 contigs
    for line in data_lines:
        assert len(line.split("\t")) == 3
    # position numbering restarts at `start` for each contig (matches bedGraph/WIG).
    assert data_lines[0] == "1\tA\t0.0000"
    assert data_lines[1] == "2\tT\t1.0000"
    assert data_lines[2] == "1\tG\t2.0000"
    assert data_lines[3] == "2\tC\t0.5000"


# --- issue #123: an optional 4th column, surprisal_bits, alongside entropy -------------


def test_tsv_write_adds_surprisal_column_when_given(tmp_path: Path) -> None:
    surprisal = np.array([6.643856, 0.0, 2.0], dtype=np.float32)
    path = TsvWriter().write(
        name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path), surprisal=surprisal
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_bits\tsurprisal_bits"
    # "A position where the actual base has probability 0.01 shows surprisal 6.64 bits in
    # the TSV while entropy there is low" -- issue #123's own named observable.
    assert lines[1] == "1\tA\t0.0000\t6.6439"
    for line in lines[1:]:
        assert len(line.split("\t")) == 4


def test_tsv_write_omits_surprisal_column_by_default(tmp_path: Path) -> None:
    # Backward compatible: no `surprisal=` -> the plain 3-column shape, byte-identical to
    # before this option existed.
    path = TsvWriter().write(name="locus", values=VALUES, seq=SEQ, start=1, out_dir=str(tmp_path))
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_bits"
    for line in lines[1:]:
        assert len(line.split("\t")) == 3


def test_tsv_write_multi_surprisal_blocks_align_with_contigs(tmp_path: Path) -> None:
    path = TsvWriter().write_multi(
        name="multi",
        blocks=[
            ("chrom_1", "AT", np.array([0.0, 1.0], dtype=np.float32)),
            ("chrom_2", "GC", np.array([2.0, 0.5], dtype=np.float32)),
        ],
        start=1,
        out_dir=str(tmp_path),
        surprisal_blocks=[
            np.array([0.1, 1.1], dtype=np.float32),
            np.array([2.1, 0.6], dtype=np.float32),
        ],
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_bits\tsurprisal_bits"
    data_lines = [ln for ln in lines[1:] if not ln.startswith("#")]
    assert data_lines[0] == "1\tA\t0.0000\t0.1000"
    assert data_lines[1] == "2\tT\t1.0000\t1.1000"
    assert data_lines[2] == "1\tG\t2.0000\t2.1000"
    assert data_lines[3] == "2\tC\t0.5000\t0.6000"


def test_tsv_write_multi_single_block_has_no_comment_line(tmp_path: Path) -> None:
    # A single-record write via write_multi must be indistinguishable from write().
    path = TsvWriter().write_multi(
        name="one",
        blocks=[("one", SEQ, VALUES)],
        start=1,
        out_dir=str(tmp_path),
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert not any(ln.startswith("#") for ln in lines)
    assert len(lines) == 1 + len(VALUES)


# --- Direction.BOTH_SEPARATE: 5-column position/base/fwd/rev/combined shape -----------
# (docs/science_and_formats.md section 5's proposal, confirmed here.)


def test_tsv_separate_has_five_columns_with_a_header_row(tmp_path: Path) -> None:
    fwd = np.array([0.5, 1.5], dtype=np.float32)
    rev = np.array([1.0, 0.25], dtype=np.float32)
    combined = np.array([0.75, 0.9], dtype=np.float32)
    path = TsvWriter().write_separate(
        name="sep",
        forward_values=fwd,
        reverse_values=rev,
        combined_values=combined,
        seq="AT",
        start=1,
        out_dir=str(tmp_path),
    )
    assert path.endswith("sep.entropy.tsv")
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_fwd\tentropy_rev\tentropy_combined"
    assert lines[1] == "1\tA\t0.5000\t1.0000\t0.7500"
    assert lines[2] == "2\tT\t1.5000\t0.2500\t0.9000"


def test_tsv_write_multi_separate_separates_contigs_with_a_comment(tmp_path: Path) -> None:
    a = np.array([0.1], dtype=np.float32)
    b = np.array([0.2], dtype=np.float32)
    c = np.array([0.3], dtype=np.float32)
    path = TsvWriter().write_multi_separate(
        name="multisep",
        blocks=[
            ("chrom_1", "A", a, b, c),
            ("chrom_2", "T", b, c, a),
        ],
        start=1,
        out_dir=str(tmp_path),
    )
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_fwd\tentropy_rev\tentropy_combined"
    assert "# contig: chrom_1" in lines
    assert "# contig: chrom_2" in lines
    data_lines = [ln for ln in lines[1:] if not ln.startswith("#")]
    assert len(data_lines) == 2
    for line in data_lines:
        assert len(line.split("\t")) == 5
