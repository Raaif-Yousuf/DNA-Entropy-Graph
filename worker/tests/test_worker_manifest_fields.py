"""Manifest fields added for issue #704: annotation (GFF3), accession, gc/codons outputs,
analysis.gcWindow and the preset. Each one maps onto a RunConfig field; absent means off."""

from __future__ import annotations

import json

import pytest

from dna_entropy.worker.manifest import JobManifest, ManifestError

BASE = {
    "schema": 1,
    "jobId": "20260918-142233-k7q2vx",
    "inputs": [{"id": "in1", "path": "input/a.fasta", "name": "a"}],
    "store": {"kind": "localdir", "root": "x"},
}


def _m(inputs=None, **top) -> JobManifest:
    d = json.loads(json.dumps(BASE))
    if inputs is not None:
        d["inputs"] = inputs
    d.update(top)
    return JobManifest.parse(json.dumps(d))


def _cfg(m: JobManifest, **kw):
    return m.build_run_config(m.inputs[0], local_input_path="in", local_out_dir="out", **kw)


def _inp(**over) -> list[dict]:
    d = {"id": "in1", "path": "input/a.fasta", "name": "a"}
    d.update(over)
    return [{k: v for k, v in d.items() if v is not None}]


# --- inputs[].annotation (#135) ---------------------------------------------------


def test_annotation_maps_to_annotation_path() -> None:
    m = _m(_inp(annotation="input/a.gff3"))
    assert m.inputs[0].annotation == "input/a.gff3"
    assert _cfg(m, local_annotation_path="L/a.gff3").annotation_path == "L/a.gff3"


def test_annotation_absent_means_none() -> None:
    m = _m()
    assert m.inputs[0].annotation is None
    assert _cfg(m).annotation_path is None


@pytest.mark.parametrize("bad", ["", 5, ["x"], True])
def test_annotation_must_be_a_non_empty_string(bad) -> None:
    with pytest.raises(ManifestError, match="annotation"):
        _m(_inp(annotation=bad))


@pytest.mark.parametrize("informat", ["genbank", "embl", "snapgene"])
def test_annotation_with_a_non_fasta_informat_is_refused(informat) -> None:
    with pytest.raises(ManifestError, match="annotation"):
        _m(_inp(annotation="input/a.gff3", informat=informat))


def test_annotation_declared_but_not_staged_is_refused_at_build() -> None:
    m = _m(_inp(annotation="input/a.gff3"))
    with pytest.raises(ManifestError, match="annotation"):
        _cfg(m)


# --- inputs[].accession / accessionRange (#133) -----------------------------------


def test_accession_input_needs_no_path() -> None:
    m = _m(_inp(path=None, accession="NC_045512.2"))
    spec = m.inputs[0]
    assert spec.accession == "NC_045512.2"
    assert spec.accession_range is None
    assert spec.path == ""


def test_accession_range_parses() -> None:
    m = _m(_inp(path=None, accession="NC_045512.2", accessionRange="100-500"))
    assert m.inputs[0].accession_range == "100-500"


@pytest.mark.parametrize("bad", ["100", "a-b", "500-100", "0-5", "1-2-3", "", 7])
def test_bad_accession_range_is_refused(bad) -> None:
    with pytest.raises(ManifestError, match="accessionRange"):
        _m(_inp(path=None, accession="NC_045512.2", accessionRange=bad))


def test_accession_range_without_accession_is_refused() -> None:
    with pytest.raises(ManifestError, match="accessionRange"):
        _m(_inp(accessionRange="1-5"))


@pytest.mark.parametrize("bad", ["not an accession", "NC_045512.2; rm", "", 5])
def test_a_malformed_accession_is_refused_at_parse(bad) -> None:
    with pytest.raises(ManifestError, match="accession"):
        _m(_inp(path=None, accession=bad))


def test_path_and_accession_together_are_refused() -> None:
    with pytest.raises(ManifestError, match="accession"):
        _m(_inp(accession="NC_045512.2"))


def test_neither_path_nor_accession_is_refused() -> None:
    with pytest.raises(ManifestError, match="path"):
        _m(_inp(path=None))


