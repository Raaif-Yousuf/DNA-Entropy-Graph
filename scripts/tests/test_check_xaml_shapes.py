"""Tests for scripts/check_xaml_shapes.py (#423): the XAML shapes that crash XamlCompiler.exe silently."""

from __future__ import annotations

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


def test_local_converter_in_resources_found_by_class_name_and_by_suffix():
    xaml = '<Page>\n<Page.Resources>\n  <local:BoolToVis x:Key="a"/>\n  <conv:FooConverter x:Key="b"/>\n</Page.Resources>\n</Page>'
    assert _codes(xaml, {"BoolToVis"}) == [("LOCAL-CONVERTER", 3), ("LOCAL-CONVERTER", 4)]


def test_converter_outside_resources_and_stock_resources_are_fine():
    xaml = (
        '<Page><Page.Resources><x:Double x:Key="d">1</x:Double><Style x:Key="s" TargetType="Button"/></Page.Resources><local:BoolToVis/></Page>'
    )
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
    assert "app/src/App/W.xaml:2: NO-ISENABLED" in bad.stderr
    (root / "app" / "src" / "App" / "W.xaml").write_text("<Window>\n<StackPanel/>\n</Window>", encoding="utf-8", newline="\n")
    good = _run(root)
    assert good.returncode == 0 and "1 XAML file(s) checked" in good.stdout


def test_cli_without_app_src_says_it_checked_nothing(tmp_path):
    result = _run(tmp_path)
    assert result.returncode == 2 and "checked nothing" in result.stderr


def test_the_real_tree_is_clean_and_self_test_passes():
    assert _run(SCRIPTS_DIR.parent).returncode == 0
    proc = subprocess.run([sys.executable, str(SCRIPT), "--self-test"], capture_output=True, text=True, check=False, timeout=60)
    assert proc.returncode == 0 and "PASS" in proc.stdout
