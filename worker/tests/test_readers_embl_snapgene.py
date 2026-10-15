"""Issue #134: EMBL and SnapGene (.dna) input, mapped to the same records/features as GenBank.

The SnapGene fixtures are built here byte by byte (the documented packet layout: type byte,
big-endian uint32 length, data), so no binary blob is committed.
"""

from __future__ import annotations

import struct
from pathlib import Path

import pytest

from dna_entropy.config import RunConfig
from dna_entropy.readers import detect
from dna_entropy.readers.embl import read_embl
from dna_entropy.readers.genbank import GenBankReadError, read_genbank
from dna_entropy.readers.input import load_input
from dna_entropy.readers.snapgene import read_snapgene
from dna_entropy.validation.validators import ValidationError

DATA = Path(__file__).parent / "data"
GB_SEQ = read_genbank(str(DATA / "sample.gb"))[0][0]


def _packet(ptype: int, data: bytes) -> bytes:
    return struct.pack(">BI", ptype, len(data)) + data


def make_snapgene(
    seq: str = GB_SEQ.seq,
    *,
    circular: bool = False,
    features: str | None = None,
    sequence_bytes: bytes | None = None,
) -> bytes:
    if features is None:
        features = (
            '<Features nextValidID="3">'
            '<Feature recentID="0" name="geneA" type="gene" directionality="1">'
            '<Segment range="1-42" type="standard"/>'
            '<Q name="gene"><V text="geneA"/></Q></Feature>'
            '<Feature recentID="1" name="geneB" type="gene" directionality="2">'
            '<Segment range="85-126" type="standard"/>'
            '<Q name="gene"><V text="geneB"/></Q></Feature>'
            '<Feature recentID="2" name="geneA" type="CDS" directionality="1">'
            '<Segment range="1-42" type="standard"/></Feature>'
            "</Features>"
        )
    body = sequence_bytes if sequence_bytes is not None else seq.encode("ascii")
    return (
        _packet(0x09, struct.pack(">8sHHH", b"SnapGene", 1, 15, 15))
        + _packet(0x00, bytes([1 if circular else 0]) + body)
        + _packet(0x0A, features.encode("utf-8"))
    )


def _write(tmp_path: Path, name: str, data: bytes) -> str:
    p = tmp_path / name
    p.write_bytes(data)
    return str(p)


# ---- EMBL -----------------------------------------------------------------------------


def test_embl_matches_the_genbank_export_of_the_same_record() -> None:
    records, notices = read_embl(str(DATA / "sample.embl"))
    assert len(records) == 1
    rec = records[0]
    assert rec.seq.upper() == GB_SEQ.seq.upper()
    assert [(f.begin, f.end, f.strand, f.gene_id) for f in rec.features] == [
        (f.begin, f.end, f.strand, f.gene_id) for f in GB_SEQ.features
    ]
    assert len(rec.features) == 2
    assert rec.circular is False
    assert any("EMBL" in n and "2 gene feature" in n for n in notices)


def test_embl_circular_topology_is_read() -> None:
    rec = read_embl(str(DATA / "circular.embl"))[0][0]
    assert rec.circular is True


def test_embl_multi_record(tmp_path: Path) -> None:
    text = (DATA / "sample.embl").read_text(encoding="utf-8")
    second = text.replace("toy_locus", "toy_two")
    p = tmp_path / "two.embl"
    p.write_text(text + second, encoding="utf-8", newline="\n")
    records, _ = read_embl(str(p))
    # Biopython's EMBL ids are accession.version
    assert [r.record_id.split(".")[0] for r in records] == ["toy_locus", "toy_two"]


def test_embl_empty_file_is_a_named_refusal(tmp_path: Path) -> None:
    p = tmp_path / "empty.embl"
    p.write_text("", encoding="utf-8")
    with pytest.raises(GenBankReadError, match="EMBL"):
        read_embl(str(p))


