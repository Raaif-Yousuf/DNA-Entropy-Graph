"""Tests for scripts/resw_merge.py: the key-aware 3-way merge driver for Resources.resw."""

from __future__ import annotations

import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import resw_merge as rm  # noqa: E402

HEADER = '<?xml version="1.0" encoding="utf-8"?>\n<root>\n  <resheader name="version">\n    <value>2.0</value>\n  </resheader>\n'
FOOTER = "</root>\n"


def blk(name: str, value: str) -> str:
    return f'  <data name="{name}" xml:space="preserve">\n    <value>{value}</value>\n  </data>\n'


def doc(*blocks: str, header: str = HEADER, footer: str = FOOTER) -> str:
    return header + "".join(blocks) + footer


def crlf(text: str) -> str:
    return text.replace("\n", "\r\n")


def names(text: str) -> list[str]:
    return [d.attrib["name"] for d in ET.fromstring(text.encode("utf-8")).findall("data")]


BASE = doc(blk("A", "a"), blk("B", "b"))


def test_both_add_adjacent_at_the_end_keeps_both_ours_first():  # the real failing shape
    ours = doc(blk("A", "a"), blk("B", "b"), blk("X", "x"))
    theirs = doc(blk("A", "a"), blk("B", "b"), blk("Y", "y"))
    text, conflicts = rm.merge_text(BASE, ours, theirs)
    assert conflicts == 0
    assert text == doc(blk("A", "a"), blk("B", "b"), blk("X", "x"), blk("Y", "y"))


def test_theirs_added_keys_go_after_ours_last_block_in_theirs_order():
    ours = doc(blk("A", "a"), blk("B", "b"), blk("X", "x"))
    theirs = doc(blk("Y", "y"), blk("A", "a"), blk("B", "b"), blk("Z", "z"))
    text, conflicts = rm.merge_text(BASE, ours, theirs)
    assert conflicts == 0
    assert names(text) == ["A", "B", "X", "Y", "Z"]


def test_both_add_same_key_same_content_is_kept_once():
    ours = doc(blk("A", "a"), blk("B", "b"), blk("X", "x"))
    text, conflicts = rm.merge_text(BASE, ours, ours)
    assert conflicts == 0 and names(text) == ["A", "B", "X"]


def test_both_add_same_key_different_content_conflicts_only_that_block():
    ours = doc(blk("A", "a"), blk("B", "b"), blk("X", "ours"), blk("P", "p"))
    theirs = doc(blk("A", "a"), blk("B", "b"), blk("X", "theirs"))
    text, conflicts = rm.merge_text(BASE, ours, theirs)
    assert conflicts == 1
    assert text.count("<<<<<<<") == 1 and text.count(">>>>>>>") == 1 and text.count("=======") == 1
    inside = text.split("<<<<<<<")[1].split(">>>>>>>")[0]
    assert "ours" in inside and "theirs" in inside and 'name="A"' not in inside and 'name="P"' not in inside


def test_conflicting_modify_conflicts():
    text, conflicts = rm.merge_text(BASE, doc(blk("A", "o"), blk("B", "b")), doc(blk("A", "t"), blk("B", "b")))
    assert conflicts == 1 and "<<<<<<<" in text


def test_modify_on_one_side_only_wins():
    text, conflicts = rm.merge_text(BASE, doc(blk("A", "a"), blk("B", "b")), doc(blk("A", "a"), blk("B", "new")))
    assert conflicts == 0 and text == doc(blk("A", "a"), blk("B", "new"))


def test_same_modify_on_both_sides_is_clean():
    both = doc(blk("A", "z"), blk("B", "b"))
    text, conflicts = rm.merge_text(BASE, both, both)
    assert conflicts == 0 and text == both


def test_delete_on_one_side_unchanged_on_other_is_deleted_either_way():
    gone = doc(blk("A", "a"))
    assert rm.merge_text(BASE, gone, BASE) == (gone, 0)
    assert rm.merge_text(BASE, BASE, gone) == (gone, 0)