@pytest.mark.parametrize("informat", ["fasta", "embl", "snapgene", "paste"])
def test_accession_with_a_non_genbank_informat_is_refused(informat) -> None:
    with pytest.raises(ManifestError, match="informat"):
        _m(_inp(path=None, accession="NC_045512.2", informat=informat))


def test_accession_with_annotation_is_refused() -> None:
    with pytest.raises(ManifestError, match="annotation"):
        _m(_inp(path=None, accession="NC_045512.2", annotation="input/a.gff3"))


def test_the_manifest_has_no_api_key_field() -> None:
    from dna_entropy.worker.manifest import InputSpec

    assert not [f for f in InputSpec.__dataclass_fields__ if "key" in f.lower()]


# --- inputs[].informat vocabulary ---------------------------------------------------


@pytest.mark.parametrize("informat", ["auto", "genbank", "fasta", "paste", "embl", "snapgene"])
def test_informat_accepts_the_full_vocabulary(informat) -> None:
    assert _m(_inp(informat=informat)).inputs[0].informat == informat


@pytest.mark.parametrize("bad", ["gb", "EMBL", "snapgen", "", "dna"])
def test_informat_typo_is_refused(bad) -> None:
    with pytest.raises(ManifestError, match="informat"):
        _m(_inp(informat=bad))


# --- outputs gc / codons, analysis.gcWindow (#191) ----------------------------------


def test_gc_and_codons_are_off_when_outputs_is_empty() -> None:
    cfg = _cfg(_m())
    assert cfg.include_gc is False
    assert cfg.include_codons is False


def test_gc_and_codons_are_off_when_outputs_names_others() -> None:
    cfg = _cfg(_m(outputs=["bedgraph", "tsv"]))
    assert (cfg.include_gc, cfg.include_codons) == (False, False)


def test_gc_named_turns_only_gc_on() -> None:
    cfg = _cfg(_m(outputs=["bedgraph", "gc"]))
    assert (cfg.include_gc, cfg.include_codons) == (True, False)


def test_codons_named_turns_only_codons_on() -> None:
    cfg = _cfg(_m(outputs=["bedgraph", "codons"]))
    assert (cfg.include_gc, cfg.include_codons) == (False, True)


def test_gc_window_default_and_mapping() -> None:
    assert _m().analysis.gc_window == 51
    assert _cfg(_m()).gc_window == 51
    m = _m(analysis={"gcWindow": 101})
    assert m.analysis.gc_window == 101
    assert _cfg(m).gc_window == 101


@pytest.mark.parametrize("bad", [50, 0, -3, 1.5, "51", True, 100003])
def test_bad_gc_window_is_refused(bad) -> None:
    with pytest.raises(ManifestError, match="gcWindow"):
        _m(analysis={"gcWindow": bad})


# --- preset (#136) -------------------------------------------------------------------


def test_preset_maps_to_the_run_config() -> None:
    m = _m(preset={"id": "plasmid-qc", "name": "Plasmid QC"})
    assert m.preset is not None
    assert (m.preset.id, m.preset.name) == ("plasmid-qc", "Plasmid QC")
    cfg = _cfg(m)
    assert (cfg.preset_id, cfg.preset_name) == ("plasmid-qc", "Plasmid QC")


def test_preset_absent_is_none() -> None:
    m = _m()
    assert m.preset is None
    assert _cfg(m).preset_id is None


@pytest.mark.parametrize(
    "bad", ["plasmid", [], {"id": "x"}, {"name": "x"}, {"id": "", "name": "x"}, {"id": 1, "name": "x"}]
)
def test_bad_preset_is_refused(bad) -> None:
    with pytest.raises(ManifestError, match="preset"):
        _m(preset=bad)


def test_unknown_fields_are_still_tolerated() -> None:
    m = _m(_inp(futureField=1), futureTop={"a": 1}, preset={"id": "a", "name": "b", "color": "red"})
    assert m.inputs[0].path == "input/a.fasta"


def test_every_checked_in_sample_manifest_parses() -> None:
    from pathlib import Path

    samples = Path(__file__).resolve().parents[2] / "tests" / "contract-fixtures" / "manifest_samples"
    files = sorted(samples.glob("*.json"))
    assert any(f.name == "annotation_accession_gc_preset.json" for f in files)
    for f in files:
        JobManifest.parse(f.read_text(encoding="utf-8"))