def test_embl_garbage_is_a_validation_error_not_a_traceback(tmp_path: Path) -> None:
    p = tmp_path / "bad.embl"
    p.write_text("ID   x; SV 1; linear; DNA; STD; UNC; 5 BP.\nFT   gene   ???\nSQ   Sequence\n  zz\n//\n")
    with pytest.raises(ValidationError):
        read_embl(str(p))


def test_embl_non_ascii_letter_in_sequence_is_refused_not_case_folded(tmp_path: Path) -> None:
    """Same class as GenBank #492: 'long s' upper-cases to a valid-looking S."""
    text = (DATA / "sample.embl").read_text(encoding="utf-8").replace("atgcgtacgt", "atgcgſacgt", 1)
    p = tmp_path / "ns.embl"
    p.write_text(text, encoding="utf-8")
    with pytest.raises(GenBankReadError, match="U\\+017F at position 6"):
        read_embl(str(p))


@pytest.mark.parametrize("bad_location", ["1..?", "foo", "0..5", "1.5", "<1..>?"])
def test_embl_feature_with_unparseable_location_is_dropped_not_a_crash(
    tmp_path: Path, bad_location: str
) -> None:
    """MEASURED 2026-10-15: Biopython warns and sets such a feature's location to None; the reader
    keeps every well-formed neighbour (geneB here) and neither raises nor invents a span."""
    text = (DATA / "sample.embl").read_text(encoding="utf-8")
    p = tmp_path / "bad.embl"
    p.write_text(
        text.replace("gene            1..42", f"gene            {bad_location}", 1), encoding="utf-8"
    )
    records, notices = read_embl(str(p))  # Biopython's own warning is turned into a notice (#543)
    assert [(f.begin, f.end, f.gene_id) for f in records[0].features] == [(85, 126, "geneB")]
    assert any("EMBL record 1" in n and "could not be read" in n for n in notices)


def test_embl_unterminated_join_is_a_named_refusal(tmp_path: Path) -> None:
    text = (DATA / "sample.embl").read_text(encoding="utf-8")
    p = tmp_path / "join.embl"
    p.write_text(text.replace("gene            1..42", "gene            join(1..5,", 1), encoding="utf-8")
    with pytest.raises(GenBankReadError, match=r"Could not parse the EMBL file \(record 1\)"):
        read_embl(str(p))


def test_embl_well_formed_file_keeps_both_genes() -> None:
    assert len(read_embl(str(DATA / "sample.embl"))[0][0].features) == 2


def _embl_with_gene_location(location: str, *, circular: bool) -> str:
    text = (DATA / "circular.embl" if circular else DATA / "sample.embl").read_text(encoding="utf-8")
    return text.replace("gene            1..42", f"gene            {location}", 1)


@pytest.mark.parametrize("circular", [True, False])
def test_embl_origin_join_keeps_its_segments_like_genbank(tmp_path: Path, circular: bool) -> None:
    """The GenBank path carries a wrapping join as outer span + segments (gene_summary reads the
    segments). EMBL goes through the same mapper, so it must give the identical feature."""
    p = tmp_path / "w.embl"
    p.write_text(_embl_with_gene_location("join(120..126,1..10)", circular=circular), encoding="utf-8")
    records, notices = read_embl(str(p))
    wrap = records[0].features[0]
    assert (wrap.begin, wrap.end, wrap.segments) == (1, 126, ((1, 10), (120, 126)))
    text = " ".join(notices)
    if circular:
        assert "wraps the origin" in text and "intron" not in text
    else:
        assert "intron" in text and "wraps the origin" not in text


def test_embl_origin_join_summarises_as_a_wrapping_gene_on_a_circular_record(tmp_path: Path) -> None:
    import numpy as np

    from dna_entropy.analysis.gene_summary import summarize_genes

    p = tmp_path / "w.embl"
    p.write_text(_embl_with_gene_location("join(120..126,1..10)", circular=True), encoding="utf-8")
    rec = read_embl(str(p))[0][0]
    row = summarize_genes("c", rec.features, np.ones(126), circular=rec.circular, start=1)[0]
    assert (row.begin, row.end, row.wraps_origin, row.length) == (120, 10, True, 17)


