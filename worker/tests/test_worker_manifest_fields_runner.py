"""Issue #704 end to end through run_job: the new manifest fields reach the pipeline and the
outputs (mock predictor, LocalBlobstore, no network: efetch is faked)."""

from __future__ import annotations

import json
import re
from pathlib import Path

import pytest

from dna_entropy.readers import entrez
from dna_entropy.worker.blobstore import LocalBlobstore
from dna_entropy.worker.runner import MANIFEST_PATH, run_job

DATA = Path(__file__).parent / "data"
SEQ = "ACGT" * 40


def _job(store: LocalBlobstore, *, inputs, **over) -> None:
    manifest = {
        "schema": 1,
        "jobId": "20260918-142233-k7q2vx",
        "inputs": inputs,
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "outputs": ["bedgraph"],
        "store": {"kind": "localdir", "root": "unused"},
    }
    manifest.update(over)
    store.write_text(MANIFEST_PATH, json.dumps(manifest))


def _names(store: LocalBlobstore, name: str) -> list[str]:
    return sorted(p.name for p in (store.root / "output" / name).iterdir())


def _provenance(store: LocalBlobstore, name: str) -> dict:
    return json.loads((store.root / "output" / name / "provenance.json").read_text(encoding="utf-8"))


def test_gc_output_and_gc_window_reach_the_outputs(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("input/a.fasta", f">seq\n{SEQ}\n")
    _job(
        store,
        inputs=[{"id": "in1", "path": "input/a.fasta", "name": "a"}],
        outputs=["bedgraph", "gc"],
        analysis={
            "contextLength": 128,
            "window": 256,
            "stride": 128,
            "direction": "forward-only",
            "gcWindow": 11,
        },
    )
    assert run_job(store).status == "done"
    assert "a.gc.bedgraph" in _names(store, "a")
    assert "a.codons.tsv" not in _names(store, "a")


def test_unspecified_outputs_do_not_write_gc_or_codons(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("input/a.fasta", f">seq\n{SEQ}\n")
    _job(store, inputs=[{"id": "in1", "path": "input/a.fasta", "name": "a"}], outputs=[])
    assert run_job(store).status == "done"
    names = _names(store, "a")
    assert not [n for n in names if ".gc." in n or ".codons." in n]


def test_annotation_is_staged_and_replaces_prodigal(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("input/a.fasta", f">seq\n{SEQ}\n")
    store.write_text("input/a.gff3", "##gff-version 3\nseq\tsrc\tgene\t5\t40\t.\t+\t.\tID=g1;Name=mygene\n")
    _job(
        store,
        inputs=[{"id": "in1", "path": "input/a.fasta", "name": "a", "annotation": "input/a.gff3"}],
        outputs=["bedgraph", "genes_gff3", "codons"],
    )
    assert run_job(store).status == "done"
    names = _names(store, "a")
    assert "a.genes.gff3" in names
    gff = (store.root / "output" / "a" / "a.genes.gff3").read_text(encoding="utf-8")
    assert "mygene" in gff
    assert "a.codons.tsv" in names


def test_missing_annotation_object_fails_that_input_only(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("input/a.fasta", f">seq\n{SEQ}\n")
    _job(store, inputs=[{"id": "in1", "path": "input/a.fasta", "name": "a", "annotation": "input/none.gff3"}])
    result = run_job(store)
    assert result.inputs[0].status == "failed"


def test_preset_is_recorded_in_provenance(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("input/a.fasta", f">seq\n{SEQ}\n")
    _job(
        store,
        inputs=[{"id": "in1", "path": "input/a.fasta", "name": "a"}],
        preset={"id": "plasmid-qc", "name": "Plasmid QC"},
    )
    assert run_job(store).status == "done"
    assert _provenance(store, "a")["run"]["preset"] == {"id": "plasmid-qc", "name": "Plasmid QC"}


def test_no_preset_means_no_preset_key(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("input/a.fasta", f">seq\n{SEQ}\n")
    _job(store, inputs=[{"id": "in1", "path": "input/a.fasta", "name": "a"}])
    run_job(store)
    assert "preset" not in _provenance(store, "a")["run"]


@pytest.fixture
def fake_efetch(monkeypatch, tmp_path):
    calls: list[str] = []
    text = (DATA / "sample.gb").read_text(encoding="utf-8")
    body = re.sub(r"(?m)^VERSION .*$", "VERSION     NC_045512.2", text, count=1).encode("utf-8")

    def opener(url: str) -> bytes:
        calls.append(url)
        return body

    monkeypatch.setattr(entrez, "_urlopen_bytes", opener)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "cache"))
    monkeypatch.setattr(entrez, "_last_request", None)
    return calls


def test_accession_input_is_fetched_on_the_worker_and_recorded(tmp_path: Path, fake_efetch) -> None:
    store = LocalBlobstore(tmp_path)
    _job(store, inputs=[{"id": "in1", "name": "cov", "accession": "NC_045512.2"}])
    result = run_job(store)
    assert result.status == "done", result
    assert result.inputs[0].status == "done"
    assert fake_efetch, "no efetch request was made: the accession was wired to nothing"
    assert "api_key" not in fake_efetch[0]
    src = _provenance(store, "cov")["input_source"]
    assert src["accession"] == "NC_045512.2"
    assert _provenance(store, "cov")["run"]["input_format"] == "genbank"


def test_accession_range_is_sent_as_seq_start_stop(tmp_path: Path, fake_efetch) -> None:
    store = LocalBlobstore(tmp_path)
    _job(store, inputs=[{"id": "in1", "name": "cov", "accession": "NC_045512.2", "accessionRange": "10-90"}])
    run_job(store)
    assert "seq_start=10" in fake_efetch[0] and "seq_stop=90" in fake_efetch[0]


def test_a_failed_fetch_fails_that_input_with_the_fetch_message(tmp_path: Path, monkeypatch) -> None:
    def boom(url: str) -> bytes:
        raise OSError("offline")

    monkeypatch.setattr(entrez, "_urlopen_bytes", boom)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "cache"))
    monkeypatch.setattr(entrez, "_last_request", None)
    monkeypatch.setattr(entrez.time, "sleep", lambda s: None)
    store = LocalBlobstore(tmp_path)
    _job(store, inputs=[{"id": "in1", "name": "cov", "accession": "NC_045512.2"}])
    result = run_job(store)
    assert result.inputs[0].status == "failed"
