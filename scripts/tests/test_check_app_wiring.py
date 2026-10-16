"""Tests for scripts/check_app_wiring.py.

These are deliberately NOT a wrapper around the script's own `--self-test`.
One of them runs it, because a broken self-test should fail something; the
rest build their own fixture trees and assert the rule directly. This repo has
already shipped a self-test that could not fail
(`.claude/memory/a-self-test-that-always-passes`), and a test file whose only
assertion is "the self-test said PASS" would have agreed with it.

Every fixture here is a temporary tree. Nothing asserts a fact about the real
`app/` directory except that the guard can read it, because an assertion about
the repository's current state is a timer, not a test.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_app_wiring as caw  # noqa: E402

SCRIPT = SCRIPTS_DIR / "check_app_wiring.py"
REPO_ROOT = SCRIPTS_DIR.parent


def _tree(root: Path, files: dict[str, str]) -> Path:
    for relative, content in files.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
    return root


VM = """
namespace Demo.ViewModels;

public sealed partial class ThingViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _caption;

    [RelayCommand]
    private void Refresh()
    {
    }
}
"""

PAGE = """
<Page x:Class="Demo.Views.ThingPage">
    <TextBlock x:Uid="ThingCaption" Text="{Binding Caption}" />
    <Button Command="{x:Bind RefreshCommand}" />
</Page>
"""

RESW = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <data name="ThingCaption.Text" xml:space="preserve"><value>Caption</value></data>
</root>
"""


def _wired(root: Path) -> Path:
    return _tree(
        root,
        {
            "src/Demo.Presentation/ViewModels/ThingViewModel.cs": VM,
            "src/Demo.App/Views/ThingPage.xaml": PAGE,
            "src/Demo.App/Strings/en-US/Resources.resw": RESW,
        },
    )


def _codes(root: Path) -> set[str]:
    findings, _, _ = caw.collect_findings(root)
    return {f.code for f in findings}


def test_self_test_passes():
    result = subprocess.run(
        [sys.executable, str(SCRIPT), "--self-test"],
        check=False,
        capture_output=True,
        text=True,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "SELF-TEST PASSED" in result.stdout


def test_help_exits_zero():
    result = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True, check=False)
    assert result.returncode == 0


def test_a_fully_wired_tree_has_no_findings(tmp_path):
    assert _codes(_wired(tmp_path / "w")) == set()


def _typed(root: Path, item_cs: str, template_body: str, quote: str = '"') -> Path:
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace(
            "</Page>", f"<DataTemplate x:DataType={quote}vm:Item{quote}>{template_body}</DataTemplate></Page>"
        ),
        encoding="utf-8",
    )
    (root / "src/Demo.Presentation/Item.cs").write_text(item_cs, encoding="utf-8")
    return root


POSITIONAL_RECORD = "public sealed record Item(string Header, IReadOnlyList<string> Items, int Count = 0);\n"


def test_a_typed_template_binding_a_positional_record_parameter_is_clean(tmp_path):
    root = _typed(_wired(tmp_path / "w"), POSITIONAL_RECORD, '<TextBlock Text="{x:Bind Header}" /><ListView ItemsSource="{x:Bind Items}" />')
    assert "DANGLING-BINDING" not in _codes(root)


def test_a_positional_record_default_value_parameter_is_still_a_member(tmp_path):
    root = _typed(_wired(tmp_path / "w"), POSITIONAL_RECORD, '<TextBlock Text="{x:Bind Count}" />')
    assert "DANGLING-BINDING" not in _codes(root)


def test_a_typed_template_binding_a_name_the_positional_record_lacks_is_caught(tmp_path):
    root = _typed(_wired(tmp_path / "w"), POSITIONAL_RECORD, '<TextBlock Text="{x:Bind Heder}" />')
    assert "DANGLING-BINDING" in _codes(root)


def test_the_primary_constructor_parameters_of_a_plain_class_are_not_members(tmp_path):
    cs = "public sealed class Item(string Header) { public string Own { get; } = Header; }\n"
    root = _typed(_wired(tmp_path / "w"), cs, '<TextBlock Text="{x:Bind Header}" />')
    assert "DANGLING-BINDING" in _codes(root)


def test_a_member_of_another_type_in_the_same_file_is_not_a_member(tmp_path):
    cs = (
        'public sealed class Item { public string Own { get; } = ""; }\npublic sealed class Other { public string OnlyOnOther { get; } = ""; }\n'
    )
    bad = _typed(_wired(tmp_path / "bad"), cs, '<TextBlock Text="{x:Bind OnlyOnOther}" />')
    good = _typed(_wired(tmp_path / "good"), cs, '<TextBlock Text="{x:Bind Own}" />')
    assert "DANGLING-BINDING" in _codes(bad)
    assert "DANGLING-BINDING" not in _codes(good)