def test_delete_vs_modify_conflicts_either_way():
    gone = doc(blk("A", "a"))
    mod = doc(blk("A", "a"), blk("B", "changed"))
    for ours, theirs in ((gone, mod), (mod, gone)):
        text, conflicts = rm.merge_text(BASE, ours, theirs)
        assert conflicts == 1 and "<<<<<<<" in text and "changed" in text


def test_crlf_file_stays_crlf_including_blocks_taken_from_an_lf_side():
    ours = crlf(doc(blk("A", "a"), blk("B", "b"), blk("X", "x")))
    theirs = doc(blk("A", "a"), blk("B", "b"), blk("Y", "y"))  # LF
    text, conflicts = rm.merge_text(crlf(BASE), ours, theirs)
    assert conflicts == 0
    assert text == crlf(doc(blk("A", "a"), blk("B", "b"), blk("X", "x"), blk("Y", "y")))


def test_header_and_untouched_blocks_are_byte_identical():
    weird = '<?xml version="1.0" encoding="utf-8"?>\n<!-- keep   me -->\n<root>\n\t<schema>  odd\tspacing </schema>\n'
    odd = '  <data name="A"   xml:space="preserve">\n\t<value>a  &amp; b</value>\n\t<comment>c</comment>\n  </data>\n'
    base = doc(odd, header=weird)
    ours = doc(odd, blk("X", "x"), header=weird)
    theirs = doc(odd, blk("Y", "y"), header=weird)
    text, conflicts = rm.merge_text(base, ours, theirs)
    assert conflicts == 0 and text.startswith(weird + odd)


def test_header_changed_on_one_side_is_taken_and_on_both_differently_conflicts():
    h2 = HEADER.replace("2.0", "3.0")
    h3 = HEADER.replace("2.0", "4.0")
    text, conflicts = rm.merge_text(BASE, BASE, doc(blk("A", "a"), blk("B", "b"), header=h2))
    assert conflicts == 0 and "3.0" in text
    _, conflicts = rm.merge_text(BASE, doc(blk("A", "a"), blk("B", "b"), header=h3), doc(blk("A", "a"), blk("B", "b"), header=h2))
    assert conflicts == 1


def test_comment_line_travels_with_the_block_it_precedes():
    ours = doc(blk("A", "a"), blk("B", "b"), "  <!-- wave -->\n" + blk("X", "x"))
    theirs = doc(blk("A", "a"), blk("B", "b"), blk("Y", "y"))
    text, conflicts = rm.merge_text(BASE, ours, theirs)
    assert conflicts == 0 and text.index("<!-- wave -->") < text.index('name="X"') < text.index('name="Y"')


def _write3(tmp_path: Path, base: str, ours: str, theirs: str) -> list[str]:
    paths = [tmp_path / n for n in ("o", "a", "b")]
    for p, t in zip(paths, (base, ours, theirs), strict=True):
        p.write_bytes(t.encode())
    return [str(p) for p in paths]


def test_duplicate_name_in_the_result_is_refused(tmp_path):
    assert rm.main(_write3(tmp_path, BASE, doc(blk("A", "a"), blk("A", "again")), BASE)) == 1


def test_a_merge_that_is_not_xml_is_refused(tmp_path):
    assert rm.main(_write3(tmp_path, BASE, BASE.replace("</root>", ""), BASE)) == 1


def test_cli_writes_the_result_into_the_second_path_and_exits_zero(tmp_path):
    args = _write3(tmp_path, BASE, doc(blk("A", "a"), blk("B", "b"), blk("X", "x")), doc(blk("A", "a"), blk("B", "b"), blk("Y", "y")))
    assert rm.main(args) == 0
    assert names(Path(args[1]).read_text(encoding="utf-8")) == ["A", "B", "X", "Y"]


def test_cli_on_a_real_conflict_exits_one_and_leaves_markers(tmp_path):
    args = _write3(tmp_path, BASE, doc(blk("A", "o"), blk("B", "b")), doc(blk("A", "t"), blk("B", "b")))
    assert rm.main(args) == 1
    assert "<<<<<<<" in Path(args[1]).read_text(encoding="utf-8")


