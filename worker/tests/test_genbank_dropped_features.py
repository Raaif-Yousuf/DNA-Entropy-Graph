"""Issue #543: a feature whose location Biopython cannot parse used to vanish with no message.

Biopython sets such a feature's location to None (and only emits a warning); the reader skipped
it silently, so a gene the user annotated disappeared from every exported track. GenBank and EMBL
share one mapper (``genbank.records_from_parsed``) and so share the notice.

MEASURED 2026-10-15 (Biopython 1.88), the bad-location shapes split in two:
- ``LocationParserError`` -> a warning and ``location = None`` -> covered by the notice: ``abc``,
  ``0..42``, ``-1``, ``1..?``, ``?..5``, ``1..``, ``<<1..5``, ``gap(10)``, ``1.5..9``, and ``42..1``
  / ``5..3`` / ``complement(42..1)`` on a record that is NOT circular. On a circular record
  ``42..1`` is a valid origin-wrapping join, so nothing is dropped.
- a plain ``ValueError`` (``join()``, ``order()``, ``complement(1..3)..5``, ``99999999999999999999..5``,
  an unterminated ``join(1..5,``) or a bare ``AssertionError`` (``1..5,9..12``, ``1..3)``) -> the whole
  file is refused with a named action (``GenBankReadError``). Biopython cannot resume a record
  after these, so the notice cannot cover them.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from Bio import BiopythonParserWarning

from dna_entropy import pipeline
from dna_entropy.config import RunConfig
from dna_entropy.readers.embl import read_embl
from dna_entropy.readers.genbank import GenBankReadError, read_genbank
from dna_entropy.readers.snapgene import read_snapgene

DATA = Path(__file__).parent / "data"
BAD = str(DATA / "malformed" / "genbank_unparseable_location.gb")
SAMPLE = DATA / "sample.gb"
SAMPLE_EMBL = DATA / "sample.embl"
CIRCULAR_EMBL = DATA / "circular.embl"
GENE_LINE = "     gene            1..42\n"


def _dropped_notices(notices: list[str]) -> list[str]:
    return [n for n in notices if "could not be read" in n]


def _gb_with_first_gene_at(tmp_path: Path, location: str, *, circular: bool = False) -> str:
    text = SAMPLE.read_text(encoding="utf-8").replace(GENE_LINE, f"     gene            {location}\n", 1)
    if circular:
        text = text.replace("DNA              UNK", "DNA     circular UNK", 1)
    p = tmp_path / "x.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    return str(p)


def _embl_with_first_gene_at(tmp_path: Path, location: str, *, circular: bool = False) -> str:
    src = CIRCULAR_EMBL if circular else SAMPLE_EMBL
    text = src.read_text(encoding="utf-8").replace("gene            1..42", f"gene            {location}", 1)
    p = tmp_path / "x.embl"
    p.write_text(text, encoding="utf-8", newline="\n")
    return str(p)


def test_unparseable_locations_produce_one_counted_notice_naming_key_and_location_text() -> None:
    records, notices = read_genbank(BAD)
    (notice,) = _dropped_notices(notices)
    assert "GenBank record 1" in notice
    # Only a feature that would have become a gene is counted: the gene 'abc' is, and the CDS
    # '0..42' is NOT, because this record has gene features so its CDS features are never genes.
    assert "1 gene feature(s) were dropped" in notice
    assert "gene 'abc'" in notice
    assert "0..42" not in notice
    assert "1..42" in notice  # names the action: what a good location looks like
    # the readable gene survives; geneA's CDS is '0..42' (also unreadable), so no stand-in exists (#694)
    assert [f.gene_id for f in records[0].features] == ["geneB"]


def test_the_notice_calls_it_location_text_not_raw() -> None:
    (notice,) = _dropped_notices(read_genbank(BAD)[1])
    assert "location text" in notice
    assert "raw" not in notice.lower()


def test_cds_features_are_counted_when_the_record_has_no_gene_features(tmp_path: Path) -> None:
    text = SAMPLE.read_text(encoding="utf-8")
    # keep only CDS features: turn both gene features into misc_feature, break the CDS location
    text = text.replace("     gene            1..42\n", "     misc_feature    1..42\n", 1)
    text = text.replace("     gene            complement(85..126)\n", "     misc_feature    85..126\n", 1)
    text = text.replace("     CDS             1..42\n", "     CDS             abc\n", 1)
    p = tmp_path / "cds.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    records, notices = read_genbank(str(p))
    (notice,) = _dropped_notices(notices)
    assert "1 CDS feature(s) were dropped" in notice and "CDS 'abc'" in notice
    assert records[0].features == []


def test_a_dropped_cds_is_not_counted_when_a_readable_gene_exists(tmp_path: Path) -> None:
    text = SAMPLE.read_text(encoding="utf-8").replace(
        "     CDS             1..42\n", "     CDS             abc\n", 1
    )
    p = tmp_path / "cdsbad.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    records, notices = read_genbank(str(p))
    assert _dropped_notices(notices) == []  # the CDS would never have become a gene here
    assert len(records[0].features) == 2


def test_well_formed_features_produce_no_such_notice() -> None:
    assert _dropped_notices(read_genbank(str(SAMPLE))[1]) == []
    assert _dropped_notices(read_embl(str(SAMPLE_EMBL))[1]) == []


COVERED = ["abc", "0..42", "42..1", "1..?", "?..5", "1..", "-1", "<<1..5", "gap(10)", "1.5..9", "5..3"]


@pytest.mark.parametrize("location", COVERED)
def test_each_covered_shape_is_reported_on_a_linear_genbank_record(location: str, tmp_path: Path) -> None:
    records, notices = read_genbank(_gb_with_first_gene_at(tmp_path, location))
    (notice,) = _dropped_notices(notices)
    assert f"gene {location!r}" in notice
    assert [f.gene_id for f in records[0].features] == ["geneB", "geneA"]  # geneA via its CDS (#694)


@pytest.mark.parametrize("location", COVERED)
def test_each_covered_shape_is_reported_on_a_linear_embl_record(location: str, tmp_path: Path) -> None:
    records, notices = read_embl(_embl_with_first_gene_at(tmp_path, location))
    (notice,) = _dropped_notices(notices)
    assert "EMBL record 1" in notice and f"gene {location!r}" in notice
    assert [f.gene_id for f in records[0].features] == ["geneB", "geneA"]  # geneA via its CDS (#694)


@pytest.mark.parametrize("location", ["42..1", "5..3", "complement(42..1)"])
def test_a_reversed_range_on_a_circular_record_is_an_origin_wrap_not_a_drop(
    location: str, tmp_path: Path
) -> None:
    """MEASURED 2026-10-15: on a circular record Biopython reads 42..1 as a join across the origin,
    so nothing is dropped and no dropped-feature notice is made."""
    for records, notices in (
        read_genbank(_gb_with_first_gene_at(tmp_path, location, circular=True)),
        read_embl(_embl_with_first_gene_at(tmp_path, location, circular=True)),
    ):
        assert _dropped_notices(notices) == []
        assert len(records[0].features) == 2
        assert records[0].features[0].segments  # kept as a wrapping/compound feature


@pytest.mark.parametrize(
    "location",
    [
        "join()",
        "order()",
        "complement(1..3)..5",
        "99999999999999999999..5",
        "join(1..5,",
        "1..5,9..12",
        "1..3)",
    ],
)
@pytest.mark.parametrize("circular", [False, True])
def test_a_plain_valueerror_shape_refuses_the_whole_file_with_a_named_action(
    location: str, circular: bool, tmp_path: Path
) -> None:
    """The notice cannot cover these (Biopython raises out of the record); the refusal is the
    reader's own error naming what to check, never a raw traceback."""
    with pytest.raises(GenBankReadError, match=r"Could not parse the GenBank file \(record 1\)") as exc:
        read_genbank(_gb_with_first_gene_at(tmp_path, location, circular=circular))
    assert "Check the file was not truncated or hand-edited" in str(exc.value)


