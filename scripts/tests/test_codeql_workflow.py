"""Structure test for .github/workflows/codeql.yml and .github/codeql/codeql-config.yml (#484).

Text based on purpose, like test_ci_worker_workflow.py: PyYAML is not in the venv premerge uses.
"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
WORKFLOW = ROOT / ".github" / "workflows" / "codeql.yml"
CONFIG = ROOT / ".github" / "codeql" / "codeql-config.yml"
APP_PROJECT = "src/DnaEntropyGraph.App/DnaEntropyGraph.App.csproj"
TOOLS_PROJECT = "tools/DnaEntropyGraph.CloudCli/DnaEntropyGraph.CloudCli.csproj"
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


def _steps(text: str) -> list[str]:
    """The workflow's steps, one string each (a step starts at a six-space `- `); a plain split, so no regex can backtrack."""
    return [chunk for chunk in re.split(r"(?m)^(?=      - )", text) if chunk.startswith("      - ")]


def test_init_step_references_the_config_file():
    text = WORKFLOW.read_text(encoding="utf-8")
    init = [s for s in _steps(text) if re.search(r"codeql-action/init@[0-9a-f]{40}", s)]
    assert init, "no codeql-action/init step"
    assert any("\n        with:\n" in s and "          config-file: ./.github/codeql/codeql-config.yml\n" in s for s in init)


def test_config_ignores_the_legacy_trees():
    assert CONFIG.is_file()
    text = CONFIG.read_text(encoding="utf-8")
    m = re.search(r"^paths-ignore:\n((?:  - .*\n)+)", text, re.MULTILINE)
    assert m, "no paths-ignore list"
    ignored = {ln.strip()[2:].strip().strip("\"'") for ln in m.group(1).splitlines()}
    assert {"legacy/**", "worker/legacy/**"} <= ignored


def test_csharp_build_step_is_intact():
    text = WORKFLOW.read_text(encoding="utf-8")
    assert f"dotnet restore {APP_PROJECT}" in text and f"dotnet build {APP_PROJECT} -c Release -p:Platform=x64 --no-restore" in text
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
    analyze = [s for s in _steps(body) if re.search(r"codeql-action/analyze@[0-9a-f]{40}", s) and "if: matrix.language == 'csharp'" in s]
    assert any("\n        with:\n" in s and "          upload: failure-only" in s for s in analyze)
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


def _csharp_build_run() -> str:
    text = WORKFLOW.read_text(encoding="utf-8")
    m = re.search(r"- name: build \(csharp\)\n(?:        .*\n)*?        run: \|\n((?:          .*\n)+)", text)
    assert m, "build (csharp) step not found"
    return m.group(1)


def test_csharp_build_is_product_code_only_not_the_solution():
    # #761 / #653: MEASURED 2026-10-28: test stubs `GetString(key) => key` flowed into product
    # string.Format calls (cs/invalid-string-formatting, alerts 1383/1417/1466/1467) and most
    # cs/path-combine noise. Building only product projects keeps tests out of the CodeQL database. That
    # does NOT remove the product provider's own `return key` fallback as a flow source, which is why
    # cs/invalid-string-formatting is also excluded (next test).
    run = _csharp_build_run()
    # Exact project paths, not a "tests not in run" substring: restore and build name the same two projects.
    assert re.findall(r"dotnet restore (\S+)", run) == [APP_PROJECT, TOOLS_PROJECT]
    assert re.findall(r"dotnet build (\S+)", run) == [APP_PROJECT, TOOLS_PROJECT]


def test_built_projects_reach_every_product_project_so_the_extractor_sees_all_product_code():
    # app/src projects are reached through the App project; app/tools (CloudCli) is not referenced by
    # App, so the workflow must build it by name. Tests are never in either set.
    def refs(csproj: Path) -> set[Path]:
        text = csproj.read_text(encoding="utf-8")
        return {(csproj.parent / r.replace("\\", "/")).resolve() for r in re.findall(r'ProjectReference Include="([^"]+)"', text)}

    built = re.findall(r"dotnet build (\S+\.csproj)", _csharp_build_run())
    assert APP_PROJECT in built and TOOLS_PROJECT in built, built
    seen: set[Path] = set()
    todo = [(ROOT / "app" / b).resolve() for b in built]
    while todo:
        cur = todo.pop()
        if cur not in seen:
            seen.add(cur)
            todo.extend(refs(cur))
    product = {p.resolve() for p in (ROOT / "app" / "src").glob("*/*.csproj")} | {
        p.resolve() for p in (ROOT / "app" / "tools").glob("*/*.csproj")
    }
    assert len(product) >= 7, "vacuous: expected the six app/src projects and CloudCli"
    assert product <= seen, f"not built or referenced, so CodeQL would not scan: {sorted(p.name for p in product - seen)}"
    assert not any("tests" in p.parts[-3:] for p in seen), "a test project is in the CodeQL build"


