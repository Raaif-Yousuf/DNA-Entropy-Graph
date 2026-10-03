"""Structure test for .github/workflows/codeql.yml and .github/codeql/codeql-config.yml (#484).

Text based on purpose, like test_ci_worker_workflow.py: PyYAML is not in the venv premerge uses.
"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
WORKFLOW = ROOT / ".github" / "workflows" / "codeql.yml"
CONFIG = ROOT / ".github" / "codeql" / "codeql-config.yml"
PATHS = ("app/**", "worker/**", "scripts/**", ".github/workflows/**")


def _on_block() -> str:
    text = WORKFLOW.read_text(encoding="utf-8")
    m = re.search(r"^on:\n(.*?)(?=^\S)", text, re.DOTALL | re.MULTILINE)
    assert m, "the `on:` block was not found"
    return m.group(1)


def _paths(trigger: str) -> list[str]:
    m = re.search(rf"^  {trigger}:\n(?:    .*\n)*?    paths:\n((?:      - .*\n)+)", _on_block(), re.MULTILINE)
    assert m, f"{trigger} has no paths filter"
    return [ln.strip()[2:].strip().strip("\"'") for ln in m.group(1).splitlines()]


def test_workflow_dispatch_is_kept():
    assert re.search(r"^  workflow_dispatch:", _on_block(), re.MULTILINE)


def test_weekly_schedule():
    m = re.search(r"^  schedule:\n\s+- cron: ['\"]([^'\"]+)['\"]", _on_block(), re.MULTILINE)
    assert m, "no schedule trigger"
    minute, hour, dom, month, dow = m.group(1).split()
    assert dom == "*" and month == "*" and dow.isdigit(), "must be a once-a-week cron"


def test_pull_request_and_push_to_main_are_path_filtered():
    for trigger in ("pull_request", "push"):
        assert set(_paths(trigger)) == set(PATHS), trigger
    assert re.search(r"^  push:\n(?:    .*\n)*?    branches: \[main\]", _on_block(), re.MULTILINE)


def test_init_step_references_the_config_file():
    text = WORKFLOW.read_text(encoding="utf-8")
    assert re.search(
        r"codeql-action/init@[0-9a-f]{40}[^\n]*\n\s+with:\n(?:\s+.*\n)*?\s+config-file: \./\.github/codeql/codeql-config\.yml", text
    )


def test_config_ignores_the_legacy_trees():
    assert CONFIG.is_file()
    text = CONFIG.read_text(encoding="utf-8")
    m = re.search(r"^paths-ignore:\n((?:  - .*\n)+)", text, re.MULTILINE)
    assert m, "no paths-ignore list"
    ignored = {ln.strip()[2:].strip().strip("\"'") for ln in m.group(1).splitlines()}
    assert {"legacy/**", "worker/legacy/**"} <= ignored


def test_csharp_build_step_is_intact():
    text = WORKFLOW.read_text(encoding="utf-8")
    assert "dotnet restore" in text and "dotnet build -c Release -p:Platform=x64 --no-restore" in text
    assert re.search(r"languages: \$\{\{ matrix\.language \}\}", text)


def test_dependency_review_stays_skipped_until_the_owner_enables_dependency_graph():
    # #325: with a pull_request trigger now present, an unconditional `== 'pull_request'` job
    # would be a permanently red check (Dependency graph is off), so it needs the variable gate.
    text = WORKFLOW.read_text(encoding="utf-8")
    assert "if: github.event_name == 'pull_request' && vars.DEPENDENCY_GRAPH_ENABLED == 'true'" in text


def test_config_also_lists_build_output_dirs_for_the_interpreted_lanes():
    text = CONFIG.read_text(encoding="utf-8")
    assert "  - '**/obj/**'" in text and "  - '**/bin/**'" in text


def _csharp_filter_steps() -> str:
    """The text of the analyze steps; csharp must be filtered after analysis, because paths-ignore is
    not applied when CodeQL builds the code (docs.github.com, 'Specifying directories to scan')."""
    text = WORKFLOW.read_text(encoding="utf-8")
    m = re.search(r"codeql-action/analyze@.*?(?=^  dependency-review:)", text, re.DOTALL | re.MULTILINE)
    assert m, "analyze steps not found"
    return m.group(0)


def test_csharp_results_are_filtered_of_generated_code_before_upload():
    body = _csharp_filter_steps()
    # analyze for csharp must NOT upload on its own, and writes SARIF where the filter reads it.
    assert re.search(r"if: matrix\.language == 'csharp'\n\s+with:\n(?:\s+.*\n)*?\s+upload: failure-only", body)
    assert "output: sarif-results" in body
    assert re.search(r"advanced-security/filter-sarif@[0-9a-f]{40}", body)
    for pattern in ("-**/obj/**", "-**/bin/**"):
        assert pattern in body
    assert "sarif-results/csharp.sarif" in body
    assert re.search(r"codeql-action/upload-sarif@[0-9a-f]{40}", body)
    assert re.search(r"sarif_file: sarif-results/csharp\.sarif", body)


def test_non_csharp_lanes_still_upload_from_analyze_directly():
    body = _csharp_filter_steps()
    assert re.search(r"if: matrix\.language != 'csharp'\n\s+with:\n\s+category:", body)