def test_one_notice_per_record_each_with_its_own_index(tmp_path: Path) -> None:
    text = SAMPLE.read_text(encoding="utf-8").replace(GENE_LINE, "     gene            abc\n", 1)
    p = tmp_path / "two.gb"
    p.write_text(text + text, encoding="utf-8", newline="\n")
    _, notices = read_genbank(str(p))
    found = _dropped_notices(notices)
    assert len(found) == 2
    assert "GenBank record 1" in found[0] and "GenBank record 2" in found[1]


def test_many_dropped_features_are_counted_not_listed_in_full(tmp_path: Path) -> None:
    extra = "".join(f'     gene            bad{i}\n                     /gene="g{i}"\n' for i in range(8))
    text = SAMPLE.read_text(encoding="utf-8").replace(
        "FEATURES             Location/Qualifiers\n", "FEATURES             Location/Qualifiers\n" + extra, 1
    )
    p = tmp_path / "many.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    (notice,) = _dropped_notices(read_genbank(str(p))[1])
    assert "8 gene feature(s)" in notice
    assert "and 5 more" in notice
    assert "bad7" not in notice


def test_location_text_is_ascii_and_truncated(tmp_path: Path) -> None:
    """Hard Rule 5: the notice reaches the console, so a non-ASCII or very long location text is
    shown as ascii() and cut, never repr'd verbatim."""
    long_text = "x" * 200
    for location in ("éα..5", long_text):
        _, notices = read_genbank(_gb_with_first_gene_at(tmp_path, location))
        (notice,) = _dropped_notices(notices)
        assert notice.isascii()
        assert long_text not in notice
        assert len(notice) < 600
    (notice,) = _dropped_notices(read_genbank(_gb_with_first_gene_at(tmp_path, "éα..5"))[1])
    assert "\\xe9\\u03b1..5" in notice


