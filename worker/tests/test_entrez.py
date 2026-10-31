"""Issue #133: fetch a GenBank record by NCBI accession. No live network in this suite:
every test injects a fake ``opener`` (url -> bytes) and a fake clock/sleep. The two tests that
need a real socket (a slow server, an oversized body) talk to a throwaway server on 127.0.0.1."""

from __future__ import annotations

import http.server
import json
import re
import threading
import time
import urllib.error
import urllib.parse
from collections.abc import Iterator
from email.message import Message
from pathlib import Path

import pytest
from typer.testing import CliRunner

from dna_entropy.cli import app
from dna_entropy.readers import entrez
from dna_entropy.readers.genbank import read_genbank
from dna_entropy.validation.validators import ValidationError

SAMPLE = (Path(__file__).parent / "data" / "sample.gb").read_bytes()


def genbank_for(version: str, template: bytes = SAMPLE) -> bytes:
    """The sample record with its VERSION line (the id NCBI resolved) set to ``version``."""
    text = template.decode("utf-8")
    return re.sub(r"(?m)^VERSION .*$", f"VERSION     {version}", text, count=1).encode("utf-8")


class FakeNcbi:
    """Answers every efetch with the sample record whose VERSION is the requested id, or the
    current version ``.2`` when the request had none (what NCBI does for an unversioned id)."""

    def __init__(self, body: bytes | None = None, current_version: str = "2") -> None:
        self.body = body
        self.current_version = current_version
        self.urls: list[str] = []

    def __call__(self, url: str) -> bytes:
        self.urls.append(url)
        if self.body is not None:
            return self.body
        requested = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)["id"][0]
        resolved = requested if "." in requested else f"{requested}.{self.current_version}"
        return genbank_for(resolved)


class Scripted:
    """An opener that raises/returns the scripted outcomes in order."""

    def __init__(self, *outcomes) -> None:
        self.outcomes = list(outcomes)
        self.urls: list[str] = []

    def __call__(self, url: str) -> bytes:
        self.urls.append(url)
        outcome = self.outcomes.pop(0) if len(self.outcomes) > 1 else self.outcomes[0]
        if isinstance(outcome, BaseException):
            raise outcome
        return outcome


@pytest.fixture(autouse=True)
def _fresh_limiter() -> None:
    entrez._reset_rate_limiter()


def _fetch(tmp_path: Path, fake, accession="NC_045512.2", **kw):
    sleeps: list[float] = []
    kw.setdefault("sleep", sleeps.append)
    kw.setdefault("clock", lambda: 0.0)
    rec = entrez.fetch_genbank(accession, cache_dir=tmp_path / "cache", opener=fake, **kw)
    return rec, sleeps


def _http_error(code: int, url: str = "https://x/efetch?api_key=SECRETKEY") -> urllib.error.HTTPError:
    return urllib.error.HTTPError(url, code, "msg", Message(), None)


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
        "requested": "NC_045512.2",
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
    assert "KEY123" not in "".join(p.read_text(encoding="utf-8") for p in (tmp_path / "cache").glob("*.json"))


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


# ---- the RESOLVED versioned accession ----------------------------------------------------


def test_unversioned_request_records_and_caches_the_resolved_version(tmp_path: Path) -> None:
    fake = FakeNcbi()
    rec, _ = _fetch(tmp_path, fake, accession="nc_045512", today="2026-10-15")
    assert rec.accession == "NC_045512.2"  # the version NCBI resolved, from the VERSION line
    assert rec.requested == "NC_045512"
    assert Path(rec.path).name == "NC_045512.2.gb"
    prov = rec.provenance()
    assert (prov["accession"], prov["requested"]) == ("NC_045512.2", "NC_045512")
    meta = json.loads((tmp_path / "cache" / "NC_045512.2.json").read_text(encoding="utf-8"))
    assert meta["accession"] == "NC_045512.2" and meta["requested"] == "NC_045512"


