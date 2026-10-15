"""Issue #133: fetch a GenBank record by NCBI accession. No live network in this suite:
every test injects a fake ``opener`` (url -> bytes) and a fake clock/sleep."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from typer.testing import CliRunner

from dna_entropy.cli import app
from dna_entropy.readers import entrez
from dna_entropy.readers.genbank import read_genbank
from dna_entropy.validation.validators import ValidationError

SAMPLE = (Path(__file__).parent / "data" / "sample.gb").read_bytes()


class FakeNcbi:
    def __init__(self, body: bytes = SAMPLE) -> None:
        self.body = body
        self.urls: list[str] = []

    def __call__(self, url: str) -> bytes:
        self.urls.append(url)
        return self.body


@pytest.fixture(autouse=True)
def _fresh_limiter() -> None:
    entrez._reset_rate_limiter()


def _fetch(tmp_path: Path, fake: FakeNcbi, accession="NC_045512.2", **kw):
    sleeps: list[float] = []
    kw.setdefault("sleep", sleeps.append)
    kw.setdefault("clock", lambda: 0.0)
    rec = entrez.fetch_genbank(accession, cache_dir=tmp_path / "cache", opener=fake, **kw)
    return rec, sleeps


def test_fetch_writes_a_readable_genbank_and_records_accession_and_date(tmp_path: Path) -> None:
    fake = FakeNcbi()
    rec, _ = _fetch(tmp_path, fake, today="2026-10-15")
    assert Path(rec.path).is_file()
    records, _ = read_genbank(rec.path)
    assert len(records[0].features) == 2  # the record's genes survive the round trip
    assert rec.accession == "NC_045512.2"
    assert rec.retrieved == "2026-10-15"
    assert rec.from_cache is False
    assert rec.provenance() == {
        "type": "ncbi_entrez",
        "accession": "NC_045512.2",
        "range": None,
        "retrieved": "2026-10-15",
        "from_cache": False,
    }


def test_request_url_names_db_id_and_rettype(tmp_path: Path) -> None:
    fake = FakeNcbi()
    _fetch(tmp_path, fake)
    (url,) = fake.urls
    assert url.startswith("https://eutils.ncbi.nlm.nih.gov/entrez/eutils/efetch.fcgi?")
    assert "db=nuccore" in url and "id=NC_045512.2" in url
    assert "rettype=gb" in url and "retmode=text" in url
    assert "api_key" not in url and "seq_start" not in url


def test_range_and_api_key_go_in_the_url(tmp_path: Path) -> None:
    fake = FakeNcbi()
    rec, _ = _fetch(tmp_path, fake, start=100, stop=500, api_key="KEY123")
    (url,) = fake.urls
    assert "seq_start=100" in url and "seq_stop=500" in url and "api_key=KEY123" in url
    assert rec.provenance()["range"] == [100, 500]
    assert "KEY123" not in str(rec.path)
    assert "KEY123" not in json.dumps(rec.provenance())  # the key is never recorded


def test_second_fetch_is_served_from_cache_without_the_network(tmp_path: Path) -> None:
    fake = FakeNcbi()
    first, _ = _fetch(tmp_path, fake, today="2026-10-15")
    second, _ = _fetch(tmp_path, fake, today="2026-11-01")
    assert len(fake.urls) == 1
    assert second.from_cache is True
    assert second.retrieved == "2026-10-15"  # the ORIGINAL retrieval date, not today's
    assert second.path == first.path


def test_different_range_is_a_different_cache_entry(tmp_path: Path) -> None:
    fake = FakeNcbi()
    _fetch(tmp_path, fake)
    _fetch(tmp_path, fake, start=1, stop=50)
    assert len(fake.urls) == 2


def test_rate_limit_waits_between_requests_without_and_with_a_key(tmp_path: Path) -> None:
    sleeps: list[float] = []
    fake = FakeNcbi()
    kw = dict(cache_dir=tmp_path / "c", opener=fake, sleep=sleeps.append, clock=lambda: 0.0)
    entrez.fetch_genbank("NC_000001.1", **kw)
    entrez.fetch_genbank("NC_000002.1", **kw)  # no time has passed: must wait
    assert sleeps and sleeps[-1] == pytest.approx(entrez.MIN_INTERVAL_NO_KEY)
    sleeps.clear()
    entrez.fetch_genbank("NC_000003.1", api_key="K", **kw)
    assert sleeps and sleeps[-1] == pytest.approx(entrez.MIN_INTERVAL_WITH_KEY)


@pytest.mark.parametrize(
    "bad", ["", "abc", "NC_045512.2&db=x", "../etc/passwd", "NC 045512", "NC_0455\n12.2", "123"]
)
def test_malformed_accession_refused_before_any_request(tmp_path: Path, bad: str) -> None:
    fake = FakeNcbi()
    with pytest.raises(entrez.EntrezError) as ei:
        _fetch(tmp_path, fake, accession=bad)
    assert not fake.urls
    assert isinstance(ei.value, ValidationError)
    assert "accession" in str(ei.value).lower()


@pytest.mark.parametrize("start,stop", [(0, 10), (10, 5), (5, None), (None, 5)])
def test_bad_range_refused(tmp_path: Path, start, stop) -> None:
    with pytest.raises(entrez.EntrezError, match="range"):
        _fetch(tmp_path, FakeNcbi(), start=start, stop=stop)


def test_ncbi_error_body_with_http_200_is_refused_and_not_cached(tmp_path: Path) -> None:
    fake = FakeNcbi(b"Error: Failed to retrieve sequence: NC_000000.9\n")
    with pytest.raises(entrez.EntrezError, match="did not return a GenBank record"):
        _fetch(tmp_path, fake, accession="NC_000000.9")
    assert not list((tmp_path / "cache").glob("*.gb"))


def test_network_failure_names_an_action(tmp_path: Path) -> None:
    def boom(url: str) -> bytes:
        raise OSError("name resolution failed")

    with pytest.raises(entrez.EntrezError) as ei:
        entrez.fetch_genbank(
            "NC_045512.2", cache_dir=tmp_path / "c", opener=boom, sleep=lambda s: None, clock=lambda: 0.0
        )
    assert "internet" in str(ei.value).lower()
    assert str(ei.value).isascii()


def test_cli_accession_runs_pipeline_and_writes_provenance(tmp_path: Path, monkeypatch) -> None:
    fake = FakeNcbi()
    monkeypatch.setattr(entrez, "_urlopen_bytes", fake)
    monkeypatch.setattr(entrez.time, "sleep", lambda s: None)
    result = CliRunner().invoke(
        app,
        [
            "run", "--accession", "NC_045512.2", "--name", "acc", "--out", str(tmp_path / "out"),
            "--cache-dir", str(tmp_path / "cache"), "--context-length", "128",
        ],
    )  # fmt: skip
    assert result.exit_code == 0, result.output
    assert len(fake.urls) == 1
    prov = json.loads((tmp_path / "out" / "acc" / "provenance.json").read_text(encoding="utf-8"))
    assert prov["input_source"]["accession"] == "NC_045512.2"
    assert prov["input_source"]["retrieved"]
    assert "genes: 2" in result.output


def test_cli_accession_and_input_are_mutually_exclusive(tmp_path: Path) -> None:
    result = CliRunner().invoke(
        app, ["run", "--accession", "NC_045512.2", "--input", "x.fa", "--name", "n", "--out", str(tmp_path)]
    )
    assert result.exit_code == 1
    assert "ERROR" in result.output
