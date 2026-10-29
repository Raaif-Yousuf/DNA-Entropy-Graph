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
VM = "app/src/DnaEntropyGraph.Presentation/Vm.cs"


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
    return [(f.code, f.key) for f in cfa.scan_repo(tmp, known_dynamic={}).findings]


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
    cs = 'void F(object[] args) {}\nvar s = string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);\nvar t = string.Format(_strings.GetString("A"), args);'  # noqa: E501
    result = cfa.scan_repo(_tree(tmp_path, _resw(A="{0}"), cs), known_dynamic={VM: 2})
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


def test_comments_of_every_form_are_ignored(tmp_path):
    cs = (
        '/// <summary>string.Format(_strings.GetString("A"), a, b)</summary>\n'
        '/* string.Format(_strings.GetString("A"), a, b, c) */\n'
        '// string.Format(_strings.GetString("A"), a, b)\n'
        'var s = string.Format(_strings.GetString("A"), a);\n'
    )
    assert _codes(_tree(tmp_path, _resw(A="{0}"), cs)) == []


def test_comment_markers_inside_strings_and_char_literals_do_not_hide_a_mismatch(tmp_path):
    cs = 'var s = string.Format(_strings.GetString("A"), "http://x", \'"\', \'/\', "/* not a comment");'
    assert _codes(_tree(tmp_path, _resw(A="{0}"), cs)) == [("TOO-MANY-ARGS", "A")]


def test_raw_string_argument_counts_as_one_argument(tmp_path):
    cs = 'var s = string.Format(_strings.GetString("A"), """a, "b", c""", x);'
    assert _codes(_tree(tmp_path, _resw(A="{0} {1}"), cs)) == []


def test_new_object_array_argument_is_expanded(tmp_path):
    cs = 'var s = string.Format(_strings.GetString("A"), new object[] { a, b, c });'
    assert _codes(_tree(tmp_path, _resw(A="{0} {1}"), cs)) == [("TOO-MANY-ARGS", "A")]


def test_const_in_a_nested_type_and_static_readonly_keys_resolve(tmp_path):
    copy = (
        "static class Outer {\n"
        '    public const string First = "K_First";\n'
        '    public static class Inner { public const string Deep = "K_Deep"; }\n'
        '    public static readonly string After = "K_After";\n'
        "}\n"
    )
    cs = (
        "var a = string.Format(_strings.GetString(Outer.Inner.Deep), x, y);\n"
        "var b = string.Format(_strings.GetString(Outer.After), x, y);\n"
        "var c = string.Format(_strings.GetString(Outer.First), x, y);\n"
    )
    root = _tree(tmp_path, _resw(K_First="{0}", K_Deep="{0}", K_After="{0}"), cs, copy)
    assert _codes(root) == [("TOO-MANY-ARGS", "K_Deep"), ("TOO-MANY-ARGS", "K_After"), ("TOO-MANY-ARGS", "K_First")]


def test_every_resw_is_read_not_only_the_first(tmp_path):
    root = _tree(tmp_path, _resw(A="plain"), 'var s = string.Format(_strings.GetString("B"), a, b);')
    other = root / "app" / "src" / "Other" / "Strings" / "en-US"
    other.mkdir(parents=True)
    (other / "Resources.resw").write_text(_resw(B="{0}"), encoding="utf-8", newline="\n")
    assert _codes(root) == [("TOO-MANY-ARGS", "B")]


def test_an_unlisted_dynamic_site_is_a_finding(tmp_path):
    root = _tree(tmp_path, _resw(A="plain"), "var s = string.Format(_strings.GetString(key), a);")
    assert _codes(root) == [("UNEXPECTED-DYNAMIC", "")]


def test_a_stale_dynamic_allowlist_entry_is_a_finding(tmp_path):
    root = _tree(tmp_path, _resw(A="{0}"), 'var s = string.Format(_strings.GetString("A"), a);')
    codes = [f.code for f in cfa.scan_repo(root, known_dynamic={VM: 1}).findings]
    assert codes == ["STALE-DYNAMIC-ALLOWLIST"]


