"""Tests for input readers and the read+validate pipeline wiring."""

from __future__ import annotations

from pathlib import Path

from dna_entropy.config import RunConfig
from dna_entropy.pipeline import load_and_validate
from dna_entropy.readers import PasteReader, Reader


def test_paste_reader_satisfies_protocol() -> None:
    assert isinstance(PasteReader(), Reader)


def test_paste_reader_reads_file(tmp_path: Path) -> None:
    f = tmp_path / "locus.txt"
    f.write_text("ATGCATGCAT", encoding="utf-8")
    assert PasteReader(str(f)).read() == "ATGCATGCAT"


def test_paste_reader_handles_non_utf8_bytes_without_crashing(tmp_path: Path) -> None:
    """#294: a non-UTF-8 byte must not crash the paste path with UnicodeDecodeError,
    matching readers/fasta.py and readers/detect.py, both of which already tolerate this."""
    f = tmp_path / "latin1.txt"
    f.write_bytes(b"ATGC\xffATGC")  # 0xFF is never valid as a standalone UTF-8 byte
    text = PasteReader(str(f)).read()  # must not raise
    assert "ATGC" in text


def test_paste_reader_handles_non_utf8_stdin_without_crashing(monkeypatch) -> None:
    """#294: the stdin branch must agree with the file branch on encoding robustness."""
    import io

    class FakeStdin:
        buffer = io.BytesIO(b"ATGC\xffATGC")

    monkeypatch.setattr("sys.stdin", FakeStdin())
    text = PasteReader().read()  # must not raise
    assert "ATGC" in text


def test_paste_reader_strips_a_utf8_bom(tmp_path: Path) -> None:
    """#330: a UTF-8 BOM must not survive into the returned text as a literal U+FEFF
    character (which would otherwise fail validate_sequence's alphabet check with a
    confusing "Invalid character '\\ufeff'")."""
    f = tmp_path / "bom.txt"
    f.write_bytes(b"\xef\xbb\xbfATGCATGCAT")
    assert PasteReader(str(f)).read() == "ATGCATGCAT"


def test_paste_reader_decodes_utf16_le_bom(tmp_path: Path) -> None:
    """#330: Windows Notepad's "Unicode" save option produces UTF-16 (little-endian,
    with a BOM) -- a real, unremarkable way a Windows user saves a plain-text file. It
    must decode in full, not turn into a wall of U+FFFD replacement characters."""
    f = tmp_path / "utf16le.txt"
    f.write_bytes("ATGCATGCAT".encode("utf-16"))  # Python's utf-16 codec writes a BOM
    assert PasteReader(str(f)).read() == "ATGCATGCAT"


def test_paste_reader_decodes_utf16_be_bom(tmp_path: Path) -> None:
    f = tmp_path / "utf16be.txt"
    f.write_bytes(b"\xfe\xff" + "ATGCATGCAT".encode("utf-16-be"))
    assert PasteReader(str(f)).read() == "ATGCATGCAT"


def test_pipeline_load_and_validate_from_file(tmp_path: Path) -> None:
    f = tmp_path / "locus.fasta"
    f.write_text(">demo\nATGC ATGC\nATGC", encoding="utf-8")
    result = load_and_validate(RunConfig(input_path=str(f)))
    assert result.seq == "ATGCATGCATGC"
    assert any("header" in n.lower() for n in result.notices)


def test_pipeline_load_and_validate_with_raw_override() -> None:
    # raw provided => no file/stdin read needed
    result = load_and_validate(RunConfig(), raw="atgcatgcat")
    assert result.seq == "ATGCATGCAT"