def test_a_member_declared_before_the_class_in_the_same_file_does_not_leak_in(tmp_path):
    cs = 'public sealed class Before { public string Early { get; } = ""; }\npublic sealed class Item { public string Own { get; } = ""; }\n'
    assert "DANGLING-BINDING" in _codes(_typed(_wired(tmp_path / "w"), cs, '<TextBlock Text="{x:Bind Early}" />'))


def test_a_single_quoted_data_type_is_read_both_ways(tmp_path):
    cs = 'public sealed class Item { public string Own { get; } = ""; }\n'
    bad = _typed(_wired(tmp_path / "bad"), cs, '<TextBlock Text="{x:Bind Nope}" />', quote="'")
    good = _typed(_wired(tmp_path / "good"), cs, '<TextBlock Text="{x:Bind Own}" />', quote="'")
    assert "DANGLING-BINDING" in _codes(bad)
    assert "DANGLING-BINDING" not in _codes(good)


def test_a_binding_in_a_nested_typed_template_is_held_to_the_inner_type(tmp_path):
    cs = 'public sealed record Group(string Header);\npublic sealed class Row { public string Title { get; } = ""; }\n'
    inner = '<TextBlock Text="{x:Bind Header}" /><DataTemplate x:DataType="vm:Row"><TextBlock Text="{x:Bind %s}" /></DataTemplate>'
    page = tmp_path / "w"
    root = _wired(page)
    pg = root / "src/Demo.App/Views/ThingPage.xaml"
    pg.write_text(
        pg.read_text(encoding="utf-8").replace("</Page>", '<DataTemplate x:DataType="vm:Group">' + inner % "Title" + "</DataTemplate></Page>"),
        encoding="utf-8",
    )
    (root / "src/Demo.Presentation/Item.cs").write_text(cs, encoding="utf-8")
    assert "DANGLING-BINDING" not in _codes(root)
    pg.write_text(pg.read_text(encoding="utf-8").replace("{x:Bind Title}", "{x:Bind Titel}"), encoding="utf-8")
    assert "DANGLING-BINDING" in _codes(root)


def test_a_command_no_xaml_binds_is_unbound(tmp_path):
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace('<Button Command="{x:Bind RefreshCommand}" />', ""),
        encoding="utf-8",
    )
    assert "UNBOUND-COMMAND" in _codes(root)


def test_an_observable_no_xaml_binds_is_unbound(tmp_path):
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace('Text="{Binding Caption}"', ""),
        encoding="utf-8",
    )
    assert "UNBOUND-OBSERVABLE" in _codes(root)


def test_a_binding_to_a_name_that_does_not_exist_is_dangling(tmp_path):
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace("{Binding Caption}", "{Binding Captoin}"),
        encoding="utf-8",
    )
    assert "DANGLING-BINDING" in _codes(root)


def test_a_uid_with_no_resource_is_a_finding(tmp_path):
    """The measured false pass: the window opens and the label is blank."""
    root = _wired(tmp_path / "w")
    resw = root / "src/Demo.App/Strings/en-US/Resources.resw"
    resw.write_text(
        resw.read_text(encoding="utf-8").replace("ThingCaption.Text", "Unrelated.Text"),
        encoding="utf-8",
    )
    assert "MISSING-UID-RESOURCE" in _codes(root)


def test_a_resource_no_uid_and_no_code_names_is_an_orphan(tmp_path):
    root = _wired(tmp_path / "w")
    resw = root / "src/Demo.App/Strings/en-US/Resources.resw"
    resw.write_text(
        resw.read_text(encoding="utf-8").replace(
            "</root>",
            '  <data name="NeverShown.Text"><value>x</value></data>\n</root>',
        ),
        encoding="utf-8",
    )
    assert "ORPHAN-RESOURCE" in _codes(root)


def test_a_binding_on_a_page_with_no_matching_viewmodel_is_skipped_not_guessed(tmp_path):
    """Guessing here would be a confident false positive, which is how a guard
    gets switched off. The skip must be reported, not silent."""
    root = _tree(
        tmp_path / "w",
        {"src/Demo.App/Views/MysteryPage.xaml": '<Page><T Text="{Binding Whatever}" /></Page>'},
    )
    findings, _, skips = caw.collect_findings(root)
    assert not any(f.code == "DANGLING-BINDING" for f in findings)
    assert any("MysteryPage.xaml" in s for s in skips)


