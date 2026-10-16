"""Tests for scripts/check_citation_cff.py (#168). Temp files only."""

from __future__ import annotations

import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_citation_cff as ccc  # noqa: E402

GOOD = """\
cff-version: 1.2.0
message: "If you use this software, please cite it as below."
title: "DNA Entropy Graph"
type: software
authors:
  - family-names: "Yousuf"
    given-names: "Raaif"
license: MIT
repository-code: "https://github.com/Raaif-Yousuf/DNA-Entropy-Graph"
"""


def _write(tmp_path: Path, text: str) -> Path:
    p = tmp_path / "CITATION.cff"
    p.write_text(text, encoding="utf-8", newline="\n")
    return p


def test_good_file_is_clean(tmp_path):
    assert ccc.check(_write(tmp_path, GOOD)) == []


def test_missing_file_fails(tmp_path):
    problems = ccc.check(tmp_path / "CITATION.cff")
    assert problems and "missing" in problems[0]


def test_missing_required_key_fails(tmp_path):
    problems = ccc.check(_write(tmp_path, GOOD.replace('title: "DNA Entropy Graph"\n', "")))
    assert any("title" in p for p in problems)


def test_wrong_cff_version_fails(tmp_path):
    problems = ccc.check(_write(tmp_path, GOOD.replace("1.2.0", "1.1.0")))
    assert any("cff-version" in p for p in problems)


def test_author_without_family_name_fails(tmp_path):
    problems = ccc.check(_write(tmp_path, GOOD.replace('  - family-names: "Yousuf"\n', "  - name: x\n")))
    assert any("family-names" in p for p in problems)


def test_tab_indent_fails(tmp_path):
    problems = ccc.check(_write(tmp_path, GOOD.replace("  - family", "\t- family")))
    assert any("tab" in p for p in problems)
