"""Every file reader accepts a pathlib.Path as well as a str (#1029).

Runtime always worked (Path(path), open(path)); the point is the declared type: pyright (the
scripts/check_pyright.py gate) flags a Path argument when the signature says ``str``.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from dna_entropy.readers.detect import detect_kind
from dna_entropy.readers.embl import read_embl
from dna_entropy.readers.encoding import read_text
from dna_entropy.readers.fasta import read_fasta
from dna_entropy.readers.genbank import GenBankReadError, read_genbank
from dna_entropy.readers.gff3 import read_gff3
from dna_entropy.readers.snapgene import read_snapgene

GB = "LOCUS       T1   8 bp    DNA     linear   UNA 01-JAN-2000\nORIGIN\n        1 acgtacgt\n//\n"
EMBL = "ID   T1; SV 1; linear; genomic DNA; STD; UNC; 8 BP.\nSQ   Sequence 8 BP;\n     acgtacgt     8\n//\n"


def _write(path: Path, text: str) -> Path:
    path.write_text(text, encoding="utf-8", newline="\n")
    return path


def test_the_text_readers_accept_a_path(tmp_path: Path) -> None:
    gb = _write(tmp_path / "a.gb", GB)
    records, _ = read_genbank(gb)
    assert [str(r.seq) for r in records] == ["ACGTACGT"]
    assert read_genbank(gb)[0][0].seq == read_genbank(str(gb))[0][0].seq
    fa = _write(tmp_path / "a.fa", ">x\nACGT\n")
    assert len(read_fasta(fa)[0]) == 1
    embl = _write(tmp_path / "a.embl", EMBL)
    assert len(read_embl(embl)[0]) == 1
    assert read_text(fa).startswith(">x")
    gff = _write(tmp_path / "a.gff3", "##gff-version 3\nseq1\tsrc\tgene\t1\t6\t.\t+\t.\tID=g1;Name=g1\n")
    assert list(read_gff3(gff)[0]) == ["seq1"]


def test_detect_kind_accepts_a_path(tmp_path: Path) -> None:
    assert detect_kind(_write(tmp_path / "a.gb", GB)) == "genbank"
    assert detect_kind(_write(tmp_path / "a.fa", ">x\nACGT\n")) == "fasta"


def test_read_snapgene_accepts_a_path_and_refuses_a_non_snapgene_file_in_its_own_words(
    tmp_path: Path,
) -> None:
    bad = _write(tmp_path / "a.dna", "not snapgene")
    # The reader's own class, not any exception (a TypeError from an unsupported Path must not pass).
    with pytest.raises(GenBankReadError) as err:
        read_snapgene(bad)
    # The refusal names the cause; it deliberately omits the file name (docs/threat_model.md).
    assert "SnapGene's file header" in str(err.value)
    assert "a.dna" not in str(err.value)


def test_read_snapgene_names_a_missing_file_path_in_its_own_words(tmp_path: Path) -> None:
    with pytest.raises(GenBankReadError) as err:
        read_snapgene(tmp_path / "missing.dna")
    assert "Could not open the SnapGene file" in str(err.value)