def test_the_implementation_half_of_a_two_type_registration_is_not_dead(tmp_path):
    """`AddSingleton<IFoo, FooService>()` - nothing injects FooService by name,
    and calling that dead would fire on the commonest registration there is."""
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": (
                "public static class ServiceRegistration {\n"
                "  public static IServiceCollection AddDemo(this IServiceCollection s) {\n"
                "    s.AddSingleton<IFoo, FooService>();\n"
                "    return s;\n"
                "  }\n"
                "}\n"
            ),
            "src/Demo.App/Services/FooService.cs": "public sealed class FooService : IFoo { }\n",
            "src/Demo.App/Services/Consumer.cs": ("public sealed class Consumer { public Consumer(IFoo foo) { } }\n"),
        },
    )
    dead = {f.symbol for f in caw.collect_findings(root)[0] if f.code == "DEAD-REGISTRATION"}
    assert "FooService" not in dead


def test_a_factory_delegate_in_the_registration_file_counts_as_a_consumer(tmp_path):
    """One object behind several interfaces is this app's real shape; treating
    the registration file as a non-consumer made every such object look dead."""
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": (
                "public static class ServiceRegistration {\n"
                "  public static IServiceCollection AddDemo(this IServiceCollection s) {\n"
                "    s.AddSingleton<FakeGcp>();\n"
                "    s.AddSingleton<IFoo>(sp => sp.GetRequiredService<FakeGcp>());\n"
                "    return s;\n"
                "  }\n"
                "}\n"
            ),
            "src/Demo.App/Services/Consumer.cs": ("public sealed class Consumer { public Consumer(IFoo foo) { } }\n"),
        },
    )
    dead = {f.symbol for f in caw.collect_findings(root)[0] if f.code == "DEAD-REGISTRATION"}
    assert "FakeGcp" not in dead


def test_an_unregistered_constructor_parameter_is_a_finding(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": (
                "public static class ServiceRegistration {\n"
                "  public static IServiceCollection AddDemo(this IServiceCollection s) {\n"
                "    s.AddSingleton<Consumer>();\n"
                "    return s;\n"
                "  }\n"
                "}\n"
            ),
            "src/Demo.App/Services/Consumer.cs": ("public sealed class Consumer { public Consumer(INeverRegistered x) { } }\n"),
        },
    )
    codes = _codes(root)
    assert "UNREGISTERED-DEPENDENCY" in codes


def test_another_types_declaration_of_the_same_name_is_not_a_consumer(tmp_path):
    """The regression this distinction exists for: `RunOptions.ModelId`'s own
    declaration must not make `NewRunViewModel.ModelId` look consumed."""
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace('Text="{Binding Caption}"', ""),
        encoding="utf-8",
    )
    (root / "src/Demo.Core/Options.cs").parent.mkdir(parents=True, exist_ok=True)
    (root / "src/Demo.Core/Options.cs").write_text(
        "public sealed record Options { public string? Caption { get; init; } }\n",
        encoding="utf-8",
    )
    codes = _codes(root)
    assert "UNBOUND-OBSERVABLE" in codes
    assert "CODE-ONLY-OBSERVABLE" not in codes


def test_a_real_member_access_elsewhere_downgrades_to_code_only(tmp_path):
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace('Text="{Binding Caption}"', ""),
        encoding="utf-8",
    )
    (root / "src/Demo.App/Services/Reader.cs").parent.mkdir(parents=True, exist_ok=True)
    (root / "src/Demo.App/Services/Reader.cs").write_text(
        "public class Reader { void A(ThingViewModel vm) { _ = vm.Caption; } }\n",
        encoding="utf-8",
    )
    codes = _codes(root)
    assert "CODE-ONLY-OBSERVABLE" in codes
    assert "UNBOUND-OBSERVABLE" not in codes


def test_an_allowlist_entry_that_no_longer_fires_fails(tmp_path, capsys):
    root = _wired(tmp_path / "w")
    allowlist = tmp_path / "allow.json"
    allowlist.write_text(
        json.dumps({"UNBOUND-COMMAND:ThingViewModel.RefreshCommand": "gone"}),
        encoding="utf-8",
    )
    assert caw.run(root, allowlist, verbose=False, as_json=False) == 1
    assert "no longer fire" in capsys.readouterr().out


def test_an_allowlist_entry_that_does_fire_suppresses_it(tmp_path):
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace('<Button Command="{x:Bind RefreshCommand}" />', ""),
        encoding="utf-8",
    )
    allowlist = tmp_path / "allow.json"
    allowlist.write_text(
        json.dumps({"UNBOUND-COMMAND:ThingViewModel.RefreshCommand": "wired by #63"}),
        encoding="utf-8",
    )
    assert caw.run(root, allowlist, verbose=False, as_json=False) == 0


def test_a_missing_root_says_it_checked_nothing_rather_than_passing_quietly(tmp_path, capsys):
    """A guard that exits 0 because its target does not exist is a guard that
    silently stopped enforcing. check_guard_drift.py watches for this notice."""
    exit_code = caw.main(["--root", str(tmp_path / "nope")])
    assert exit_code == 0
    assert "checked nothing" in capsys.readouterr().out


