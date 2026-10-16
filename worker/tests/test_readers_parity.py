"""Issue #393: replay ``tests/contract-fixtures/readers_parity.json`` against the real readers.

The C# ``FastaLiteTests``/``GenBankLiteTests`` replay the same file against their ports, so a
wording change in ``read_fasta``/``read_genbank`` fails here AND there until the fixture and both
ports agree. The fixture's ``python`` blocks are the worker's own output (the values this test
re-derives); the file is never edited to make a case pass."""

from __future__ import annotations

import json
import warnings
from pathlib import Path

import pytest

from dna_entropy.readers.embl import read_embl
from dna_entropy.readers.fasta import FastaReadError, read_fasta
from dna_entropy.readers.genbank import GenBankReadError, read_genbank

FIXTURE = Path(__file__).resolve().parents[2] / "tests" / "contract-fixtures" / "readers_parity.json"
DATA = json.loads(FIXTURE.read_text(encoding="utf-8"))


def _ids(cases: list[dict]) -> list[str]:
    return [c["id"] for c in cases]


def _write(tmp_path: Path, text: str, suffix: str) -> str:
    p = tmp_path / f"in{suffix}"
    p.write_bytes(text.encode("utf-8"))
    return str(p)


def test_the_fixture_carries_the_cases_the_issue_names() -> None:
    fasta = set(_ids(DATA["fasta_cases"]))
    genbank = set(_ids(DATA["genbank_cases"]))
    assert {
        "multi_record_three",
        "duplicate_ids_different_descriptions",
        "empty_after_header_is_skipped",
    } <= fasta
    assert {"multi_record_two", "truncated_mid_feature_table", "no_locus_line"} <= genbank
    assert any("error" in c["python"] for c in DATA["fasta_cases"] + DATA["genbank_cases"])


@pytest.mark.parametrize("case", DATA["fasta_cases"], ids=_ids(DATA["fasta_cases"]))
def test_read_fasta_matches_the_fixture(case: dict, tmp_path: Path) -> None:
    path = _write(tmp_path, case["text"], ".fasta")
    expected = case["python"]
    if "error" in expected:
        with pytest.raises(FastaReadError) as ei:
            read_fasta(path)
        assert str(ei.value) == expected["error"]
        return
    records, notices = read_fasta(path)
    assert [{"header": r.header, "seq": r.seq} for r in records] == expected["records"]
    assert notices == expected["notices"]


@pytest.mark.parametrize("case", DATA["genbank_cases"], ids=_ids(DATA["genbank_cases"]))
def test_read_genbank_matches_the_fixture(case: dict, tmp_path: Path) -> None:
    path = _write(tmp_path, case["text"], ".gb")
    expected = case["python"]
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")  # Biopython warns about the toy LOCUS lines
        if "error" in expected:
            with pytest.raises(GenBankReadError) as ei:
                read_genbank(path)
            assert str(ei.value) == expected["error"]
            return
        records, notices = read_genbank(path)
    assert [{"seq": r.seq, "gene_count": len(r.features)} for r in records] == expected["records"]
    assert notices == expected["notices"]


@pytest.mark.parametrize("case", DATA["embl_cases"], ids=_ids(DATA["embl_cases"]))
def test_read_embl_matches_the_fixture(case: dict, tmp_path: Path) -> None:
    """Issue #796: the EMBL cases the C# EmblLite replays (dropped-location notice, location refusals)."""
    path = _write(tmp_path, case["text"], ".embl")
    expected = case["python"]
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        if "error" in expected:
            with pytest.raises(GenBankReadError) as ei:
                read_embl(path)
            assert str(ei.value) == expected["error"]
            return
        records, notices = read_embl(path)
    assert [{"seq": r.seq, "gene_count": len(r.features)} for r in records] == expected["records"]
    assert notices == expected["notices"]


def test_every_csharp_divergence_names_a_reason_and_a_tracking_issue() -> None:
    blocks = [
        c["csharp_divergence"] for c in DATA["genbank_cases"] + DATA["embl_cases"] if "csharp_divergence" in c
    ]
    # Zero is the goal state (#689): every remaining block must still say why and name its issue.
    for block in blocks:
        assert block["reason"].strip()
        assert isinstance(block["issue"], int)
        assert "error" in block or "notices" in block


def test_a_wording_change_in_the_skipped_record_notice_would_fail(tmp_path: Path) -> None:
    """The issue's observable, checked directly: the fixture pins the notice text itself."""
    case = next(c for c in DATA["fasta_cases"] if c["id"] == "empty_after_header_is_skipped")
    assert any(
        n.startswith("Skipped FASTA record 2 (14 chars): no sequence lines")
        for n in case["python"]["notices"]
    )
