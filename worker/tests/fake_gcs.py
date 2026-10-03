"""An in-memory stand-in for the GCS JSON API calls GcsBlobstore makes (shared by the
blobstore contract tests and the runner's store cross-check test). No network."""

from __future__ import annotations

import json
import urllib.error
import urllib.parse


class _Resp:
    def __init__(self, body: bytes) -> None:
        self._body = body

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> _Resp:
        return self

    def __exit__(self, *exc) -> bool:
        return False


class FakeGcs:
    """A stateful stand-in for the three JSON API calls GcsBlobstore makes, plus the
    metadata token endpoint. ``page_size`` makes list responses paginate like the real
    API does (it caps a page at 1000 objects; a small cap here exercises the same loop)."""

    def __init__(self, bucket: str, *, page_size: int = 1000) -> None:
        self.bucket = bucket
        self.objects: dict[str, bytes] = {}
        self.page_size = page_size
        self.list_calls = 0

    def __call__(self, req):
        url = req.full_url
        parsed = urllib.parse.urlparse(url)
        if parsed.hostname == "metadata.google.internal":
            return _Resp(json.dumps({"access_token": "t", "expires_in": 3600}).encode())
        qs = urllib.parse.parse_qs(parsed.query)
        if parsed.path.startswith("/upload/storage/v1/b/"):
            name = qs["name"][0]
            self.objects[name] = req.data
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
            return _Resp(self.objects[name] if qs.get("alt") == ["media"] else b"{}")
        raise AssertionError(f"unexpected request: {url}")