def test_generated_and_build_output_is_not_scanned(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/obj/Generated.cs": VM,
            "src/Demo.App/bin/Other.g.cs": VM,
        },
    )
    _, scan, _ = caw.collect_findings(root)
    assert scan.cs_files == {}


def test_suggest_allowlist_leaves_every_reason_empty_and_writes_nothing(tmp_path, capsys):
    """An allowlist entry whose reason nobody wrote is a baseline silently
    raised, which is the one thing this guard exists to stop. So the skeleton
    must come out blank, and it must not touch the allowlist file."""
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace('<Button Command="{x:Bind RefreshCommand}" />', ""),
        encoding="utf-8",
    )
    allowlist = tmp_path / "allow.json"
    assert caw.main(["--root", str(root), "--allowlist", str(allowlist), "--suggest-allowlist"]) == 0
    suggested = json.loads(capsys.readouterr().out)
    assert suggested, "nothing suggested for a tree that has a finding"
    assert set(suggested.values()) == {""}
    assert not allowlist.exists(), "--suggest-allowlist must not write the file"


def test_it_reads_the_real_app_tree_when_one_exists():
    """Asserts the RULE (it reads what is there), never a count or a verdict:
    an assertion about today's app/ contents would go red on schedule."""
    app = REPO_ROOT / "app"
    if not app.exists():
        return
    _, scan, _ = caw.collect_findings(app)
    assert scan.cs_files, "the guard read zero .cs files from a tree that has them"


# --- #461: three false results, each reproduced by a fixture first ----------------------------------------


def _registration(*adds: str) -> str:
    body = "".join(f"    {a}\n" for a in adds)
    return (
        "public static class ServiceRegistration {\n"
        "  public static IServiceCollection AddDemo(this IServiceCollection s) {\n"
        f"{body}    return s;\n  }}\n}}\n"
    )


def _findings(root: Path):
    return caw.collect_findings(root)[0]


def test_a_constructor_parameter_with_a_default_value_parses_to_its_type():
    text = 'public sealed class C { public C(IFoo foo, Action<string, JobPhase>? onPhase = null, int retries = 3, string name = "a,b") { } }'
    assert caw._constructor_parameters(text, "C") == ["IFoo", "Action<string,JobPhase>", "int", "string"]


def test_a_defaulted_unregistered_parameter_is_not_an_unregistered_dependency_and_never_named_equals(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Runner>();"),
            "src/Demo.App/Services/Runner.cs": (
                "public sealed class Runner { public Runner(Action<string, JobPhase>? onPhaseChanged = null) { } }\n"
            ),
        },
    )
    symbols = [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"]
    assert symbols == [], symbols  # was: ['Runner(=)']


def test_a_required_unregistered_parameter_next_to_a_defaulted_one_is_still_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Runner>();"),
            "src/Demo.App/Services/Runner.cs": ("public sealed class Runner { public Runner(INeverRegistered x, Action? cb = null) { } }\n"),
        },
    )
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == ["Runner(INeverRegistered)"]


def test_a_registered_type_taken_with_a_default_value_still_counts_as_consumed(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Runner>();", "s.AddSingleton<IClock, Clock>();"),
            "src/Demo.App/Services/Runner.cs": "public sealed class Runner { public Runner(IClock? clock = null) { } }\n",
            "src/Demo.App/Services/Clock.cs": "public sealed class Clock : IClock { }\n",
        },
    )
    assert not [f for f in _findings(root) if f.symbol == "IClock"]


def test_the_instance_registration_form_registers_the_type(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Consumer>();", "s.AddSingleton(new Dep());"),
            "src/Demo.App/Services/Consumer.cs": "public sealed class Consumer { public Consumer(Dep d) { } }\n",
            "src/Demo.App/Services/Dep.cs": "public sealed class Dep { }\n",
        },
    )
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == []  # was: Consumer(Dep)


def test_an_instance_registered_type_nobody_asks_for_is_still_a_dead_registration(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton(new Orphan());"),
            "src/Demo.App/Services/Orphan.cs": "public sealed class Orphan { }\n",
        },
    )
    assert [f.symbol for f in _findings(root) if f.code == "DEAD-REGISTRATION"] == ["Orphan"]


def _unbound_caption_tree(root: Path, reader: str) -> Path:
    _wired(root)
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(page.read_text(encoding="utf-8").replace('Text="{Binding Caption}"', ""), encoding="utf-8")
    (root / "src/Demo.Core").mkdir(parents=True, exist_ok=True)
    (root / "src/Demo.Core/Options.cs").write_text("public sealed record Options { public string? Caption { get; init; } }\n", encoding="utf-8")
    (root / "src/Demo.App/Services").mkdir(parents=True, exist_ok=True)
    (root / "src/Demo.App/Services/Reader.cs").write_text(reader, encoding="utf-8")
    return root


