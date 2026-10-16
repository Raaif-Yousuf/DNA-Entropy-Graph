"""Tests for scripts/wave_findings.py (#477): draft issues from pasted wave findings, flag duplicates, never file."""

from __future__ import annotations

import io
import json
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import wave_findings as wf  # noqa: E402

# A stub `gh`: logs its argv, prints the canned issue list for `issue list`, and FAILS the test run if it is ever asked
# to create or close anything (the tool only drafts).
GH_STUB = """\
import json, sys
from pathlib import Path
data = Path(sys.argv[1])
log = data.with_suffix(".log")
log.open("a", encoding="utf-8", newline="\\n").write(json.dumps(sys.argv[2:]) + "\\n")
if sys.argv[2:4] == ["issue", "list"]:
    if (data.parent / "gh_fail").exists():
        sys.stderr.write("HTTP 502")
        sys.exit(1)
    print(data.read_text(encoding="utf-8"))
    sys.exit(0)
sys.exit(99)
"""

ISSUES = [
    {
        "number": 478,
        "title": "tooling: check_app_wiring.py truncates constructor params at the first ')' and splits nested generics",
        "state": "OPEN",
    },
    {"number": 12, "title": "viewer: igv.js track colours ignore the dark theme", "state": "CLOSED"},
]

TWO_LINES = (
    "## Suspected bugs\n"
    "- check_app_wiring truncates constructor params at the first paren (scripts/check_app_wiring.py:88)\n"
    "- The Results page flickers when the run history refreshes twice (app/src/DnaEntropyGraph.App/Views/ResultsPage.xaml.cs:41)\n"
    "\n"
    "some prose that is not a finding\n"
)


@pytest.fixture()
def gh(tmp_path):
    stub = tmp_path / "gh_stub.py"
    stub.write_text(GH_STUB, encoding="utf-8", newline="\n")
    data = tmp_path / "issues.json"
    data.write_text(json.dumps(ISSUES), encoding="utf-8", newline="\n")
    return [sys.executable, str(stub), str(data)], data


def _calls(data: Path) -> list[list[str]]:
    log = data.with_suffix(".log")
    return [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()] if log.exists() else []


def test_parse_takes_bullets_with_an_optional_trailing_location_and_ignores_prose():
    found = wf.parse_findings(TWO_LINES)
    assert [f.location for f in found] == ["scripts/check_app_wiring.py:88", "app/src/DnaEntropyGraph.App/Views/ResultsPage.xaml.cs:41"]
    assert found[0].text.startswith("check_app_wiring truncates")
    assert wf.parse_findings("- no location here\n* star bullet (a/b.py:3)\n")[0].location == ""
    assert [f.location for f in wf.parse_findings("- no location here\n* star bullet (a/b.py:3)\n")] == ["", "a/b.py:3"]
    assert wf.parse_findings("") == []


def test_markdown_decoration_is_stripped_from_the_text():
    (finding,) = wf.parse_findings("- **THEORY (unverified):** the `foo` loop stalls (scripts/x.py:1)\n")
    assert "**" not in finding.text and "`" not in finding.text
    assert "foo" in finding.text


def test_area_and_prefix_are_guessed_from_the_path():
    assert wf.guess_area("scripts/land_wt.py:5") == ("area:packaging", "tooling")
    assert wf.guess_area("app/src/DnaEntropyGraph.Cloud/X.cs:1")[0] == "area:cloud"
    assert wf.guess_area("app/src/DnaEntropyGraph.App/Views/A.xaml:1")[0] == "area:app"
    assert wf.guess_area("worker/src/dna_entropy/a.py:1")[0] == "area:worker"
    assert wf.guess_area("docs/tests.md:3")[0] == "area:docs"
    assert wf.guess_area("")[0] == "area:app"  # no path: the draft still needs a label the owner can change


def test_two_findings_one_duplicate_prints_one_duplicate_line_and_one_draft(gh, capsys):
    argv, data = gh
    src = io.StringIO(TWO_LINES)
    assert wf.run(src, argv) == 0
    out = capsys.readouterr().out
    assert out.count("DUPLICATE") == 1 and "#478" in out
    assert out.count("gh issue create") == 1
    assert "--label 'area:app'" in out and "--label 'P3'" in out
    assert "## Done when" in out and "Results page flickers" in out
    assert out.isascii()
    # the duplicate gets no draft; every gh call was a read-only `issue list` with --state all and --limit 50
    calls = _calls(data)
    assert calls and all(c[:2] == ["issue", "list"] for c in calls)
    assert all("--state" in c and c[c.index("--state") + 1] == "all" and c[c.index("--limit") + 1] == "50" for c in calls)


def test_a_closed_duplicate_is_marked_closed(gh, capsys):
    argv, _ = gh
    wf.run(io.StringIO("- igv.js track colours ignore the dark theme again (app/src/Viewer/igv.js:3)\n"), argv)
    out = capsys.readouterr().out
    assert "DUPLICATE" in out and "#12" in out and "closed" in out.lower()
    assert "gh issue create" not in out


