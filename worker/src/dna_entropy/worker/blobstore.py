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

import json
import os
import time
import urllib.error
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol, runtime_checkable


class BlobstoreError(RuntimeError):
    """Raised when a blobstore operation fails (missing object, transport failure, ...)."""


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
        ...

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
        ...

    def upload_file(self, local_src: Path, path: str) -> None:
        """Copy a local file at ``local_src`` up to ``path`` in the store. Used to publish
        an output file ``pipeline.run()`` already wrote locally."""
        ...


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

    def _resolve(self, path: str) -> Path:
        # Reject absolute/parent-escaping paths outright: every path here is supposed to
        # be relative to the job prefix, and a `../../etc/passwd`-shaped path must never
        # be honoured just because some caller upstream forgot to sanitize it.
        p = Path(path)
        if p.is_absolute() or ".." in p.parts:
            raise BlobstoreError(f"path must be relative and non-escaping, got {path!r}")
        return self.root / p

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
        base = self._resolve(prefix)
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
        local_dest.parent.mkdir(parents=True, exist_ok=True)
        local_dest.write_bytes(full.read_bytes())

    def upload_file(self, local_src: Path, path: str) -> None:
        if not local_src.exists():
            raise BlobstoreError(f"local file not found: {local_src}")
        full = self._resolve(path)
        full.parent.mkdir(parents=True, exist_ok=True)
        full.write_bytes(local_src.read_bytes())

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
HttpOpener = Callable[[urllib.request.Request], "object"]


def default_http_opener(req: urllib.request.Request) -> object:  # pragma: no cover - real I/O
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
            with self._opener(req) as resp:  # type: ignore[union-attr]
                payload = json.loads(resp.read().decode("utf-8"))
        except urllib.error.URLError as exc:
            raise BlobstoreError(f"could not fetch a metadata-server token: {exc}") from exc
        token = payload["access_token"]
        expires_in = float(payload.get("expires_in", 3600))
        self._cached = _CachedToken(value=token, expires_at=now + expires_in)
        return token


class GcsBlobstore:
    """Metadata-token REST against the GCS JSON API. Not exercised against real GCS by
    anything in this repo yet — see the module docstring.
    """

    def __init__(
        self,
        bucket: str,
        prefix: str,
        *,
        opener: HttpOpener = default_http_opener,
        token_provider: MetadataTokenProvider | None = None,
    ) -> None:
        self.bucket = bucket
        self.prefix = prefix.rstrip("/") + "/" if prefix else ""
        self._opener = opener
        self._tokens = token_provider or MetadataTokenProvider(opener=opener)

    def _object_name(self, path: str) -> str:
        p = Path(path)
        if p.is_absolute() or ".." in p.parts:
            raise BlobstoreError(f"path must be relative and non-escaping, got {path!r}")
        return f"{self.prefix}{path}"

    def _auth_headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self._tokens.get()}"}

    def _request(self, req: urllib.request.Request, *, not_found_ok: bool = False):
        try:
            return self._opener(req)
        except urllib.error.HTTPError as exc:
            if not_found_ok and exc.code == 404:
                return None
            raise BlobstoreError(f"GCS request failed ({exc.code}): {exc.reason}") from exc
        except urllib.error.URLError as exc:
            raise BlobstoreError(f"GCS request failed: {exc}") from exc

    def read_text(self, path: str) -> str:
        return self._download_bytes(path).decode("utf-8")

    def write_text(self, path: str, text: str) -> None:
        self._upload_bytes(path, text.encode("utf-8"), content_type="text/plain; charset=utf-8")

    def exists(self, path: str) -> bool:
        name = urllib.request.quote(self._object_name(path), safe="")
        url = f"{_STORAGE_API}/b/{self.bucket}/o/{name}"
        req = urllib.request.Request(url, headers=self._auth_headers())
        resp = self._request(req, not_found_ok=True)
        return resp is not None

    def list_prefix(self, prefix: str) -> list[str]:
        full_prefix = self._object_name(prefix)
        url = f"{_STORAGE_API}/b/{self.bucket}/o?prefix={urllib.request.quote(full_prefix, safe='')}"
        req = urllib.request.Request(url, headers=self._auth_headers())
        resp = self._request(req)
        payload = json.loads(resp.read().decode("utf-8"))
        names = [item["name"] for item in payload.get("items", [])]
        # Strip this job's own prefix so callers see paths relative to it, like LocalBlobstore.
        return sorted(n[len(self.prefix) :] if n.startswith(self.prefix) else n for n in names)

    def download_file(self, path: str, local_dest: Path) -> None:
        data = self._download_bytes(path)
        local_dest.parent.mkdir(parents=True, exist_ok=True)
        local_dest.write_bytes(data)

    def upload_file(self, local_src: Path, path: str) -> None:
        if not local_src.exists():
            raise BlobstoreError(f"local file not found: {local_src}")
        self._upload_bytes(path, local_src.read_bytes(), content_type="application/octet-stream")

    def _download_bytes(self, path: str) -> bytes:
        name = urllib.request.quote(self._object_name(path), safe="")
        url = f"{_STORAGE_API}/b/{self.bucket}/o/{name}?alt=media"
        req = urllib.request.Request(url, headers=self._auth_headers())
        resp = self._request(req)
        return resp.read()

    def _upload_bytes(self, path: str, data: bytes, *, content_type: str) -> None:
        # A single PUT/POST of the whole object body is atomic at the object level in GCS
        # — no reader ever observes a partial object — so no temp-object-then-rename dance
        # is needed here the way LocalBlobstore needs one for a plain filesystem.
        name = urllib.request.quote(self._object_name(path), safe="")
        url = f"{_STORAGE_UPLOAD_API}/b/{self.bucket}/o?uploadType=media&name={name}"
        headers = {**self._auth_headers(), "Content-Type": content_type}
        req = urllib.request.Request(url, data=data, headers=headers, method="POST")
        self._request(req)