@pytest.mark.parametrize("bad", ["", "not xml"])
def test_garbage_input_exits_nonzero_not_a_traceback(tmp_path, bad):
    assert rm.main(_write3(tmp_path, bad, bad, bad)) == 1


def _assert_marked_whole_file(args: list[str], ours: bytes, theirs: bytes) -> None:
    result = Path(args[1]).read_bytes()
    assert result.startswith(b"<<<<<<< ours\n") and result.endswith(b">>>>>>> theirs\n")
    assert ours in result and theirs in result and b"=======\n" in result


@pytest.mark.parametrize(
    "ours",
    [
        BASE.replace("</root>", ""),  # not XML after the merge
        doc(blk("A", "a"), blk("A", "again")),  # duplicate name
        BASE.replace("  </data>\n</root>", "</root>"),  # unterminated <data>
    ],
    ids=["invalid-xml", "duplicate-name", "unterminated-data"],
)
def test_every_failure_exit_leaves_conflict_markers_so_git_add_cannot_drop_theirs(tmp_path, ours):
    theirs = doc(blk("A", "a"), blk("B", "b"), blk("Y", "y"))
    args = _write3(tmp_path, BASE, ours, theirs)
    assert rm.main(args) == 1
    _assert_marked_whole_file(args, ours.encode(), theirs.encode())


def test_undecodable_input_leaves_markers_around_the_raw_bytes(tmp_path):
    args = _write3(tmp_path, BASE, BASE, BASE)
    Path(args[1]).write_bytes(b"\xff\xfe broken")
    assert rm.main(args) == 1
    _assert_marked_whole_file(args, b"\xff\xfe broken", BASE.encode())


def test_line_ending_follows_the_first_line_ending_of_ours_not_any_crlf_inside_it():
    lf_with_crlf_value = doc(blk("A", "a\r\nsecond line"), blk("B", "b"), blk("X", "x"))
    theirs = doc(blk("A", "a\r\nsecond line"), blk("B", "b"), blk("Y", "y"))
    base = doc(blk("A", "a\r\nsecond line"), blk("B", "b"))
    text, conflicts = rm.merge_text(base, lf_with_crlf_value, theirs)
    # structural line endings stay LF (a CRLF inside one value used to flip the whole file to CRLF)
    assert conflicts == 0 and text.count("</data>\n") == 4 and "</data>\r\n" not in text and "</root>\n" in text


def test_a_data_tag_inside_a_comment_is_not_a_block():
    commented = '  <!-- <data name="Ghost">\n    <value>g</value>\n  </data> -->\n'
    base = doc(commented, blk("A", "a"))
    ours = doc(commented, blk("A", "a"), blk("X", "x"))
    theirs = doc(commented, blk("A", "a"), blk("Y", "y"))
    text, conflicts = rm.merge_text(base, ours, theirs)
    assert conflicts == 0 and names(text) == ["A", "X", "Y"] and text.count("Ghost") == 1
    assert [n for n, _ in rm.parse(base).blocks] == ["A"]  # the commented-out block is not a key


def test_single_quoted_names_are_keys_too():
    sq = "  <data name='S' xml:space=\"preserve\">\n    <value>s</value>\n  </data>\n"
    base = doc(sq)
    text, conflicts = rm.merge_text(base, doc(sq.replace(">s<", ">o<")), doc(sq.replace(">s<", ">t<")))
    assert conflicts == 1  # same key changed on both sides: seen as a keyed block, not text noise
    assert [n for n, _ in rm.parse(base).blocks] == ["S"]


def test_a_multiline_comment_attaches_to_the_following_block_not_the_previous_one():
    note = "  <!-- first line\n       second line -->\n"
    ours = doc(blk("A", "a"), blk("B", "b"), note + blk("X", "x"))
    theirs = doc(blk("A", "a"), blk("B", "b"), blk("Y", "y"))
    text, conflicts = rm.merge_text(BASE, ours, theirs)
    assert conflicts == 0
    assert text == doc(blk("A", "a"), blk("B", "b"), note + blk("X", "x"), blk("Y", "y"))
    # deleting B on theirs must not delete the comment that belongs to X
    gone = doc(blk("A", "a"))
    text, conflicts = rm.merge_text(doc(blk("A", "a"), blk("B", "b")), ours, gone)
    assert conflicts == 0 and "second line" in text and 'name="B"' not in text


