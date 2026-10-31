"""Tests for scripts/check_doc_headings.py (#883)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_doc_headings as cdh  # noqa: E402


def _doc(*lines: str) -> str:
    return "\n".join(lines) + "\n"


def test_clean_ascending_doc_passes():
    text = _doc("# T", "## 1. One", "text", "## 2. Two", "## 4. Gap is fine", "## Related")
    assert cdh.find_problems(text) == []


def test_duplicate_number_fails_and_names_both_lines():
    text = _doc("# T", "## 19. Spot VMs", "x", "## 20. Flex", "## 19. Budget")
    problems = cdh.find_problems(text)
    assert len(problems) == 1
    p = problems[0]
    assert p.line_no == 5
    assert p.kind == "duplicate"
    assert p.first_line_no == 2
    assert "## 19. Budget" in p.message
    assert "## 19. Spot VMs" in p.message


def test_adjacent_duplicate_is_duplicate_not_descending():
    problems = cdh.find_problems(_doc("## 3. A", "## 3. B"))
    assert [p.kind for p in problems] == ["duplicate"]


def test_descending_number_fails():
    problems = cdh.find_problems(_doc("## 1. A", "## 3. C", "## 2. B"))
    assert len(problems) == 1
    assert problems[0].kind == "descending"
    assert problems[0].line_no == 3
    assert "## 3. C" in problems[0].message
    assert "## 2. B" in problems[0].message
    assert problems[0].first_line_no == 2


def test_descending_first_line_is_the_previous_headings_line_not_first_use_of_number():
    # "## 3." first appears at line 2, but the heading it follows is the one at line 5.
    problems = cdh.find_problems(_doc("## 1. A", "## 3. C", "## 4. D", "text", "## 3. dup", "## 2. B"))
    desc = [p for p in problems if p.kind == "descending"]
    assert [p.line_no for p in desc] == [6]
    assert desc[0].first_line_no == 5


def test_fenced_code_block_headings_ignored():
    text = _doc("## 1. A", "```markdown", "## 1. Template", "## 3. x", "```", "## 2. B")
    assert cdh.find_problems(text) == []


def test_tilde_fence_and_indented_fence_ignored():
    text = _doc("## 1. A", "~~~", "## 1. x", "~~~", "   ```", "## 1. y", "   ```", "## 2. B")
    assert cdh.find_problems(text) == []


def test_unclosed_fence_is_a_problem_naming_its_opening_line():
    problems = cdh.find_problems(_doc("## 1. A", "```", "## 1. B"))
    assert [p.kind for p in problems] == ["unclosed-fence"]
    assert problems[0].line_no == 2
    assert "unclosed code fence opened at line 2" in problems[0].message


def test_longer_outer_fence_is_not_closed_by_inner_shorter_fence():
    text = _doc("## 1. A", "````markdown", "```markdown", "## 1. skeleton", "```", "## 1. still inside", "````", "## 2. B")
    assert cdh.find_problems(text) == []


def test_heading_after_a_longer_outer_fence_closes_is_checked_again():
    text = _doc("## 1. A", "````", "```", "````", "## 1. dup")
    assert [p.kind for p in cdh.find_problems(text)] == ["duplicate"]


def test_closing_fence_needs_same_char_and_nothing_after_it():
    # "```python" inside an open ``` fence is not a closer; "~~~" is not a closer for ```.
    text = _doc("## 1. A", "```", "```python", "~~~", "## 1. inside", "```", "## 2. B")
    assert cdh.find_problems(text) == []
    # a closer may be longer than the opener and may carry trailing spaces
    assert cdh.find_problems(_doc("## 1. A", "```", "## 1. x", "`````  ", "## 2. B")) == []


def test_fence_with_info_string_opens_but_backtick_info_does_not_open():
    # CommonMark: a backtick fence's info string cannot contain a backtick, so this is inline code.
    assert [p.kind for p in cdh.find_problems(_doc("## 1. A", "``` a`b", "## 1. B"))] == ["duplicate"]


def test_line_numbers_ignore_unicode_line_separators():
    text = "## 1. A\n\x0c\x85  text\n## 1. B\n"
    problems = cdh.find_problems(text)
    assert problems[0].line_no == 3


def test_commonmark_heading_forms_are_counted():
    for second in ("   ## 1. B", "##  1. B", "##\t1. B", "## 1.B"):
        problems = cdh.find_problems(_doc("## 1. A", second))
        assert [p.kind for p in problems] == ["duplicate"], second


def test_four_space_indent_and_decimal_numbers_are_not_top_level_headings():
    assert cdh.find_problems(_doc("## 1. A", "    ## 1. code block")) == []
    assert cdh.find_problems(_doc("## 1. A", "## 1.2 sub", "## 1.2 sub")) == []


def test_heading_inside_html_comment_is_still_counted():
    assert [p.kind for p in cdh.find_problems(_doc("## 1. A", "<!--", "## 1. B", "-->"))] == ["duplicate"]


def test_nested_h3_numbering_is_ignored():
    text = _doc("## 1. A", "### 1. sub", "### 1. sub again", "## 2. B", "### 1. sub", "#### 7. deep")
    assert cdh.find_problems(text) == []


def test_unnumbered_headings_and_numbers_without_dot_ignored():
    text = _doc("## 2024 plans", "## 2 things", "## Related", "## 1. A")
    assert cdh.find_problems(text) == []


def test_section_zero_allowed():
    assert cdh.find_problems(_doc("## 0. Preconditions", "## 1. Next")) == []


def test_crlf_text_is_handled():
    problems = cdh.find_problems("## 1. A\r\n## 1. B\r\n")
    assert len(problems) == 1
    assert "\r" not in problems[0].message
    assert problems[0].message.count("## 1. B'") == 1


def test_check_root_flags_file_and_line(tmp_path: Path):
    docs = tmp_path / "docs" / "sub"
    docs.mkdir(parents=True)
    (docs / "bad.md").write_text("## 1. A\n## 1. B\n", encoding="utf-8", newline="\n")
    (tmp_path / "docs" / "good.md").write_text("## 1. A\n## 2. B\n", encoding="utf-8", newline="\n")
    messages = cdh.check_root(tmp_path)
    assert len(messages) == 1
    assert messages[0].startswith("docs/sub/bad.md:2:")


def test_check_root_vacuity_sees_real_docs():
    counts = cdh.numbered_heading_counts(REPO_ROOT)
    assert counts.get("docs/cloud_design.md", 0) >= 20, counts
    assert counts.get("docs/ui_conventions.md", 0) >= 15, counts
    assert sum(counts.values()) > 100


def test_real_repo_is_clean():
    assert cdh.check_root(REPO_ROOT) == []


def test_cli_exit_codes(tmp_path: Path):
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs" / "d.md").write_text("## 1. A\n## 1. B\n", encoding="utf-8", newline="\n")
    script = str(SCRIPTS_DIR / "check_doc_headings.py")
    bad = subprocess.run([sys.executable, script, "--root", str(tmp_path)], capture_output=True, text=True, timeout=30)
    assert bad.returncode == 1
    assert "ERROR:" in bad.stdout + bad.stderr
    assert "docs/d.md:2" in bad.stdout + bad.stderr
    good = subprocess.run([sys.executable, script], capture_output=True, text=True, timeout=30, cwd=REPO_ROOT)
    assert good.returncode == 0, good.stdout + good.stderr
    assert "OK:" in good.stdout