# ---- SnapGene -------------------------------------------------------------------------


def test_snapgene_yields_the_same_genes_as_the_genbank_export(tmp_path: Path) -> None:
    path = _write(tmp_path, "plasmid.dna", make_snapgene())
    records, notices = read_snapgene(path)
    assert len(records) == 1
    rec = records[0]
    assert rec.seq.upper() == GB_SEQ.seq.upper()
    assert len(rec.features) == len(GB_SEQ.features) == 2
    assert [(f.begin, f.end, f.strand) for f in rec.features] == [
        (f.begin, f.end, f.strand) for f in GB_SEQ.features
    ]
    assert rec.circular is False
    assert any("SnapGene" in n and "2 gene feature" in n for n in notices)


def test_snapgene_circular_flag(tmp_path: Path) -> None:
    rec = read_snapgene(_write(tmp_path, "c.dna", make_snapgene(circular=True)))[0][0]
    assert rec.circular is True


def test_snapgene_cds_only_features_count_as_genes(tmp_path: Path) -> None:
    feats = (
        '<Features><Feature name="orf1" type="CDS" directionality="1">'
        '<Segment range="1-30" type="standard"/></Feature></Features>'
    )
    rec = read_snapgene(_write(tmp_path, "cds.dna", make_snapgene(features=feats)))[0][0]
    assert [(f.begin, f.end) for f in rec.features] == [(1, 30)]
    assert rec.features[0].gene_id == "orf1"


_EXPORT_ACTION = "export it from SnapGene as GenBank"


def test_snapgene_not_a_snapgene_file_is_refused(tmp_path: Path) -> None:
    with pytest.raises(GenBankReadError, match="does not start with SnapGene's file header") as exc:
        read_snapgene(_write(tmp_path, "x.dna", b"this is just text, not a plasmid map"))
    assert _EXPORT_ACTION in str(exc.value)


def test_snapgene_truncated_file_is_refused(tmp_path: Path) -> None:
    with pytest.raises(GenBankReadError, match="cut off") as exc:
        read_snapgene(_write(tmp_path, "t.dna", make_snapgene()[:40]))
    assert _EXPORT_ACTION in str(exc.value)


def test_snapgene_empty_file_is_refused(tmp_path: Path) -> None:
    with pytest.raises(GenBankReadError, match="does not start with SnapGene's file header"):
        read_snapgene(_write(tmp_path, "e.dna", b""))


def test_snapgene_non_ascii_sequence_byte_is_refused(tmp_path: Path) -> None:
    data = make_snapgene(sequence_bytes=b"ACGT\xc5\xbfACGT")
    with pytest.raises(GenBankReadError, match="non-ASCII byte.*position 5"):
        read_snapgene(_write(tmp_path, "n.dna", data))


def test_snapgene_malformed_feature_xml_is_refused(tmp_path: Path) -> None:
    data = make_snapgene(features="<Features><Feature")
    with pytest.raises(GenBankReadError, match="feature XML is not well formed"):
        read_snapgene(_write(tmp_path, "m.dna", data))


def test_snapgene_no_sequence_is_refused(tmp_path: Path) -> None:
    data = _packet(0x09, struct.pack(">8sHHH", b"SnapGene", 1, 15, 15))
    with pytest.raises(GenBankReadError, match="has no DNA sequence"):
        read_snapgene(_write(tmp_path, "ns.dna", data))


# ---- SnapGene hostile XML (#134 cold review) --------------------------------------------

_BILLION_LAUGHS = (
    '<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol0 "lol">'
    + "".join(f'<!ENTITY lol{i} "' + f"&lol{i - 1};" * 10 + '">' for i in range(1, 10))
    + "]><Features>&lol9;</Features>"
)


