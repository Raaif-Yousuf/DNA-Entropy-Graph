"""Tests for scripts/sync_memory.py, especially the secret scan issue #273
requires. Every test uses an isolated tmp_path "live" directory (via the
DEG_MEMORY_DIR override) and an isolated "mirror" directory (by monkeypatching
the module's MIRROR_DIR) -- nothing here ever touches this machine's real
`~/.claude/projects/.../memory/` directory or this repo's real
`.claude/memory/`.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import sync_memory as sm  # noqa: E402


@pytest.fixture()
def live_and_mirror(tmp_path, monkeypatch):
    live = tmp_path / "live"
    mirror = tmp_path / "mirror"
    live.mkdir()
    mirror.mkdir()
    monkeypatch.setenv("DEG_MEMORY_DIR", str(live))
    monkeypatch.setattr(sm, "MIRROR_DIR", mirror)
    return live, mirror


# ---------------------------------------------------------------------------
# find_secrets() / the pattern set itself
# ---------------------------------------------------------------------------

def test_self_test_passes():
    assert sm.self_test() is True


def test_pattern_names_are_unique():
    names = [p.name for p in sm.SECRET_PATTERNS]
    assert len(names) == len(set(names))


def test_find_secrets_flags_email_with_line_number():
    hits = sm.find_secrets("line one\ncontact raaif.yousuf@vanderbilt.edu\nline three")
    assert any(h.pattern_name == "email" and h.line_no == 2 for h in hits)


def test_find_secrets_does_not_flag_ordinary_prose():
    text = (
        "evo2 returns a nested tuple; unwrap it before indexing.\n"
        "the fix landed in worker/src/dna_entropy/cli.py, see #123.\n"
    )
    assert sm.find_secrets(text) == []


def test_find_secrets_redacts_the_match_in_its_own_report():
    hits = sm.find_secrets("raaif.yousuf@vanderbilt.edu")
    assert hits
    assert "raaif.yousuf@vanderbilt.edu" not in hits[0].redacted


def test_find_secrets_does_not_flag_scrubbed_placeholder_paths():
    # The redaction convention this repo already uses: `<user-home>` is not
    # a real username and must not trip the scanner.
    assert sm.find_secrets(r"<user-home>\DNA-Entropy-Graph\worker") == []


# ---------------------------------------------------------------------------
# find_live_dir() honours the override
# ---------------------------------------------------------------------------

def test_find_live_dir_honours_env_override(live_and_mirror):
    live, _mirror = live_and_mirror
    assert sm.find_live_dir() == live


def test_find_live_dir_none_when_override_missing(tmp_path, monkeypatch):
    monkeypatch.setenv("DEG_MEMORY_DIR", str(tmp_path / "does-not-exist"))
    assert sm.find_live_dir() is None


# ---------------------------------------------------------------------------
# run("push", ...): the secret scan is the point of this whole script
# ---------------------------------------------------------------------------

def test_push_refuses_whole_batch_when_one_file_has_a_secret(live_and_mirror):
    live, mirror = live_and_mirror
    (live / "clean.md").write_text("- evo2 returns a nested tuple\n", encoding="utf-8")
    (live / "leaky.md").write_text("contact raaif.yousuf@vanderbilt.edu\n", encoding="utf-8")

    rc = sm.run("push", apply_changes=True)

    assert rc == 1
    assert not (mirror / "clean.md").exists()  # whole push refused, nothing copied
    assert not (mirror / "leaky.md").exists()


def test_push_hand_planted_email_produces_refusal_not_silent_push(live_and_mirror):
    """The exact scenario issue #273 names as the observable proof."""
    live, mirror = live_and_mirror
    (live / "note.md").write_text(
        "- lesson: reach out to owner@example.com if this recurs\n", encoding="utf-8"
    )

    rc = sm.run("push", apply_changes=True)

    assert rc == 1
    assert list(mirror.glob("*.md")) == []


def test_push_copies_clean_files_when_nothing_is_flagged(live_and_mirror):
    live, mirror = live_and_mirror
    (live / "clean.md").write_text("- evo2 returns a nested tuple\n", encoding="utf-8")

    rc = sm.run("push", apply_changes=True)

    assert rc == 0
    assert (mirror / "clean.md").read_text(encoding="utf-8") == "- evo2 returns a nested tuple\n"


def test_push_allow_flagged_skip_copies_clean_and_skips_flagged(live_and_mirror):
    live, mirror = live_and_mirror
    (live / "clean.md").write_text("- evo2 returns a nested tuple\n", encoding="utf-8")
    (live / "leaky.md").write_text("contact raaif.yousuf@vanderbilt.edu\n", encoding="utf-8")

    rc = sm.run("push", apply_changes=True, allow_flagged_skip=True)

    assert rc == 0
    assert (mirror / "clean.md").exists()
    assert not (mirror / "leaky.md").exists()


