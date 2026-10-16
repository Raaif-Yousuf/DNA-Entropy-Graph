"""Tests for scripts/check_xaml_shapes.py (#423): the XAML shapes that crash XamlCompiler.exe silently."""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_xaml_shapes as cxs  # noqa: E402

SCRIPT = SCRIPTS_DIR / "check_xaml_shapes.py"


def _codes(xaml: str, converters: set[str] | None = None) -> list[tuple[str, int]]:
    return [(f.code, f.line) for f in cxs.scan_xaml(xaml, "x.xaml", converters or set())]


def test_titlebar_attribute_on_window_names_the_line_of_the_window_tag():
    xaml = '<Window\n    x:Class="A.W"\n    ExtendsContentIntoTitleBar="True">\n  <Grid/>\n</Window>\n'
    assert _codes(xaml) == [("TITLEBAR-ATTRIBUTE", 1)]


def test_titlebar_in_a_comment_or_on_another_element_is_fine():
    xaml = '<Window>\n<!-- ExtendsContentIntoTitleBar="True" -->\n<Page ExtendsContentIntoTitleBar="True"/></Window>'
    assert _codes(xaml) == []


def _levels(xaml: str, converters: set[str]) -> list[tuple[str, str, int]]:
    return [(f.level, f.code, f.line) for f in cxs.scan_xaml(xaml, "x.xaml", converters)]


def test_local_converter_in_window_resources_is_a_hard_fail():
    xaml = '<Window>\n<Window.Resources>\n  <local:BoolToVis x:Key="a"/>\n</Window.Resources>\n</Window>'
    assert _levels(xaml, {"BoolToVis"}) == [("FAIL", "LOCAL-CONVERTER", 3)]


def test_local_converter_in_page_resources_is_measured_ok_and_silent():
    xaml = '<Page>\n<Page.Resources>\n  <local:BoolToVis x:Key="a"/>\n</Page.Resources>\n</Page>'
    assert _levels(xaml, {"BoolToVis"}) == []


def test_local_converter_in_an_unmeasured_scope_only_warns():
    xaml = '<Page>\n<Grid>\n<Grid.Resources>\n  <local:BoolToVis x:Key="a"/>\n</Grid.Resources>\n</Grid>\n</Page>'
    assert _levels(xaml, {"BoolToVis"}) == [("WARN", "LOCAL-CONVERTER-UNMEASURED", 4)]


def test_suffix_only_converter_in_window_resources_only_warns():
    xaml = '<Window>\n<Window.Resources>\n  <conv:FooConverter x:Key="b"/>\n</Window.Resources>\n</Window>'
    assert _levels(xaml, set()) == [("WARN", "LOCAL-CONVERTER-UNMEASURED", 3)]


def test_converter_outside_resources_and_stock_resources_are_fine():
    xaml = '<Window><Window.Resources><x:Double x:Key="d">1</x:Double></Window.Resources><local:BoolToVis/></Window>'
    assert _codes(xaml, {"BoolToVis"}) == []


def test_isenabled_on_a_stackpanel_is_flagged_and_on_a_button_is_not():
    xaml = '<Page>\n<StackPanel IsEnabled="{x:Bind VM.Ok, Mode=OneWay}">\n<Button IsEnabled="False"/>\n</StackPanel>\n</Page>'
    assert _codes(xaml) == [("NO-ISENABLED", 2)]


def test_a_binding_containing_a_greater_than_sign_does_not_end_the_tag_early():
    xaml = '<Page>\n<Button Content="{x:Bind a > b}"/>\n<StackPanel Margin="1" IsEnabled="True"/>\n</Page>'
    assert _codes(xaml) == [("NO-ISENABLED", 3)]


def test_local_converters_come_from_ivalueconverter_implementations():
    cs = ["public sealed class BoolToVis : IValueConverter { }", "class Other : IDisposable { }", "class Pair : Base, IValueConverter { }"]
    assert cxs.local_converters(cs) == {"BoolToVis", "Pair"}


def _repo(tmp_path: Path, xaml: str, cs: str = "") -> Path:
    app = tmp_path / "app" / "src" / "App"
    app.mkdir(parents=True)
    (app / "W.xaml").write_text(xaml, encoding="utf-8", newline="\n")
    (app / "C.cs").write_text(cs, encoding="utf-8", newline="\n")
    return tmp_path


def _run(root: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run([sys.executable, str(SCRIPT), "--root", str(root)], capture_output=True, text=True, check=False, timeout=60)


def test_cli_fails_with_file_line_and_shape_then_passes_when_fixed(tmp_path):
    root = _repo(tmp_path, '<Window>\n<StackPanel IsEnabled="False"/>\n</Window>')
    bad = _run(root)
    assert bad.returncode == 1
    assert "app/src/App/W.xaml:2: FAIL: NO-ISENABLED" in bad.stderr
    (root / "app" / "src" / "App" / "W.xaml").write_text("<Window>\n<StackPanel/>\n</Window>", encoding="utf-8", newline="\n")
    good = _run(root)
    assert good.returncode == 0 and "1 XAML file(s) checked" in good.stdout


def test_cli_warning_does_not_fail_but_is_printed(tmp_path):
    root = _repo(tmp_path, "<Page><Grid><Grid.Resources><local:Conv/></Grid.Resources></Grid></Page>", "class Conv : IValueConverter { }")
    result = _run(root)
    assert result.returncode == 0 and "WARN" in result.stdout and "THEORY (unverified)" in result.stdout


def test_cli_without_app_src_says_it_checked_nothing(tmp_path):
    result = _run(tmp_path)
    assert result.returncode == 2 and "checked nothing" in result.stderr


def test_the_real_tree_is_clean_and_self_test_passes():
    real = _run(SCRIPTS_DIR.parent)
    assert real.returncode == 0, real.stderr
    checked = re.search(r"OK: (\d+) XAML file", real.stdout)
    assert checked and int(checked.group(1)) >= 1, real.stdout
    proc = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True, check=False, timeout=60)
    assert proc.returncode == 0 and "PASS" in proc.stdout
