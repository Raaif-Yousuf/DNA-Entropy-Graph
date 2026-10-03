"""Tests for scripts/check_third_party_notices.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_third_party_notices as ctpn  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_third_party_notices.py"), "--self-test"],
        capture_output=True, text=True, timeout=60,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_third_party_notices.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_check_notices_missing_gives_clean_notice(tmp_path):
    problems, notices = ctpn.check(tmp_path)
    assert problems == []
    assert any(f"#{ctpn.NOTICES_ISSUE}" in n for n in notices)


def test_check_notices_present_generator_missing_gives_clean_notice(tmp_path):
    (tmp_path / ctpn.NOTICES_RELATIVE).write_text("# notices\n", encoding="utf-8")
    problems, notices = ctpn.check(tmp_path)
    assert problems == []
    assert notices


def test_check_against_this_repos_real_tree_is_clean(real_dependency_tree):
    """THIRD-PARTY-NOTICES.md and scripts/gen_third_party_notices.py both exist and agree
    as of this session (issue #34 landed): the real generate-and-diff must find this
    repo's own committed file clean, not merely a synthetic fixture. This is the one test
    in this file that would have caught the pre-#34 permanent no-op (it always returned
    problems == [] "clean" without ever regenerating anything to compare against)."""
    repo_root = SCRIPTS_DIR.parent
    problems, notices = ctpn.check(repo_root)
    assert problems == [], f"real tree should be clean; got {problems}"
    assert notices == []


def test_a_planted_stale_line_in_the_real_committed_file_is_caught(real_dependency_tree, tmp_path):
    """Copies this repo's real, currently-clean THIRD-PARTY-NOTICES.md, appends one line,
    and proves the real generator (not a fake) disagrees with it -- the decisive proof that
    this check is not a false pass on the real dependency tree, only on synthetic fixtures."""
    repo_root = SCRIPTS_DIR.parent
    real_notices = (repo_root / ctpn.NOTICES_RELATIVE).read_text(encoding="utf-8")
    (tmp_path / ctpn.NOTICES_RELATIVE).write_text(real_notices + "\nplanted stale line\n", encoding="utf-8")
    (tmp_path / "scripts").mkdir()
    (tmp_path / ctpn.GENERATOR_RELATIVE).write_text(
        (repo_root / ctpn.GENERATOR_RELATIVE).read_text(encoding="utf-8"), encoding="utf-8"
    )
    # The generator resolves the vendored manifest + assets against the checked root, so the
    # tmp root needs them too (copied, so the real tree is never touched).
    import shutil
    shutil.copy(repo_root / "scripts" / "vendored_assets.json", tmp_path / "scripts" / "vendored_assets.json")
    import json
    for entry in json.loads((repo_root / "scripts" / "vendored_assets.json").read_text(encoding="utf-8"))["assets"]:
        for rel in (entry["path"], entry["license_file"]):
            (tmp_path / rel).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(repo_root / rel, tmp_path / rel)
    problems, _notices = ctpn.check(tmp_path, app_root=repo_root / "app", worker_root=repo_root / "worker")
    assert problems and any("stale" in p for p in problems)


def test_check_reports_a_vendored_sha_mismatch_naming_the_file(tmp_path):
    """The real generator, a real manifest, a tampered asset: the check must fail and the
    problem text must name the file (the sha256 gate, not just byte-staleness)."""
    import hashlib
    import json

    repo_root = SCRIPTS_DIR.parent
    (tmp_path / "scripts").mkdir()
    (tmp_path / "web").mkdir()
    (tmp_path / "web" / "lib.js").write_bytes(b"tampered\n")
    (tmp_path / "web" / "lib.LICENSE.txt").write_text("MIT\n", encoding="utf-8")
    (tmp_path / "scripts" / "vendored_assets.json").write_text(json.dumps({"assets": [{
        "path": "web/lib.js", "name": "lib.js", "description": "d", "version": "1", "license": "MIT",
        "license_file": "web/lib.LICENSE.txt", "source": "s",
        "sha256": hashlib.sha256(b"original\n").hexdigest(),
    }]}), encoding="utf-8")
    (tmp_path / ctpn.NOTICES_RELATIVE).write_text("# anything\n", encoding="utf-8")
    (tmp_path / ctpn.GENERATOR_RELATIVE).write_text(
        (repo_root / ctpn.GENERATOR_RELATIVE).read_text(encoding="utf-8"), encoding="utf-8"
    )
    problems, _ = ctpn.check(tmp_path)
    assert problems and any("web/lib.js" in p and "sha256" in p for p in problems), problems
