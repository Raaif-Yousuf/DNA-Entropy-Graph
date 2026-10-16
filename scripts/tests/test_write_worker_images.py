"""scripts/write_worker_images.py writes the just-built image digests into worker-images.json (#178, #458)."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import write_worker_images as wwi  # noqa: E402

D1 = "sha256:" + "a" * 64
D2 = "sha256:" + "b" * 64
REPO = "ghcr.io/raaif-yousuf/dna-entropy-worker"


def test_entry_is_keyed_by_version_without_v_and_pinned_by_digest():
    doc = wwi.add_entry({"images": []}, "v1.2.3", D1, D2)
    assert doc == {"images": [{"version": "1.2.3", "cuda": f"{REPO}@{D1}", "cpu": f"{REPO}@{D2}"}]}


def test_same_version_is_replaced_not_duplicated():
    first = wwi.add_entry({"images": []}, "1.0.0", D1, D2)
    second = wwi.add_entry(first, "1.0.0", D2, D1)
    assert len(second["images"]) == 1
    assert second["images"][0]["cuda"].endswith(D2)


@pytest.mark.parametrize("bad", ["latest", "sha256:abc", "sha256:" + "A" * 64, "", "sha256:" + "a" * 63, "a" * 64])
def test_a_digest_that_is_not_sha256_and_64_lowercase_hex_is_refused(bad):
    with pytest.raises(ValueError):
        wwi.add_entry({"images": []}, "1.0.0", bad, D2)
    with pytest.raises(ValueError):
        wwi.add_entry({"images": []}, "1.0.0", D1, bad)


def test_a_document_without_an_images_array_is_refused():
    with pytest.raises(ValueError):
        wwi.add_entry({}, "1.0.0", D1, D2)


def _run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "write_worker_images.py"), *args],
        capture_output=True,
        text=True,
        timeout=30,
    )


def test_cli_rewrites_the_file_with_lf_and_utf8(tmp_path):
    f = tmp_path / "worker-images.json"
    f.write_bytes(b'{\n  "images": []\n}\n')
    proc = _run("--file", str(f), "--version", "v0.0.1", "--cuda-digest", D1, "--cpu-digest", D2)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    raw = f.read_bytes()
    assert b"\r" not in raw
    assert json.loads(raw)["images"][0]["version"] == "0.0.1"


def test_cli_exits_1_on_a_bad_digest_and_leaves_the_file_alone(tmp_path):
    f = tmp_path / "worker-images.json"
    f.write_bytes(b'{"images": []}')
    proc = _run("--file", str(f), "--version", "0.0.1", "--cuda-digest", "oops", "--cpu-digest", D2)
    assert proc.returncode == 1
    assert "ERROR:" in proc.stdout + proc.stderr
    assert f.read_bytes() == b'{"images": []}'
