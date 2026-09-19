"""Issue #283/#47: multi-record FASTA parity with GenBank (process all records).

Design D14 changes the prototype's behaviour: a multi-record FASTA must be treated like a
multi-record GenBank — every record analyzed, one contig each — not just the first.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.config import RunConfig
from dna_entropy.readers import detect
from dna_entropy.readers.input import Contig, _assert_unique_contig_names, _safe_contig_name, load_input
from dna_entropy.validation.validators import ValidationError

THREE_RECORD_FASTA = (
    ">record_one first locus\n"
    "ACGTACGTACGTACGTACGTACGTACGTACGT\n"
    ">record_two second locus\n"
    "TTTTGGGGCCCCAAAATTTTGGGGCCCCAAAA\n"
    ">record_three third locus\n"
    "GATCGATCGATCGATCGATCGATCGATCGATC\n"
)


@pytest.fixture
def three_record_fasta(tmp_path: Path) -> Path:
    p = tmp_path / "three.fasta"
    p.write_text(THREE_RECORD_FASTA, encoding="utf-8")
    return p


# --- readers.input: every FASTA record becomes its own Contig -------------------------


def test_load_input_routes_every_fasta_record_to_its_own_contig(three_record_fasta: Path) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta))
    loaded = load_input(cfg)
    assert loaded.source_kind == detect.FASTA
    assert len(loaded.contigs) == 3
    assert [c.name for c in loaded.contigs] == ["three_1", "three_2", "three_3"]
    assert loaded.contigs[0].seq.startswith("ACGT")
    assert loaded.contigs[1].seq.startswith("TTTT")
    assert loaded.contigs[2].seq.startswith("GATC")
    # Original FASTA headers are preserved as provenance (source_id), like GenBank's
    # record_id — but contig NAMES never depend on header text (collision-proof).
    assert loaded.contigs[0].source_id == "record_one first locus"


def test_fasta_contig_names_are_collision_proof_regardless_of_header_text(
    tmp_path: Path,
) -> None:
    # Two records sharing the exact same header must still get distinct contig names.
    p = tmp_path / "dup_headers.fasta"
    p.write_text(">same\nACGTACGTACGTACGT\n>same\nTTTTGGGGCCCCAAAA\n", encoding="utf-8")
    cfg = RunConfig(name="dup", input_path=str(p))
    loaded = load_input(cfg)
    assert len(loaded.contigs) == 2
    assert [c.name for c in loaded.contigs] == ["dup_1", "dup_2"]
    assert loaded.contigs[0].name != loaded.contigs[1].name


# --- issue #350: _safe_contig_name must never produce a name Windows refuses -----------

_RESERVED_DEVICE_NAMES = ["CON", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT9"]


@pytest.mark.parametrize("reserved", _RESERVED_DEVICE_NAMES + [n.lower() for n in _RESERVED_DEVICE_NAMES])
def test_safe_contig_name_disambiguates_a_reserved_windows_device_name(reserved: str) -> None:
    """A single-record input (no numeric suffix) whose id sanitizes to exactly a
    Windows-reserved device name must not come back unmodified -- 'CON.fasta' is exactly
    as forbidden as 'CON' on a real Windows filesystem."""
    name = _safe_contig_name(reserved, 0, 1, out_dir="out")
    assert name.split(".", 1)[0].upper() not in {n.upper() for n in _RESERVED_DEVICE_NAMES}


def test_safe_contig_name_dots_or_spaces_only_falls_back_to_seq() -> None:
    assert _safe_contig_name("...", 0, 1) == "seq"
    assert _safe_contig_name("   ", 0, 1) == "seq"


def test_safe_contig_name_strips_a_trailing_dot_or_space() -> None:
    assert _safe_contig_name("geneA.", 0, 1) == "geneA"
    assert _safe_contig_name("geneA ", 0, 1) == "geneA"


def test_safe_contig_name_caps_length_for_a_realistic_output_directory() -> None:
    long_id = "A" * 300
    name = _safe_contig_name(long_id, 0, 1, out_dir=r"C:\Users\someone\Downloads")
    # <out_dir>/<name><longest writer suffix (.entropy.geneious.gff3, 22 chars)> must
    # stay comfortably under Windows' legacy MAX_PATH (260).
    assert len(r"C:\Users\someone\Downloads" + "\\" + name + ".entropy.geneious.gff3") < 260
    assert len(name) < 300


def test_safe_contig_name_truncation_preserves_the_index_suffix(tmp_path: Path) -> None:
    """Truncating a too-long base must never eat into the '_<n>' suffix that keeps two
    records in the same multi-record file from colliding -- otherwise the length cap
    fix would silently reintroduce the exact collision it exists to prevent."""
    long_id = "B" * 300
    tiny_out_dir = "o"  # forces a very small truncation budget
    name1 = _safe_contig_name(long_id, 0, 2, out_dir=tiny_out_dir)
    name2 = _safe_contig_name(long_id, 1, 2, out_dir=tiny_out_dir)
    assert name1 != name2
    assert name1.endswith("_1")
    assert name2.endswith("_2")


def test_assert_unique_contig_names_raises_on_a_collision() -> None:
    contigs = [Contig(name="dup", seq="ACGT"), Contig(name="dup", seq="TTTT")]
    with pytest.raises(ValidationError, match="collide"):
        _assert_unique_contig_names(contigs)


def test_assert_unique_contig_names_passes_when_all_distinct() -> None:
    contigs = [Contig(name="a", seq="ACGT"), Contig(name="b", seq="TTTT")]
    _assert_unique_contig_names(contigs)  # must not raise


def test_load_input_refuses_a_contig_name_collision_rather_than_overwriting(
    monkeypatch, tmp_path: Path
) -> None:
    """#350 (defense in depth): if _safe_contig_name ever produced the same name for two
    different records in one file -- unreachable today by construction, since the index
    suffix is preserved through truncation, but proven wired here directly -- load_input
    must refuse rather than silently letting one record's output overwrite another's."""
    import dna_entropy.readers.input as input_mod

    p = tmp_path / "two.fasta"
    p.write_text(">a\nACGTACGTACGT\n>b\nTTTTGGGGCCCC\n", encoding="utf-8")
    monkeypatch.setattr(
        input_mod,
        "_safe_contig_name",
        lambda base, index, total, out_dir="out": "always_the_same",
    )
    cfg = RunConfig(name="two", input_path=str(p))
    with pytest.raises(ValidationError, match="collide"):
        load_input(cfg)


# --- pipeline.run: N records -> N contigs -> N-block outputs --------------------------


def test_pipeline_multi_record_fasta_produces_n_contigs(
    three_record_fasta: Path,
    tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    assert result.contigs == 3
    assert result.total_nt == 32 * 3


def test_pipeline_multi_record_fasta_writes_one_fasta_record_per_contig(
    three_record_fasta: Path,
    tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    fasta_path = next(p for p in result.outputs if p.endswith("three.fasta"))
    text = Path(fasta_path).read_text(encoding="utf-8")
    assert text.count(">") == 3
    assert ">three_1" in text and ">three_2" in text and ">three_3" in text


def test_pipeline_multi_record_fasta_bedgraph_has_a_block_per_contig(
    three_record_fasta: Path,
    tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    bg_path = next(p for p in result.outputs if p.endswith("three.entropy.bedgraph"))
    text = Path(bg_path).read_text(encoding="utf-8")
    assert text.count("track type=bedGraph") == 1  # one header covers every block
    assert "three_1\t" in text and "three_2\t" in text and "three_3\t" in text


def test_pipeline_multi_record_fasta_summary_has_a_section_per_record(
    three_record_fasta: Path,
    tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    summary_path = next(p for p in result.outputs if p.endswith("three.summary.txt"))
    text = Path(summary_path).read_text(encoding="utf-8")
    assert "records:            3" in text
    assert "[three_1]" in text and "[three_2]" in text and "[three_3]" in text


def test_pipeline_multi_record_fasta_genbank_bonus_holds_all_records(
    three_record_fasta: Path,
    tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    from Bio import SeqIO

    gb_path = next(p for p in result.outputs if p.endswith("three.gb"))
    recs = list(SeqIO.parse(gb_path, "genbank"))
    assert len(recs) == 3


def test_pipeline_single_record_fasta_still_produces_exactly_the_old_output_set(
    tmp_path: Path,
) -> None:
    """Guards against a multi-record refactor silently changing the single-record shape."""
    p = tmp_path / "single.fasta"
    p.write_text(">only\nACGTACGTACGTACGTACGTACGTACGTACGT\n", encoding="utf-8")
    cfg = RunConfig(name="single", input_path=str(p), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    names = {Path(p_).name for p_ in result.outputs}
    assert names == {
        "single.fasta",
        "single.entropy.bedgraph",
        "single.entropy.geneious.gff3",
        "single.summary.txt",
        "single.gb",
        "single.entropy.tsv",
    }
    assert result.contigs == 1


# --- issue #314: max_total_len bounds the WHOLE input, not each record in isolation ---


def test_load_input_enforces_max_total_len_across_all_fasta_records_not_per_record(
    tmp_path: Path,
) -> None:
    """5 records of 10 nt each = 50 nt total; every record alone is far under a cap of
    25, so a per-record-only check would (wrongly) let this whole file through."""
    text = "".join(f">rec{i}\nACGTACGTAC\n" for i in range(5))
    p = tmp_path / "many.fasta"
    p.write_text(text, encoding="utf-8")
    cfg = RunConfig(name="many", input_path=str(p), max_total_len=25)
    with pytest.raises(ValidationError) as exc:
        load_input(cfg)
    msg = str(exc.value)
    assert "25" in msg  # names the configured whole-input cap
    assert "30" in msg  # names the running total at the point it tipped over (fail-fast:
    # raised on the 3rd record, without needing to read the remaining 2)
    assert "exceed" in msg.lower()


def test_load_input_max_total_len_boundary_is_inclusive_for_fasta(tmp_path: Path) -> None:
    """Sum == cap must pass; sum == cap + 1 must fail (off-by-one guard)."""
    p = tmp_path / "boundary.fasta"
    p.write_text(">a\nACGTACGTAC\n>b\nACGTACGTAC\n", encoding="utf-8")  # 10 + 10 = 20 nt

    cfg_ok = RunConfig(name="ok", input_path=str(p), max_total_len=20)
    loaded = load_input(cfg_ok)
    assert len(loaded.contigs) == 2

    cfg_over = RunConfig(name="over", input_path=str(p), max_total_len=19)
    with pytest.raises(ValidationError):
        load_input(cfg_over)


def test_pipeline_multi_record_fasta_all_values_concatenates_every_contig(
    three_record_fasta: Path,
    tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    assert result.all_values.shape == (32 * 3,)
    assert np.array_equal(result.all_values[:32], result.values)  # values = first contig