def test_a_comment_that_spells_a_member_access_is_not_a_read(tmp_path):
    root = _unbound_caption_tree(tmp_path / "w", "public class Reader {\n  // see ThingViewModel.Caption\n  /* vm.Caption */\n}\n")
    codes = _codes(root)
    assert "UNBOUND-OBSERVABLE" in codes and "CODE-ONLY-OBSERVABLE" not in codes


def test_a_read_through_another_declared_type_name_is_not_a_read_of_this_member(tmp_path):
    root = _unbound_caption_tree(tmp_path / "w", "public class Reader { string A() => Options.Caption; }\n")
    codes = _codes(root)
    assert "UNBOUND-OBSERVABLE" in codes and "CODE-ONLY-OBSERVABLE" not in codes


def test_a_read_through_the_owning_type_name_or_an_instance_is_still_a_read(tmp_path):
    for reader in (
        "public class Reader { string A() => ThingViewModel.Caption; }\n",
        "public class Reader { string A(object o) => o.Caption; }\n",
    ):
        root = _unbound_caption_tree(tmp_path / f"w{abs(hash(reader))}", reader)
        assert "CODE-ONLY-OBSERVABLE" in _codes(root), reader


def test_a_url_in_a_string_literal_is_not_mistaken_for_a_comment(tmp_path):
    root = _unbound_caption_tree(tmp_path / "w", 'public class Reader { string A(Vm v) { var u = "http://x"; return v.Caption; } }\n')
    assert "CODE-ONLY-OBSERVABLE" in _codes(root)


def test_a_read_through_a_variable_declared_as_another_type_is_not_a_read_of_this_member(tmp_path):
    """The real #461 shape: `options.ModelId` where `options` is a `RunOptions` parameter, not the ViewModel."""
    root = _unbound_caption_tree(tmp_path / "w", "public class Reader { string A(Options options) => options.Caption; }\n")
    codes = _codes(root)
    assert "UNBOUND-OBSERVABLE" in codes and "CODE-ONLY-OBSERVABLE" not in codes


def test_a_variable_of_unknown_or_owning_type_is_still_a_read(tmp_path):
    for reader in (
        "public class Reader { string A(ThingViewModel options) => options.Caption; }\n",
        "public class Reader { string A() { var options = Make(); return options.Caption; } }\n",
    ):
        root = _unbound_caption_tree(tmp_path / f"v{abs(hash(reader))}", reader)
        assert "CODE-ONLY-OBSERVABLE" in _codes(root), reader


# --- #530: a factory registration builds the type itself, the container injects none of its constructor ------


def test_a_factory_registered_type_with_a_func_parameter_is_not_an_unregistered_dependency(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<Reconciler>();",
                "s.AddSingleton<Watcher>(sp => new Watcher(\n        () => sp.GetRequiredService<Reconciler>()));",
                "s.AddSingleton<IObserver>(sp => sp.GetRequiredService<Watcher>());",
            ),
            "src/Demo.Core/Reconciler.cs": "public sealed class Reconciler { }\n",
            "src/Demo.Core/Watcher.cs": "public sealed class Watcher { public Watcher(Func<Reconciler> factory) { } }\n",
        },
    )
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == []  # was: Watcher(Func<Reconciler>)


def test_a_factory_registered_type_with_an_interface_and_a_lambda_parameter_is_skipped_too(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<IWatcher, Watcher>((IServiceProvider _) => new Watcher(null!));",
            ),
            "src/Demo.Core/Watcher.cs": "public sealed class Watcher : IWatcher { public Watcher(Func<int> f) { } }\n",
        },
    )
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == []


def test_a_type_registered_without_a_factory_and_taking_a_func_is_still_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Watcher>();"),
            "src/Demo.Core/Watcher.cs": "public sealed class Watcher { public Watcher(Func<int> f) { } }\n",
        },
    )
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == ["Watcher(Func<int>)"]


# --- #530 cold-review round: the factory skip must not blind the check ---------------------------------------


def _codes_of(root: Path, code: str) -> list[str]:
    return [f.symbol for f in _findings(root) if f.code == code]


def test_a_get_required_service_of_an_unregistered_type_inside_a_factory_is_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<W>(sp => new W(sp.GetRequiredService<Missing>()));",
            ),
            "src/Demo.Core/W.cs": "public sealed class W { public W(Missing m) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == ["Missing"]  # was: nothing (and W(Missing) before the skip)


