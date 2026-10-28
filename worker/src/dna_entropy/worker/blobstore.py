"""Blobstore: where a job's files live, behind one interface (docs/job_contract.md §1).

``Blobstore`` is a ``Protocol``, not a class, on purpose: the worker never special-cases
"am I on a VM". Every path passed to a method here is RELATIVE to the job's own prefix
(``jobs/<jobId>/`` in a bucket, or ``%LOCALAPPDATA%\\DNAEntropyGraph\\runs\\<jobId>\\``
locally) — e.g. ``"manifest.json"``, ``"output/SetTnpB/SetTnpB.gb"``, ``"control/cancel"``.

Two implementations:
- :class:`LocalBlobstore` — a plain rooted directory. Used by every test in this repo and
  by the local engine (design §5.7). Fully implemented and unit-tested; no network.
- :class:`GcsBlobstore` — metadata-token REST against the JSON API (never the Python
  ``google-cloud-storage`` SDK, which is too heavy for the "keep the worker lean" rule and
  is not needed for the handful of operations this contract requires). Implemented but
  **not called anywhere in this codebase tonight** — no cloud spend, no cloud calls (the
  overnight brief's hard rule). Every test that exercises it injects a fake HTTP
  transport; none makes a real network call.
"""

from __future__ import annotations

import http.client
import json
import os
import random
import shutil
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import Any, Literal, Protocol, overload, runtime_checkable


class BlobstoreError(RuntimeError):
    """Raised when a blobstore operation fails (missing object, transport failure, ...)."""


class TransferCancelled(BlobstoreError):
    """A ``should_stop`` poll asked a file transfer to end early. Nothing is left at the
    destination object, and the caller decides what a cancel means (#496)."""


@runtime_checkable
class Blobstore(Protocol):
    """Reads/writes one job's files. See the module docstring for the path convention."""

    def read_text(self, path: str) -> str:
        """Return the UTF-8 text content of ``path``. Raises :class:`BlobstoreError` if
        it does not exist."""
        ...

    def write_text(self, path: str, text: str) -> None:
        """Write ``text`` to ``path``, atomically from a reader's perspective — a
        concurrent reader never observes a partial write (job_contract.md §4: "overwritten
        atomically"). Creates any missing parent directories/prefixes."""

    def exists(self, path: str) -> bool:
        """Return whether ``path`` exists. Used for ``control/cancel`` (job_contract.md
        §6: "presence of the object is the entire signal")."""
        ...

    def list_prefix(self, prefix: str) -> list[str]:
        """Return every path under ``prefix`` (relative to the job prefix, not to
        ``prefix`` itself), in no particular order."""
        ...

    def download_file(self, path: str, local_dest: Path) -> None:
        """Copy ``path`` from the store to a local file at ``local_dest`` (creating parent
        directories). Used to stage an input file where ``pipeline.run()`` — which only
        knows how to read a local path — can read it."""

    def upload_file(
        self, local_src: Path, path: str, *, should_stop: Callable[[], bool] | None = None
    ) -> None:
        """Copy a local file at ``local_src`` up to ``path`` in the store. Used to publish
        an output file ``pipeline.run()`` already wrote locally. Transfers are streamed, so
        peak memory is bounded by one chunk, never the file size (#496). ``should_stop`` is
        polled between chunks of a multi-chunk upload; when it returns true
        :class:`TransferCancelled` is raised and no object is left at ``path``."""


def _checked_relative(path: str, *, allow_empty: bool = False) -> str:
    """Return ``path`` unchanged if it is a plain job-relative path, else raise.

    Rejects, on EVERY platform (the same manifest runs on a Windows PC and a Linux VM):
    absolute paths in either syntax (``/x``, ``\\x``, ``C:\\x``, ``C:x``), any ``..``
    component split on either separator, and empty or NUL-containing names. ``Path`` alone
    is not enough: on Windows ``Path("/abs.txt").is_absolute()`` is False (no drive), yet
    ``root / "/abs.txt"`` resolves to the drive root, outside the job folder.
    """
    if path == "" and allow_empty:
        return path  # list_prefix("") means "the whole job"
    if not path or "\0" in path:
        raise BlobstoreError(f"path must be a non-empty relative path, got {path!r}")
    posix, win = PurePosixPath(path), PureWindowsPath(path)
    if (
        posix.is_absolute()
        or win.is_absolute()
        or win.drive
        or win.root
        or ".." in posix.parts
        or ".." in win.parts
    ):
        raise BlobstoreError(f"path must be relative and non-escaping, got {path!r}")
    return path


