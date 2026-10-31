"""Bounded-memory, resumable transfers (issue #496).

A multi-GB weights shard must never be held in RAM next to a loading model, and one blip must
not restart a multi-GB upload from zero. Every test runs against ``fake_gcs.FakeGcs``, which
models the GCS resumable-upload protocol and records each request, so "no request body is
larger than one chunk" is an assertion on what was actually sent.
"""

from __future__ import annotations

import os
from pathlib import Path

import pytest
from fake_gcs import FakeGcs

import dna_entropy.worker.blobstore as mod
from dna_entropy.worker.blobstore import (
    BlobstoreError,
    GcsBlobstore,
    LocalBlobstore,
    TransferCancelled,
)
from dna_entropy.worker.weights import cache_prefix, save_to_cache

CHUNK = 256 * 1024
PREFIX = "jobs/j1/"


def _store(fake: FakeGcs, sleeps: list[float] | None = None) -> GcsBlobstore:
    return GcsBlobstore(
        "bkt",
        PREFIX,
        opener=fake,
        sleep=(lambda s: sleeps.append(s)) if sleeps is not None else (lambda _s: None),
        chunk_bytes=CHUNK,
    )


def _payload(n: int) -> bytes:
    return os.urandom(n)


def _puts(fake: FakeGcs) -> list[tuple[str, str, int, dict[str, str]]]:
    return [r for r in fake.requests if r[0] == "PUT"]


def test_chunk_size_must_be_a_multiple_of_256_kib() -> None:
    with pytest.raises(ValueError, match="256"):
        GcsBlobstore("b", "p/", opener=FakeGcs("b"), chunk_bytes=CHUNK + 1)


def test_the_default_chunk_is_a_multiple_of_256_kib() -> None:
    assert mod._CHUNK_BYTES % (256 * 1024) == 0
    assert mod._CHUNK_BYTES >= 256 * 1024