def test_a_get_required_service_of_a_registered_type_is_not_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<Dep>();",
                "s.AddSingleton<W>(sp => new W(sp.GetRequiredService<Dep>()));",
                "s.AddSingleton<IView>(sp => sp.GetRequiredService<W>());",
            ),
            "src/Demo.Core/W.cs": "public sealed class W { public W(Dep d) { } }\n",
            "src/Demo.Core/Dep.cs": "public sealed class Dep { }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == []


def test_a_resolve_named_only_in_a_comment_is_not_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "// sp.GetRequiredService<Ghost>() is how a caller would ask",
                "s.AddSingleton<Dep>();",
            ),
            "src/Demo.Core/Dep.cs": "public sealed class Dep { public Dep(int n) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == []


def test_a_type_registered_by_a_factory_and_plainly_is_still_checked_as_a_plain_registration(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<Watcher>(sp => new Watcher(() => 1));",
                "s.AddTransient<Watcher>();",
            ),
            "src/Demo.Core/Watcher.cs": "public sealed class Watcher { public Watcher(Func<int> f) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-DEPENDENCY") == ["Watcher(Func<int>)"]


def test_static_and_parenthesised_and_nested_paren_lambdas_are_factory_registrations(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<A>(static sp => new A(() => 1));",
                "s.AddSingleton<B>((sp) => new B(() => 1));",
                "s.AddSingleton<C>(static (IServiceProvider sp) => new C(() => 1));",
                "s.AddSingleton<D>(async (IServiceProvider sp, object? key) => new D(() => 1));",
            ),
            "src/Demo.Core/Types.cs": (
                "public sealed class A { public A(Func<int> f) { } }\n"
                "public sealed class B { public B(Func<int> f) { } }\n"
                "public sealed class C { public C(Func<int> f) { } }\n"
                "public sealed class D { public D(Func<int> f) { } }\n"
            ),
        },
    )
    assert _codes_of(root, "UNREGISTERED-DEPENDENCY") == []


def test_the_inferred_type_factory_form_registers_the_type_it_news_up(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                's.AddSingleton(_ => new Db("p"));',
                "s.AddSingleton<IRepo>(sp => new Repo(sp.GetRequiredService<Db>()));",
            ),
            "src/Demo.Core/Repo.cs": "public sealed class Repo : IRepo { public Repo(Db d) { } }\n",
            "src/Demo.Core/Db.cs": "public sealed class Db { public Db(string path) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == []  # was: Db
    assert _codes_of(root, "UNREGISTERED-DEPENDENCY") == []


# --- #530 cold-review round 2 ---------------------------------------------------------------------------------


def test_resolving_the_implementation_type_of_an_interface_registration_is_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<IToastService, ToastService>();",
                "s.AddSingleton<Other>(sp => new Other(sp.GetRequiredService<ToastService>()));",
            ),
            "src/Demo.Core/Toast.cs": "public sealed class ToastService : IToastService { }\n",
            "src/Demo.Core/Other.cs": "public sealed class Other { public Other(ToastService t) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == ["ToastService"]


def test_resolving_the_interface_of_an_interface_registration_is_not_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<IToastService, ToastService>();",
                "s.AddSingleton<Other>(sp => new Other(sp.GetRequiredService<IToastService>()));",
            ),
            "src/Demo.Core/Toast.cs": "public sealed class ToastService : IToastService { }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == []


def test_a_commented_out_registration_does_not_count_as_registered(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "// s.AddSingleton<Dep>();",
                "s.AddSingleton<W>(sp => new W(sp.GetRequiredService<Dep>()));",
            ),
            "src/Demo.Core/W.cs": "public sealed class W { public W(Dep d) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == ["Dep"]


def test_a_commented_out_plain_registration_does_not_defeat_the_factory_skip(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "// s.AddTransient<Watcher>();",
                "s.AddSingleton<Watcher>(sp => new Watcher(() => 1));",
            ),
            "src/Demo.Core/Watcher.cs": "public sealed class Watcher { public Watcher(Func<int> f) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-DEPENDENCY") == []


def test_get_service_the_optional_resolve_is_not_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<W>(sp => new W(sp.GetService<Maybe>()));",
            ),
            "src/Demo.Core/W.cs": "public sealed class W { public W(Maybe? m) { } }\n",
        },
    )
    assert _codes_of(root, "UNREGISTERED-RESOLVE") == []


# --- #478: balanced parameter lists and nested generics ---------------------------------------------


def test_a_default_value_with_parentheses_does_not_hide_the_later_parameters():
    text = "public class Foo { public Foo(IBar bar = default(IBar), IBaz baz) { } }"
    assert caw._constructor_parameters(text, "Foo") == ["IBar", "IBaz"]  # was ['IBar']: the list stopped at default(IBar
    assert caw._constructor_parameters(text, "Foo", skip_defaults=True) == ["IBaz"]