def read_json(store: Blobstore, path: str) -> dict:
    """Read and parse ``path`` as JSON."""
    return json.loads(store.read_text(path))


def write_json(store: Blobstore, path: str, data: dict) -> None:
    """Serialize ``data`` as pretty JSON (trailing newline, deterministic key order is the
    caller's responsibility) and write it atomically."""
    text = json.dumps(data, indent=2) + "\n"
    store.write_text(path, text)


# --- LocalBlobstore --------------------------------------------------------------------


class LocalBlobstore:
    """A plain rooted directory. Every job/test in this repo uses this implementation.

    Atomicity: writes go to ``<path>.tmp-<pid>-<counter>`` in the same directory, then
    ``os.replace`` swaps it into place — ``os.replace`` is atomic on both POSIX and
    Windows (unlike a plain ``rename``, which POSIX allows to fail if the destination
    exists), so a concurrent reader of ``path`` never observes a partial write.
    """

    def __init__(self, root: str | Path) -> None:
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        self._tmp_counter = 0

    def _resolve(self, path: str, *, allow_empty: bool = False) -> Path:
        # Reject absolute/parent-escaping paths outright: every path here is supposed to
        # be relative to the job prefix, and a `../../etc/passwd`-shaped path must never
        # be honoured just because some caller upstream forgot to sanitize it.
        return self.root / _checked_relative(path, allow_empty=allow_empty)

    def read_text(self, path: str) -> str:
        full = self._resolve(path)
        try:
            return full.read_text(encoding="utf-8")
        except FileNotFoundError as exc:
            raise BlobstoreError(f"not found: {path}") from exc

    def write_text(self, path: str, text: str) -> None:
        full = self._resolve(path)
        full.parent.mkdir(parents=True, exist_ok=True)
        self._tmp_counter += 1
        tmp = full.with_name(f"{full.name}.tmp-{os.getpid()}-{self._tmp_counter}")
        with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        os.replace(tmp, full)  # atomic on POSIX and Windows

    def exists(self, path: str) -> bool:
        return self._resolve(path).exists()

    def list_prefix(self, prefix: str) -> list[str]:
        base = self._resolve(prefix, allow_empty=True)
        if not base.exists():
            return []
        if base.is_file():
            return [prefix]
        out = []
        for p in base.rglob("*"):
            if p.is_file() and not p.name.startswith(".tmp-") and ".tmp-" not in p.name:
                out.append(str(p.relative_to(self.root)).replace(os.sep, "/"))
        return sorted(out)

    def download_file(self, path: str, local_dest: Path) -> None:
        full = self._resolve(path)
        if not full.exists():
            raise BlobstoreError(f"not found: {path}")
        self._atomic_copy(full, local_dest)

    def upload_file(
        self, local_src: Path, path: str, *, should_stop: Callable[[], bool] | None = None
    ) -> None:
        if not local_src.exists():
            raise BlobstoreError(f"local file not found: {local_src}")
        # A local copy is one block-streamed copy with no chunk boundary to poll at; the
        # caller's own between-files poll is the cancel point. ``should_stop`` is accepted so
        # both stores share one signature.
        self._atomic_copy(local_src, self._resolve(path))

    def _atomic_copy(self, src: Path, dest: Path) -> None:
        """Copy ``src`` to a temp sibling of ``dest`` (``shutil.copyfile``, which streams in
        blocks: no whole-file read, #496) and ``os.replace`` it into place, so a reader never
        observes a half-written file (issue #40). The temp name contains ``.tmp-`` so
        :meth:`list_prefix` never reports it."""
        dest.parent.mkdir(parents=True, exist_ok=True)
        self._tmp_counter += 1
        tmp = dest.with_name(f"{dest.name}.tmp-{os.getpid()}-{self._tmp_counter}")
        try:
            shutil.copyfile(src, tmp)
            os.replace(tmp, dest)
        except BaseException:
            tmp.unlink(missing_ok=True)
            raise

    def delete(self, path: str) -> None:
        """Not part of the Blobstore Protocol (a job never deletes its own files), but
        useful for test cleanup (e.g. simulating an app writing then withdrawing
        ``control/cancel``)."""
        full = self._resolve(path)
        if full.exists():
            full.unlink()


