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


def test_report_returns_nonzero_when_suspect(capsys):
    c = ip.Commit(sha="abc1234", subject="feat(#1): thing", source_files=["worker/src/dna_entropy/cli.py"])
    ev = {1: ip.Evidence(number=1, state="open", commits=[c])}
    rc = ip.report(ev, suspect_only=False)
    assert rc == 1
    out = capsys.readouterr().out
    assert "SUSPECT" in out


def test_report_returns_zero_when_clean(capsys):
    ev = {1: ip.Evidence(number=1, state="open")}
    rc = ip.report(ev, suspect_only=False)
    assert rc == 0


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