def test_the_real_allowlist_is_exact():
    assert cfa.KNOWN_DYNAMIC, "the allowlist is empty: the wrappers it names moved or the scanner stopped seeing them"
    assert sum(cfa.KNOWN_DYNAMIC.values()) == cfa.scan_repo(REPO).skipped


def test_script_fails_when_it_checked_fewer_sites_than_the_floor(tmp_path):
    root = _tree(tmp_path, _resw(A="{0}"), 'var s = string.Format(_strings.GetString("A"), a);')
    p = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root)], capture_output=True, text=True, check=False)
    assert p.returncode == 1 and "floor" in p.stderr
    ok = subprocess.run(
        [sys.executable, str(SCRIPT), "--root", str(root), "--min-checked", "1", "--no-dynamic-check"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert ok.returncode == 0, ok.stderr


def test_escaped_string_literal_before_a_format_call_does_not_desync_literal_pairing(tmp_path):
    # Found on the 2026-10-29 merge: `"\n\n" + string.Format(..., GetString("K"), x)` made the literal
    # regex (no backslash allowed) pair quotes across the call, so K looked unreferenced.
    root = _tree(
        tmp_path,
        _resw(A="x {0}", B="y {0}"),
        'text += "\\n\\n" + string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), a);\n'
        'var t = "\\"q\\"" + string.Format(CultureInfo.CurrentCulture, _strings.GetString("B"), b);',
    )
    assert _codes(root) == []


HELPER = (
    "private string Format(string key, params object[] args)\n    => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);\n"
)


def test_planted_mismatch_through_a_wrapper_helper_fails(tmp_path):
    root = _tree(tmp_path, _resw(NewRunGpuLocked="x {0}"), HELPER + 'var s = Format("NewRunGpuLocked", a, b, c);')
    assert _codes(root) == [("TOO-MANY-ARGS", "NewRunGpuLocked")]


def test_planted_too_few_args_through_a_wrapper_helper_fails(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0} {1}"), HELPER + 'var s = Format("A", a);')
    assert _codes(root) == [("TOO-FEW-ARGS", "A")]


def test_correct_wrapper_helper_call_passes_and_its_body_is_not_a_dynamic_site(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0} {1}"), HELPER + 'var s = Format("A", a, b);')
    assert _codes(root) == []
    result = cfa.scan_repo(root, known_dynamic={})
    assert result.checked == 1 and result.skipped == 0


def test_wrapper_helper_with_a_non_literal_key_is_dynamic(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0}"), HELPER + 'var s = Format(someKey, a);\nvar t = Format("A", a);')
    assert [c for c, _ in _codes(root)] == ["UNEXPECTED-DYNAMIC"]


def test_helper_with_a_const_key_is_resolved(tmp_path):
    root = _tree(
        tmp_path,
        _resw(A="x {0}"),
        HELPER + "var s = Format(Keys.Gpu, a, b);",
        extra_cs='static class Keys { public const string Gpu = "A"; }',
    )
    assert _codes(root) == [("TOO-MANY-ARGS", "A")]


def test_spread_of_any_object_array_variable_is_dynamic_not_one_argument(tmp_path):
    cs = 'var parts = new object[] { a, b };\nvar s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), parts);'
    root = _tree(tmp_path, _resw(A="x {0} {1}"), cs)
    result = cfa.scan_repo(root, known_dynamic=None)
    assert result.findings == [] and result.checked == 0 and result.skipped == 1


def test_a_single_non_array_argument_is_still_one_argument(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0} {1}"), 'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), count);')
    assert _codes(root) == [("TOO-FEW-ARGS", "A")]


def test_get_string_wrapped_in_another_call_is_not_a_key_argument(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0}"), 'var s = string.Format(CultureInfo.CurrentCulture, Wrap(_strings.GetString("A")), x, y);')
    result = cfa.scan_repo(root, known_dynamic=None)
    assert result.checked == 0 and result.skipped == 1


def test_string_literals_regular_verbatim_and_raw_are_tokenized_correctly():
    text = 'var a = "x\\"y"; var b = @"C:\\dir\\"; var c = @"say ""hi"""; var d = """ raw "quoted" \\ """; var e = "K";'
    assert cfa.string_literals(text) == ['x\\"y', "C:\\dir\\", 'say "hi"', ' raw "quoted" \\ ', "K"]


def test_interpolated_verbatim_prefix_in_either_order_is_verbatim():
    text = 'var a = $@"C:\\{x}\\"; var b = @$"D:\\"; var c = "K";'
    assert cfa.string_literals(text)[-1] == "K" and len(cfa.string_literals(text)) == 3


def test_verbatim_path_ending_in_backslash_does_not_hide_a_later_key_reference(tmp_path):
    cs = (
        'var p = @"C:\\dir\\";\nvar q = _strings.GetString("B");\nvar s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), x);'
    )
    root = _tree(tmp_path, _resw(A="x {0}", B="y {0}"), cs)
    assert _codes(root) == []


def test_raw_string_with_quotes_does_not_hide_a_later_key_reference(tmp_path):
    cs = (
        'var r = """ has "quotes" and \\ """;\nvar q = _strings.GetString("B");\n'
        'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), x);'
    )
    root = _tree(tmp_path, _resw(A="x {0}", B="y {0}"), cs)
    assert _codes(root) == []


def test_a_stale_resw_copy_under_obj_or_bin_does_not_override_the_real_template(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0}"), 'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), a);')
    for build in ("obj", "bin"):
        stale = root / "app" / "src" / "ZZ.Stale" / build / "en-US"
        stale.mkdir(parents=True)
        (stale / "Resources.resw").write_text(_resw(A="x"), encoding="utf-8", newline="\n")
    assert _codes(root) == []


def test_product_code_under_app_tools_is_scanned(tmp_path):
    root = _tree(tmp_path, _resw(A="x {0}"), 'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), a);')
    tools = root / "app" / "tools" / "T"
    tools.mkdir(parents=True)
    (tools / "Cli.cs").write_text(
        'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), a, b);', encoding="utf-8", newline="\n"
    )
    (tools / "obj").mkdir()
    (tools / "obj" / "Gen.cs").write_text(
        'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), a, b, c);', encoding="utf-8", newline="\n"
    )
    assert _codes(root) == [("TOO-MANY-ARGS", "A")]


def test_object_array_forms_with_spacing_and_implicit_arrays_count_each_element(tmp_path):
    for form in ("new object [] { a, b }", "new[] { a, b }", "new object[]{a,b}"):
        cs = f'var s = string.Format(CultureInfo.CurrentCulture, _strings.GetString("A"), {form});'
        assert _codes(_tree(tmp_path / str(abs(hash(form))), _resw(A="x {0}"), cs)) == [("TOO-MANY-ARGS", "A")], form


def test_helper_call_with_a_conditional_key_checks_every_branch(tmp_path):
    cs = HELPER + 'var s = Format(on ? "A" : other ? "B" : "C", x);'
    root = _tree(tmp_path, _resw(A="a {0}", B="b", C="c {0}"), cs)
    assert _codes(root) == [("TOO-MANY-ARGS", "B")]
    assert cfa.scan_repo(root, known_dynamic={}).checked == 3


def test_parenthesised_receiver_before_get_string_is_still_a_key_argument(tmp_path):
    cs = 'show(string.Format(CultureInfo.CurrentCulture, (strings ?? new Provider()).GetString("A"), x, y));'
    assert _codes(_tree(tmp_path, _resw(A="a {0}"), cs)) == [("TOO-MANY-ARGS", "A")]
