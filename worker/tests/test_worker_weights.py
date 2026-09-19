"""Tests for worker/weights.py: cache/models/<id>/... check/restore/save, LocalBlobstore only."""

from __future__ import annotations

from pathlib import Path

from dna_entropy.worker.blobstore import LocalBlobstore
from dna_entropy.worker.weights import cache_prefix, is_cached, restore_from_cache, save_to_cache


def test_cache_prefix_shape() -> None:
    assert cache_prefix("evo2_7b") == "cache/models/evo2_7b/"


def test_is_cached_false_when_nothing_there(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    assert is_cached(store, "evo2_7b") is False


def test_is_cached_true_after_something_is_cached(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("cache/models/evo2_7b/weights.bin", "fake-weights")
    assert is_cached(store, "evo2_7b") is True


def test_is_cached_does_not_confuse_different_models(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text("cache/models/evo2_7b_262k/weights.bin", "x")
    assert is_cached(store, "evo2_7b") is False  # different model id, same prefix start


def test_restore_from_cache_copies_every_file(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path / "store")
    store.write_text("cache/models/evo2_7b/config.json", '{"a": 1}')
    store.write_text("cache/models/evo2_7b/weights/shard0.bin", "shard0")
    store.write_text("cache/models/evo2_7b/weights/shard1.bin", "shard1")

    local = tmp_path / "local"
    count = restore_from_cache(store, "evo2_7b", local)

    assert count == 3
    assert (local / "config.json").read_text() == '{"a": 1}'
    assert (local / "weights" / "shard0.bin").read_text() == "shard0"
    assert (local / "weights" / "shard1.bin").read_text() == "shard1"


def test_restore_from_cache_returns_zero_when_nothing_cached(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path / "store")
    count = restore_from_cache(store, "evo2_7b", tmp_path / "local")
    assert count == 0


def test_save_to_cache_uploads_every_file(tmp_path: Path) -> None:
    local = tmp_path / "downloaded"
    (local / "sub").mkdir(parents=True)
    (local / "config.json").write_text('{"a": 1}', encoding="utf-8")
    (local / "sub" / "shard0.bin").write_text("shard0", encoding="utf-8")

    store = LocalBlobstore(tmp_path / "store")
    count = save_to_cache(store, "evo2_7b", local)

    assert count == 2
    assert store.read_text("cache/models/evo2_7b/config.json") == '{"a": 1}'
    assert store.read_text("cache/models/evo2_7b/sub/shard0.bin") == "shard0"


def test_save_to_cache_missing_dir_is_zero_not_an_error(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path / "store")
    count = save_to_cache(store, "evo2_7b", tmp_path / "does_not_exist")
    assert count == 0


def test_roundtrip_save_then_restore(tmp_path: Path) -> None:
    downloaded = tmp_path / "downloaded"
    downloaded.mkdir()
    (downloaded / "weights.bin").write_bytes(b"\x00\x01\x02binary-ish")

    store = LocalBlobstore(tmp_path / "store")
    save_to_cache(store, "evo2_7b", downloaded)

    restored = tmp_path / "restored"
    count = restore_from_cache(store, "evo2_7b", restored)

    assert count == 1
    assert (restored / "weights.bin").read_bytes() == b"\x00\x01\x02binary-ish"