# --- GcsBlobstore ------------------------------------------------------------------------

_METADATA_TOKEN_URL = (
    "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token"
)
_METADATA_HEADERS = {"Metadata-Flavor": "Google"}
_STORAGE_API = "https://storage.googleapis.com/storage/v1"
_STORAGE_UPLOAD_API = "https://storage.googleapis.com/upload/storage/v1"


# Injectable HTTP transport, so this module never needs a real socket to be importable or
# testable. Signature matches urllib.request.urlopen closely enough that the default IS
# urllib.request.urlopen: (Request) -> a context-manager-like object with .read()/.status.
class HttpResponse(Protocol):
    """The slice of ``http.client.HTTPResponse`` this module uses (what ``urlopen`` returns).
    Declared so a fake opener in a test and the real one share one type."""

    status: int
    headers: Any

    def read(self, amt: int | None = None, /) -> bytes: ...

    def __enter__(self) -> HttpResponse: ...

    def __exit__(self, *exc_info: object) -> object: ...


HttpOpener = Callable[[urllib.request.Request], HttpResponse]


def default_http_opener(req: urllib.request.Request) -> HttpResponse:  # pragma: no cover - real I/O
    return urllib.request.urlopen(req, timeout=30)


@dataclass
class _CachedToken:
    value: str
    expires_at: float


class MetadataTokenProvider:
    """Fetches and caches a GCE metadata-server access token.

    Never called by anything in this codebase yet (see the module docstring); exists so
    :class:`GcsBlobstore` has a real, correct implementation ready for the first real GPU
    VM to use, without a single network call happening in a test — every test that touches
    this class injects ``opener`` and asserts on the constructed request.
    """

    def __init__(
        self, *, opener: HttpOpener = default_http_opener, clock: Callable[[], float] = time.time
    ) -> None:
        self._opener = opener
        self._clock = clock
        self._cached: _CachedToken | None = None

    def get(self) -> str:
        now = self._clock()
        if self._cached is not None and self._cached.expires_at - 30 > now:
            return self._cached.value
        req = urllib.request.Request(_METADATA_TOKEN_URL, headers=_METADATA_HEADERS)
        try:
            with self._opener(req) as resp:
                payload = json.loads(resp.read().decode("utf-8"))
        except urllib.error.URLError as exc:
            raise BlobstoreError(f"could not fetch a metadata-server token: {exc}") from exc
        token = payload["access_token"]
        expires_in = float(payload.get("expires_in", 3600))
        self._cached = _CachedToken(value=token, expires_at=now + expires_in)
        return token

    def invalidate(self) -> None:
        """Force the next :meth:`get` to fetch a fresh token, rather than serving the
        cached one that a 401 may already indicate is stale/revoked (issue #322)."""
        self._cached = None