def test_key_only_fallback_when_the_warnings_do_not_match_the_features(tmp_path: Path, monkeypatch) -> None:
    """If the count of Biopython warnings does not match the count of dropped features the texts
    cannot be paired safely, so the notice names the feature key alone rather than a wrong text."""
    from dna_entropy.readers import genbank

    monkeypatch.setattr(genbank, "_location_text_of_warning", lambda message: None)
    # every warning is now treated as "another warning" and re-emitted, so no texts were recorded
    with pytest.warns(BiopythonParserWarning):
        _, notices = read_genbank(_gb_with_first_gene_at(tmp_path, "abc"))
    (notice,) = _dropped_notices(notices)
    assert "(gene)" in notice
    assert "abc" not in notice


def test_unrelated_biopython_warnings_are_still_raised(tmp_path: Path) -> None:
    """The reader records warnings to find dropped locations; it must not swallow the rest
    (a LOCUS length that disagrees with the sequence is Biopython's own warning)."""
    text = SAMPLE.read_text(encoding="utf-8").replace("126 bp", "999 bp", 1)
    p = tmp_path / "len.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    with pytest.warns(BiopythonParserWarning, match="Expected sequence length 999"):
        read_genbank(str(p))


def test_the_notice_reaches_the_run_result(tmp_path: Path) -> None:
    cfg = RunConfig(name="r", input_path=BAD, out_dir=str(tmp_path), context_length=128)
    result = pipeline.run(cfg)
    assert any("could not be read" in n for n in result.notices)
    assert len(result.genes) == 1