_NOISE_RULES = (
    "cs/xmldoc/missing-summary",
    "cs/linq/missed-select",
    "cs/linq/missed-where",
    "cs/unmanaged-code",
    "cs/call-to-unmanaged-code",
    "py/import-and-import-from",
)


def test_config_excludes_the_style_only_rules_triaged_as_noise():
    # #488 / #491: security-and-quality ships these style notes; every hit was triaged as not a defect
    # (P/Invoke is the point of the Windows shell code; the rest are style). Real findings stay visible.
    text = CONFIG.read_text(encoding="utf-8")
    excluded = set(re.findall(r"^  - exclude:\n      id: (\S+)$", text, re.MULTILINE))
    assert set(_NOISE_RULES) <= excluded
    assert "cs/path-combine" not in excluded, "path-combine stays visible: it is the rule that flags a rooted second argument"


def _filter_patterns() -> list[str]:
    """The pattern lines of the filter-sarif step (a block scalar of `-glob` lines)."""
    step = next(s for s in _steps(_csharp_filter_steps()) if re.search(r"filter-sarif@[0-9a-f]{40}", s))
    head, sep, tail = step.partition("          patterns: |\n")
    assert sep, "filter-sarif has no patterns block"
    out: list[str] = []
    for line in tail.splitlines():
        if not line.startswith("            "):
            break
        out.append(line.strip())
    return out


def test_sarif_filter_drops_test_projects_even_if_a_product_project_ever_compiles_them():
    # Defence in depth (#761 / #653): the build above names no test project, but if the App build ever
    # reached app/tests (a ProjectReference, a Compile glob), no test-project alert may gate a PR.
    patterns = _filter_patterns()
    assert "-app/tests/**" in patterns, patterns
    assert "-**/obj/**" in patterns and "-**/bin/**" in patterns, patterns


def test_config_ignores_python_test_roots_for_the_interpreted_lanes():
    # paths-ignore is honoured for python (interpreted), so the pytest trees never raise alerts (#761).
    text = CONFIG.read_text(encoding="utf-8")
    ignored = set(re.findall(r"^  - '?([^'\n]+?)'?$", text.partition("paths-ignore:\n")[2].partition("\n\n")[0], re.MULTILINE))
    assert {"worker/tests/**", "scripts/tests/**"} <= ignored, ignored
    for root in ("worker/tests", "scripts/tests"):
        assert (ROOT / root).is_dir(), f"vacuous: {root} no longer exists"
    assert (ROOT / "app" / "tests").is_dir()


def test_cs_path_combine_and_catch_all_stay_on():
    # DECISION (agent-made, reversible) in codeql-config.yml: product hits of both rules take
    # caller-supplied segments or are not all top-level, so neither is excluded.
    text = CONFIG.read_text(encoding="utf-8")
    excluded = set(re.findall(r"^  - exclude:\n      id: (\S+)$", text, re.MULTILINE))
    assert not {"cs/path-combine", "cs/catch-of-all-exceptions"} & excluded


def test_invalid_string_formatting_is_excluded_because_the_arity_script_replaces_it():
    # DECISION (agent-made, reversible): ReswStringResourceProvider.GetString returns `key` on an empty
    # value and in its catch, the same flow source as the test stubs, so CodeQL can still raise the rule
    # on correct product sites. scripts/check_format_arity.py reads the real template instead.
    text = CONFIG.read_text(encoding="utf-8")
    excluded = set(re.findall(r"^  - exclude:\n      id: (\S+)$", text, re.MULTILINE))
    assert "cs/invalid-string-formatting" in excluded
    assert "check_format_arity.py" in text.partition("cs/invalid-string-formatting")[0].rpartition("DECISION")[2]