def test_new_and_nested_call_defaults_are_read_through_to_the_closing_paren():
    text = 'public class Foo { public Foo(IA a = new(), IB b = new Bar(1, Make(2)), IC c, string s = ")") { } }'
    assert caw._constructor_parameters(text, "Foo") == ["IA", "IB", "IC", "string"]


def test_an_unregistered_parameter_after_a_parenthesised_default_is_reported(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Foo>();"),
            "src/Demo.App/Services/Foo.cs": ("public sealed class Foo { public Foo(IBar bar = default(IBar), IBaz baz) { } }\n"),
        },
    )
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == ["Foo(IBaz)"]


def test_a_nested_generic_registration_is_one_type_not_two_half_types():
    every, services = caw._registered_types("s.AddSingleton<IRepo<Run, Guid>, Repo>();")
    assert every == {"IRepo<Run,Guid>", "Repo"}  # was {'IRepo<Run', 'Guid>', 'Repo'}
    assert services == {"IRepo<Run,Guid>"}


def test_a_doubly_nested_generic_and_a_qualified_name_are_normalised_like_constructor_parameters():
    every, services = caw._registered_types("s.AddTransient<Demo.IRepo<Dictionary<string, int>, Guid>, Demo.Repo>();")
    assert every == {"IRepo<Dictionary<string,int>,Guid>", "Repo"}
    assert services == {"IRepo<Dictionary<string,int>,Guid>"}


def test_a_consumer_of_a_nested_generic_service_is_satisfied_and_the_registration_is_not_dead(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.App/Startup/ServiceRegistration.cs": _registration(
                "s.AddSingleton<Consumer>();", "s.AddSingleton<IRepo<Run, Guid>, Repo>();"
            ),
            "src/Demo.App/Services/Consumer.cs": "public sealed class Consumer { public Consumer(IRepo<Run, Guid> repo) { } }\n",
            "src/Demo.App/Services/Repo.cs": "public sealed class Repo : IRepo<Run, Guid> { }\n",
        },
    )
    # (Consumer itself is a dead registration in this tiny tree: nothing takes a Consumer. That is not what this is about.)
    bad = [(f.code, f.symbol) for f in _findings(root) if f.code in {"UNREGISTERED-DEPENDENCY", "DEAD-REGISTRATION"} and f.symbol != "Consumer"]
    assert bad == [], bad


def test_a_nested_generic_factory_registration_is_still_a_factory_registration():
    text = "s.AddSingleton<IRepo<Run, Guid>>(sp => new Repo());"
    assert caw._factory_only_types(text) == {"IRepo<Run,Guid>"}


# --- #568: a display property no XAML binds is wired to nothing, whatever C# and tests read -------------------

ITEM_CS = """
namespace Demo.ViewModels;

public sealed class RunListItem
{
    public string Title { get; init; } = "";

    public string ReasonText { get; init; } = "";
}
"""

ITEM_PAGE = """
<Page x:Class="Demo.Views.RunsPage">
    <ListView>
        <ListView.ItemTemplate>
            <DataTemplate x:DataType="vm:RunListItem">
                <StackPanel>
                    <TextBlock Text="{x:Bind Title}" />
                    <TextBlock Text="{x:Bind ReasonText}" />
                </StackPanel>
            </DataTemplate>
        </ListView.ItemTemplate>
    </ListView>
</Page>
"""

ITEM_READER = "public class Reader { string A(RunListItem i) => i.ReasonText; }\n"
ITEM_TEST = "public class T { void A(RunListItem i) { _ = i.ReasonText; } }\n"


def _item_tree(root: Path, page: str = ITEM_PAGE, extra: dict[str, str] | None = None) -> Path:
    files = {
        "src/Demo.Presentation/RunListItem.cs": ITEM_CS,
        "src/Demo.App/Views/RunsPage.xaml": page,
    }
    files.update(extra or {})
    return _tree(root, files)


def test_a_template_item_property_bound_in_the_template_is_clean(tmp_path):
    assert _codes_of(_item_tree(tmp_path / "w"), "UNBOUND-DISPLAY-PROPERTY") == []


def test_a_template_item_property_with_its_x_bind_deleted_is_flagged_by_name(tmp_path):
    page = ITEM_PAGE.replace('<TextBlock Text="{x:Bind ReasonText}" />', "")
    assert _codes_of(_item_tree(tmp_path / "w", page), "UNBOUND-DISPLAY-PROPERTY") == ["RunListItem.ReasonText"]


def test_production_and_test_references_do_not_count_as_a_binding(tmp_path):
    page = ITEM_PAGE.replace('<TextBlock Text="{x:Bind ReasonText}" />', "")
    root = _item_tree(
        tmp_path / "w",
        page,
        {"src/Demo.App/Reader.cs": ITEM_READER, "tests/Demo.Tests/ItemTests.cs": ITEM_TEST},
    )
    assert _codes_of(root, "UNBOUND-DISPLAY-PROPERTY") == ["RunListItem.ReasonText"]


