"""Tests for scripts/fix_eol.py (#462): make a stale CRLF working tree LF so `dotnet format` stops reporting ENDOFLINE."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import fix_eol  # noqa: E402

LISTING = (
    "i/lf    w/crlf  attr/text=auto eol=lf \tapp/a.cs\n"
    "i/lf    w/lf    attr/text=auto eol=lf \tapp/b.cs\n"
    "i/crlf  w/crlf  attr/text eol=crlf    \tscripts/x.ps1\n"
    "i/lf    w/mixed attr/text=auto eol=lf \tdocs/c.md\n"
    "i/-text w/-text attr/-text            \tworker/tests/data/malformed/bin.fa\n"
    "i/lf    w/crlf  attr/text=auto eol=lf \tdir with space/d.txt\n"
)


def test_only_files_committed_as_lf_but_not_lf_in_the_tree_are_listed():
    assert fix_eol.stale_files(LISTING) == ["app/a.cs", "docs/c.md", "dir with space/d.txt"]


def test_eol_crlf_attribute_files_are_crlf_in_the_tree_on_purpose():
    listing = "i/lf    w/crlf  attr/text eol=crlf            \tapp/x.sln\n"
    assert fix_eol.stale_files(listing) == []


def test_a_committed_crlf_file_and_a_binary_are_never_touched():
    assert "scripts/x.ps1" not in fix_eol.stale_files(LISTING)
    # committed CRLF (index side crlf) is the repo's own choice, whatever the attributes say
    assert fix_eol.stale_files("i/crlf  w/crlf  attr/text=auto            \told.cs\n") == []
    assert "worker/tests/data/malformed/bin.fa" not in fix_eol.stale_files(LISTING)


def test_normalize_rewrites_crlf_to_lf_as_bytes_and_leaves_a_lone_cr(tmp_path):
    target = tmp_path / "a.txt"
    target.write_bytes(b"one\r\ntwo\r\nthree\rfour\r\n")
    assert fix_eol.normalize(tmp_path, ["a.txt"]) == ["a.txt"]
    assert target.read_bytes() == b"one\ntwo\nthree\rfour\n"
    assert fix_eol.normalize(tmp_path, ["a.txt"]) == []  # idempotent: nothing left to change


def _git(cwd: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=cwd, capture_output=True, check=False)
    assert proc.returncode == 0, proc.stderr.decode(errors="replace")
    return proc.stdout.decode("utf-8", errors="replace")


@pytest.fixture()
def stale_repo(tmp_path):
    _git(tmp_path, "init", "-q")
    _git(tmp_path, "config", "user.email", "t@example.invalid")
    _git(tmp_path, "config", "user.name", "t")
    (tmp_path / ".gitattributes").write_bytes(b"* text=auto eol=lf\n")
    (tmp_path / "a.cs").write_bytes(b"class A {}\n")
    (tmp_path / "ok.cs").write_bytes(b"class B {}\n")
    _git(tmp_path, "add", "-A")
    _git(tmp_path, "commit", "-q", "-m", "init")
    (tmp_path / "a.cs").write_bytes(b"class A {}\r\n")  # a tool or an old checkout left CRLF behind
    return tmp_path


def test_check_reports_the_stale_file_and_fails_without_changing_it(stale_repo, capsys):
    assert fix_eol.main(["--check", "--root", str(stale_repo)]) == 1
    assert (stale_repo / "a.cs").read_bytes() == b"class A {}\r\n"
    out = capsys.readouterr().out
    assert "a.cs" in out and "ok.cs" not in out and out.isascii()


def test_fix_makes_the_tree_lf_and_a_second_run_is_clean(stale_repo, capsys):
    assert fix_eol.main(["--root", str(stale_repo)]) == 0
    assert (stale_repo / "a.cs").read_bytes() == b"class A {}\n"
    assert _git(stale_repo, "status", "--porcelain").strip() == ""  # the tree now matches the index
    assert fix_eol.main(["--check", "--root", str(stale_repo)]) == 0
    assert "a.cs" in capsys.readouterr().out


def test_a_clean_tree_exits_zero(tmp_path):
    _git(tmp_path, "init", "-q")
    (tmp_path / "x.txt").write_bytes(b"x\n")
    _git(tmp_path, "add", "-A")
    assert fix_eol.main(["--check", "--root", str(tmp_path)]) == 0
