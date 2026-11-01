"""Tests for scripts/check_totest_format.py."""

from __future__ import annotations

import os
import subprocess
import sys
from datetime import UTC, datetime
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_totest_format as ctf  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_totest_format.py"), "--self-test"],
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero_and_documents_max_age():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_totest_format.py"), "--help"],
        capture_output=True,
        text=True,
        timeout=15,
    )
    assert proc.returncode == 0
    assert "--max-age-days" in proc.stdout


def test_row_age_days_pure_arithmetic():
    now = datetime(2026, 9, 19, tzinfo=UTC)
    closed = datetime(2026, 8, 1, tzinfo=UTC)
    assert ctf.row_age_days(closed, now) == 49


def test_is_stale_boundary():
    assert ctf.is_stale(46, 45) is True
    assert ctf.is_stale(45, 45) is False
    assert ctf.is_stale(0, 45) is False


def test_parse_rows_skips_header_separator_and_sentinel():
    text = ctf._EMPTY_TABLE
    assert ctf.parse_rows(text) == []


def test_parse_rows_reads_a_real_row():
    text = ctf._GOOD_TABLE.format(sha="deadbee")
    rows = ctf.parse_rows(text)
    assert len(rows) == 1
    assert rows[0].cells[0] == "#12"
    assert rows[0].cells[2] == "installer"


def test_validate_row_shape_wrong_column_count():
    row = ctf.Row(line_no=1, cells=("#1", "abc1234", "installer"))
    problems = ctf.validate_row_shape(row)
    assert any("column(s)" in p for p in problems)


def test_validate_row_shape_bad_needs_value():
    row = ctf.Row(line_no=1, cells=("#1", "abc1234", "bogus-need", "do it", "ok", "not ok"))
    problems = ctf.validate_row_shape(row)
    assert any("Needs" in p for p in problems)


def test_validate_row_shape_accepts_well_formed_row():
    row = ctf.Row(line_no=1, cells=("#1", "abc1234", "installer", "do it", "ok", "not ok"))
    assert ctf.validate_row_shape(row) == []


def test_extract_sha_finds_hex_token():
    assert ctf.extract_sha("abc1234 (build check)") == "abc1234"
    assert ctf.extract_sha("no sha here") is None


def test_check_against_this_repos_real_tree_runs_without_crashing():
    """Not an assertion about the current queue's contents -- ToTest.md's
    real state changes as issues close -- only that the check runs cleanly
    end to end and returns a list."""
    repo_root = SCRIPTS_DIR.parent
    problems = ctf.check(repo_root, max_age_days=ctf.DEFAULT_MAX_AGE_DAYS)
    assert isinstance(problems, list)


def _row(line_no, issue, do_this):
    return ctf.Row(line_no=line_no, cells=(issue, "abc1234", "installer", do_this, "ok", "not ok"))


def test_find_duplicate_rows_names_both_lines_after_whitespace_normalisation():
    rows = [_row(10, "#31", "Open  a PR\nthen merge"), _row(20, "#31", "open a pr then merge")]
    problems = ctf.find_duplicate_rows(rows)
    assert len(problems) == 1
    assert "L10" in problems[0] and "L20" in problems[0]


def test_find_duplicate_rows_allows_one_issue_with_different_checks():
    rows = [_row(10, "#31", "Open a PR"), _row(20, "#31", "Close the PR")]
    assert ctf.find_duplicate_rows(rows) == []


def test_find_duplicate_rows_allows_same_text_on_different_issues():
    rows = [_row(10, "#31", "Open a PR"), _row(20, "#32", "Open a PR")]
    assert ctf.find_duplicate_rows(rows) == []


def test_check_reports_an_appended_duplicate_row(tmp_path):
    table = ctf._GOOD_TABLE.format(sha="abc1234")
    last = table.strip().splitlines()[-1]
    (tmp_path / "docs").mkdir()
    (tmp_path / ctf.TOTEST_RELATIVE).write_text(table + last + "\n", encoding="utf-8", newline="\n")
    problems = ctf.check(tmp_path, 45)
    assert any("duplicate" in p.lower() for p in problems), problems


# --- #652: a row split across two lines must fail, naming the line ---------------------------------------------

_ROW = "| #12 | {sha} | installer | Install and run once | The tray icon appears | Process runs, no tray icon |"