def test_a_three_chunk_file_is_sent_as_three_content_range_puts(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    src = tmp_path / "shard.bin"
    data = _payload(3 * CHUNK)
    src.write_bytes(data)
    _store(fake).upload_file(src, "cache/shard.bin")
    puts = _puts(fake)
    assert len(puts) == 3
    ranges = [r[3]["Content-range"] for r in puts]
    assert ranges == [
        f"bytes 0-{CHUNK - 1}/{3 * CHUNK}",
        f"bytes {CHUNK}-{2 * CHUNK - 1}/{3 * CHUNK}",
        f"bytes {2 * CHUNK}-{3 * CHUNK - 1}/{3 * CHUNK}",
    ]
    assert fake.objects[PREFIX + "cache/shard.bin"] == data


def test_no_request_body_is_larger_than_one_chunk(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    src = tmp_path / "shard.bin"
    src.write_bytes(_payload(5 * CHUNK + 123))  # a ragged last chunk
    _store(fake).upload_file(src, "cache/shard.bin")
    assert fake.max_body <= CHUNK
    assert len(_puts(fake)) == 6
    assert len(fake.objects[PREFIX + "cache/shard.bin"]) == 5 * CHUNK + 123


def test_a_file_that_fits_in_one_chunk_is_not_sent_as_a_resumable_session(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    src = tmp_path / "small.bin"
    src.write_bytes(b"tiny")
    _store(fake).upload_file(src, "small.bin")
    assert fake.sessions == {}
    assert fake.objects[PREFIX + "small.bin"] == b"tiny"


def test_an_empty_file_uploads_as_an_empty_object(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    src = tmp_path / "empty.bin"
    src.write_bytes(b"")
    _store(fake).upload_file(src, "empty.bin")
    assert fake.objects[PREFIX + "empty.bin"] == b""


@pytest.mark.parametrize("mode", ["fail_before_commit", "lose_response_after_commit"])
def test_a_failed_chunk_resumes_from_the_committed_offset_not_from_zero(tmp_path: Path, mode: str) -> None:
    """A 503 on chunk 1: the committed offset is asked of the server (a ``bytes */total`` PUT)
    and only the missing bytes are sent. ``lose_response_after_commit`` is the nasty one: the
    server HAS the chunk, so re-sending it would be non-contiguous."""
    fake = FakeGcs("bkt")
    getattr(fake, mode).add(1)
    src = tmp_path / "shard.bin"
    data = _payload(3 * CHUNK)
    src.write_bytes(data)
    sleeps: list[float] = []
    _store(fake, sleeps).upload_file(src, "cache/shard.bin")
    assert fake.objects[PREFIX + "cache/shard.bin"] == data
    queries = [r for r in _puts(fake) if r[3]["Content-range"].startswith("bytes */")]
    assert len(queries) == 1
    assert sleeps, "a retry must back off"
    assert fake.chunk_puts == (3 if mode == "lose_response_after_commit" else 4)


def test_a_chunk_that_keeps_failing_gives_up_with_a_blobstore_error(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    fake.fail_before_commit.update(range(1, 100))
    src = tmp_path / "shard.bin"
    src.write_bytes(_payload(3 * CHUNK))
    with pytest.raises(BlobstoreError):
        _store(fake).upload_file(src, "cache/shard.bin")
    assert PREFIX + "cache/shard.bin" not in fake.objects


def test_a_non_retriable_session_error_does_not_retry(tmp_path: Path) -> None:
    import urllib.error
    from email.message import Message

    fake = FakeGcs("bkt")

    def _boom(idx: int) -> None:
        raise urllib.error.HTTPError("u", 403, "Forbidden", Message(), None)

    fake.on_chunk_put = _boom
    src = tmp_path / "shard.bin"
    src.write_bytes(_payload(3 * CHUNK))
    with pytest.raises(BlobstoreError, match="403"):
        _store(fake).upload_file(src, "cache/shard.bin")
    assert len(_puts(fake)) == 1


def test_should_stop_is_polled_between_chunks_and_aborts_the_upload(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    src = tmp_path / "shard.bin"
    src.write_bytes(_payload(4 * CHUNK))
    polls = {"n": 0}

    def _stop() -> bool:
        polls["n"] += 1
        return True  # first poll is between chunk 1 and chunk 2

    with pytest.raises(TransferCancelled):
        _store(fake).upload_file(src, "cache/shard.bin", should_stop=_stop)
    assert polls["n"] == 1
    assert fake.chunk_puts == 1
    assert PREFIX + "cache/shard.bin" not in fake.objects


def test_the_weights_mirror_stops_mid_file_and_writes_no_marker(tmp_path: Path) -> None:
    """The #75 cold-review Done-when: a one-file, several-chunk upload cancelled after chunk 1
    leaves no marker, so the partial set is never restored."""
    fake = FakeGcs("bkt")
    store = _store(fake)
    local = tmp_path / "hf"
    local.mkdir()
    (local / "shard.bin").write_bytes(_payload(4 * CHUNK))
    state = {"n": 0}

    def _stop() -> bool:
        state["n"] += 1
        return state["n"] > 1  # pass before the file, stop between chunk 1 and 2

    assert save_to_cache(store, "evo2_7b", local, should_stop=_stop) == 0
    assert fake.chunk_puts == 1
    assert not any(n.endswith("complete.json") or "marker" in n for n in fake.objects)
    assert [n for n in fake.objects if n.startswith(PREFIX + cache_prefix("evo2_7b"))] == []


def test_the_weights_mirror_still_completes_through_the_chunked_path(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    store = _store(fake)
    local = tmp_path / "hf"
    local.mkdir()
    data = _payload(3 * CHUNK + 7)
    (local / "shard.bin").write_bytes(data)
    assert save_to_cache(store, "evo2_7b", local) == 1
    assert fake.objects[PREFIX + cache_prefix("evo2_7b") + "shard.bin"] == data


# --- download ----------------------------------------------------------------------------


def test_download_streams_in_bounded_reads_to_the_destination(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    data = _payload(5 * CHUNK + 9)
    fake.objects[PREFIX + "big.bin"] = data
    dest = tmp_path / "out" / "big.bin"
    _store(fake).download_file("big.bin", dest)
    assert dest.read_bytes() == data
    assert fake.read_sizes and None not in fake.read_sizes, "a whole-body read() was made"
    assert max(s for s in fake.read_sizes if s is not None) <= CHUNK
    assert not list(dest.parent.glob("*.part"))


def test_a_missing_object_leaves_neither_dest_nor_part(tmp_path: Path) -> None:
    dest = tmp_path / "out" / "x.bin"
    with pytest.raises(BlobstoreError):
        _store(FakeGcs("bkt")).download_file("nope.bin", dest)
    assert not dest.exists()
    assert not list(dest.parent.glob("*"))


def test_a_stream_cut_midway_resumes_with_a_range_request(tmp_path: Path) -> None:
    fake = FakeGcs("bkt")
    data = _payload(4 * CHUNK)
    fake.objects[PREFIX + "big.bin"] = data
    fake.break_download_after = CHUNK + 5
    dest = tmp_path / "big.bin"
    _store(fake).download_file("big.bin", dest)
    assert dest.read_bytes() == data
    gets = [r for r in fake.requests if r[0] == "GET" and "alt=media" in r[1]]
    assert len(gets) == 2
    assert (
        "Range" not in gets[0][3] and gets[1][3]["Range"] == f"bytes={CHUNK + 5}-"
    )  # the bytes already on disk


def test_a_download_that_never_recovers_leaves_no_file_at_dest(tmp_path: Path) -> None:
    class _AlwaysBreaks(FakeGcs):
        def __call__(self, req):
            self.break_download_after = 10
            return super().__call__(req)

    fake = _AlwaysBreaks("bkt")
    fake.objects[PREFIX + "big.bin"] = _payload(2 * CHUNK)
    dest = tmp_path / "big.bin"
    with pytest.raises(BlobstoreError):
        _store(fake).download_file("big.bin", dest)
    assert not dest.exists()
    assert not list(tmp_path.glob("*.part"))


def test_a_failed_download_does_not_clobber_an_existing_dest(tmp_path: Path) -> None:
    class _AlwaysBreaks(FakeGcs):
        def __call__(self, req):
            self.break_download_after = 10
            return super().__call__(req)

    fake = _AlwaysBreaks("bkt")
    fake.objects[PREFIX + "big.bin"] = _payload(2 * CHUNK)
    dest = tmp_path / "big.bin"
    dest.write_bytes(b"previous good copy")
    with pytest.raises(BlobstoreError):
        _store(fake).download_file("big.bin", dest)
    assert dest.read_bytes() == b"previous good copy"


# --- LocalBlobstore ------------------------------------------------------------------------


def test_local_transfers_never_read_the_whole_file_into_memory(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    def _no_read_bytes(self):  # noqa: ANN001
        raise AssertionError("Path.read_bytes() would hold the whole file in RAM")

    s = LocalBlobstore(tmp_path / "root")
    src = tmp_path / "src.bin"
    data = _payload(3 * CHUNK)
    src.write_bytes(data)
    monkeypatch.setattr(Path, "read_bytes", _no_read_bytes)
    s.upload_file(src, "a/b.bin")
    dest = tmp_path / "d" / "b.bin"
    s.download_file("a/b.bin", dest)
    monkeypatch.undo()
    assert dest.read_bytes() == data