# Retry policy (issue #322). Two budgets, not one: a status/progress write is recoverable
# by simply waiting for the next heartbeat tick (StatusWriter's own `_safe_write`, #318,
# already tolerates the eventual failure gracefully) and result.json's own write is
# further protected by run_job()'s three-step tail (#320: a failed write there still lets
# status.stop()/apply_lifecycle() run). An UPLOADED OUTPUT FILE or a DOWNLOADED INPUT has
# no such second life: the bytes exist nowhere else once the VM that produced/needs them
# stops or deletes itself, so those get a materially larger budget. Reads (manifest,
# exists, list_prefix) sit in between: important enough to not give up after one blip, but
# each individual read is retried again by its own caller in most real call patterns
# (CancelWatcher polls repeatedly; a failed manifest read is fatal either way).
_WRITE_MAX_ATTEMPTS = 4
_READ_MAX_ATTEMPTS = 4
_TRANSFER_MAX_ATTEMPTS = 6  # upload_file / download_file: actual job data, no second copy

_RETRIABLE_STATUS: frozenset[int] = frozenset({429, 500, 502, 503, 504})
_BASE_BACKOFF_SECONDS = 0.5
_MAX_BACKOFF_SECONDS = 8.0

# Streamed transfers (#496): peak memory is one chunk. GCS requires every resumable chunk but
# the last to be a multiple of 256 KiB; 8 MiB keeps the request count low for the ~14 GB
# Evo 2 weights (about 1,800 chunks) without holding a meaningful amount of RAM.
_CHUNK_GRANULARITY = 256 * 1024
_CHUNK_BYTES = 8 * 1024 * 1024


class _RetryableChunkError(Exception):
    """A resumable-upload request failed in a way worth asking the session about."""