def test_unversioned_request_always_asks_ncbi_so_it_can_never_serve_a_stale_version(tmp_path: Path) -> None:
    fake = FakeNcbi()
    first, _ = _fetch(tmp_path, fake, accession="NC_045512")
    fake.current_version = "3"  # NCBI published a new version in between
    second, _ = _fetch(tmp_path, fake, accession="NC_045512")
    assert len(fake.urls) == 2
    assert (first.accession, second.accession) == ("NC_045512.2", "NC_045512.3")
    assert second.from_cache is False


def test_a_versioned_request_is_served_from_the_entry_an_unversioned_one_left(tmp_path: Path) -> None:
    fake = FakeNcbi()
    _fetch(tmp_path, fake, accession="NC_045512")
    again, _ = _fetch(tmp_path, fake, accession="NC_045512.2")
    assert len(fake.urls) == 1 and again.from_cache is True


def test_a_record_for_a_different_version_than_asked_is_refused_and_not_cached(tmp_path: Path) -> None:
    fake = FakeNcbi(body=genbank_for("NC_000001.1"))
    with pytest.raises(entrez.EntrezError, match="different record than the one you asked for") as exc:
        _fetch(tmp_path, fake, accession="NC_045512.2")
    assert "Check the accession" in str(exc.value)
    assert not list((tmp_path / "cache").glob("*"))


def test_a_record_with_no_version_line_is_refused_and_not_cached(tmp_path: Path) -> None:
    body = re.sub(rb"(?m)^VERSION .*\n", b"", SAMPLE)
    with pytest.raises(entrez.EntrezError, match="no VERSION line"):
        _fetch(tmp_path, FakeNcbi(body=body))
    assert not list((tmp_path / "cache").glob("*"))


def test_rate_limit_waits_between_requests_without_and_with_a_key(tmp_path: Path) -> None:
    sleeps: list[float] = []
    fake = FakeNcbi()

    def fetch(accession: str, api_key: str | None = None) -> None:
        entrez.fetch_genbank(
            accession,
            cache_dir=tmp_path / "c",
            api_key=api_key,
            opener=fake,
            sleep=sleeps.append,
            clock=lambda: 0.0,
        )

    fetch("NC_000001.1")
    fetch("NC_000002.1")  # no time has passed: must wait
    assert sleeps and sleeps[-1] == pytest.approx(entrez.MIN_INTERVAL_NO_KEY)
    sleeps.clear()
    fetch("NC_000003.1", api_key="K")
    assert sleeps and sleeps[-1] == pytest.approx(entrez.MIN_INTERVAL_WITH_KEY)


# ---- the accession pattern ---------------------------------------------------------------


@pytest.mark.parametrize(
    "bad",
    [
        "",
        "abc",
        "NC_045512.2&db=x",
        "../etc/passwd",
        "NC 045512",
        "NC_0455\n12.2",
        "123",
        "NC_٠٤٥٥١٢.2",  # Arabic-Indic digits: \d matches them, ASCII does not
        "NC_045512.٢",  # a non-ASCII digit in the version
        "NC_045512２",  # a full-width digit
        "ſA123456",  # long s: str.upper() folds it into a valid-looking S
        "ıA123456",  # dotless i: upper() gives I
    ],
)
def test_malformed_accession_refused_before_any_request(tmp_path: Path, bad: str) -> None:
    fake = FakeNcbi()
    with pytest.raises(entrez.EntrezError) as ei:
        _fetch(tmp_path, fake, accession=bad)
    assert not fake.urls
    assert isinstance(ei.value, ValidationError)
    assert "accession" in str(ei.value).lower()


def test_the_pattern_itself_is_ascii_only() -> None:
    """validate_request() also checks isascii() first (for the upper() fold); the pattern must not
    depend on that, so the pattern alone is pinned too."""
    assert entrez._ACCESSION.fullmatch("NC_045512.2")
    assert entrez._ACCESSION.fullmatch("NC_٠٤٥٥١٢.2") is None
    assert entrez._ACCESSION.fullmatch("NC_045512.٢") is None


