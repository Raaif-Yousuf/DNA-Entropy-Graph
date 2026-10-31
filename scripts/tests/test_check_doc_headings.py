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


def test_fenced_code_block_headings_ignored():
    text = _doc("## 1. A", "```markdown", "## 1. Template", "## 3. x", "```", "## 2. B")
    assert cdh.find_problems(text) == []


def test_tilde_fence_and_indented_fence_ignored():
    text = _doc("## 1. A", "~~~", "## 1. x", "~~~", "   ```", "## 1. y", "   ```", "## 2. B")
    assert cdh.find_problems(text) == []


def test_unclosed_fence_swallows_rest():
    assert cdh.find_problems(_doc("## 1. A", "```", "## 1. B")) == []


def test_nested_h3_numbering_is_ignored():
    text = _doc("## 1. A", "### 1. sub", "### 1. sub again", "## 2. B", "### 1. sub", "#### 7. deep")
    assert cdh.find_problems(text) == []


def test_unnumbered_headings_and_numbers_without_dot_ignored():
    text = _doc("## 2024 plans", "## 2 things", "## Related", "## 1. A")
    assert cdh.find_problems(text) == []


def test_section_zero_allowed():
    assert cdh.find_problems(_doc("## 0. Preconditions", "## 1. Next")) == []


def test_crlf_text_is_handled():
    assert len(cdh.find_problems("## 1. A\r\n## 1. B\r\n")) == 1


def test_check_root_flags_file_and_line(tmp_path: Path):
    docs = tmp_path / "docs" / "sub"
    docs.mkdir(parents=True)
    (docs / "bad.md").write_text("## 1. A\n## 1. B\n", encoding="utf-8", newline="\n")
    (tmp_path / "docs" / "good.md").write_text("## 1. A\n## 2. B\n", encoding="utf-8", newline="\n")
    messages = cdh.check_root(tmp_path)
    assert len(messages) == 1
    assert messages[0].startswith("docs/sub/bad.md:2:")


def test_check_root_vacuity_sees_real_docs():
    scanned = cdh.numbered_heading_count(REPO_ROOT)
    assert scanned > 20, "the scanner must see the repo's real numbered headings"


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