def test_an_unrelated_finding_that_shares_one_word_is_not_a_duplicate(gh, capsys):
    argv, _ = gh
    wf.run(io.StringIO("- The wiring diagram in the docs is out of date (docs/architecture.md:9)\n"), argv)
    out = capsys.readouterr().out
    assert "DUPLICATE" not in out and out.count("gh issue create") == 1


def test_a_failed_search_warns_and_still_drafts(gh, capsys):
    argv, data = gh
    (data.parent / "gh_fail").write_text("x", encoding="utf-8")
    assert wf.run(io.StringIO("- a brand new problem with the thing (scripts/x.py:1)\n"), argv) == 0
    out = capsys.readouterr().out
    assert "WARNING" in out and "unchecked" in out and out.count("gh issue create") == 1


def test_quotes_in_a_finding_cannot_break_out_of_the_drafted_command(gh, capsys):
    argv, _ = gh
    wf.run(io.StringIO("- it's a 'quoted' problem with `code` and a dollar $x (scripts/q.py:2)\n"), argv)
    out = capsys.readouterr().out
    title_line = next(line for line in out.splitlines() if "gh issue create" in line)
    assert "it''s" in title_line  # PowerShell single-quote escaping


def test_non_ascii_text_is_printed_as_ascii(gh, capsys):
    argv, _ = gh
    wf.run(io.StringIO("- the caf\u00e9 \u2014 a dash and an accent break the thing (scripts/u.py:1)\n"), argv)
    assert capsys.readouterr().out.isascii()


def test_main_reads_a_file_and_an_empty_input_is_a_usage_error(gh, tmp_path, capsys):
    argv, _ = gh
    findings = tmp_path / "f.md"
    findings.write_text(TWO_LINES, encoding="utf-8", newline="\n")
    assert wf.main([str(findings)], gh=argv) == 0
    empty = tmp_path / "e.md"
    empty.write_text("nothing to see\n", encoding="utf-8", newline="\n")
    assert wf.main([str(empty)], gh=argv) == 2
    assert "no findings" in capsys.readouterr().out


def test_main_reads_stdin_when_no_file_is_given(gh, monkeypatch, capsys):
    argv, _ = gh
    monkeypatch.setattr(sys, "stdin", io.StringIO("- a fresh stdin finding about the thing (scripts/s.py:4)\n"))
    assert wf.main([], gh=argv) == 0
    assert "gh issue create" in capsys.readouterr().out


def test_gh_option_replaces_the_executable(monkeypatch, tmp_path):
    seen = []
    monkeypatch.setattr(wf, "run", lambda src, gh_argv: seen.append(gh_argv) or 0)
    findings = tmp_path / "f.md"
    findings.write_text("- x (a:1)\n", encoding="utf-8", newline="\n")
    assert wf.main(["--gh", r"C:\Program Files\GitHub CLI\gh.exe", str(findings)]) == 0
    assert seen == [[r"C:\Program Files\GitHub CLI\gh.exe"]]


def test_the_module_never_names_a_mutating_gh_verb_in_an_argv():
    text = (SCRIPTS_DIR / "wave_findings.py").read_text(encoding="utf-8")
    assert '"create"' not in text and '"close"' not in text and '"edit"' not in text and '"comment"' not in text


def test_two_shared_words_are_not_enough_even_when_they_are_most_of_a_short_finding():
    issues = [{"number": 5, "title": "tooling: land_wt junction cleanup", "state": "OPEN"}]
    assert wf.find_duplicate(wf.keywords("land_wt junction"), issues) is None  # 2 of 2 shared, still below 3 words


def test_three_shared_words_in_a_long_finding_are_not_enough_either():
    issues = [{"number": 5, "title": "tooling: land_wt junction cleanup", "state": "OPEN"}]
    words = wf.keywords("land_wt junction cleanup but also the premerge gate prints stale advice about retries")
    assert len(words) == wf.MAX_KEYWORDS
    assert wf.find_duplicate(words, issues) is None  # 3 of 8 shared is below the 60% overlap


def test_three_shared_words_covering_most_of_the_finding_is_a_duplicate():
    issues = [{"number": 5, "title": "tooling: land_wt junction cleanup", "state": "OPEN"}]
    assert wf.find_duplicate(wf.keywords("land_wt junction cleanup fails"), issues) == issues[0]


def test_the_search_asks_for_every_state_with_a_limit_of_fifty(gh):
    argv, data = gh
    wf.run(io.StringIO("- something entirely different happens here (scripts/z.py:1)\n"), argv)
    (call,) = _calls(data)
    assert call[call.index("--state") + 1] == "all" and call[call.index("--limit") + 1] == "50"