@pytest.mark.parametrize("start,stop", [(0, 10), (10, 5), (5, None), (None, 5)])
def test_bad_range_refused(tmp_path: Path, start, stop) -> None:
    with pytest.raises(entrez.EntrezError, match="range"):
        _fetch(tmp_path, FakeNcbi(), start=start, stop=stop)


# ---- a bad answer ------------------------------------------------------------------------


def test_ncbi_error_body_with_http_200_is_refused_and_not_cached(tmp_path: Path) -> None:
    fake = FakeNcbi(b"Error: Failed to retrieve sequence: NC_000000.9\n")
    with pytest.raises(entrez.EntrezError, match="did not return a GenBank record"):
        _fetch(tmp_path, fake, accession="NC_000000.9")
    assert not list((tmp_path / "cache").glob("*.gb"))


def test_a_body_cut_off_before_its_closing_slashes_is_refused_and_not_cached(tmp_path: Path) -> None:
    cut = SAMPLE.rstrip()[:-2]  # the sample ends with "//"
    assert not cut.rstrip().endswith(b"//")
    with pytest.raises(entrez.EntrezError, match="cut off") as exc:
        _fetch(tmp_path, FakeNcbi(body=cut))
    assert "try again" in str(exc.value).lower()
    assert not list((tmp_path / "cache").glob("*"))


def test_a_complete_body_with_trailing_blank_lines_is_accepted(tmp_path: Path) -> None:
    rec, _ = _fetch(tmp_path, FakeNcbi(body=genbank_for("NC_045512.2") + b"\n\n"))
    assert Path(rec.path).is_file()


# ---- failures: each class has its own message and one action ------------------------------


def _msg(tmp_path: Path, *outcomes, **kw) -> tuple[str, Scripted]:
    fake = Scripted(*outcomes)
    with pytest.raises(entrez.EntrezError) as ei:
        _fetch(tmp_path, fake, **kw)
    return str(ei.value), fake


def test_429_is_retried_once_with_a_backoff_then_succeeds(tmp_path: Path) -> None:
    good = genbank_for("NC_045512.2")
    fake = Scripted(_http_error(429), good)
    rec, sleeps = _fetch(tmp_path, fake)
    assert len(fake.urls) == 2 and Path(rec.path).is_file()
    assert any(s >= 1.0 for s in sleeps)  # waited before the retry


def test_503_is_retried_once_then_succeeds(tmp_path: Path) -> None:
    fake = Scripted(_http_error(503), genbank_for("NC_045512.2"))
    rec, sleeps = _fetch(tmp_path, fake)
    assert len(fake.urls) == 2 and rec.from_cache is False
    assert any(s >= 1.0 for s in sleeps)


def test_429_that_persists_names_the_rate_limit_and_the_api_key(tmp_path: Path) -> None:
    message, fake = _msg(tmp_path, _http_error(429))
    assert len(fake.urls) == 2  # one try plus exactly one retry
    assert "limiting how fast" in message
    assert "--ncbi-api-key" in message and "Wait a minute" in message


def test_5xx_that_persists_names_ncbi_trouble_and_waiting(tmp_path: Path) -> None:
    message, fake = _msg(tmp_path, _http_error(503))
    assert len(fake.urls) == 2
    assert "having trouble (HTTP 503)" in message
    assert "try again in a few minutes" in message.lower()


def test_400_is_not_retried_and_says_to_check_the_accession_and_range(tmp_path: Path) -> None:
    message, fake = _msg(tmp_path, _http_error(400))
    assert len(fake.urls) == 1
    assert "rejected the request (HTTP 400)" in message
    assert "Check the accession and the range" in message


def test_404_is_treated_like_400(tmp_path: Path) -> None:
    message, fake = _msg(tmp_path, _http_error(404))
    assert len(fake.urls) == 1 and "rejected the request (HTTP 404)" in message


def test_403_points_at_the_api_key(tmp_path: Path) -> None:
    message, fake = _msg(tmp_path, _http_error(403), api_key="SECRETKEY")
    assert len(fake.urls) == 1
    assert "refused the request (HTTP 403)" in message
    assert "--ncbi-api-key" in message