@pytest.mark.parametrize(
    "xml",
    [
        _BILLION_LAUGHS,
        '<!DOCTYPE Features [<!ENTITY a "x">]><Features>&a;</Features>',
        "<!doctype Features><Features/>",  # a harmless DOCTYPE is refused too: SnapGene never writes one
        '<Features><!ENTITY a "x"></Features>',
    ],
)
def test_snapgene_doctype_or_entity_is_refused_before_biopython_sees_it(tmp_path: Path, xml: str) -> None:
    import time

    path = _write(tmp_path, "h.dna", make_snapgene(features=xml))
    t0 = time.perf_counter()
    with pytest.raises(GenBankReadError, match="DOCTYPE or ENTITY declaration") as exc:
        read_snapgene(path)
    assert time.perf_counter() - t0 < 2.0
    assert _EXPORT_ACTION in str(exc.value)


def test_snapgene_doctype_in_a_non_feature_packet_is_refused_too(tmp_path: Path) -> None:
    """Biopython also parses the primers/notes/properties packets as XML, so every non-DNA packet is
    scanned, not only the features one."""
    data = make_snapgene() + _packet(0x05, b'<!DOCTYPE Primers [<!ENTITY a "x">]><Primers/>')
    with pytest.raises(GenBankReadError, match="DOCTYPE or ENTITY declaration"):
        read_snapgene(_write(tmp_path, "h2.dna", data))


def test_snapgene_xml_packet_over_the_cap_is_refused(tmp_path: Path) -> None:
    from dna_entropy.readers import snapgene

    filler = b" " * (snapgene._MAX_XML_PACKET_BYTES + 1)
    data = make_snapgene() + _packet(0x0A, b"<Features>" + filler + b"</Features>")
    with pytest.raises(GenBankReadError, match="larger than .* MiB"):
        read_snapgene(_write(tmp_path, "big.dna", data))


def test_snapgene_xml_packet_just_under_the_cap_is_read(tmp_path: Path) -> None:
    from dna_entropy.readers import snapgene

    pad = b" " * (snapgene._MAX_XML_PACKET_BYTES - 200)
    data = make_snapgene(features="<Features>" + pad.decode() + "</Features>")
    rec = read_snapgene(_write(tmp_path, "ok.dna", data))[0][0]
    assert rec.features == []


# ---- origin-wrapping features ------------------------------------------------------------

_WRAP = (
    '<Features><Feature name="wrapA" type="gene" directionality="1">'
    '<Segment range="120-10" type="standard"/></Feature></Features>'
)


def test_snapgene_origin_wrap_on_a_circular_plasmid_keeps_segments_and_says_wraps(tmp_path: Path) -> None:
    records, notices = read_snapgene(_write(tmp_path, "w.dna", make_snapgene(circular=True, features=_WRAP)))
    wrap = records[0].features[0]
    assert (wrap.begin, wrap.end, wrap.segments) == (1, 126, ((1, 10), (120, 126)))
    text = " ".join(notices)
    assert "wraps the origin" in text and "120..126" in text and "intron" not in text


def test_snapgene_origin_wrap_summarises_as_a_wrapping_gene(tmp_path: Path) -> None:
    import numpy as np

    from dna_entropy.analysis.gene_summary import summarize_genes

    rec = read_snapgene(_write(tmp_path, "w.dna", make_snapgene(circular=True, features=_WRAP)))[0][0]
    row = summarize_genes("c", rec.features, np.ones(126), circular=rec.circular, start=1)[0]
    assert (row.begin, row.end, row.wraps_origin, row.length) == (120, 10, True, 17)


def test_snapgene_origin_wrap_minus_strand(tmp_path: Path) -> None:
    feats = _WRAP.replace('directionality="1"', 'directionality="2"')
    rec = read_snapgene(_write(tmp_path, "m.dna", make_snapgene(circular=True, features=feats)))[0][0]
    f = rec.features[0]
    assert (f.strand, f.segments) == ("-", ((1, 10), (120, 126)))