def test_push_dry_run_writes_nothing_but_reports_flagged(live_and_mirror, capsys):
    live, mirror = live_and_mirror
    (live / "leaky.md").write_text("contact raaif.yousuf@vanderbilt.edu\n", encoding="utf-8")

    rc = sm.run("push", apply_changes=True, dry_run=True)

    assert rc == 1
    assert not (mirror / "leaky.md").exists()
    out = capsys.readouterr().out
    assert "leaky.md" in out
    assert "email" in out


def test_push_with_no_candidate_files_is_a_clean_noop(live_and_mirror):
    rc = sm.run("push", apply_changes=True)
    assert rc == 0


# ---------------------------------------------------------------------------
# run("pull", ...): unscanned by design (see module docstring)
# ---------------------------------------------------------------------------

def test_pull_copies_mirror_to_live_without_scanning(live_and_mirror):
    live, mirror = live_and_mirror
    # Even a mirror file that WOULD be flagged on push is not blocked on
    # pull: it is already committed content, not a new exposure.
    (mirror / "already-committed.md").write_text(
        "contact raaif.yousuf@vanderbilt.edu\n", encoding="utf-8"
    )

    rc = sm.run("pull", apply_changes=True)

    assert rc == 0
    assert (live / "already-committed.md").exists()


def test_pull_never_overwrites_identical_file(live_and_mirror):
    live, mirror = live_and_mirror
    mirror_file = mirror / "a.md"
    mirror_file.write_text("- x\n", encoding="utf-8")
    live_file = live / "a.md"
    live_file.write_text("- x\n", encoding="utf-8")
    before = live_file.stat().st_mtime

    sm.copy_over  # sanity import touch
    new, changed, _only_dst = sm.compare(mirror, live)
    assert new == [] and changed == []


def test_pull_missing_live_dir_is_not_an_error(tmp_path, monkeypatch):
    monkeypatch.setenv("DEG_MEMORY_DIR", str(tmp_path / "nowhere"))
    assert sm.run("pull", apply_changes=True) == 0


# ---------------------------------------------------------------------------
# compare()
# ---------------------------------------------------------------------------

def test_compare_reports_new_changed_and_dst_only(tmp_path):
    src = tmp_path / "src"
    dst = tmp_path / "dst"
    src.mkdir()
    dst.mkdir()
    (src / "new.md").write_text("new\n", encoding="utf-8")
    (src / "changed.md").write_text("v2\n", encoding="utf-8")
    (dst / "changed.md").write_text("v1\n", encoding="utf-8")
    (dst / "only_dst.md").write_text("x\n", encoding="utf-8")

    new, changed, only_dst = sm.compare(src, dst)
    assert new == ["new.md"]
    assert changed == ["changed.md"]
    assert only_dst == ["only_dst.md"]


# ---------------------------------------------------------------------------
# CLI surface
# ---------------------------------------------------------------------------

def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "sync_memory.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "--dry-run" in proc.stdout
    assert "--self-test" in proc.stdout


def test_cli_self_test_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "sync_memory.py"), "--self-test"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "PASS" in proc.stdout


def test_cli_list_patterns_exits_zero_and_lists_every_pattern():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "sync_memory.py"), "--list-patterns"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    for spec in sm.SECRET_PATTERNS:
        assert spec.name in proc.stdout


def test_cli_requires_a_mode_flag():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "sync_memory.py")],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2


def test_cli_allow_flagged_skip_without_push_errors():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "sync_memory.py"), "--pull", "--allow-flagged-skip"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2


def test_cli_push_end_to_end_refuses_on_planted_secret(live_and_mirror):
    live, mirror = live_and_mirror
    (live / "leaky.md").write_text("contact raaif.yousuf@vanderbilt.edu\n", encoding="utf-8")

    import os
    full_env = dict(os.environ)
    full_env["DEG_MEMORY_DIR"] = str(live)

    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "sync_memory.py"), "--push"],
        capture_output=True, text=True, timeout=15, env=full_env,
        cwd=str(SCRIPTS_DIR.parent),
    )
    # NOTE: the subprocess re-imports the module fresh, so MIRROR_DIR is the
    # REAL repo mirror, not the monkeypatched tmp one -- only DEG_MEMORY_DIR
    # travels via the environment. This test therefore only proves the CLI
    # wiring refuses (exit 1) and never silently succeeds; the write-target
    # behaviour itself is covered in-process by the tests above, which is
    # also why this test must not assert anything about the real mirror
    # directory's contents.
    assert proc.returncode == 1
    assert "leaky.md" in proc.stdout or "leaky.md" in proc.stderr