def test_a_trailing_comment_before_root_end_stays_in_the_footer():
    tail = "  <!-- end note -->\n"
    base = doc(blk("A", "a"), footer=tail + FOOTER)
    text, conflicts = rm.merge_text(base, doc(blk("A", "a"), blk("X", "x"), footer=tail + FOOTER), base)
    assert conflicts == 0 and text == doc(blk("A", "a"), blk("X", "x"), footer=tail + FOOTER)


# --- real git ---------------------------------------------------------------------------------------


def _git(cwd: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    proc = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, encoding="utf-8", check=False)
    assert not check or proc.returncode == 0, proc.stderr
    return proc


def _repo(tmp_path: Path, configure: bool = True) -> Path:
    repo = tmp_path / "r"
    repo.mkdir()
    _git(repo, "init", "-q", "-b", "main")
    _git(repo, "config", "user.email", "t@example.invalid")
    _git(repo, "config", "user.name", "t")
    _git(repo, "config", "core.autocrlf", "false")
    (repo / ".gitattributes").write_text("Resources.resw merge=resw\n", encoding="utf-8", newline="\n")
    if configure:
        script = (SCRIPTS_DIR / "resw_merge.py").as_posix()
        _git(repo, "config", "merge.resw.driver", f'"{Path(sys.executable).as_posix()}" "{script}" %O %A %B')
    (repo / "Resources.resw").write_bytes(BASE.encode())
    _git(repo, "add", "-A")
    _git(repo, "commit", "-q", "-m", "base")
    return repo


def _branch(repo: Path, name: str, text: str) -> None:
    _git(repo, "switch", "-q", "-c", name, "main")
    (repo / "Resources.resw").write_bytes(text.encode())
    _git(repo, "commit", "-q", "-am", name)


def test_git_merges_two_branches_that_both_append_a_key(tmp_path):
    repo = _repo(tmp_path)
    _branch(repo, "one", doc(blk("A", "a"), blk("B", "b"), blk("X", "x")))
    _branch(repo, "two", doc(blk("A", "a"), blk("B", "b"), blk("Y", "y")))
    _git(repo, "merge", "-q", "--no-edit", "one")
    # we are on "two": ours first, theirs ("one") after
    assert names((repo / "Resources.resw").read_text(encoding="utf-8")) == ["A", "B", "Y", "X"]


def test_without_the_driver_the_same_merge_conflicts(tmp_path):  # the control: the driver is what fixes it
    repo = _repo(tmp_path, configure=False)
    _branch(repo, "one", doc(blk("A", "a"), blk("B", "b"), blk("X", "x")))
    _branch(repo, "two", doc(blk("A", "a"), blk("B", "b"), blk("Y", "y")))
    assert _git(repo, "merge", "-q", "--no-edit", "one", check=False).returncode != 0


def test_git_reports_a_real_conflict_as_unmerged(tmp_path):
    repo = _repo(tmp_path)
    _branch(repo, "one", doc(blk("A", "one"), blk("B", "b")))
    _branch(repo, "two", doc(blk("A", "two"), blk("B", "b")))
    assert _git(repo, "merge", "-q", "--no-edit", "one", check=False).returncode != 0
    assert "Resources.resw" in _git(repo, "diff", "--name-only", "--diff-filter=U").stdout
    assert "<<<<<<<" in (repo / "Resources.resw").read_text(encoding="utf-8")


def test_the_repo_gitattributes_routes_the_resw_path_to_the_driver():
    out = subprocess.run(
        ["git", "check-attr", "merge", "--", "app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw"],
        cwd=SCRIPTS_DIR.parent,
        capture_output=True,
        text=True,
        check=False,
    ).stdout
    assert out.strip().endswith("merge: resw"), out
