"""An in-memory stand-in for the GCS JSON API calls GcsBlobstore makes (shared by the
blobstore contract tests and the runner's store cross-check test). No network.

Besides the plain media upload it models the RESUMABLE upload protocol (initiate -> chunked
PUTs with ``Content-Range`` -> 308 ``Range`` while incomplete -> 200 when complete, and the
``bytes */total`` status query) and ranged, streamed downloads, and it records every request
so a test can assert that no single request or read is larger than one chunk (#496)."""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.parse


class _Resp:
    def __init__(
        self,
        body: bytes,
        *,
        status: int = 200,
        headers: dict[str, str] | None = None,
        break_after: int | None = None,
        on_read=None,
    ) -> None:
        self._body = body
        self._pos = 0
        self.status = status
        self.headers = headers or {}
        self._break_after = break_after
        self._on_read = on_read

    def read(self, n: int | None = None) -> bytes:
        if self._on_read is not None:
            self._on_read(n)
        if self._break_after is not None and self._pos >= self._break_after:
            raise ConnectionResetError("connection reset mid-stream")
        end = len(self._body) if n is None else self._pos + n
        if self._break_after is not None:
            end = min(end, self._break_after)
        out = self._body[self._pos : end]
        self._pos += len(out)
        return out

    def close(self) -> None:
        pass

    def __enter__(self) -> _Resp:
        return self

    def __exit__(self, *exc) -> bool:
        return False


class FakeGcs:
    """A stateful stand-in for the JSON API calls GcsBlobstore makes, plus the metadata token
    endpoint. ``page_size`` makes list responses paginate like the real API does (it caps a
    page at 1000 objects; a small cap here exercises the same loop).

    Fault injection (one-shot, keyed by the 0-based index of the chunk PUT):
    ``lose_response_after_commit`` commits the chunk then answers 503, ``fail_before_commit``
    answers 503 without committing; ``break_download_after`` cuts the next download's stream
    after that many bytes."""

    def __init__(self, bucket: str, *, page_size: int = 1000) -> None:
        self.bucket = bucket
        self.objects: dict[str, bytes] = {}
        self.page_size = page_size
        self.list_calls = 0
        self.requests: list[tuple[str, str, int, dict[str, str]]] = []
        self.sessions: dict[str, dict] = {}
        self.chunk_puts = 0
        self.max_body = 0
        self.read_sizes: list[int | None] = []
        self.lose_response_after_commit: set[int] = set()
        self.fail_before_commit: set[int] = set()
        self.break_download_after: int | None = None
        self.on_chunk_put = None

    def __call__(self, req):
        url = req.full_url
        parsed = urllib.parse.urlparse(url)
        if parsed.hostname == "metadata.google.internal":
            return _Resp(json.dumps({"access_token": "t", "expires_in": 3600}).encode())
        qs = urllib.parse.parse_qs(parsed.query)
        data = req.data or b""
        self.requests.append((req.get_method(), url, len(data), dict(req.header_items())))
        self.max_body = max(self.max_body, len(data))
        if parsed.path.startswith("/upload/storage/v1/b/"):
            if qs.get("uploadType") == ["resumable"]:
                return self._resumable(req, qs, data)
            name = qs["name"][0]
            self.objects[name] = data
            return _Resp(json.dumps({"name": name}).encode())
        base = f"/storage/v1/b/{self.bucket}/o"
        if parsed.path == base:
            self.list_calls += 1
            prefix = qs.get("prefix", [""])[0]
            names = sorted(n for n in self.objects if n.startswith(prefix))
            start = int(qs["pageToken"][0]) if "pageToken" in qs else 0
            page = names[start : start + self.page_size]
            body: dict = {"items": [{"name": n} for n in page]}
            if start + self.page_size < len(names):
                body["nextPageToken"] = str(start + self.page_size)
            return _Resp(json.dumps(body).encode())
        if parsed.path.startswith(base + "/"):
            name = urllib.parse.unquote(parsed.path[len(base) + 1 :])
            if name not in self.objects:
                raise urllib.error.HTTPError(url, 404, "Not Found", {}, None)  # type: ignore[arg-type]
            if qs.get("alt") != ["media"]:
                return _Resp(b"{}")
            body_bytes = self.objects[name]
            rng = req.get_header("Range")
            start = int(re.match(r"bytes=(\d+)-", rng).group(1)) if rng else 0
            break_after = self.break_download_after
            self.break_download_after = None
            return _Resp(
                body_bytes[start:],
                status=206 if rng else 200,
                break_after=break_after,
                on_read=self.read_sizes.append,
            )
        raise AssertionError(f"unexpected request: {url}")

    def _resumable(self, req, qs, data: bytes):
        if "upload_id" not in qs:  # initiate
            sid = str(len(self.sessions) + 1)
            self.sessions[sid] = {
                "name": qs["name"][0],
                "buf": bytearray(),
                "size": int(req.get_header("X-upload-content-length")),
            }
            loc = (
                f"https://storage.googleapis.com/upload/storage/v1/b/{self.bucket}"
                f"/o?uploadType=resumable&upload_id={sid}"
            )
            return _Resp(b"{}", headers={"Location": loc})
        sess = self.sessions[qs["upload_id"][0]]
        cr = req.get_header("Content-range")
        m = re.fullmatch(r"bytes (?:(\d+)-(\d+)|\*)/(\d+)", cr or "")
        assert m, f"bad Content-Range: {cr!r}"
        buf: bytearray = sess["buf"]
        if m.group(1) is None:  # status query
            return self._progress(sess)
        idx = self.chunk_puts
        self.chunk_puts += 1
        if self.on_chunk_put is not None:
            self.on_chunk_put(idx)
        start, end = int(m.group(1)), int(m.group(2))
        assert start == len(buf), f"non-contiguous chunk: starts {start}, server has {len(buf)}"
        assert end - start + 1 == len(data)
        if idx in self.fail_before_commit:
            raise urllib.error.HTTPError(req.full_url, 503, "Service Unavailable", {}, None)  # type: ignore[arg-type]
        buf += data
        if idx in self.lose_response_after_commit:
            raise urllib.error.HTTPError(req.full_url, 503, "Service Unavailable", {}, None)  # type: ignore[arg-type]
        return self._progress(sess)

    def _progress(self, sess):
        buf = sess["buf"]
        if len(buf) == sess["size"]:
            self.objects[sess["name"]] = bytes(buf)
            return _Resp(json.dumps({"name": sess["name"]}).encode())
        headers = {"Range": f"bytes=0-{len(buf) - 1}"} if buf else {}
        raise urllib.error.HTTPError("session", 308, "Resume Incomplete", headers, None)  # type: ignore[arg-type]
