"""Tests for scripts/check_format_arity.py (#761): string.Format arity against the Resources.resw template."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_format_arity as cfa  # noqa: E402

SCRIPT = SCRIPTS_DIR / "check_format_arity.py"
REPO = SCRIPTS_DIR.parent


def _resw(**entries: str) -> str:
    body = "".join(f'  <data name="{k}" xml:space="preserve">\n    <value>{v}</value>\n  </data>\n' for k, v in entries.items())
    return f'<?xml version="1.0" encoding="utf-8"?>\n<root>\n{body}</root>\n'


def _tree(tmp: Path, resw: str, cs: str, extra_cs: str = "") -> Path:
    strings = tmp / "app" / "src" / "DnaEntropyGraph.App" / "Strings" / "en-US"
    strings.mkdir(parents=True)
    (strings / "Resources.resw").write_text(resw, encoding="utf-8", newline="\n")
    pres = tmp / "app" / "src" / "DnaEntropyGraph.Presentation"
    pres.mkdir(parents=True)
    (pres / "Vm.cs").write_text(cs, encoding="utf-8", newline="\n")
    if extra_cs:
        (pres / "Copy.cs").write_text(extra_cs, encoding="utf-8", newline="\n")
    return tmp


def _codes(tmp: Path) -> list[tuple[str, str]]:
    return [(f.code, f.key) for f in cfa.scan_repo(tmp).findings]


def test_matching_arity_is_clean(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0} y {1}"), 'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), a, b);')
    assert _codes(root) == []


def test_planted_extra_argument_is_the_alert_shape(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0}"), 'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), a, b);')
    assert _codes(root) == [("TOO-MANY-ARGS", "A")]


def test_planted_missing_argument_would_throw_at_runtime(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0} {1}"), 'var s = string.Format(_strings.GetString("A"), a);')
    assert _codes(root) == [("TOO-FEW-ARGS", "A")]


def test_nested_commas_and_strings_in_arguments_count_as_one_argument(tmp_path):
    cs = 'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), Math.Max(1, (int)Math.Round(t.X, 2)), "a,b)", \'c\');'
    assert _codes(_tree(tmp_path, _resw(A="{0} {1} {2}"), cs)) == []


def test_const_key_is_resolved_through_the_copy_class(tmp_path):
    copy = 'static class RunsCopy { public const string Badge = "Runs_Badge"; }'
    cs = "var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString(RunsCopy.Badge), a, b);"
    assert _codes(_tree(tmp_path, _resw(Runs_Badge="{0}"), cs, copy)) == [("TOO-MANY-ARGS", "Runs_Badge")]


def test_repeated_and_formatted_placeholders_use_the_max_index(tmp_path):
    cs = 'var s = string.Format(_strings.GetString("A"), a, b);'
    assert _codes(_tree(tmp_path, _resw(A="{1:N0} then {0} and {1}"), cs)) == []


def test_escaped_braces_are_not_placeholders(tmp_path):
    cs = 'var s = string.Format(_strings.GetString("A"), a);'
    assert _codes(_tree(tmp_path, _resw(A="{{literal}} {0}"), cs)) == []


def test_dynamic_key_and_params_spread_are_skipped_and_counted(tmp_path):
    cs = 'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);\nvar t = string.Format(_strings.GetString("A"), args);'  # noqa: E501
    result = cfa.scan_repo(_tree(tmp_path, _resw(A="{0}"), cs))
    assert result.findings == [] and result.checked == 0 and result.skipped == 2


def test_missing_resw_key_is_reported(tmp_path):
    root = _tree(tmp_path, _resw(B="{0}"), 'var s = string.Format(_strings.GetString("Nope"), a);')
    assert ("MISSING-KEY", "Nope") in _codes(root)


def test_placeholder_template_never_referenced_is_flagged(tmp_path):
    root = _tree(tmp_path, _resw(Used="{0}", Orphan="{0} here", Plain="no holes"), 'var s = string.Format(_strings.GetString("Used"), a);')
    assert _codes(root) == [("UNREFERENCED-TEMPLATE", "Orphan")]


def test_template_reached_via_a_wrapper_key_literal_counts_as_referenced(tmp_path):
    cs = 'var s = Format("Wrapped", a);'
    assert _codes(_tree(tmp_path, _resw(Wrapped="{0}"), cs)) == []


def test_vacuity_the_real_tree_has_checked_sites_and_is_clean():
    result = cfa.scan_repo(REPO)
    assert result.checked >= 20, f"scanner saw only {result.checked} call sites; the pattern drifted"
    assert [str(f) for f in result.findings] == []


def test_script_exits_one_on_a_planted_mismatch(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0}"), 'var s = string.Format(_strings.GetString("A"), a, b);')
    p = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root)], capture_output=True, text=True, check=False)
    assert p.returncode == 1 and "TOO-MANY-ARGS" in p.stderr
