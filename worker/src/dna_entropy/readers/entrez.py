"""Fetch a GenBank record by NCBI accession (issue #133).

Users often have an accession, not a file. This asks NCBI E-utilities (``efetch``) for the
record, caches the text under a cache directory, and hands the cached path to the normal
GenBank reader, so everything downstream (validation, genes, outputs) is unchanged.

Rules this module keeps:

* The accession is checked against a strict ASCII-only pattern BEFORE any request, so it can
  never inject query parameters or path pieces (``\\d`` alone would accept Arabic-Indic digits,
  and ``str.upper()`` folds some non-ASCII letters into ASCII ones, so both are refused).
* NCBI answers some failures with HTTP 200 and an error text; a body that does not start
  with ``LOCUS``, does not end with the record terminator ``//`` (a cut-off download), has no
  ``VERSION`` line, or names a different record than was asked for is refused and never cached.
* The record is cached under, and provenance records, the RESOLVED versioned accession from its
  ``VERSION`` line (``NC_045512`` asked, ``NC_045512.2`` resolved). An unversioned request always
  asks NCBI, so it can never be served a version NCBI has since replaced; a versioned request
  is immutable and is served from the cache.
* A slow or endless answer is cut off by a total deadline, and an answer over a size cap is
  refused; both are checked as the body streams in.
* Each failure class has its own message and one action: rate limited (429), NCBI trouble (5xx),
  a rejected request (400, 404), a refused key (401, 403), a timeout, a too-large record, and
  any other network failure. 429 and 5xx are retried once after a backoff; nothing else is.
* Polite rate limiting: NCBI allows 3 requests/second without an API key and 10 with one.
* The optional API key goes in the request only. It is never written to the cache,
  the provenance, or an error message.
* The cache is keyed by resolved accession and range, with a sidecar holding the ORIGINAL
  retrieval date, so provenance stays truthful when a later run is served from the cache.

Network access is injectable (``opener``) so the test suite never touches the network.
"""

from __future__ import annotations

import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from ..validation.validators import ValidationError

EFETCH_URL = "https://eutils.ncbi.nlm.nih.gov/entrez/eutils/efetch.fcgi"
MIN_INTERVAL_NO_KEY = 0.34  # 3 requests per second
MIN_INTERVAL_WITH_KEY = 0.1  # 10 requests per second
_TIMEOUT_SECONDS = 30  # connect, and each single wait for more bytes
_TOTAL_DEADLINE_SECONDS = 120.0  # the whole body, however slowly it drips in
_MAX_BODY_BYTES = 64 * 1024 * 1024  # same ceiling the app applies to any staged input
_RETRY_BACKOFF_SECONDS = 2.0
_RETRY_AFTER_MAX_SECONDS = 30.0

# re.ASCII: ``\d`` is otherwise any Unicode decimal digit (Arabic-Indic, full-width, ...).
_ACCESSION = re.compile(r"[A-Za-z]{1,6}_?[A-Za-z]{0,6}\d{5,10}(?:\.\d{1,3})?", re.ASCII)
_VERSION_LINE = re.compile(r"^VERSION[ \t]+(\S+)", re.MULTILINE)


class EntrezError(ValidationError):
    """An accession could not be fetched. A ``ValidationError`` so the run reports
    ``INPUT_INVALID`` and names one action, like every other input problem."""


class _TooLarge(Exception):
    """The answer is over the size cap (raised while it streams in)."""


@dataclass(frozen=True)
class FetchedRecord:
    """A GenBank file on disk plus where it came from."""

    path: str
    accession: str  # the RESOLVED versioned accession, from the record's VERSION line
    start: int | None
    stop: int | None
    retrieved: str  # ISO date of the ORIGINAL download
    from_cache: bool
    requested: str = ""  # what the user asked for (may have no version)

    def provenance(self) -> dict[str, Any]:
        """The ``input_source`` block of provenance.json. Never contains the API key."""
        return {
            "type": "ncbi_entrez",
            "accession": self.accession,
            "requested": self.requested or self.accession,
            "range": [self.start, self.stop] if self.start is not None else None,
            "retrieved": self.retrieved,
            "from_cache": self.from_cache,
        }


_last_request: float | None = None


def _reset_rate_limiter() -> None:
    global _last_request
    _last_request = None


