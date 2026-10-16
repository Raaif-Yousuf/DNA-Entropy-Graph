"""land_wt.py configures the Resources.resw merge driver for its throwaway-worktree merge of origin/main."""

from __future__ import annotations

import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

from test_land_wt import _git, _land, world  # noqa: E402, F401  (world is a pytest fixture)

import land_wt as lw  # noqa: E402

RESW = "app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw"


def _resw(*keys: str) -> bytes:
    body = "".join(f'  <data name="{k}" xml:space="preserve">\n    <value>{k}</value>\n  </data>\n' for k in keys)
    return ('<?xml version="1.0" encoding="utf-8"?>\n<root>\n' + body + "</root>\n").encode()


def _advance(w, data: bytes) -> None:
    (w.clone / RESW).parent.mkdir(parents=True, exist_ok=True)
    w.advance_main({RESW: data})


def test_both_sides_appending_resw_keys_lands_with_both_keys_through_the_driver(world):  # noqa: F811
    # the real failing shape: the lane branch has no .gitattributes line and no driver config of its own
    _advance(world, _resw("A"))
    sha = world.commit_on_branch("feat/1-x", {RESW: _resw("A", "LaneKey")})
    _advance(world, _resw("A", "MainKey"))
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    merged = _git(world.origin, "show", f"feat/1-x:{RESW}")
    assert 'name="LaneKey"' in merged and 'name="MainKey"' in merged and "<<<<" not in merged
    assert world.venv_intact() and world.leftover_worktrees() == []


def test_a_real_resw_conflict_still_refuses_naming_the_file(world, capsys):  # noqa: F811
    _advance(world, _resw("A"))
    sha = world.commit_on_branch("feat/1-x", {RESW: _resw("A").replace(b"<value>A", b"<value>lane")})
    _advance(world, _resw("A").replace(b"<value>A", b"<value>main"))
    assert _land(world, "feat/1-x", sha) == lw.EXIT_REFUSED
    assert "Resources.resw" in capsys.readouterr().out
    assert world.remote_branches() == ["main"]


def test_driver_flags_name_the_script_beside_land_wt_the_running_python_and_the_resw_path(world):  # noqa: F811
    flags, attrs_path = lw.resw_driver_flags(world.clone)
    try:
        assert flags[0] == "-c" and flags[2] == "-c"
        assert flags[1].startswith("merge.resw.driver=") and "resw_merge.py" in flags[1] and flags[1].endswith("%O %A %B")
        assert Path(sys.executable).as_posix() in flags[1]
        assert flags[3] == f"core.attributesFile={attrs_path.as_posix()}"
        attrs = attrs_path.read_text(encoding="utf-8")
        assert RESW in attrs and "merge=resw" in attrs
    finally:
        attrs_path.unlink()


def test_the_users_own_global_attributes_survive_next_to_the_resw_line(world):  # noqa: F811
    mine = world.root / "my_attributes"
    mine.write_text("*.foo -diff\n", encoding="utf-8", newline="\n")
    _git(world.clone, "config", "core.attributesFile", mine.as_posix())
    flags, attrs_path = lw.resw_driver_flags(world.clone)
    try:
        out = _git(world.clone, *flags, "check-attr", "diff", "merge", "--", "x.foo", RESW)
        assert "x.foo: diff: unset" in out  # the user's rule still applies during the merge
        assert f"{RESW}: merge: resw" in out
    finally:
        attrs_path.unlink()


def test_every_call_gets_its_own_attributes_file(world):  # noqa: F811
    (_, a), (_, b) = lw.resw_driver_flags(world.clone), lw.resw_driver_flags(world.clone)
    try:
        assert a != b
    finally:
        a.unlink()
        b.unlink()


def test_a_landing_leaves_no_attributes_file_and_nothing_in_the_shared_repo_config(world, monkeypatch):  # noqa: F811
    made: list[Path] = []
    real = lw.resw_driver_flags

    def spy(repo):
        flags, path = real(repo)
        made.append(path)
        return flags, path

    monkeypatch.setattr(lw, "resw_driver_flags", spy)
    _advance(world, _resw("A"))
    sha = world.commit_on_branch("feat/1-x", {RESW: _resw("A", "LaneKey")})
    _advance(world, _resw("A", "MainKey"))
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert len(made) == 1 and not made[0].exists()
    common = Path(_git(world.clone, "rev-parse", "--git-common-dir"))
    common = common if common.is_absolute() else world.clone / common
    assert not (common / "info" / "attributes").exists()
    assert subprocess_config(world.clone, "merge.resw.driver") == ""
    assert subprocess_config(world.clone, "core.attributesFile") == ""


def subprocess_config(repo: Path, key: str) -> str:
    import subprocess

    proc = subprocess.run(["git", "config", "--get", key], cwd=repo, capture_output=True, text=True, check=False)
    return proc.stdout.strip()


def test_a_linked_worktrees_own_info_attributes_is_not_read_by_git():
    """Why the attributes go in a core.attributesFile and not info/attributes (MEASURED 2026-10-16):
    git reads info/attributes from the COMMON dir only, so writing it for a throwaway worktree would leak
    into the shared repo. This pins the git behaviour the design relies on."""
    import subprocess
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        main = root / "r"
        run = lambda cwd, *a: subprocess.run(["git", *a], cwd=cwd, capture_output=True, text=True, check=True).stdout.strip()  # noqa: E731
        run(root, "init", "-q", "-b", "main", str(main))
        run(main, "config", "user.email", "t@e.i")
        run(main, "config", "user.name", "t")
        (main / "a.txt").write_text("x", encoding="utf-8")
        run(main, "add", "-A")
        run(main, "commit", "-q", "-m", "i")
        wt = root / "wt"
        run(main, "worktree", "add", "-q", "--detach", str(wt))
        gitdir = Path(run(wt, "rev-parse", "--absolute-git-dir"))
        (gitdir / "info").mkdir(exist_ok=True)
        (gitdir / "info" / "attributes").write_text("a.txt merge=resw\n", encoding="utf-8")
        assert run(wt, "check-attr", "merge", "--", "a.txt").endswith("unspecified")