@pytest.mark.parametrize(
    "exc",
    [TimeoutError("timed out"), urllib.error.URLError(TimeoutError("timed out"))],
)
def test_a_timeout_is_its_own_class_and_is_not_retried(tmp_path: Path, exc: BaseException) -> None:
    message, fake = _msg(tmp_path, exc)
    assert len(fake.urls) == 1
    assert "did not answer in time" in message
    assert "try again in a few minutes" in message


def test_network_failure_names_an_action(tmp_path: Path) -> None:
    message, fake = _msg(tmp_path, OSError("name resolution failed"))
    assert len(fake.urls) == 1
    assert "internet" in message.lower()
    assert message.isascii()


def test_every_failure_class_has_its_own_message_and_none_leaks_the_key_or_url(tmp_path: Path) -> None:
    outcomes = {
        "429": _http_error(429),
        "503": _http_error(503),
        "400": _http_error(400),
        "403": _http_error(403),
        "timeout": TimeoutError("x"),
        "network": OSError("x"),
    }
    messages = {}
    for label, outcome in outcomes.items():
        entrez._reset_rate_limiter()
        messages[label], _ = _msg(tmp_path, outcome, api_key="SECRETKEY")
    assert len(set(messages.values())) == len(messages)
    for text in messages.values():
        assert "SECRETKEY" not in text and "eutils" not in text and text.isascii()


# ---- a slow or oversized server (real sockets on 127.0.0.1) -------------------------------


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args) -> None:  # keep the test output clean
        pass

    def do_GET(self) -> None:
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.end_headers()
        try:
            if self.path.startswith("/drip"):
                self.wfile.write(b"LOCUS       x\n")
                for _ in range(300):  # one byte every 0.1 s would take 30 s: never finishes in time
                    time.sleep(0.1)
                    self.wfile.write(b"a")
                    self.wfile.flush()
            else:  # /big: far more than the cap, sent at once
                self.wfile.write(b"LOCUS       x\n" + b"a" * 200_000)
        except OSError:
            pass  # the client gave up: that is the point


@pytest.fixture
def local_ncbi(monkeypatch) -> Iterator[str]:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    base = f"http://127.0.0.1:{server.server_address[1]}"
    try:
        yield base
    finally:
        server.shutdown()
        server.server_close()


def test_a_server_that_drips_forever_is_cut_off_by_the_total_deadline(
    tmp_path: Path, local_ncbi: str, monkeypatch
) -> None:
    monkeypatch.setattr(entrez, "EFETCH_URL", f"{local_ncbi}/drip")
    monkeypatch.setattr(entrez, "_TOTAL_DEADLINE_SECONDS", 1.0)
    t0 = time.monotonic()
    with pytest.raises(entrez.EntrezError, match="did not answer in time"):
        entrez.fetch_genbank("NC_045512.2", cache_dir=tmp_path / "c", sleep=lambda s: None)
    assert time.monotonic() - t0 < 6.0  # the drip alone would take 30 s
    assert not list((tmp_path / "c").glob("*"))


def test_a_body_over_the_size_cap_is_refused_with_its_own_action(
    tmp_path: Path, local_ncbi: str, monkeypatch
) -> None:
    monkeypatch.setattr(entrez, "EFETCH_URL", f"{local_ncbi}/big")
    monkeypatch.setattr(entrez, "_MAX_BODY_BYTES", 10_000)
    with pytest.raises(entrez.EntrezError, match="larger than") as exc:
        entrez.fetch_genbank("NC_045512.2", cache_dir=tmp_path / "c", sleep=lambda s: None)
    message = str(exc.value)
    # Issue #948: the app user has no command line, so the first action named is the From base and
    # To base boxes; the CLI flag stays as the command-line route.
    assert "From base" in message
    assert "To base" in message
    # The range is only captured by choosing Fetch record again; Run alone re-sends the same item.
    assert "Fetch record" in message
    assert "--accession-range" in message
    assert message.index("From base") < message.index("--accession-range")
    assert "\u2014" not in message
    assert not list((tmp_path / "c").glob("*"))