def test_a_name_in_plain_xaml_text_that_is_not_a_binding_does_not_count(tmp_path):
    page = ITEM_PAGE.replace('<TextBlock Text="{x:Bind ReasonText}" />', '<TextBlock x:Name="ReasonText" />')
    assert _codes_of(_item_tree(tmp_path / "w", page), "UNBOUND-DISPLAY-PROPERTY") == ["RunListItem.ReasonText"]


def test_a_property_used_inside_an_x_bind_function_call_counts_as_bound(tmp_path):
    page = ITEM_PAGE.replace("{x:Bind ReasonText}", "{x:Bind local:Fmt.Show(ReasonText)}")
    assert _codes_of(_item_tree(tmp_path / "w", page), "UNBOUND-DISPLAY-PROPERTY") == []


def test_a_positional_record_parameter_nothing_binds_is_flagged(tmp_path):
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.Presentation/Row.cs": "public sealed record Row(string Header, string Hidden);\n",
            "src/Demo.App/Views/RowsPage.xaml": (
                '<Page><DataTemplate x:DataType="vm:Row"><TextBlock Text="{x:Bind Header}" /></DataTemplate></Page>'
            ),
        },
    )
    assert _codes_of(root, "UNBOUND-DISPLAY-PROPERTY") == ["Row.Hidden"]


def test_a_page_viewmodel_plain_property_nothing_binds_is_flagged(tmp_path):
    vm = VM.replace("[RelayCommand]", 'public string Summary => "";\n\n    [RelayCommand]')
    root = _tree(
        tmp_path / "w",
        {
            "src/Demo.Presentation/ViewModels/ThingViewModel.cs": vm,
            "src/Demo.App/Views/ThingPage.xaml": PAGE,
            "src/Demo.App/Strings/en-US/Resources.resw": RESW,
        },
    )
    assert _codes_of(root, "UNBOUND-DISPLAY-PROPERTY") == ["ThingViewModel.Summary"]


def test_a_command_property_and_a_private_or_static_property_are_not_display_properties(tmp_path):
    cs = (
        "public sealed class RunListItem\n{\n"
        '    public string Title { get; init; } = "";\n'
        "    public IRelayCommand OpenCommand { get; }\n"
        '    private string Secret { get; } = "";\n'
        '    public static string Shared { get; } = "";\n'
        '    public string Method() => "";\n}\n'
    )
    page = '<Page><DataTemplate x:DataType="vm:RunListItem"><TextBlock Text="{x:Bind Title}" /></DataTemplate></Page>'
    root = _tree(
        tmp_path / "w",
        {"src/Demo.Presentation/RunListItem.cs": cs, "src/Demo.App/Views/RunsPage.xaml": page},
    )
    assert _codes_of(root, "UNBOUND-DISPLAY-PROPERTY") == []


def test_the_display_property_scanner_sees_the_real_tree():
    # Vacuity: the scanner must find at least one typed template and its properties on the real app.
    scan = caw.read_tree(REPO_ROOT / "app")
    assert caw._display_property_candidates(scan), "no display-property candidates found in app/"


def test_only_string_text_is_held_to_a_binding_not_state_flags_enums_or_identifiers(tmp_path):
    cs = (
        "public sealed class RunListItem\n{\n"
        '    public string Title { get; init; } = "";\n'
        "    public bool IsOk { get; init; }\n"
        "    public RunStatusFilter Status { get; init; }\n"
        '    public string JobId { get; init; } = "";\n'
        "    public string? ActionUrl { get; init; }\n"
        '    public string Hint { get; init; } = "";\n}\n'
    )
    page = '<Page><DataTemplate x:DataType="vm:RunListItem"><TextBlock Text="{x:Bind Title}" /></DataTemplate></Page>'
    root = _tree(
        tmp_path / "w",
        {"src/Demo.Presentation/RunListItem.cs": cs, "src/Demo.App/Views/RunsPage.xaml": page},
    )
    assert _codes_of(root, "UNBOUND-DISPLAY-PROPERTY") == ["RunListItem.Hint"]


def test_a_nullable_string_property_is_a_display_property(tmp_path):
    cs = "public sealed class RunListItem\n{\n    public string? ReasonText { get; }\n}\n"
    page = '<Page><DataTemplate x:DataType="vm:RunListItem"><TextBlock Text="x" /></DataTemplate></Page>'
    root = _tree(
        tmp_path / "w",
        {"src/Demo.Presentation/RunListItem.cs": cs, "src/Demo.App/Views/RunsPage.xaml": page},
    )
    assert _codes_of(root, "UNBOUND-DISPLAY-PROPERTY") == ["RunListItem.ReasonText"]
