"""Tests for scripts/check_unused_fields.py."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_unused_fields as cuf  # noqa: E402

SCRIPT = SCRIPTS_DIR / "check_unused_fields.py"


def _package(tmp_path: Path) -> Path:
    """A two-module package with one consumed, one write-only and one
    serialization-only field, plus a ClassVar that is not a field at all."""
    root = tmp_path / "pkg"
    root.mkdir()
    (root / "models.py").write_text(
        "from dataclasses import dataclass\n"
        "from typing import ClassVar\n"
        "\n"
        "@dataclass\n"
        "class Job:\n"
        "    consumed: int = 0\n"
        "    only_serialized: int = 0\n"
        "    never_read: int = 0\n"
        "    version: ClassVar[int] = 1\n"
        "\n"
        "    def to_dict(self):\n"
        "        return {'a': self.only_serialized}\n",
        encoding="utf-8",
    )
    (root / "use.py").write_text(
        "from .models import Job\n"
        "\n"
        "def go(job):\n"
        "    job.never_read = 5\n"
        "    other = Job(never_read=6)\n"
        "    return job.consumed + other.consumed\n",
        encoding="utf-8",
    )
    return root


def _allowlist(tmp_path: Path, name: str, allowed: dict) -> Path:
    p = tmp_path / name
    p.write_text(json.dumps({"allowed": allowed}), encoding="utf-8")
    return p


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "--self-test"],
        capture_output=True, text=True, timeout=60,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "self-test passed" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_a_write_and_a_kwarg_are_not_reads(tmp_path):
    root = _package(tmp_path)
    findings, _ = cuf.check(root, _allowlist(tmp_path, "none.json", {}))
    kinds = {f.key: f.kind for f in findings}
    assert kinds["Job.never_read"] == "UNREAD"


def test_a_field_read_only_while_serializing_is_reported_separately(tmp_path):
    root = _package(tmp_path)
    findings, _ = cuf.check(root, _allowlist(tmp_path, "none.json", {}))
    kinds = {f.key: f.kind for f in findings}
    assert kinds["Job.only_serialized"] == "SERIALIZED-ONLY"


def test_a_consumed_field_and_a_classvar_are_not_flagged(tmp_path):
    root = _package(tmp_path)
    findings, _ = cuf.check(root, _allowlist(tmp_path, "none.json", {}))
    keys = {f.key for f in findings}
    assert "Job.consumed" not in keys
    assert "Job.version" not in keys


def test_a_class_wildcard_covers_every_field_of_that_class(tmp_path):
    root = _package(tmp_path)
    findings, stale = cuf.check(root, _allowlist(tmp_path, "wild.json", {"Job.*": "wire format"}))
    assert findings == []
    assert stale == []


def test_a_wildcard_for_a_class_that_does_not_exist_is_stale(tmp_path):
    root = _package(tmp_path)
    _, stale = cuf.check(root, _allowlist(tmp_path, "gone.json", {"Missing.*": "gone"}))
    assert len(stale) == 1
    assert "Missing.*" in stale[0]


def test_an_allowlist_entry_for_a_field_that_is_read_is_stale(tmp_path):
    """The half of the guard that keeps the allowlist from rotting. Without it
    the file becomes a list of things that used to be true, which is worse than
    no allowlist because it reads as a set of considered decisions."""
    root = _package(tmp_path)
    _, stale = cuf.check(root, _allowlist(tmp_path, "rot.json", {"Job.consumed": "r"}))
    assert len(stale) == 1
    assert stale[0].startswith("Job.consumed")
    assert "is read now" in stale[0]


def test_a_read_in_an_extra_root_clears_the_finding(tmp_path):
    """A generator outside the package can be a field's only consumer, which is
    the real case scripts/gen_manifest_schema.py and ErrorCodeSpec.raised_by
    made concrete."""
    root = _package(tmp_path)
    gen = tmp_path / "gen"
    gen.mkdir()
    (gen / "render.py").write_text(
        "def render(job):" + chr(10) + "    return job.never_read" + chr(10),
        encoding="utf-8",
    )
    findings, _ = cuf.check(
        root, _allowlist(tmp_path, "none.json", {}), extra_read_roots=(gen,)
    )
    assert "Job.never_read" not in {f.key for f in findings}


def test_an_extra_root_contributes_no_fields_of_its_own(tmp_path):
    """Reads only. A dataclass defined in scripts/ is not the worker's contract
    and must not start failing this guard."""
    root = _package(tmp_path)
    gen = tmp_path / "gen"
    gen.mkdir()
    (gen / "helper.py").write_text(
        "from dataclasses import dataclass" + chr(10)
        + chr(10)
        + "@dataclass" + chr(10)
        + "class Helper:" + chr(10)
        + "    unused_here: int = 0" + chr(10),
        encoding="utf-8",
    )
    findings, _ = cuf.check(
        root, _allowlist(tmp_path, "none.json", {}), extra_read_roots=(gen,)
    )
    assert "Helper.unused_here" not in {f.key for f in findings}


def test_an_unparseable_file_in_an_extra_root_does_not_fail_the_guard(tmp_path):
    root = _package(tmp_path)
    gen = tmp_path / "gen"
    gen.mkdir()
    (gen / "broken.py").write_text("def (:::", encoding="utf-8")
    findings, _ = cuf.check(
        root, _allowlist(tmp_path, "none.json", {}), extra_read_roots=(gen,)
    )
    assert {f.key for f in findings} == {"Job.never_read", "Job.only_serialized"}


def test_a_package_with_no_dataclasses_is_an_error_not_a_pass(tmp_path):
    """A guard that passes because it found nothing to check is not passing."""
    root = tmp_path / "empty"
    root.mkdir()
    (root / "mod.py").write_text("x = 1\n", encoding="utf-8")
    try:
        cuf.check(root, _allowlist(tmp_path, "none.json", {}))
    except SystemExit as exc:
        assert "no dataclass fields" in str(exc)
    else:
        raise AssertionError("expected the guard to refuse a package with nothing to check")


def test_check_against_this_repos_real_tree_runs_without_crashing():
    """Not an assertion that this repo is currently clean -- that is what the
    guard's own run reports, and what several concurrent agents may be changing
    right now. Only that it runs end to end and returns the two lists."""
    repo_root = SCRIPTS_DIR.parent
    findings, stale = cuf.check(
        repo_root / "worker/src/dna_entropy",
        repo_root / "scripts/unused_fields_allowlist.json",
        extra_read_roots=(repo_root / "scripts",),
    )
    assert isinstance(findings, list)
    assert isinstance(stale, list)
