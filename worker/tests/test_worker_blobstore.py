"""Tests for worker/blobstore.py.

LocalBlobstore is fully exercised (it is what every other worker test and the local
engine use). GcsBlobstore is exercised with a FAKE HTTP transport only — no test here
makes, or could make, a real network call; each test asserts on the request that would
have been sent (method, URL, headers, body) and feeds back a canned response.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from dna_entropy.worker.blobstore import (
    Blobstore,
    BlobstoreError,
    GcsBlobstore,
    LocalBlobstore,
    read_json,
    write_json,
)

# --- LocalBlobstore ----------------------------------------------------------------


def test_local_blobstore_satisfies_the_protocol(tmp_path: Path) -> None:
    assert isinstance(LocalBlobstore(tmp_path), Blobstore)


def test_local_blobstore_creates_its_root(tmp_path: Path) -> None:
    root = tmp_path / "does" / "not" / "exist" / "yet"
    LocalBlobstore(root)
    assert root.is_dir()


def test_write_then_read_text_roundtrips(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("manifest.json", '{"schema": 1}')
    assert store.read_text("manifest.json") == '{"schema": 1}'


def test_write_creates_nested_directories(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("output/SetTnpB/SetTnpB.gb", "LOCUS ...")
    assert (tmp_path / "output" / "SetTnpB" / "SetTnpB.gb").read_text() == "LOCUS ..."


def test_read_text_missing_raises_blobstore_error(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    with pytest.raises(BlobstoreError):
        store.read_text("nope.json")


def test_write_is_atomic_no_partial_file_left_on_disk(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("status.json", "first")
    store.write_text("status.json", "second")
    # No stray .tmp-* files left behind, and the final content is the LAST write.
    assert store.read_text("status.json") == "second"
    leftovers = [p for p in tmp_path.rglob("*.tmp-*")]
    assert leftovers == []


def test_write_overwrites_existing_file_cleanly(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("a.txt", "x" * 1000)
    store.write_text("a.txt", "y")  # much shorter — proves it's not appended/truncated wrong
    assert store.read_text("a.txt") == "y"


def test_exists_true_and_false(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    assert store.exists("control/cancel") is False
    store.write_text("control/cancel", "")
    assert store.exists("control/cancel") is True


def test_list_prefix_returns_every_file_under_it(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("output/a.txt", "1")
    store.write_text("output/nested/b.txt", "2")
    store.write_text("other/c.txt", "3")
    assert store.list_prefix("output") == ["output/a.txt", "output/nested/b.txt"]


def test_list_prefix_missing_prefix_is_empty(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    assert store.list_prefix("does/not/exist") == []


def test_list_prefix_never_returns_tmp_files(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    for i in range(5):
        store.write_text("status.json", f"write {i}")
    assert store.list_prefix("") == ["status.json"]


def test_download_file_copies_to_a_local_path(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path / "store")
    store.write_text("input/locus.fasta", ">seq\nACGT\n")
    dest = tmp_path / "staged" / "locus.fasta"
    store.download_file("input/locus.fasta", dest)
    assert dest.read_text() == ">seq\nACGT\n"


def test_download_file_missing_raises(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    with pytest.raises(BlobstoreError):
        store.download_file("nope", tmp_path / "dest")


def test_upload_file_copies_a_local_output_into_the_store(tmp_path: Path) -> None:
    local = tmp_path / "local_out.fasta"
    local.write_text(">x\nACGT\n", encoding="utf-8")
    store = LocalBlobstore(tmp_path / "store")
    store.upload_file(local, "output/x/x.fasta")
    assert store.read_text("output/x/x.fasta") == ">x\nACGT\n"


def test_upload_file_missing_local_source_raises(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    with pytest.raises(BlobstoreError):
        store.upload_file(tmp_path / "does_not_exist.txt", "output/x")


def test_path_traversal_is_rejected(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    with pytest.raises(BlobstoreError):
        store.write_text("../escape.json", "{}")
    with pytest.raises(BlobstoreError):
        store.read_text("../../etc/passwd")


def test_absolute_path_is_rejected(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    with pytest.raises(BlobstoreError):
        store.write_text(str(tmp_path / "x.json"), "{}")


def test_delete_removes_a_file(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("control/cancel", "")
    assert store.exists("control/cancel")
    store.delete("control/cancel")
    assert not store.exists("control/cancel")


def test_delete_missing_file_is_a_no_op(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.delete("never/existed")  # must not raise


def test_read_json_and_write_json_roundtrip(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    write_json(store, "manifest.json", {"schema": 1, "jobId": "x"})
    assert read_json(store, "manifest.json") == {"schema": 1, "jobId": "x"}


def test_write_json_ends_with_a_trailing_newline(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    write_json(store, "x.json", {"a": 1})
    assert store.read_text("x.json").endswith("\n")


# --- GcsBlobstore: fake transport, no network ---------------------------------------


class _FakeResponse:
    def __init__(self, body: bytes, status: int = 200) -> None:
        self._body = body
        self.status = status

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> _FakeResponse:
        return self

    def __exit__(self, *exc) -> bool:
        return False


class _RecordingOpener:
    """Records every request it was asked to open; returns canned responses in order."""

    def __init__(self, responses: list) -> None:
        self.requests: list = []
        self._responses = list(responses)

    def __call__(self, req):
        self.requests.append(req)
        resp = self._responses.pop(0)
        if isinstance(resp, Exception):
            raise resp
        return resp


def _token_response() -> _FakeResponse:
    return _FakeResponse(json.dumps({"access_token": "fake-token", "expires_in": 3600}).encode())


def test_gcs_blobstore_satisfies_the_protocol() -> None:
    store = GcsBlobstore("bucket", "jobs/x/", opener=_RecordingOpener([]))
    assert isinstance(store, Blobstore)


def test_gcs_write_text_fetches_a_token_then_puts_the_object() -> None:
    opener = _RecordingOpener([_token_response(), _FakeResponse(b"{}")])
    store = GcsBlobstore("my-bucket", "jobs/abc/", opener=opener)
    store.write_text("status.json", '{"stage": "running"}')

    assert len(opener.requests) == 2
    token_req, upload_req = opener.requests
    assert "metadata.google.internal" in token_req.full_url
    assert token_req.headers.get("Metadata-flavor") == "Google"
    assert "storage.googleapis.com" in upload_req.full_url
    assert "my-bucket" in upload_req.full_url
    assert "jobs%2Fabc%2Fstatus.json" in upload_req.full_url or "jobs/abc/status.json" in upload_req.full_url
    assert upload_req.headers.get("Authorization") == "Bearer fake-token"
    assert upload_req.data == b'{"stage": "running"}'


def test_gcs_read_text_uses_alt_media() -> None:
    opener = _RecordingOpener([_token_response(), _FakeResponse(b"hello")])
    store = GcsBlobstore("bucket", "jobs/x/", opener=opener)
    text = store.read_text("manifest.json")
    assert text == "hello"
    _, read_req = opener.requests
    assert "alt=media" in read_req.full_url


def test_gcs_exists_true_when_object_found() -> None:
    opener = _RecordingOpener([_token_response(), _FakeResponse(b"{}")])
    store = GcsBlobstore("bucket", "jobs/x/", opener=opener)
    assert store.exists("control/cancel") is True


def test_gcs_exists_false_on_404() -> None:
    import urllib.error

    opener = _RecordingOpener(
        [
            _token_response(),
            urllib.error.HTTPError("url", 404, "Not Found", {}, None),
        ]
    )
    store = GcsBlobstore("bucket", "jobs/x/", opener=opener)
    assert store.exists("control/cancel") is False


def test_gcs_other_http_error_raises_blobstore_error() -> None:
    import urllib.error

    opener = _RecordingOpener(
        [
            _token_response(),
            urllib.error.HTTPError("url", 403, "Forbidden", {}, None),
        ]
    )
    store = GcsBlobstore("bucket", "jobs/x/", opener=opener)
    with pytest.raises(BlobstoreError):
        store.exists("control/cancel")


def test_gcs_token_is_cached_across_calls() -> None:
    opener = _RecordingOpener([_token_response(), _FakeResponse(b"{}"), _FakeResponse(b"{}")])
    store = GcsBlobstore("bucket", "jobs/x/", opener=opener)
    store.write_text("a.json", "{}")
    store.write_text("b.json", "{}")
    # Only ONE token fetch for two writes — the second write reuses the cached token.
    token_fetches = [r for r in opener.requests if "metadata.google.internal" in r.full_url]
    assert len(token_fetches) == 1


def test_gcs_path_traversal_is_rejected() -> None:
    store = GcsBlobstore("bucket", "jobs/x/", opener=_RecordingOpener([]))
    with pytest.raises(BlobstoreError):
        store.write_text("../escape.json", "{}")


def test_gcs_list_prefix_strips_the_job_prefix() -> None:
    listing = {
        "items": [
            {"name": "jobs/x/output/a.txt"},
            {"name": "jobs/x/output/nested/b.txt"},
        ]
    }
    opener = _RecordingOpener([_token_response(), _FakeResponse(json.dumps(listing).encode())])
    store = GcsBlobstore("bucket", "jobs/x/", opener=opener)
    assert store.list_prefix("output") == ["output/a.txt", "output/nested/b.txt"]


def test_gcs_upload_file_reads_local_bytes_and_puts_them(tmp_path: Path) -> None:
    local = tmp_path / "out.fasta"
    local.write_bytes(b">x\nACGT\n")
    opener = _RecordingOpener([_token_response(), _FakeResponse(b"{}")])
    store = GcsBlobstore("bucket", "jobs/x/", opener=opener)
    store.upload_file(local, "output/x/x.fasta")
    _, upload_req = opener.requests
    assert upload_req.data == b">x\nACGT\n"


def test_gcs_upload_file_missing_local_source_raises(tmp_path: Path) -> None:
    store = GcsBlobstore("bucket", "jobs/x/", opener=_RecordingOpener([]))
    with pytest.raises(BlobstoreError):
        store.upload_file(tmp_path / "missing.txt", "output/x")