def _queue(*lines):
    head = (
        "## Queue\n\nProse before the table is fine.\n\n"
        "| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |\n"
        "| --- | --- | --- | --- | --- | --- |\n"
    )
    return head + "\n".join(lines) + "\n"


def test_a_well_formed_table_has_no_stray_lines():
    assert ctf.find_stray_lines(_queue(_ROW.format(sha="abc1234"))) == []


def test_prose_before_the_table_and_blank_lines_in_it_are_allowed():
    text = _queue(_ROW.format(sha="abc1234"), "", _ROW.format(sha="abc1235"))
    assert ctf.find_stray_lines(text) == []


def test_a_continuation_line_after_a_split_row_is_reported_with_its_line_number():
    row = _ROW.format(sha="abc1234")
    first, second = row[:60], row[60:]
    text = _queue(first, second)
    problems = ctf.find_stray_lines(text)
    line_of_second = text.splitlines().index(second) + 1
    assert any(p.startswith(f"L{line_of_second}:") for p in problems), problems


def test_a_row_with_no_closing_pipe_is_reported():
    # The first half of a split row still has six cells by a count of '|', so only the missing closing pipe shows it.
    row = "| #12 | abc1234 | installer | Install | Passing | False pass"
    problems = ctf.find_stray_lines(_queue(row))
    assert any("closing" in p for p in problems), problems


def test_a_heading_after_the_table_ends_the_scan():
    text = _queue(_ROW.format(sha="abc1234")) + "\n## Later\n\nFree prose here.\n"
    assert ctf.find_stray_lines(text) == []


def test_a_non_row_line_directly_under_the_table_is_reported():
    text = _queue(_ROW.format(sha="abc1234"), "stray words")
    assert len(ctf.find_stray_lines(text)) == 1


def test_check_fails_on_a_split_row_in_a_real_file(tmp_path):
    sha = _init_repo(tmp_path)
    row = _ROW.format(sha=sha)
    (tmp_path / "docs").mkdir()
    (tmp_path / ctf.TOTEST_RELATIVE).write_text(_queue(row[:70], row[70:]), encoding="utf-8", newline="\n")
    problems = ctf.check(tmp_path, 36500)
    assert any("not a table row" in p or "closing" in p for p in problems), problems


def test_check_is_clean_for_the_same_row_unsplit(tmp_path):
    sha = _init_repo(tmp_path)
    (tmp_path / "docs").mkdir()
    (tmp_path / ctf.TOTEST_RELATIVE).write_text(_queue(_ROW.format(sha=sha)), encoding="utf-8", newline="\n")
    assert ctf.check(tmp_path, 36500) == []


def _init_repo(root):
    return ctf._init_throwaway_repo(root)


def test_the_stray_line_scanner_sees_rows_in_the_real_queue():
    # Vacuity: the scanner must have walked at least one real table row, not returned [] for lack of a table.
    text = (SCRIPTS_DIR.parent / ctf.TOTEST_RELATIVE).read_text(encoding="utf-8")
    assert ctf.count_table_lines(text) >= 3


def test_a_lone_carriage_return_inside_a_cell_is_reported():
    # MEASURED 2026-10-03 (#106): the split was a bare CR (a PowerShell backtick-r), not an LF.
    row = _ROW.format(sha="abc1234").replace("Install and", "Install\rand")
    assert len(ctf.find_stray_lines(_queue(row))) == 2


# --- #1092: one batched git call for every sha, never two processes per row ---


def _git_env():
    return {k: v for k, v in os.environ.items() if k not in ("GIT_DIR", "GIT_INDEX_FILE", "GIT_WORK_TREE")}


def _git_out(tmp_path, *args):
    return subprocess.run(["git", *args], cwd=tmp_path, check=True, capture_output=True, text=True, env=_git_env()).stdout.strip()


def _make_repo(tmp_path):
    def run(*args):
        return _git_out(tmp_path, *args)

    run("init", "-q")
    run("config", "user.email", "t@example.com")
    run("config", "user.name", "T")
    (tmp_path / "f.txt").write_text("x\n", encoding="utf-8", newline="\n")
    run("add", "f.txt")
    run("commit", "-q", "-m", "one")
    commit = run("rev-parse", "HEAD")
    tree = run("rev-parse", "HEAD^{tree}")
    return commit, tree