def test_snapgene_range_that_wraps_on_a_linear_molecule_is_a_spliced_join_like_genbank(
    tmp_path: Path,
) -> None:
    records, notices = read_snapgene(_write(tmp_path, "l.dna", make_snapgene(circular=False, features=_WRAP)))
    assert records[0].features[0].segments == ((1, 10), (120, 126))
    assert "intron" in " ".join(notices) and "wraps the origin" not in " ".join(notices)


# ---- detection ------------------------------------------------------------------------


def test_detect_by_extension_and_magic(tmp_path: Path) -> None:
    assert detect.detect_kind(str(DATA / "sample.embl")) == detect.EMBL
    assert detect.detect_kind(_write(tmp_path, "a.dna", make_snapgene())) == detect.SNAPGENE
    # magic wins over a misleading extension, and an unknown extension is still found
    assert detect.detect_kind(_write(tmp_path, "renamed.bin", make_snapgene())) == detect.SNAPGENE
    emb = tmp_path / "noext.txt"
    emb.write_text((DATA / "sample.embl").read_text(encoding="utf-8"), encoding="utf-8")
    assert detect.detect_kind(str(emb)) == detect.EMBL


def test_dot_dna_that_is_not_snapgene_is_not_claimed(tmp_path: Path) -> None:
    p = tmp_path / "plain.dna"
    p.write_text(">x\nACGT\n", encoding="utf-8")
    assert detect.detect_kind(str(p)) == detect.FASTA  # sniffed by content


# ---- load_input / pipeline wiring -----------------------------------------------------


@pytest.mark.parametrize("kind", ["embl", "snapgene"])
def test_load_input_gives_contigs_with_genes_and_genbank_style_source(kind: str, tmp_path: Path) -> None:
    path = (
        str(DATA / "sample.embl")
        if kind == "embl"
        else _write(tmp_path, "p.dna", make_snapgene(circular=True))
    )
    loaded = load_input(RunConfig(name="r", input_path=path, out_dir=str(tmp_path / "o")))
    assert len(loaded.contigs) == 1
    assert len(loaded.contigs[0].features) == 2
    assert loaded.source_kind == detect.GENBANK  # annotated flat file: same output set as GenBank
    assert loaded.input_format == kind  # ...but the REAL format is kept for provenance
    assert loaded.contigs[0].circular is (kind == "snapgene")


def test_load_input_genbank_records_genbank_as_its_input_format(tmp_path: Path) -> None:
    loaded = load_input(RunConfig(name="r", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path / "o")))
    assert (loaded.source_kind, loaded.input_format) == (detect.GENBANK, "genbank")


@pytest.mark.parametrize("kind", ["embl", "snapgene", "genbank"])
def test_provenance_records_the_real_input_format(kind: str, tmp_path: Path) -> None:
    import json

    from dna_entropy import pipeline

    path = {
        "embl": str(DATA / "sample.embl"),
        "genbank": str(DATA / "sample.gb"),
        "snapgene": _write(tmp_path, "p.dna", make_snapgene()),
    }[kind]
    out = tmp_path / "o"
    pipeline.run(RunConfig(name="r", input_path=path, out_dir=str(out), context_length=128))
    prov = json.loads((out / "provenance.json").read_text(encoding="utf-8"))
    assert prov["run"]["input_format"] == kind


def test_pipeline_run_on_snapgene_uses_its_genes_without_prodigal(tmp_path: Path) -> None:
    from dna_entropy import pipeline

    path = _write(tmp_path, "p.dna", make_snapgene())
    cfg = RunConfig(name="r", input_path=path, out_dir=str(tmp_path / "o"), context_length=128)
    result = pipeline.run(cfg)
    assert len(result.genes) == 2
    assert any(Path(o).suffix == ".gb" for o in result.outputs)
