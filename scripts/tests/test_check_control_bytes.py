"""Tests for scripts/check_control_bytes.py (#1016).

Every forbidden byte is built at runtime (bytes([n])) so this file itself holds none and the
real guard never flags it.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_control_bytes as ccb  # noqa: E402

BS = bytes([0x08])
VT = bytes([0x0B])
ESC = bytes([0x1B])
NUL = bytes([0x00])

needs_git = pytest.mark.skipif(shutil.which("git") is None, reason="git not installed")


def test_clean_file_passes() -> None:
    assert ccb.scan_bytes("a.txt", b"hello\nworld\n") == []


def test_tab_cr_lf_are_text() -> None:
    assert ccb.scan_bytes("a.txt", b"a\tb\r\nc\n") == []


def test_esc_is_allowed_for_ansi_logs() -> None:
    assert ccb.scan_bytes("log.txt", ESC + b"[31mred" + ESC + b"[0m\n") == []


def test_backspace_is_named_with_file_line_column() -> None:
    found = ccb.scan_bytes("dir/a.txt", b"ok\nab" + BS + b"c\n")
    assert [(f.path, f.line, f.column, f.byte) for f in found] == [("dir/a.txt", 2, 3, 0x08)]
    assert str(found[0]) == "dir/a.txt:2:3: control byte 0x08"


def test_every_forbidden_byte_is_caught_and_neighbours_pass() -> None:
    forbidden = [*range(0x00, 0x09), 0x0B, 0x0C, *range(0x0E, 0x1B), *range(0x1C, 0x20)]
    for n in range(0, 0x20):
        # NUL would make the file binary under git's rule, so test the raw scanner (no binary rule)
        got = ccb.scan_bytes("f", b"x" + bytes([n]) + b"y")
        assert bool(got) == (n in forbidden), hex(n)


def test_first_column_and_first_line() -> None:
    found = ccb.scan_bytes("f", VT + b"abc")
    assert (found[0].line, found[0].column) == (1, 1)


def test_findings_per_file_are_capped() -> None:
    assert len(ccb.scan_bytes("f", BS * 100)) == ccb.MAX_PER_FILE


def test_is_binary_uses_nul_in_first_8000_bytes() -> None:
    assert ccb.is_binary(b"abc" + NUL + b"def")
    assert not ccb.is_binary(b"x" * 9000 + NUL)


def test_scan_files_skips_binary_and_allowlisted() -> None:
    files = {
        "img.bin": b"\x89PNG" + NUL + BS,
        "bad.txt": b"a" + BS,
        "vendored.js": b"a" + bytes([0x01]),
    }
    found = ccb.scan_files(files, allowlist={"vendored.js"})
    assert [f.path for f in found] == ["bad.txt"]


def test_repo_allowlist_paths_are_listed_in_vendored_assets_json() -> None:
    """The allowlist duplicates a vendored path on purpose (see ALLOWLIST); this keeps the two from drifting."""
    manifest = json.loads((SCRIPTS_DIR / "vendored_assets.json").read_text(encoding="utf-8"))
    vendored = {a["path"] for a in manifest["assets"]}
    assert ccb.ALLOWLIST, "vacuity: the allowlist must not be empty"
    assert set(ccb.ALLOWLIST) <= vendored


def test_repo_allowlist_entries_are_not_stale() -> None:
    """Every allowlisted path must still exist and still hold a control byte, else delete the entry.

    An empty allowlist passes this loop by design (nothing to be stale); the test above guards vacuity.
    """
    repo = SCRIPTS_DIR.parent
    for rel in ccb.ALLOWLIST:
        data = (repo / rel).read_bytes()
        assert ccb.scan_bytes(rel, data), f"stale allowlist entry: {rel}"


def _repo(tmp_path: Path, files: dict[str, bytes], gitattributes: str = "") -> Path:
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True, capture_output=True)
    if gitattributes:
        (tmp_path / ".gitattributes").write_bytes(gitattributes.encode())
    for rel, data in files.items():
        target = tmp_path / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return tmp_path


@needs_git
def test_check_repo_names_planted_byte_in_tracked_and_untracked(tmp_path: Path) -> None:
    root = _repo(tmp_path, {"ok.txt": b"fine\n", "sub/bad.txt": b"l1\nl2 " + BS + b"\n"})
    subprocess.run(["git", "add", "ok.txt"], cwd=root, check=True, capture_output=True)  # sub/bad.txt stays untracked
    found = ccb.check_repo(root)
    assert [(f.path, f.line, f.column) for f in found] == [("sub/bad.txt", 2, 4)]


@needs_git
def test_untracked_findings_are_labelled_and_tracked_ones_are_not(tmp_path: Path) -> None:
    root = _repo(tmp_path, {"t.txt": b"a" + BS, "u.txt": b"a" + BS})
    subprocess.run(["git", "add", "t.txt"], cwd=root, check=True, capture_output=True)
    by_path = {f.path: str(f) for f in ccb.check_repo(root)}
    assert by_path["u.txt"] == "u.txt:1:2: control byte 0x08 (untracked file)"
    assert by_path["t.txt"] == "t.txt:1:2: control byte 0x08"


@needs_git
def test_untracked_files_above_the_size_cap_are_skipped_but_tracked_ones_are_not(tmp_path: Path) -> None:
    big = b"a" + BS + b"x" * ccb.MAX_UNTRACKED_BYTES
    root = _repo(tmp_path, {"big_untracked.txt": big, "big_tracked.txt": big})
    subprocess.run(["git", "add", "big_tracked.txt"], cwd=root, check=True, capture_output=True)
    assert [f.path for f in ccb.check_repo(root)] == ["big_tracked.txt"]


@needs_git
def test_non_utf8_file_name_does_not_crash(tmp_path: Path) -> None:
    if sys.platform == "win32":
        # Windows file names are UTF-16; a lone surrogate name cannot be created on NTFS via Python.
        # Exercise the same code path through the attribute helper with a surrogate-escaped name instead.
        root = _repo(tmp_path, {"a.txt": b"x\n"})
        assert ccb.text_attr_unset(root, ["bad\udcffname.txt"]) == set()
        return
    root = _repo(tmp_path, {})  # pragma: no cover
    with open(os.fsencode(root) + b"/bad\xffname.txt", "wb") as fh:  # pragma: no cover
        fh.write(b"a" + BS)
    assert len(ccb.check_repo(root)) == 1  # pragma: no cover


@needs_git
def test_text_attr_unset_with_source_reads_attributes_from_that_tree(tmp_path: Path) -> None:
    root = _repo(tmp_path, {"raw/x.txt": b"a" + BS, "y.txt": b"a"}, gitattributes="raw/** -text\n")
    subprocess.run(["git", "add", "-A"], cwd=root, check=True, capture_output=True)
    subprocess.run(
        ["git", "-c", "user.email=t@e.invalid", "-c", "user.name=t", "commit", "-q", "-m", "m"],
        cwd=root,
        check=True,
        capture_output=True,
    )
    sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=root, check=True, capture_output=True, text=True).stdout.strip()
    # Remove the attributes file from index and working tree: only --source=<sha> still knows the rule.
    subprocess.run(["git", "rm", "-q", "-f", ".gitattributes"], cwd=root, check=True, capture_output=True)
    assert ccb.text_attr_unset(root, ["raw/x.txt", "y.txt"]) == set()
    assert ccb.text_attr_unset(root, ["raw/x.txt", "y.txt"], source=sha) == {"raw/x.txt"}


@needs_git
def test_main_outside_a_git_repo_exits_2(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("GIT_CEILING_DIRECTORIES", str(tmp_path.parent))
    probe = subprocess.run(["git", "rev-parse"], cwd=tmp_path, capture_output=True, check=False)
    if probe.returncode == 0:
        pytest.skip("tmp dir is inside a git repository on this machine")
    assert ccb.main(["--root", str(tmp_path)]) == 2


@needs_git
def test_check_repo_clean_repo_passes(tmp_path: Path) -> None:
    assert ccb.check_repo(_repo(tmp_path, {"a.txt": b"x\n"})) == []


@needs_git
def test_check_repo_skips_binary_by_nul_and_by_text_attribute(tmp_path: Path) -> None:
    root = _repo(
        tmp_path,
        {"pic.dat": NUL + BS, "fixtures/raw.txt": b"a" + BS, "bad.txt": b"a" + BS},
        gitattributes="fixtures/** -text\n",
    )
    assert [f.path for f in ccb.check_repo(root)] == ["bad.txt"]


@needs_git
def test_main_exit_codes_and_ascii_output(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    (tmp_path / "c").mkdir()
    (tmp_path / "d").mkdir()
    clean = _repo(tmp_path / "c", {"a.txt": b"x\n"})
    assert ccb.main(["--root", str(clean)]) == 0
    dirty = _repo(tmp_path / "d", {"a.txt": b"x" + BS})
    assert ccb.main(["--root", str(dirty)]) == 1
    err = capsys.readouterr().err
    assert "ERROR: a.txt:1:2: control byte 0x08" in err
    assert err.isascii()


def test_self_test_passes() -> None:
    assert ccb.main(["--self-test"]) == 0