def _table(shas):
    rows = "\n".join(f"| #{i + 1} | {sha} | installer | Do thing {i} | Works | Does not |" for i, sha in enumerate(shas))
    return (
        "## Queue\n\n| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |\n"
        "| --- | --- | --- | --- | --- | --- |\n" + rows + "\n"
    )


def _count_git_calls(monkeypatch):
    calls = []
    real = subprocess.run

    def spy(cmd, *a, **kw):
        if cmd and cmd[0] == "git":
            calls.append(cmd)
        return real(cmd, *a, **kw)

    monkeypatch.setattr(ctf.subprocess, "run", spy)
    return calls


def _extra_commits(tmp_path, n):
    shas = []
    for k in range(n):
        (tmp_path / "f.txt").write_text(f"v{k}\n", encoding="utf-8", newline="\n")
        _git_out(tmp_path, "commit", "-q", "-am", f"c{k}")
        shas.append(_git_out(tmp_path, "rev-parse", "HEAD"))
    return shas


def test_check_spawns_exactly_one_git_process_for_many_distinct_commits(tmp_path, monkeypatch):
    first, _tree = _make_repo(tmp_path)
    shas = [first, *_extra_commits(tmp_path, 4)]
    assert len(set(shas)) == 5
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs" / "ToTest.md").write_text(_table(shas * 3), encoding="utf-8", newline="\n")
    calls = _count_git_calls(monkeypatch)
    assert ctf.check(tmp_path, 36500) == []
    assert len(calls) == 1, calls


def test_batch_parity_missing_short_nonmerge_and_stale_shas(tmp_path):
    commit, tree = _make_repo(tmp_path)
    bogus = "0000000deadbeef0000000deadbeef00000000"
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs" / "ToTest.md").write_text(_table([commit, bogus, commit[:7], tree]), encoding="utf-8", newline="\n")
    problems = ctf.check(tmp_path, 36500)
    assert problems == [
        f"L6: sha '{bogus}' is not a commit this repo has (git cat-file -e failed)",
        f"L8: sha '{tree}' is not a commit this repo has (git cat-file -e failed)",
    ]
    stale = ctf.check(tmp_path, -1)
    assert [p.split(":")[0] for p in stale if "day(s) old" in p] == ["L5", "L7"]


def test_batch_outside_a_repo_reports_one_clear_git_failure(tmp_path):
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs" / "ToTest.md").write_text(_table(["abc1234", "def5678"]), encoding="utf-8", newline="\n")
    problems = ctf.check(tmp_path, 45)
    assert len(problems) == 1 and problems[0].startswith("git cat-file failed:"), problems


def test_resolve_commit_dates_matches_the_single_sha_helpers(tmp_path):
    commit, _tree = _make_repo(tmp_path)
    got = ctf.resolve_commit_dates(tmp_path, [commit, "0000000deadbeef0000000deadbeef00000000"])
    assert "0000000deadbeef0000000deadbeef00000000" not in got
    assert got[commit] is not None
    assert got[commit] == ctf.commit_date(tmp_path, commit)


def _hash_commit(tmp_path, data):
    return (
        subprocess.run(
            ["git", "hash-object", "-t", "commit", "-w", "--literally", "--stdin"],
            cwd=tmp_path,
            input=data,
            check=True,
            capture_output=True,
            env=_git_env(),
        )
        .stdout.decode()
        .strip()
    )


def _raw_commit(tree, author_line, message=b"m", extra_header=b""):
    head = b"tree " + tree.encode() + b"\n"
    if author_line is not None:
        head += author_line + b"\n"
    return head + b"committer T <t@e> 1700000000 +0000\n" + extra_header + b"\n" + message + b"\n"


def _single_row_check(tmp_path, sha, max_age=36500):
    (tmp_path / "docs").mkdir(exist_ok=True)
    (tmp_path / "docs" / "ToTest.md").write_text(_table([sha]), encoding="utf-8", newline="\n")
    return ctf.check(tmp_path, max_age)


def test_huge_author_timestamp_is_a_could_not_read_a_date_problem_not_a_traceback(tmp_path):
    _commit, tree = _make_repo(tmp_path)
    sha = _hash_commit(tmp_path, _raw_commit(tree, b"author T <t@e> 99999999999999999999 +0000"))
    assert _single_row_check(tmp_path, sha) == [f"L5: could not read a date for commit '{sha}'"]


