"""Fetch a GenBank record by NCBI accession (issue #133).

Users often have an accession, not a file. This asks NCBI E-utilities (``efetch``) for the
record, caches the text under a cache directory, and hands the cached path to the normal
GenBank reader, so everything downstream (validation, genes, outputs) is unchanged.

Rules this module keeps:

* The accession is checked against a strict pattern BEFORE any request, so it can never
  inject query parameters or path pieces.
* NCBI answers some failures with HTTP 200 and an error text; a body that does not start
  with ``LOCUS`` is refused and never cached.
* Polite rate limiting: NCBI allows 3 requests/second without an API key and 10 with one.
* The optional API key goes in the request only. It is never written to the cache,
  the provenance, or an error message.
* The cache is keyed by accession and range, with a sidecar holding the ORIGINAL retrieval
  date, so provenance stays truthful when a later run is served from the cache.

Network access is injectable (``opener``) so the test suite never touches the network.
"""

from __future__ import annotations

import json
import re
import time
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
_TIMEOUT_SECONDS = 60

_ACCESSION = re.compile(r"[A-Za-z]{1,6}_?[A-Za-z]{0,6}\d{5,10}(?:\.\d{1,3})?")


class EntrezError(ValidationError):
    """An accession could not be fetched. A ``ValidationError`` so the run reports
    ``INPUT_INVALID`` and names one action, like every other input problem."""


@dataclass(frozen=True)
class FetchedRecord:
    """A GenBank file on disk plus where it came from."""

    path: str
    accession: str
    start: int | None
    stop: int | None
    retrieved: str  # ISO date of the ORIGINAL download
    from_cache: bool

    def provenance(self) -> dict[str, Any]:
        """The ``input_source`` block of provenance.json. Never contains the API key."""
        return {
            "type": "ncbi_entrez",
            "accession": self.accession,
            "range": [self.start, self.stop] if self.start is not None else None,
            "retrieved": self.retrieved,
            "from_cache": self.from_cache,
        }


_last_request: float | None = None


def _reset_rate_limiter() -> None:
    global _last_request
    _last_request = None


def _urlopen_bytes(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "dna-entropy-graph"})
    with urllib.request.urlopen(req, timeout=_TIMEOUT_SECONDS) as resp:  # noqa: S310 (https constant)
        return resp.read()


def default_cache_dir() -> Path:
    """``%LOCALAPPDATA%\\DNAEntropyGraph\\cache\\inputs`` on Windows, ``~/.cache`` elsewhere."""
    import os

    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "DNAEntropyGraph" / "cache" / "inputs"
    return Path.home() / ".cache" / "dna-entropy-graph" / "inputs"


def _validate(accession: str, start: int | None, stop: int | None) -> str:
    acc = (accession or "").strip().upper()
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
    acc = _validate(accession, start, stop)
    cache = Path(cache_dir)
    stem = acc if start is None else f"{acc}_{start}-{stop}"
    gb_path = cache / f"{stem}.gb"
    meta_path = cache / f"{stem}.json"

    if gb_path.is_file() and meta_path.is_file():
        try:
            retrieved = str(json.loads(meta_path.read_text(encoding="utf-8"))["retrieved"])
            return FetchedRecord(str(gb_path), acc, start, stop, retrieved, True)
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
    if _last_request is not None:
        wait = interval - (now() - _last_request)
        if wait > 0:
            do_sleep(wait)
    _last_request = now()

    try:
        body = (opener or _urlopen_bytes)(url)
    except Exception as exc:  # urllib raises several families; all mean "could not reach NCBI"
        raise EntrezError(
            f"Could not reach NCBI to fetch {acc} ({type(exc).__name__}). Check your internet "
            "connection and try again, or download the record yourself and drop the file in."
        ) from None  # chained text could echo the URL, which can carry the API key

    text = body.decode("utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n")
    if not text.lstrip("﻿ \n").startswith("LOCUS"):
        raise EntrezError(
            f"NCBI did not return a GenBank record for {acc}. Check the accession (and range) and try again."
        )

    retrieved = today or datetime.now(UTC).date().isoformat()
    cache.mkdir(parents=True, exist_ok=True)
    gb_path.write_text(text, encoding="utf-8", newline="\n")
    meta = {"accession": acc, "range": [start, stop] if start is not None else None, "retrieved": retrieved}
    meta_path.write_text(json.dumps(meta, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    return FetchedRecord(str(gb_path), acc, start, stop, retrieved, False)
