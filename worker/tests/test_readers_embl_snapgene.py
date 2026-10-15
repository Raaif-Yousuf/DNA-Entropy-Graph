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


def test_embl_feature_with_unparseable_location_does_not_crash(tmp_path: Path) -> None:
    text = (DATA / "sample.embl").read_text(encoding="utf-8")
    p = tmp_path / "ok.embl"
    p.write_text(text, encoding="utf-8")
    assert len(read_embl(str(p))[0][0].features) == 2  # neighbour: well formed stays clean


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


def test_snapgene_not_a_snapgene_file_is_refused(tmp_path: Path) -> None:
    with pytest.raises(GenBankReadError, match="SnapGene"):
        read_snapgene(_write(tmp_path, "x.dna", b"this is just text, not a plasmid map"))


def test_snapgene_truncated_file_is_refused(tmp_path: Path) -> None:
    with pytest.raises(GenBankReadError, match="SnapGene"):
        read_snapgene(_write(tmp_path, "t.dna", make_snapgene()[:40]))


def test_snapgene_empty_file_is_refused(tmp_path: Path) -> None:
    with pytest.raises(GenBankReadError, match="SnapGene"):
        read_snapgene(_write(tmp_path, "e.dna", b""))


def test_snapgene_non_ascii_sequence_byte_is_refused(tmp_path: Path) -> None:
    data = make_snapgene(sequence_bytes=b"ACGT\xc5\xbfACGT")
    with pytest.raises(GenBankReadError, match="SnapGene"):
        read_snapgene(_write(tmp_path, "n.dna", data))


def test_snapgene_malformed_feature_xml_is_refused(tmp_path: Path) -> None:
    data = make_snapgene(features="<Features><Feature")
    with pytest.raises(ValidationError):
        read_snapgene(_write(tmp_path, "m.dna", data))


def test_snapgene_no_sequence_is_refused(tmp_path: Path) -> None:
    data = _packet(0x09, struct.pack(">8sHHH", b"SnapGene", 1, 15, 15))
    with pytest.raises(GenBankReadError):
        read_snapgene(_write(tmp_path, "ns.dna", data))


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
    assert loaded.contigs[0].circular is (kind == "snapgene")


def test_pipeline_run_on_snapgene_uses_its_genes_without_prodigal(tmp_path: Path) -> None:
    from dna_entropy import pipeline

    path = _write(tmp_path, "p.dna", make_snapgene())
    cfg = RunConfig(name="r", input_path=path, out_dir=str(tmp_path / "o"), context_length=128)
    result = pipeline.run(cfg)
    assert len(result.genes) == 2
    assert any(Path(o).suffix == ".gb" for o in result.outputs)
