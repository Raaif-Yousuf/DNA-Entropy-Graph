"""Issue #283/#47: multi-record FASTA parity with GenBank (process all records).

Design D14 changes the prototype's behaviour: a multi-record FASTA must be treated like a
multi-record GenBank — every record analyzed, one contig each — not just the first.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dna_entropy.config import RunConfig
from dna_entropy.readers import detect
from dna_entropy.readers.input import load_input
from dna_entropy import pipeline

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


# --- pipeline.run: N records -> N contigs -> N-block outputs --------------------------


def test_pipeline_multi_record_fasta_produces_n_contigs(
    three_record_fasta: Path, tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    assert result.contigs == 3
    assert result.total_nt == 32 * 3


def test_pipeline_multi_record_fasta_writes_one_fasta_record_per_contig(
    three_record_fasta: Path, tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    fasta_path = next(p for p in result.outputs if p.endswith("three.fasta"))
    text = Path(fasta_path).read_text(encoding="utf-8")
    assert text.count(">") == 3
    assert ">three_1" in text and ">three_2" in text and ">three_3" in text


def test_pipeline_multi_record_fasta_bedgraph_has_a_block_per_contig(
    three_record_fasta: Path, tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    bg_path = next(p for p in result.outputs if p.endswith("three.entropy.bedgraph"))
    text = Path(bg_path).read_text(encoding="utf-8")
    assert text.count("track type=bedGraph") == 1  # one header covers every block
    assert "three_1\t" in text and "three_2\t" in text and "three_3\t" in text


def test_pipeline_multi_record_fasta_summary_has_a_section_per_record(
    three_record_fasta: Path, tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    summary_path = next(p for p in result.outputs if p.endswith("three.summary.txt"))
    text = Path(summary_path).read_text(encoding="utf-8")
    assert "records:            3" in text
    assert "[three_1]" in text and "[three_2]" in text and "[three_3]" in text


def test_pipeline_multi_record_fasta_genbank_bonus_holds_all_records(
    three_record_fasta: Path, tmp_path: Path,
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


def test_pipeline_multi_record_fasta_all_values_concatenates_every_contig(
    three_record_fasta: Path, tmp_path: Path,
) -> None:
    cfg = RunConfig(name="three", input_path=str(three_record_fasta), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    assert result.all_values.shape == (32 * 3,)
    assert np.array_equal(result.all_values[:32], result.values)  # values = first contig