def _urlopen_bytes(url: str) -> bytes:
    """GET ``url`` and return the body, refusing one over the size cap and giving up after the
    total deadline (the per-wait socket timeout alone lets a server drip one byte at a time
    forever). Raises :class:`_TooLarge`, ``TimeoutError``, or urllib's own errors."""
    deadline = time.monotonic() + _TOTAL_DEADLINE_SECONDS
    req = urllib.request.Request(url, headers={"User-Agent": "dna-entropy-graph"})
    with urllib.request.urlopen(req, timeout=_TIMEOUT_SECONDS) as resp:  # noqa: S310 (https constant)
        declared = resp.headers.get("Content-Length")
        if declared and declared.isdigit() and int(declared) > _MAX_BODY_BYTES:
            raise _TooLarge
        chunks: list[bytes] = []
        total = 0
        while True:
            if time.monotonic() > deadline:
                raise TimeoutError("total deadline exceeded")
            chunk = resp.read1(65536)  # read1: returns what has arrived, never waits to fill the buffer
            if not chunk:
                break
            total += len(chunk)
            if total > _MAX_BODY_BYTES:
                raise _TooLarge
            chunks.append(chunk)
    return b"".join(chunks)


def default_cache_dir() -> Path:
    """``%LOCALAPPDATA%\\DNAEntropyGraph\\cache\\inputs`` on Windows, ``~/.cache`` elsewhere."""
    import os

    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "DNAEntropyGraph" / "cache" / "inputs"
    return Path.home() / ".cache" / "dna-entropy-graph" / "inputs"


def validate_request(accession: str, start: int | None, stop: int | None) -> str:
    """Return the normalised (upper-case) accession, or raise :class:`EntrezError`. Pure: makes no
    request, so a caller can validate before it spends anything."""
    raw = (accession or "").strip()
    # isascii() first: str.upper() folds some non-ASCII letters (long s, dotless i) into ASCII ones.
    acc = raw.upper() if raw.isascii() else ""
    if not _ACCESSION.fullmatch(acc):
        raise EntrezError(
            "That does not look like an NCBI accession (for example NC_045512.2 or MN908947.3). "
            "Check the accession for typos and try again."
        )
    if (start is None) != (stop is None):
        raise EntrezError("Give both ends of the range, or neither. Fill in both the start and the end.")
    if start is not None and stop is not None and not (1 <= start <= stop):
        raise EntrezError("The range must satisfy 1 <= start <= end. Correct the range and try again.")
    return acc


def _size_text(n: int) -> str:
    return f"{n // (1024 * 1024)} MiB" if n >= 1024 * 1024 else f"{n} bytes"


def _is_timeout(exc: BaseException) -> bool:
    if isinstance(exc, TimeoutError):
        return True
    return isinstance(exc, urllib.error.URLError) and isinstance(exc.reason, TimeoutError)


def _retry_delay(exc: urllib.error.HTTPError) -> float:
    """Backoff before the one retry: NCBI's ``Retry-After`` seconds when it sent a sensible one."""
    headers = getattr(exc, "headers", None)
    value = headers.get("Retry-After") if headers is not None else None
    if value and str(value).isdigit():
        return min(max(float(value), 1.0), _RETRY_AFTER_MAX_SECONDS)
    return _RETRY_BACKOFF_SECONDS


def _failure(exc: BaseException, acc: str) -> tuple[EntrezError, bool]:
    """``(error, retryable)`` for a failed request. The message never carries the URL (which can
    hold the API key), only a status code or an exception class name."""
    if isinstance(exc, _TooLarge):
        return EntrezError(
            f"The record for {acc} is larger than {_size_text(_MAX_BODY_BYTES)}, more than this app "
            "reads. In the app, type a smaller range into the From base and To base boxes, then choose "
            "Fetch record again; "
            "on the command line, use --accession-range START-END."
        ), False
    if isinstance(exc, urllib.error.HTTPError):
        code = exc.code
        if code == 429:
            return EntrezError(
                "NCBI is limiting how fast it answers requests (HTTP 429), even after a retry. "
                "Wait a minute and try again; an NCBI API key (--ncbi-api-key) raises the limit."
            ), True
        if 500 <= code < 600:
            return EntrezError(
                f"NCBI is having trouble (HTTP {code}), even after a retry. Try again in a few minutes."
            ), True
        if code in (400, 404):
            return EntrezError(
                f"NCBI rejected the request (HTTP {code}) for {acc}. Check the accession and the "
                "range, then try again."
            ), False
        if code in (401, 403):
            return EntrezError(
                f"NCBI refused the request (HTTP {code}). Check the NCBI API key you passed with "
                "--ncbi-api-key, or leave it out."
            ), False
    elif _is_timeout(exc):
        return EntrezError(
            f"NCBI did not answer in time for {acc}. Check your internet connection and try again "
            "in a few minutes."
        ), False
    return EntrezError(
        f"Could not reach NCBI to fetch {acc} ({type(exc).__name__}). Check your internet "
        "connection and try again, or download the record yourself and drop the file in."
    ), False