def test_an_out_of_range_tz_offset_does_not_crash_and_still_dates_the_commit(tmp_path):
    _commit, tree = _make_repo(tmp_path)
    sha = _hash_commit(tmp_path, _raw_commit(tree, b"author T <t@e> 1700000000 +9900"))
    assert ctf.resolve_commit_dates(tmp_path, [sha])[sha] == datetime.fromtimestamp(1700000000, UTC)
    assert _single_row_check(tmp_path, sha, max_age=-1)[0].startswith("L5: row closed by")


def test_a_commit_with_no_author_header_is_a_could_not_read_a_date_problem(tmp_path):
    _commit, tree = _make_repo(tmp_path)
    sha = _hash_commit(tmp_path, _raw_commit(tree, None))
    assert ctf.resolve_commit_dates(tmp_path, [sha]) == {sha: None}
    assert _single_row_check(tmp_path, sha) == [f"L5: could not read a date for commit '{sha}'"]


def test_an_annotated_tag_sha_resolves_to_its_peeled_commit_date(tmp_path):
    commit, _tree = _make_repo(tmp_path)
    _git_out(tmp_path, "tag", "-a", "v1", "-m", "x")
    tag = _git_out(tmp_path, "rev-parse", "v1")
    assert tag != commit
    assert ctf.resolve_commit_dates(tmp_path, [tag])[tag] == ctf.commit_date(tmp_path, commit)
    assert _single_row_check(tmp_path, tag) == []


def test_a_latin1_commit_body_does_not_desync_the_framing_for_the_next_sha(tmp_path):
    commit, tree = _make_repo(tmp_path)
    body = _raw_commit(
        tree,
        b"author T <t@e> 1700000000 +0000",
        message="caf\xe9 \xe9\xe9\xe9".encode("latin-1"),
        extra_header=b"encoding ISO-8859-1\n",
    )
    latin = _hash_commit(tmp_path, body)
    got = ctf.resolve_commit_dates(tmp_path, [latin, commit])
    assert got[latin] == datetime.fromtimestamp(1700000000, UTC)
    assert got[commit] == ctf.commit_date(tmp_path, commit)


def _fake_run(stdout, returncode=0, stderr=b""):
    def fake(cmd, *a, **kw):
        return subprocess.CompletedProcess(cmd, returncode, stdout, stderr)

    return fake


def _fake_record(sha):
    body = b"tree " + b"0" * 40 + b"\nauthor T <t@e> 1700000000 +0000\n\nm\n"
    return sha.encode() + b" commit " + str(len(body)).encode() + b"\n" + body + b"\n"


def test_an_ambiguous_sha_record_is_not_a_commit_and_framing_continues(tmp_path, monkeypatch):
    a, b = "a" * 7, "b" * 40
    monkeypatch.setattr(ctf.subprocess, "run", _fake_run(f"{a}^{{commit}} ambiguous\n".encode() + _fake_record(b)))
    got = ctf.resolve_commit_dates(tmp_path, [a, b])
    assert a not in got
    assert got[b] == datetime.fromtimestamp(1700000000, UTC)


def test_a_truncated_batch_stream_is_one_clear_problem(tmp_path, monkeypatch):
    shas = ["a" * 40, "b" * 40, "c" * 40]
    monkeypatch.setattr(ctf.subprocess, "run", _fake_run(_fake_record(shas[0]) + _fake_record(shas[1])))
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs" / "ToTest.md").write_text(_table(shas), encoding="utf-8", newline="\n")
    problems = ctf.check(tmp_path, 36500)
    assert len(problems) == 1, problems
    assert problems[0].startswith("git cat-file failed:") and "truncated" in problems[0]


def test_a_nonzero_git_exit_reports_its_stderr_once(tmp_path, monkeypatch):
    monkeypatch.setattr(ctf.subprocess, "run", _fake_run(b"", returncode=128, stderr=b"fatal: boom\n"))
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs" / "ToTest.md").write_text(_table(["a" * 40, "b" * 40]), encoding="utf-8", newline="\n")
    assert ctf.check(tmp_path, 36500) == ["git cat-file failed: fatal: boom"]
