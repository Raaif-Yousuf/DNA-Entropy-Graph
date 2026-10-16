"""scripts/check_trivy_allowlist.py -- the accepted-CVE list expires itself (#255)."""

from __future__ import annotations

import datetime as dt
import importlib.util
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parent.parent / "check_trivy_allowlist.py"
REPO = SCRIPT.parent.parent
TODAY = dt.date(2026, 10, 16)


@pytest.fixture(scope="module")
def mod():
    spec = importlib.util.spec_from_file_location("check_trivy_allowlist", SCRIPT)
    assert spec and spec.loader
    m = importlib.util.module_from_spec(spec)
    sys.modules["check_trivy_allowlist"] = m
    spec.loader.exec_module(m)
    return m


def _entry(cve="CVE-2026-1111", stmt="no fix upstream, not reachable", exp="2026-12-01") -> str:
    return f"  - id: {cve}\n    statement: {stmt}\n    expired_at: {exp}\n"


def test_empty_list_is_valid(mod):
    assert mod.problems("vulnerabilities: []\n", TODAY) == []


def test_comments_and_blank_lines_are_ignored(mod):
    assert mod.problems("# why\n\nvulnerabilities:\n" + _entry(), TODAY) == []


def test_a_live_entry_inside_the_horizon_is_valid(mod):
    assert mod.problems("vulnerabilities:\n" + _entry(), TODAY) == []


def test_an_expired_entry_fails(mod):
    out = mod.problems("vulnerabilities:\n" + _entry(exp="2026-10-15"), TODAY)
    assert any("expired" in p and "CVE-2026-1111" in p for p in out)


def test_an_entry_expiring_today_fails(mod):
    assert mod.problems("vulnerabilities:\n" + _entry(exp="2026-10-16"), TODAY)


def test_an_entry_beyond_the_horizon_fails(mod):
    out = mod.problems("vulnerabilities:\n" + _entry(exp="2027-06-01"), TODAY)
    assert any("days" in p for p in out)


def test_missing_expiry_fails(mod):
    text = "vulnerabilities:\n  - id: CVE-2026-1111\n    statement: why\n"
    assert any("expired_at" in p for p in mod.problems(text, TODAY))


def test_missing_statement_fails(mod):
    text = "vulnerabilities:\n  - id: CVE-2026-1111\n    expired_at: 2026-12-01\n"
    assert any("statement" in p for p in mod.problems(text, TODAY))


def test_a_malformed_id_fails(mod):
    assert mod.problems("vulnerabilities:\n" + _entry(cve="whatever"), TODAY)


def test_a_malformed_date_fails(mod):
    assert mod.problems("vulnerabilities:\n" + _entry(exp="soon"), TODAY)


def test_a_duplicate_id_fails(mod):
    out = mod.problems("vulnerabilities:\n" + _entry() + _entry(), TODAY)
    assert any("duplicate" in p for p in out)


def test_an_unknown_section_fails(mod):
    assert mod.problems("secrets:\n" + _entry(), TODAY)


def test_a_file_with_no_vulnerabilities_key_fails(mod):
    assert mod.problems("# nothing\n", TODAY)


def test_main_reports_ok_and_error(mod, tmp_path, capsys):
    good = tmp_path / "good.yaml"
    good.write_text("vulnerabilities: []\n", encoding="utf-8", newline="\n")
    assert mod.main([str(good)]) == 0
    assert capsys.readouterr().out.startswith("OK:")
    bad = tmp_path / "bad.yaml"
    bad.write_text("vulnerabilities:\n" + _entry(exp="2020-01-01"), encoding="utf-8", newline="\n")
    assert mod.main([str(bad)]) == 1
    assert capsys.readouterr().out.startswith("ERROR:")


def test_main_fails_on_a_missing_file(mod, tmp_path):
    assert mod.main([str(tmp_path / "nope.yaml")]) == 1


def test_the_committed_allowlist_is_valid_today(mod):
    assert mod.problems((REPO / ".trivyignore.yaml").read_text(encoding="utf-8"), dt.date.today()) == []