def _check_body(text: str, acc: str) -> str:
    """Validate NCBI's answer and return the resolved versioned accession, or raise."""
    if not text.lstrip("﻿ \n").startswith("LOCUS"):
        raise EntrezError(
            f"NCBI did not return a GenBank record for {acc}. Check the accession (and range) and try again."
        )
    if not text.rstrip().endswith("//"):
        raise EntrezError(
            f"NCBI's answer for {acc} was cut off before the end of the record, so it was not "
            "saved. Try again."
        )
    match = _VERSION_LINE.search(text)
    resolved = match.group(1).upper() if match else ""
    if not _ACCESSION.fullmatch(resolved):
        raise EntrezError(
            f"NCBI's answer for {acc} has no VERSION line, so it is not a complete GenBank "
            "record. Try again, or download the record yourself and drop the file in."
        )
    asked_version = "." in acc
    if (resolved != acc) if asked_version else (resolved.split(".")[0] != acc):
        raise EntrezError(
            f"NCBI returned a different record than the one you asked for ({acc}). Check the "
            "accession and try again."
        )
    return resolved


def _stem(accession: str, start: int | None, stop: int | None) -> str:
    return accession if start is None else f"{accession}_{start}-{stop}"


def fetch_genbank(
    accession: str,
    *,
    cache_dir: str | Path,
    start: int | None = None,
    stop: int | None = None,
    api_key: str | None = None,
    opener: Callable[[str], bytes] | None = None,
    sleep: Callable[[float], None] | None = None,
    clock: Callable[[], float] | None = None,
    today: str | None = None,
) -> FetchedRecord:
    """Return the GenBank record for ``accession`` (optionally ``start..stop``, 1-based
    inclusive), from the cache when present, else from NCBI."""
    global _last_request
    acc = validate_request(accession, start, stop)
    cache = Path(cache_dir)

    if "." in acc:  # a versioned record never changes, so it may come from the cache
        stem = _stem(acc, start, stop)
        gb_path, meta_path = cache / f"{stem}.gb", cache / f"{stem}.json"
        if gb_path.is_file() and meta_path.is_file():
            try:
                retrieved = str(json.loads(meta_path.read_text(encoding="utf-8"))["retrieved"])
                return FetchedRecord(str(gb_path), acc, start, stop, retrieved, True, acc)
            except (OSError, ValueError, KeyError):
                pass  # unreadable sidecar: fall through and fetch again

    params: dict[str, str | int] = {"db": "nuccore", "id": acc, "rettype": "gb", "retmode": "text"}
    if start is not None and stop is not None:
        params["seq_start"] = start
        params["seq_stop"] = stop
    if api_key:
        params["api_key"] = api_key
    url = f"{EFETCH_URL}?{urllib.parse.urlencode(params)}"

    do_sleep = sleep or time.sleep
    now = clock or time.monotonic
    interval = MIN_INTERVAL_WITH_KEY if api_key else MIN_INTERVAL_NO_KEY
    body = b""
    for attempt in (1, 2):
        if _last_request is not None:
            wait = interval - (now() - _last_request)
            if wait > 0:
                do_sleep(wait)
        _last_request = now()
        try:
            body = (opener or _urlopen_bytes)(url)
            break
        except Exception as exc:  # urllib raises several families; each is classified below
            error, retryable = _failure(exc, acc)
            if retryable and attempt == 1:
                delay = (
                    _retry_delay(exc) if isinstance(exc, urllib.error.HTTPError) else _RETRY_BACKOFF_SECONDS
                )
                do_sleep(delay)
                continue
            raise error from None  # chained text could echo the URL, which can carry the API key

    text = body.decode("utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n")
    resolved = _check_body(text, acc)

    retrieved = today or datetime.now(UTC).date().isoformat()
    stem = _stem(resolved, start, stop)
    gb_path, meta_path = cache / f"{stem}.gb", cache / f"{stem}.json"
    cache.mkdir(parents=True, exist_ok=True)
    gb_path.write_text(text, encoding="utf-8", newline="\n")
    meta = {
        "accession": resolved,
        "requested": acc,
        "range": [start, stop] if start is not None else None,
        "retrieved": retrieved,
    }
    meta_path.write_text(json.dumps(meta, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    return FetchedRecord(str(gb_path), resolved, start, stop, retrieved, False, acc)