def test_snapgene_has_no_dropped_feature_notice_a_bad_range_refuses_the_file(tmp_path: Path) -> None:
    """SnapGene's parser builds locations itself and raises on a bad range instead of returning
    None, so there is nothing to count: the file is refused with the reader's own named action."""
    import struct

    def packet(ptype: int, data: bytes) -> bytes:
        return struct.pack(">BI", ptype, len(data)) + data

    feats = (
        '<Features><Feature name="g" type="gene"><Segment range="abc" type="standard"/></Feature></Features>'
    )
    raw = (
        packet(0x09, struct.pack(">8sHHH", b"SnapGene", 1, 15, 15))
        + packet(0x00, b"\x00" + b"ACGT" * 30)
        + packet(0x0A, feats.encode("utf-8"))
    )
    p = tmp_path / "b.dna"
    p.write_bytes(raw)
    with pytest.raises(GenBankReadError, match="Could not read the SnapGene file") as exc:
        read_snapgene(str(p))
    assert "export it from SnapGene as GenBank" in str(exc.value)


# ---- issue #694: CDS fallback per gene id, and accurate notices for between-base / overflow ----


def _gb_with_gene_at(tmp_path: Path, location: str) -> str:
    return _gb_with_first_gene_at(tmp_path, location)


def test_a_dropped_gene_falls_back_to_the_cds_with_the_same_gene_id(tmp_path: Path) -> None:
    """geneA's gene feature is unreadable; its CDS (same /gene) is readable, so geneA survives."""
    records, notices = read_genbank(_gb_with_gene_at(tmp_path, "abc"))
    assert sorted(f.gene_id for f in records[0].features) == ["geneA", "geneB"]
    cds = next(f for f in records[0].features if f.gene_id == "geneA")
    assert (cds.begin, cds.end) == (1, 42)
    assert any("geneA" in n and "CDS" in n and "instead" in n for n in notices)


def test_the_fallback_is_per_gene_id_not_for_every_cds(tmp_path: Path) -> None:
    """A CDS whose gene feature was read fine is not added a second time."""
    records, _ = read_genbank(_gb_with_gene_at(tmp_path, "abc"))
    assert [f.gene_id for f in records[0].features].count("geneB") == 1


def test_no_fallback_when_the_gene_feature_is_fine(tmp_path: Path) -> None:
    records, notices = read_genbank(str(SAMPLE))
    assert sorted(f.gene_id for f in records[0].features) == ["geneA", "geneB"]
    assert not any("instead" in n for n in notices)


def test_a_dropped_gene_with_no_matching_cds_is_still_reported_as_dropped(tmp_path: Path) -> None:
    text = SAMPLE.read_text(encoding="utf-8").replace(
        "     CDS             1..42\n", "     CDS             1..20\n", 1
    )
    text = text.replace(
        '                     /gene="geneA"\n                     /product',
        '                     /gene="other"\n                     /product',
        1,
    )
    text = text.replace("     gene            1..42\n", "     gene            abc\n", 1)
    p = tmp_path / "x.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    records, notices = read_genbank(str(p))
    assert [f.gene_id for f in records[0].features] == ["geneB"]
    assert any("could not be read" in n for n in notices)


def test_a_between_bases_location_is_named_as_one(tmp_path: Path) -> None:
    records, notices = read_genbank(_gb_with_gene_at(tmp_path, "1^2"))
    note = next(n for n in notices if "geneA" in n and "between" in n)
    assert "between two bases" in note and "outside" not in note
    assert "1..42" in note  # names the action


def test_an_overflowing_position_is_named_as_too_large_not_as_outside_the_length(tmp_path: Path) -> None:
    records, notices = read_genbank(_gb_with_gene_at(tmp_path, "1..99999999999999999999"))
    note = next(n for n in notices if "too large" in n)
    assert "99999999999999999999" not in note  # no absurd number echoed into a notice
    assert "1..42" in note


def test_an_ordinary_position_past_the_end_still_says_outside_the_length(tmp_path: Path) -> None:
    _, notices = read_genbank(_gb_with_gene_at(tmp_path, "100..200"))
    assert any("outside" in n and "126" in n for n in notices)
