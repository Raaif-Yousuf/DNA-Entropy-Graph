"""One contract, two implementations (issue #40's observable).

Every test here runs against BOTH :class:`LocalBlobstore` (a temp directory) and
:class:`GcsBlobstore` (backed by a stateful in-memory fake of the GCS JSON API, marked
``gcs``): the worker must not be able to tell them apart (job_contract.md section 1). The
HTTP-shape assertions (auth headers, URL encoding, retries) stay in
``test_worker_blobstore.py``; this module only asserts behaviour a caller can observe.

No test here makes a network call: the fake is injected as the ``opener``.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from fake_gcs import FakeGcs

from dna_entropy.worker.blobstore import Blobstore, BlobstoreError, GcsBlobstore, LocalBlobstore

PREFIX = "jobs/j1/"


@pytest.fixture(params=["local", pytest.param("gcs", marks=pytest.mark.gcs)])
def store(request, tmp_path: Path) -> Blobstore:
    if request.param == "local":
        return LocalBlobstore(tmp_path / "root")
    fake = FakeGcs("bkt", page_size=3)
    s = GcsBlobstore("bkt", PREFIX, opener=fake, sleep=lambda _s: None)
    s.fake = fake  # type: ignore[attr-defined]
    return s


def test_text_roundtrip_including_non_ascii_and_newlines(store: Blobstore) -> None:
    text = "line one\nline two é\n"
    store.write_text("a/b.txt", text)
    assert store.read_text("a/b.txt") == text


def test_write_overwrites(store: Blobstore) -> None:
    store.write_text("x.txt", "one")
    store.write_text("x.txt", "two")
    assert store.read_text("x.txt") == "two"


def test_missing_object_raises_blobstore_error(store: Blobstore) -> None:
    with pytest.raises(BlobstoreError):
        store.read_text("nope.txt")


def test_exists(store: Blobstore) -> None:
    assert store.exists("control/cancel") is False
    store.write_text("control/cancel", "")
    assert store.exists("control/cancel") is True


def test_list_prefix_returns_job_relative_sorted_paths(store: Blobstore) -> None:
    for p in ("output/a/1.txt", "output/a/2.txt", "output/b/3.txt", "manifest.json"):
        store.write_text(p, "x")
    assert store.list_prefix("output/") == ["output/a/1.txt", "output/a/2.txt", "output/b/3.txt"]
    assert store.list_prefix("output/b/") == ["output/b/3.txt"]
    assert store.list_prefix("nothing/") == []


def test_list_prefix_returns_every_object_when_the_listing_spans_several_pages(store: Blobstore) -> None:
    """Issue #40 (found auditing): GCS returns at most one page per call (1000 objects in
    production; 3 in this fake) with a ``nextPageToken``. A client that reads only the
    first page silently drops the rest of a large batch's outputs."""
    expected = [f"output/n/{i:03d}.txt" for i in range(10)]
    for p in expected:
        store.write_text(p, "x")
    assert store.list_prefix("output/") == expected


def test_upload_then_download_file_roundtrips_binary_bytes(store: Blobstore, tmp_path: Path) -> None:
    payload = bytes(range(256)) * 4  # not valid UTF-8 text
    src = tmp_path / "src.bin"
    src.write_bytes(payload)
    store.upload_file(src, "output/s/blob.bin")
    dest = tmp_path / "deep" / "dir" / "copy.bin"
    store.download_file("output/s/blob.bin", dest)
    assert dest.read_bytes() == payload


def test_download_of_a_missing_object_raises_and_leaves_no_file(store: Blobstore, tmp_path: Path) -> None:
    dest = tmp_path / "never.bin"
    with pytest.raises(BlobstoreError):
        store.download_file("missing.bin", dest)
    assert not dest.exists()


def test_upload_of_a_missing_local_file_raises(store: Blobstore, tmp_path: Path) -> None:
    with pytest.raises(BlobstoreError):
        store.upload_file(tmp_path / "ghost.bin", "x.bin")


@pytest.mark.parametrize("bad", ["../escape.txt", "a/../../escape.txt", "/abs.txt"])
def test_escaping_paths_are_rejected_on_every_operation(store: Blobstore, bad: str, tmp_path: Path) -> None:
    src = tmp_path / "f"
    src.write_bytes(b"x")
    for call in (
        lambda: store.read_text(bad),
        lambda: store.write_text(bad, "x"),
        lambda: store.exists(bad),
        lambda: store.list_prefix(bad),
        lambda: store.download_file(bad, tmp_path / "d"),
        lambda: store.upload_file(src, bad),
    ):
        with pytest.raises(BlobstoreError):
            call()


# --- LocalBlobstore only: atomic temp+rename for EVERY write path (issue #40) --------------


def test_local_upload_file_is_atomic_temp_then_rename(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A reader of the destination must never see a half-copied file: upload_file writes a
    temp sibling and swaps it in with os.replace, like write_text."""
    import dna_entropy.worker.blobstore as mod

    s = LocalBlobstore(tmp_path / "root")
    src = tmp_path / "src.bin"
    src.write_bytes(b"complete payload")
    seen: list[tuple[bool, bytes]] = []
    real_replace = mod.os.replace

    def _spy(tmp, dest):
        seen.append((Path(dest).exists(), Path(tmp).read_bytes()))
        real_replace(tmp, dest)

    monkeypatch.setattr(mod.os, "replace", _spy)
    s.upload_file(src, "output/x/f.bin")
    assert seen == [(False, b"complete payload")], "destination existed before the swap, or no swap happened"
    assert not [p for p in (tmp_path / "root").rglob("*") if ".tmp-" in p.name]


def test_local_download_file_is_atomic_temp_then_rename(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import dna_entropy.worker.blobstore as mod

    s = LocalBlobstore(tmp_path / "root")
    s.write_text("input/in.txt", "payload")
    seen: list[bool] = []
    real_replace = mod.os.replace

    def _spy(tmp, dest):
        seen.append(Path(dest).exists())
        real_replace(tmp, dest)

    dest = tmp_path / "staged" / "in.txt"
    monkeypatch.setattr(mod.os, "replace", _spy)
    s.download_file("input/in.txt", dest)
    assert seen == [False]
    assert dest.read_text(encoding="utf-8") == "payload"
    assert not [p for p in dest.parent.iterdir() if ".tmp-" in p.name]
