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


def test_driver_flags_name_the_script_beside_land_wt_the_running_python_and_the_resw_path():
    flags = lw.resw_driver_flags()
    assert flags[0] == "-c" and flags[2] == "-c"
    assert flags[1].startswith("merge.resw.driver=") and "resw_merge.py" in flags[1] and flags[1].endswith("%O %A %B")
    assert Path(sys.executable).as_posix() in flags[1]
    attrs = Path(flags[3].split("=", 1)[1]).read_text(encoding="utf-8")
    assert RESW in attrs and "merge=resw" in attrs
