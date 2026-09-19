"""Tests for scripts/issue_precheck.py.

Deliberately network-free: every test either exercises a pure function
directly or drives `scan()`/the CLI with `--no-gh` / a pre-populated `known`
dict of state="unknown", so nothing here ever shells out to a real `gh`
against Raaif-Yousuf/DNA-Entropy-Graph (or anywhere else). A test whose
result depends on a live issue's state is a test that fails for reasons
unrelated to the code -- the module's own docstring makes exactly this point
about `--no-gh`.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import issue_precheck as ip  # noqa: E402


def _init_repo(root: Path) -> None:
    subprocess.run(["git", "init", "-q"], cwd=root, check=True, capture_output=True)
    subprocess.run(["git", "config", "user.email", "test@example.com"], cwd=root, check=True, capture_output=True)
    subprocess.run(["git", "config", "user.name", "Test"], cwd=root, check=True, capture_output=True)


def _commit(root: Path, path: str, content: str, message: str) -> None:
    p = root / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8")
    subprocess.run(["git", "add", path], cwd=root, check=True, capture_output=True)
    subprocess.run(["git", "commit", "-q", "-m", message], cwd=root, check=True, capture_output=True)


# ---------------------------------------------------------------------------
# Repo identity and this repo's own path/label conventions
# ---------------------------------------------------------------------------

def test_repo_constant_targets_dna_entropy_graph():
    assert ip.REPO == "Raaif-Yousuf/DNA-Entropy-Graph"


def test_classify_uses_app_worker_scripts_prefixes():
    assert ip._classify("worker/src/dna_entropy/cli.py") == "code"
    assert ip._classify("app/DnaEntropyGraph/App.xaml.cs") == "code"
    assert ip._classify("scripts/issue_precheck.py") == "code"
    # CLAIR's own path prefixes must not still be recognised as code.
    assert ip._classify("backend/routers/chat.py") == "docs"
    assert ip._classify("frontend/src/App.jsx") == "docs"


def test_classify_puts_tests_in_their_own_bucket():
    assert ip._classify("worker/tests/test_cli.py") == "tests"


def test_node_id_scan_dirs_point_at_worker():
    assert ip._NODE_ID_SCAN_DIRS == ("worker/tests", "worker/src/dna_entropy")


def test_doc_prefixes_have_no_clair_specific_filenames():
    joined = " ".join(ip.DOC_PREFIXES)
    assert "CODEX_PROMPT" not in joined
    assert "VALIDATION_PLAN" not in joined
    assert "OWNER_TODO" not in joined


# ---------------------------------------------------------------------------
# Evidence.deferred / IssueMeta.deferred: post-v1, not CLAIR's post-alpha
# ---------------------------------------------------------------------------

def test_evidence_deferred_on_post_v1_milestone():
    e = ip.Evidence(number=1, milestone="post-v1")
    assert e.deferred is True


def test_evidence_deferred_on_post_v1_label_case_insensitive():
    e = ip.Evidence(number=1, labels=("Post-V1",))
    assert e.deferred is True


def test_evidence_not_deferred_on_other_milestone():
    e = ip.Evidence(number=1, milestone="v0.1 walking skeleton")
    assert e.deferred is False


def test_issue_meta_deferred_matches_evidence_deferred():
    assert ip.IssueMeta(milestone="post-v1").deferred is True
    assert ip.IssueMeta(labels=("post-v1",)).deferred is True
    assert ip.IssueMeta(milestone="v1.0 lab release").deferred is False


# ---------------------------------------------------------------------------
# Evidence.verdict()
# ---------------------------------------------------------------------------

def test_verdict_open_with_no_trace_is_ok():
    e = ip.Evidence(number=1, state="open")
    level, _why = e.verdict()
    assert level == "OK"


def test_verdict_open_with_implementing_commit_is_suspect():
    c = ip.Commit(sha="abc1234", subject="feat(#1): thing", source_files=["worker/src/dna_entropy/cli.py"])
    e = ip.Evidence(number=1, state="open", commits=[c])
    level, _why = e.verdict()
    assert level == "SUSPECT"


def test_verdict_open_reviewed_by_thread_is_not_suspect():
    c = ip.Commit(sha="abc1234", subject="feat(#1): thing", source_files=["worker/src/dna_entropy/cli.py"])
    e = ip.Evidence(number=1, state="open", commits=[c], reviewed_shas=["abc1234"])
    level, _why = e.verdict()
    assert level == "REVIEWED"


def test_verdict_closed_with_no_trace_is_suspect():
    e = ip.Evidence(number=1, state="closed")
    level, _why = e.verdict()
    assert level == "SUSPECT"


def test_verdict_closed_with_implementing_commit_is_ok():
    c = ip.Commit(sha="abc1234", subject="fix(#1): thing", source_files=["worker/src/dna_entropy/cli.py"])
    e = ip.Evidence(number=1, state="closed", commits=[c])
    level, _why = e.verdict()
    assert level == "OK"


# ---------------------------------------------------------------------------
# scan_commits(): the commit-graph fact the whole script rests on
# ---------------------------------------------------------------------------

def test_scan_commits_finds_source_changing_commit_by_subject(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    _commit(root, "worker/src/dna_entropy/cli.py", "print('hi')\n", "feat(#42): add a thing")

    import re
    patterns = {42: re.compile(r"#42(?![0-9])")}
    found = ip.scan_commits(root, patterns, use_cache=False)
    assert 42 in found
    assert found[42][0].touched_source is True
    assert found[42][0].source_files == ["worker/src/dna_entropy/cli.py"]


def test_scan_commits_matches_body_not_only_subject(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    _commit(
        root, "worker/src/dna_entropy/cli.py", "print('hi')\n",
        "feat(worker): unrelated subject\n\n#99: the real reference is in the body",
    )
    import re
    patterns = {99: re.compile(r"#99(?![0-9])")}
    found = ip.scan_commits(root, patterns, use_cache=False)
    assert 99 in found


def test_scan_commits_trailing_lookahead_does_not_match_longer_number(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    _commit(root, "worker/src/dna_entropy/cli.py", "x\n", "feat(#521): unrelated")
    import re
    patterns = {52: re.compile(r"#52(?![0-9])")}
    found = ip.scan_commits(root, patterns, use_cache=False)
    assert 52 not in found  # #521 must never be read as a reference to #52


def test_issue_refs_extracts_and_dedupes():
    assert ip._issue_refs("see #12 and #12 and #34") == [12, 34]


# ---------------------------------------------------------------------------
# scan() end to end, fully offline (state="unknown" everywhere)
# ---------------------------------------------------------------------------

def test_scan_offline_reports_evidence_without_gh(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    _commit(root, "worker/src/dna_entropy/cli.py", "x\n", "feat(#7): a thing")

    known = {7: ip.IssueMeta(state="unknown")}
    ev = ip.scan(root, [7], known=known, use_cache=False)
    assert ev[7].state == "unknown"
    assert ev[7].implementing_commits  # the commit above touched source
    level, _why = ev[7].verdict()
    assert level == "NOTE"  # state unknown -> reported, not judged


def test_report_one_returns_suspect_level_and_prints_it(capsys):
    c = ip.Commit(sha="abc1234", subject="feat(#1): thing", source_files=["worker/src/dna_entropy/cli.py"])
    e = ip.Evidence(number=1, state="open", commits=[c])
    level = ip.report_one(1, e, suspect_only=False)
    assert level == "SUSPECT"
    out = capsys.readouterr().out
    assert "SUSPECT" in out


def test_report_one_returns_ok_level_when_clean(capsys):
    e = ip.Evidence(number=1, state="open")
    level = ip.report_one(1, e, suspect_only=False)
    assert level == "OK"


def test_report_one_suppresses_printing_but_still_returns_level_under_suspect_only(capsys):
    e = ip.Evidence(number=1, state="open")  # OK, not SUSPECT
    level = ip.report_one(1, e, suspect_only=True)
    assert level == "OK"
    assert capsys.readouterr().out == ""


def test_print_footer_reports_suspect_count_and_exit_code(capsys):
    rc = ip.print_footer(total=5, suspects=2)
    assert rc == 1
    out = capsys.readouterr().out
    assert "5 issue(s) checked, 2 suspect" in out

    rc = ip.print_footer(total=3, suspects=0)
    assert rc == 0


# ---------------------------------------------------------------------------
# get_issue_thread(): the redundancy fix (issue #307) -- one fetch shared by
# fill_reviewed_shas_one / fill_related_issues_one / fill_node_id_matches_one
# instead of each independently re-fetching the same body+comments.
# ---------------------------------------------------------------------------

def test_get_issue_thread_uses_prefetched_text_without_any_gh_call(monkeypatch):
    calls = []
    monkeypatch.setattr(ip, "_run", lambda cmd, root: calls.append(cmd) or "SHOULD NOT BE CALLED")
    e = ip.Evidence(number=1, thread_text="already fetched in bulk")
    thread = ip.get_issue_thread(Path("."), 1, e)
    assert thread == "already fetched in bulk"
    assert calls == []  # no gh call at all


def test_get_issue_thread_fetches_and_caches_when_not_prefetched(monkeypatch):
    calls = []

    def fake_run(cmd, root):
        calls.append(cmd)
        return '{"body": "the body", "comments": [{"body": "a comment"}]}'

    monkeypatch.setattr(ip, "_run", fake_run)
    e = ip.Evidence(number=1)  # thread_text is None: not prefetched
    thread = ip.get_issue_thread(Path("."), 1, e)
    assert "the body" in thread and "a comment" in thread
    assert len(calls) == 1

    # A second call for the SAME Evidence must not fetch again: cached onto e.thread_text.
    thread2 = ip.get_issue_thread(Path("."), 1, e)
    assert thread2 == thread
    assert len(calls) == 1


# ---------------------------------------------------------------------------
# CLI surface
# ---------------------------------------------------------------------------

def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "issue_precheck.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "--all-open" in proc.stdout
    assert "--no-gh" in proc.stdout


def test_cli_no_gh_against_fake_repo_reports_note(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    _commit(root, "worker/src/dna_entropy/cli.py", "x\n", "feat(#7): a thing")

    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "issue_precheck.py"), "7", "--no-gh", "--root", str(root)],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0  # NOTE is not SUSPECT
    assert "#7" in proc.stdout
    assert "State unknown" in proc.stdout


def test_cli_requires_a_number_or_all_open():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "issue_precheck.py"), "--no-gh"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2


def test_cli_all_open_and_no_gh_together_is_a_usage_error():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "issue_precheck.py"), "--all-open", "--no-gh"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2


def test_cli_label_without_all_open_is_a_usage_error():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "issue_precheck.py"), "1", "--no-gh", "--label", "area:docs"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2


def test_help_documents_limit_label_and_progress_flags():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "issue_precheck.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    for flag in ("--limit", "--label", "--no-progress"):
        assert flag in proc.stdout


def test_stdout_is_reconfigured_for_line_buffering():
    """The buffering fix (issue #307) itself: importing the module must put
    stdout in line-buffered mode, which is what makes a redirected-to-file
    run show output as it happens instead of all at once on exit. Import in
    a fresh subprocess, since the reconfigure runs at module import time and
    this test process's own stdout is already whatever pytest set it to."""
    proc = subprocess.run(
        [sys.executable, "-c",
         f"import sys; sys.path.insert(0, {str(SCRIPTS_DIR)!r}); import issue_precheck; "
         "print(sys.stdout.line_buffering)"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0, proc.stderr
    assert proc.stdout.strip() == "True"


# ---------------------------------------------------------------------------
# The streaming regression test issue #307 explicitly asks for: issue #1's
# report is printed (and flushed) BEFORE issue #2's own enrichment call has
# returned, never "everything printed only after the whole sweep finishes".
#
# Tested at the function level with a real background thread and a
# `threading.Event`, not by racing wall-clock time against a stubbed `gh`
# subprocess: Windows' CreateProcess (which is what `subprocess.run(["gh",
# ...], shell=False)` uses) only resolves a bare command name to a `.exe`,
# never a `.bat`/`.cmd` shim, so a PATH-based `gh` stub cannot intercept
# issue_precheck.py's own `_run()` calls without also changing its shell
# invocation style -- not something to do just to make a test possible.
# Monkeypatching the enrichment call directly is both more precise (no
# sleep/poll race) and does not depend on any of that.
# ---------------------------------------------------------------------------

def test_first_issue_streams_before_second_issue_finishes_processing(monkeypatch, tmp_path):
    import io
    import threading
    import time

    # An empty, fresh repo -- not this repo's own real checkout -- so the
    # bulk pre-pass (commit scan, tree scan) `run_stream()` still genuinely
    # runs is near-instant and this test is not coupled to this repo's own
    # tracked-file content or its size.
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    (root / "README.md").write_text("x\n", encoding="utf-8")
    import subprocess as _sp
    _sp.run(["git", "add", "README.md"], cwd=root, check=True, capture_output=True)
    _sp.run(["git", "commit", "-q", "-m", "initial"], cwd=root, check=True, capture_output=True)

    # No real `gh` call for either issue: _run() is the one choke point
    # every `gh` invocation in this module goes through, so faking it here
    # keeps this test both network-free and immune to the Windows
    # CreateProcess quirk that a PATH-based `gh.bat` stub cannot intercept
    # (see this test's own module comment above).
    monkeypatch.setattr(ip, "_run", lambda cmd, root: '{"body": "", "comments": []}')

    issue_two_started = threading.Event()
    issue_two_may_finish = threading.Event()
    events: list[str] = []

    real_fill_related = ip.fill_related_issues_one

    def slow_for_issue_two(root, n, e, ev):
        if n == 2:
            events.append("issue-2-enrichment-started")
            issue_two_started.set()
            # Block here until the test explicitly releases it -- standing
            # in for a slow `gh` call without any real sleep or subprocess.
            if not issue_two_may_finish.wait(timeout=10):
                raise AssertionError("test never released issue #2 -- would hang for real")
            events.append("issue-2-enrichment-finished")
        return real_fill_related(root, n, e, ev)

    monkeypatch.setattr(ip, "fill_related_issues_one", slow_for_issue_two)

    # sys.stdout is a process-global in CPython, shared (not thread-local)
    # across threads by default, so redirecting it here in the main thread
    # BEFORE starting the background thread lets this test observe the
    # background thread's own print() calls live, while it is still running.
    buf = io.StringIO()
    old_stdout = sys.stdout
    sys.stdout = buf

    result: dict[str, int] = {}

    def run():
        result["rc"] = ip.run_stream(
            root, [1, 2],
            known={1: ip.IssueMeta(state="open", title="issue one"),
                   2: ip.IssueMeta(state="open", title="issue two")},
            use_cache=False, suspect_only=False, show_progress=False,
        )

    t = threading.Thread(target=run, daemon=True)
    try:
        t.start()

        # Wait until issue #2's enrichment has genuinely started (proves the
        # loop reached #2, not merely that it is slow to start at all).
        assert issue_two_started.wait(timeout=10), "issue #2's enrichment never started"
        # A short grace window for #1's own print()+flush to land in `buf`
        # relative to the event firing; NOT what makes this deterministic --
        # the real proof is the `events` list assertion right after.
        time.sleep(0.05)

        # At THIS point -- issue #2 still genuinely blocked, unreleased --
        # issue #1 must already be visible in stdout. A buffer-everything
        # regression would print NOTHING until run_stream() returns, which
        # cannot happen before this event is released, so this assertion is
        # exactly the "first line before the second issue finishes" proof.
        assert "#1" in buf.getvalue(), "issue #1 was not printed before issue #2 finished processing"
        assert events == ["issue-2-enrichment-started"], "issue #2 must not have finished yet"

        issue_two_may_finish.set()
        t.join(timeout=10)
        assert not t.is_alive(), "run_stream() did not finish"
    finally:
        sys.stdout = old_stdout

    assert events == ["issue-2-enrichment-started", "issue-2-enrichment-finished"]
    assert result["rc"] == 0
    assert "#1" in buf.getvalue()
    assert "#2" in buf.getvalue()
    # #1's own text must appear before #2's -- proving issue order was
    # preserved and #1 was not somehow deferred until after #2 unblocked.
    assert buf.getvalue().index("#1") < buf.getvalue().index("#2")