def test_the_default_caps_are_finite_and_sane() -> None:
    assert 1 <= entrez._TOTAL_DEADLINE_SECONDS <= 600
    assert 1_000_000 <= entrez._MAX_BODY_BYTES <= 512 * 1024 * 1024


# ---- the CLI -----------------------------------------------------------------------------


def _cli(tmp_path: Path, monkeypatch, *args: str):
    fake = FakeNcbi()
    monkeypatch.setattr(entrez, "_urlopen_bytes", fake)
    monkeypatch.setattr(entrez.time, "sleep", lambda s: None)
    result = CliRunner().invoke(
        app,
        [
            "run",
            "--name",
            "acc",
            "--out",
            str(tmp_path / "out"),
            "--cache-dir",
            str(tmp_path / "cache"),
            *args,
        ],
    )
    return result, fake


def test_cli_accession_runs_pipeline_and_writes_provenance(tmp_path: Path, monkeypatch) -> None:
    result, fake = _cli(tmp_path, monkeypatch, "--accession", "NC_045512.2", "--context-length", "128")
    assert result.exit_code == 0, result.output
    assert len(fake.urls) == 1
    prov = json.loads((tmp_path / "out" / "acc" / "provenance.json").read_text(encoding="utf-8"))
    assert prov["input_source"]["accession"] == "NC_045512.2"
    assert prov["input_source"]["retrieved"]
    assert "genes: 2" in result.output


def test_cli_unversioned_accession_records_the_resolved_version(tmp_path: Path, monkeypatch) -> None:
    result, _ = _cli(tmp_path, monkeypatch, "--accession", "NC_045512", "--context-length", "128")
    assert result.exit_code == 0, result.output
    prov = json.loads((tmp_path / "out" / "acc" / "provenance.json").read_text(encoding="utf-8"))
    assert prov["input_source"]["accession"] == "NC_045512.2"
    assert prov["input_source"]["requested"] == "NC_045512"


def test_cli_accession_and_input_are_mutually_exclusive(tmp_path: Path) -> None:
    result = CliRunner().invoke(
        app, ["run", "--accession", "NC_045512.2", "--input", "x.fa", "--name", "n", "--out", str(tmp_path)]
    )
    assert result.exit_code == 1
    assert "ERROR" in result.output


@pytest.mark.parametrize(
    "bad_args, expect",
    [
        (["--predictor", "bogus"], "bogus"),
        (["--direction", "sideways"], "sideways"),
        (["--region-threshold", "5"], "region threshold"),
        (["--smoothed", "--smoothing-window", "50"], "smoothing window"),
        (["--accession-range", "nonsense"], "range must look like"),
    ],
)
def test_cli_validates_the_other_options_before_it_touches_the_network(
    tmp_path: Path, monkeypatch, bad_args: list[str], expect: str
) -> None:
    result, fake = _cli(tmp_path, monkeypatch, "--accession", "NC_045512.2", *bad_args)
    assert result.exit_code == 1
    assert "ERROR" in result.output and expect in result.output
    assert fake.urls == []  # no request was made for a run that could never start


@pytest.mark.parametrize("informat", ["fasta", "paste"])
def test_cli_refuses_a_conflicting_informat_instead_of_overwriting_it(
    tmp_path: Path, monkeypatch, informat: str
) -> None:
    result, fake = _cli(tmp_path, monkeypatch, "--accession", "NC_045512.2", "--informat", informat)
    assert result.exit_code == 1
    assert "--informat" in result.output and "Drop --informat" in result.output
    assert fake.urls == []


def test_cli_accepts_informat_genbank_with_an_accession(tmp_path: Path, monkeypatch) -> None:
    result, _ = _cli(
        tmp_path,
        monkeypatch,
        "--accession",
        "NC_045512.2",
        "--informat",
        "genbank",
        "--context-length",
        "128",
    )
    assert result.exit_code == 0, result.output