class GcsBlobstore:
    """Metadata-token REST against the GCS JSON API. Not exercised against real GCS by
    anything in this repo yet — see the module docstring.

    Retries transient failures (429/5xx, and a bare connection/timeout error) with
    bounded exponential backoff plus jitter; a 401 refreshes the cached token once and
    retries; any other 4xx (403, a real 404 without ``not_found_ok``, ...) never retries
    — spending minutes of paid VM time to reach the same answer is its own failure. Every
    attempt is logged (status code / error only — never a path or file content, matching
    issue #253's log-redaction discipline) so a support bundle shows whether a run was
    fighting the network.
    """

    def __init__(
        self,
        bucket: str,
        prefix: str,
        *,
        opener: HttpOpener = default_http_opener,
        token_provider: MetadataTokenProvider | None = None,
        sleep: Callable[[float], None] = time.sleep,
        rand: Callable[[], float] = random.random,
        chunk_bytes: int = _CHUNK_BYTES,
    ) -> None:
        if chunk_bytes <= 0 or chunk_bytes % _CHUNK_GRANULARITY:
            raise ValueError(f"chunk_bytes must be a positive multiple of 256 KiB, got {chunk_bytes}")
        self._chunk_bytes = chunk_bytes
        self.bucket = bucket
        self.prefix = prefix.rstrip("/") + "/" if prefix else ""
        self._opener = opener
        self._tokens = token_provider or MetadataTokenProvider(opener=opener)
        self._sleep = sleep
        self._rand = rand

    def _object_name(self, path: str, *, allow_empty: bool = False) -> str:
        return f"{self.prefix}{_checked_relative(path, allow_empty=allow_empty)}"

    def _auth_headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self._tokens.get()}"}

    @overload
    def _request(
        self,
        build_request: Callable[[], urllib.request.Request],
        *,
        what: str,
        max_attempts: int,
        not_found_ok: Literal[False] = False,
    ) -> HttpResponse: ...

    @overload
    def _request(
        self,
        build_request: Callable[[], urllib.request.Request],
        *,
        what: str,
        max_attempts: int,
        not_found_ok: Literal[True],
    ) -> HttpResponse | None: ...

    def _request(
        self,
        build_request: Callable[[], urllib.request.Request],
        *,
        what: str,
        max_attempts: int,
        not_found_ok: bool = False,
    ) -> HttpResponse | None:
        """Send one logical request, retrying per the policy in the class docstring.

        ``build_request`` is a FACTORY, not a pre-built request: a 401 retry needs a
        fresh ``Authorization`` header, so the request must be rebuilt after
        :meth:`MetadataTokenProvider.invalidate` runs, not merely resent.
        """
        token_refreshed = False
        delay = _BASE_BACKOFF_SECONDS
        attempt = 0
        while True:
            attempt += 1
            try:
                return self._opener(build_request())
            except urllib.error.HTTPError as exc:
                code = exc.code
                print(f"GcsBlobstore: {what} attempt {attempt} -> HTTP {code}", file=sys.stderr)
                if not_found_ok and code == 404:
                    return None
                if code == 401 and not token_refreshed:
                    # Once, not forever: a token that is STILL rejected after a refresh is
                    # a real auth problem, not a blip — the next branch's "not retriable"
                    # check catches that on the following iteration.
                    token_refreshed = True
                    self._tokens.invalidate()
                    continue
                if code not in _RETRIABLE_STATUS:
                    raise BlobstoreError(f"GCS request failed ({code}): {exc.reason}") from exc
                if attempt >= max_attempts:
                    raise BlobstoreError(
                        f"GCS request failed after {attempt} attempts ({code}): {exc.reason}"
                    ) from exc
            except urllib.error.URLError as exc:
                # Connection refused/reset, DNS failure, timeout — urllib raises these as
                # a bare URLError (HTTPError, caught above, is a URLError subclass with a
                # real HTTP status; this branch only ever sees the non-HTTP kind).
                print(f"GcsBlobstore: {what} attempt {attempt} -> {exc}", file=sys.stderr)
                if attempt >= max_attempts:
                    raise BlobstoreError(f"GCS request failed after {attempt} attempts: {exc}") from exc
            self._sleep(delay + self._rand() * delay)  # full jitter: wait in [delay, 2*delay)
            delay = min(delay * 2, _MAX_BACKOFF_SECONDS)

    def read_text(self, path: str) -> str:
        return self._download_bytes(path).decode("utf-8")

    def write_text(self, path: str, text: str) -> None:
        self._upload_bytes(
            path,
            text.encode("utf-8"),
            content_type="text/plain; charset=utf-8",
            max_attempts=_WRITE_MAX_ATTEMPTS,
        )

    def exists(self, path: str) -> bool:
        name = urllib.parse.quote(self._object_name(path), safe="")
        url = f"{_STORAGE_API}/b/{self.bucket}/o/{name}"
        resp = self._request(
            lambda: urllib.request.Request(url, headers=self._auth_headers()),
            what="exists",
            max_attempts=_READ_MAX_ATTEMPTS,
            not_found_ok=True,
        )
        return resp is not None

    def list_prefix(self, prefix: str) -> list[str]:
        full_prefix = self._object_name(prefix, allow_empty=True)
        base_url = f"{_STORAGE_API}/b/{self.bucket}/o?prefix={urllib.parse.quote(full_prefix, safe='')}"
        names: list[str] = []
        page_token: str | None = None
        while True:
            # The JSON API returns at most one page (1000 objects) per call and a
            # `nextPageToken` while more remain; reading only the first page would silently
            # drop the rest of a large batch's outputs (issue #40).
            url = base_url
            if page_token:
                url += f"&pageToken={urllib.parse.quote(page_token, safe='')}"
            resp = self._request(
                lambda url=url: urllib.request.Request(url, headers=self._auth_headers()),
                what="list_prefix",
                max_attempts=_READ_MAX_ATTEMPTS,
            )
            payload = json.loads(resp.read().decode("utf-8"))
            names += [item["name"] for item in payload.get("items", [])]
            page_token = payload.get("nextPageToken")
            if not page_token:
                break
        # Strip this job's own prefix so callers see paths relative to it, like LocalBlobstore.
        return sorted(n[len(self.prefix) :] if n.startswith(self.prefix) else n for n in names)

    def download_file(self, path: str, local_dest: Path) -> None:
        """Stream the object to ``<dest>.part`` in chunks, then rename. A stream cut midway
        resumes with a ``Range`` request from the bytes already on disk; a failure leaves no
        file at ``local_dest`` (and an existing one untouched)."""
        local_dest.parent.mkdir(parents=True, exist_ok=True)
        part = local_dest.with_name(local_dest.name + ".part")
        try:
            self._stream_to(path, part)
            os.replace(part, local_dest)
        except BaseException:
            part.unlink(missing_ok=True)
            raise

    def _stream_to(self, path: str, part: Path) -> None:
        name = urllib.parse.quote(self._object_name(path), safe="")
        url = f"{_STORAGE_API}/b/{self.bucket}/o/{name}?alt=media"
        offset = 0
        failures = 0
        delay = _BASE_BACKOFF_SECONDS
        part.unlink(missing_ok=True)
        while True:

            def _build(start: int = offset) -> urllib.request.Request:
                headers = self._auth_headers()
                if start:
                    headers["Range"] = f"bytes={start}-"
                return urllib.request.Request(url, headers=headers)

            resp = self._request(_build, what="download", max_attempts=_TRANSFER_MAX_ATTEMPTS)
            try:
                if offset and getattr(resp, "status", 206) != 206:
                    offset = 0  # the server ignored Range and is resending from byte 0
                with part.open("ab" if offset else "wb") as fh:
                    while True:
                        block = resp.read(self._chunk_bytes)
                        if not block:
                            return
                        fh.write(block)
                        offset += len(block)
            except (OSError, http.client.HTTPException) as exc:
                # A ConnectionResetError / timeout / IncompleteRead mid-body: keep what is on
                # disk and ask for the rest. A local disk error (ENOSPC) is an OSError too and
                # simply fails again until the budget is spent.
                failures += 1
                print(f"GcsBlobstore: download stream failed at byte {offset}: {exc}", file=sys.stderr)
                if failures >= _TRANSFER_MAX_ATTEMPTS:
                    raise BlobstoreError(
                        f"GCS download failed after {failures} stream errors: {exc}"
                    ) from exc
                offset = part.stat().st_size if part.exists() else 0
                self._sleep(delay + self._rand() * delay)
                delay = min(delay * 2, _MAX_BACKOFF_SECONDS)
            finally:
                close = getattr(resp, "close", None)
                if close is not None:
                    close()

    def upload_file(
        self, local_src: Path, path: str, *, should_stop: Callable[[], bool] | None = None
    ) -> None:
        if not local_src.exists():
            raise BlobstoreError(f"local file not found: {local_src}")
        size = local_src.stat().st_size
        if size <= self._chunk_bytes:
            # One request, and the body is at most one chunk, so memory stays bounded.
            self._upload_bytes(
                path,
                local_src.read_bytes(),
                content_type="application/octet-stream",
                max_attempts=_TRANSFER_MAX_ATTEMPTS,
            )
            return
        self._upload_resumable(local_src, path, size, should_stop)

    def _upload_resumable(
        self, local_src: Path, path: str, size: int, should_stop: Callable[[], bool] | None
    ) -> None:
        """GCS resumable upload: initiate a session, PUT fixed-size chunks with a
        ``Content-Range``, and after a retryable failure ask the session how much it
        committed (``bytes */total``) and continue from there, never from zero."""
        name = urllib.parse.quote(self._object_name(path), safe="")
        init_url = f"{_STORAGE_UPLOAD_API}/b/{self.bucket}/o?uploadType=resumable&name={name}"

        def _init() -> urllib.request.Request:
            headers = {
                **self._auth_headers(),
                "Content-Type": "application/json; charset=UTF-8",
                "X-Upload-Content-Type": "application/octet-stream",
                "X-Upload-Content-Length": str(size),
            }
            return urllib.request.Request(init_url, data=b"{}", headers=headers, method="POST")

        resp = self._request(_init, what="upload-init", max_attempts=_TRANSFER_MAX_ATTEMPTS)
        session = resp.headers.get("Location")
        if not session:
            raise BlobstoreError("GCS resumable upload: no session URI in the initiate response")
        offset = 0
        failures = 0
        delay = _BASE_BACKOFF_SECONDS
        with local_src.open("rb") as fh:
            while True:
                # Polled BETWEEN chunks (the caller already polled before the file started).
                if offset and should_stop is not None and should_stop():
                    raise TransferCancelled("upload cancelled")
                fh.seek(offset)
                chunk = fh.read(self._chunk_bytes)
                try:
                    committed = self._put_chunk(session, chunk, offset, size)
                except _RetryableChunkError as exc:
                    # Ask the server what it kept, retrying the question itself within budget.
                    while True:
                        failures += 1
                        print(f"GcsBlobstore: upload chunk at byte {offset} failed: {exc}", file=sys.stderr)
                        if failures >= _TRANSFER_MAX_ATTEMPTS:
                            raise BlobstoreError(
                                f"GCS upload failed after {failures} attempts: {exc}"
                            ) from exc
                        self._sleep(delay + self._rand() * delay)
                        delay = min(delay * 2, _MAX_BACKOFF_SECONDS)
                        try:
                            committed = self._put_chunk(session, b"", None, size)
                            break
                        except _RetryableChunkError as exc2:
                            exc = exc2
                if committed is None:
                    return  # the final chunk was accepted: the object exists
                if committed <= offset and chunk:
                    # A 308 that committed nothing new: count it so a server that never
                    # advances cannot loop this forever.
                    failures += 1
                    if failures >= _TRANSFER_MAX_ATTEMPTS:
                        raise BlobstoreError("GCS upload made no progress")
                offset = committed

    def _put_chunk(self, session: str, chunk: bytes, offset: int | None, total: int):
        """PUT one chunk (or, with ``offset=None``, the status query). Returns the number of
        bytes the server now holds, or ``None`` once the object is complete. Raises
        :class:`_RetryableChunkError` for a failure worth resuming and
        :class:`BlobstoreError` for one that is not."""
        if offset is None:
            content_range = f"bytes */{total}"
        else:
            content_range = f"bytes {offset}-{offset + len(chunk) - 1}/{total}"
        req = urllib.request.Request(
            session, data=chunk, headers={"Content-Range": content_range}, method="PUT"
        )
        try:
            resp = self._opener(req)
        except urllib.error.HTTPError as exc:
            if exc.code == 308:  # Resume Incomplete: the Range header says what is committed
                rng = exc.headers.get("Range") if exc.headers else None
                return int(rng.rsplit("-", 1)[1]) + 1 if rng else 0
            if exc.code in _RETRIABLE_STATUS:
                raise _RetryableChunkError(f"HTTP {exc.code}") from exc
            raise BlobstoreError(f"GCS upload failed ({exc.code}): {exc.reason}") from exc
        except (urllib.error.URLError, OSError, http.client.HTTPException) as exc:
            raise _RetryableChunkError(str(exc)) from exc
        close = getattr(resp, "close", None)
        if close is not None:
            close()
        return None

    def _download_bytes(self, path: str) -> bytes:
        name = urllib.parse.quote(self._object_name(path), safe="")
        url = f"{_STORAGE_API}/b/{self.bucket}/o/{name}?alt=media"
        resp = self._request(
            lambda: urllib.request.Request(url, headers=self._auth_headers()),
            what="download",
            max_attempts=_TRANSFER_MAX_ATTEMPTS,
        )
        return resp.read()

    def _upload_bytes(self, path: str, data: bytes, *, content_type: str, max_attempts: int) -> None:
        # A single PUT/POST of the whole object body is atomic at the object level in GCS
        # — no reader ever observes a partial object — so no temp-object-then-rename dance
        # is needed here the way LocalBlobstore needs one for a plain filesystem.
        name = urllib.parse.quote(self._object_name(path), safe="")
        url = f"{_STORAGE_UPLOAD_API}/b/{self.bucket}/o?uploadType=media&name={name}"

        def _build() -> urllib.request.Request:
            headers = {**self._auth_headers(), "Content-Type": content_type}
            return urllib.request.Request(url, data=data, headers=headers, method="POST")

        self._request(_build, what="upload", max_attempts=max_attempts)
