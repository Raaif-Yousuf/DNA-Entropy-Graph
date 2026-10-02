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
    result = subprocess.run(
        [sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True, check=False
    )
    assert result.returncode == 0


def test_a_fully_wired_tree_has_no_findings(tmp_path):
    assert _codes(_wired(tmp_path / "w")) == set()


def test_a_command_no_xaml_binds_is_unbound(tmp_path):
    root = _wired(tmp_path / "w")
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(
        page.read_text(encoding="utf-8").replace(
            '<Button Command="{x:Bind RefreshCommand}" />', ""
        ),
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
            "src/Demo.App/Services/Consumer.cs": (
                "public sealed class Consumer { public Consumer(IFoo foo) { } }\n"
            ),
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
            "src/Demo.App/Services/Consumer.cs": (
                "public sealed class Consumer { public Consumer(IFoo foo) { } }\n"
            ),
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
            "src/Demo.App/Services/Consumer.cs": (
                "public sealed class Consumer { public Consumer(INeverRegistered x) { } }\n"
            ),
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
        page.read_text(encoding="utf-8").replace(
            '<Button Command="{x:Bind RefreshCommand}" />', ""
        ),
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
        page.read_text(encoding="utf-8").replace(
            '<Button Command="{x:Bind RefreshCommand}" />', ""
        ),
        encoding="utf-8",
    )
    allowlist = tmp_path / "allow.json"
    assert (
        caw.main(["--root", str(root), "--allowlist", str(allowlist), "--suggest-allowlist"])
        == 0
    )
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
    text = (
        "public sealed class C { public C(IFoo foo, Action<string, JobPhase>? onPhase = null, int retries = 3, "
        "string name = \"a,b\") { } }"
    )
    assert caw._constructor_parameters(text, "C") == ["IFoo", "Action<string,JobPhase>", "int", "string"]


def test_a_defaulted_unregistered_parameter_is_not_an_unregistered_dependency_and_never_named_equals(tmp_path):
    root = _tree(tmp_path / "w", {
        "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Runner>();"),
        "src/Demo.App/Services/Runner.cs": (
            "public sealed class Runner { public Runner(Action<string, JobPhase>? onPhaseChanged = null) { } }\n"
        ),
    })
    symbols = [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"]
    assert symbols == [], symbols  # was: ['Runner(=)']


def test_a_required_unregistered_parameter_next_to_a_defaulted_one_is_still_reported(tmp_path):
    root = _tree(tmp_path / "w", {
        "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Runner>();"),
        "src/Demo.App/Services/Runner.cs": (
            "public sealed class Runner { public Runner(INeverRegistered x, Action? cb = null) { } }\n"
        ),
    })
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == ["Runner(INeverRegistered)"]


def test_a_registered_type_taken_with_a_default_value_still_counts_as_consumed(tmp_path):
    root = _tree(tmp_path / "w", {
        "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Runner>();", "s.AddSingleton<IClock, Clock>();"),
        "src/Demo.App/Services/Runner.cs": "public sealed class Runner { public Runner(IClock? clock = null) { } }\n",
        "src/Demo.App/Services/Clock.cs": "public sealed class Clock : IClock { }\n",
    })
    assert not [f for f in _findings(root) if f.symbol == "IClock"]


def test_the_instance_registration_form_registers_the_type(tmp_path):
    root = _tree(tmp_path / "w", {
        "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton<Consumer>();", "s.AddSingleton(new Dep());"),
        "src/Demo.App/Services/Consumer.cs": "public sealed class Consumer { public Consumer(Dep d) { } }\n",
        "src/Demo.App/Services/Dep.cs": "public sealed class Dep { }\n",
    })
    assert [f.symbol for f in _findings(root) if f.code == "UNREGISTERED-DEPENDENCY"] == []  # was: Consumer(Dep)


def test_an_instance_registered_type_nobody_asks_for_is_still_a_dead_registration(tmp_path):
    root = _tree(tmp_path / "w", {
        "src/Demo.App/Startup/ServiceRegistration.cs": _registration("s.AddSingleton(new Orphan());"),
        "src/Demo.App/Services/Orphan.cs": "public sealed class Orphan { }\n",
    })
    assert [f.symbol for f in _findings(root) if f.code == "DEAD-REGISTRATION"] == ["Orphan"]


def _unbound_caption_tree(root: Path, reader: str) -> Path:
    _wired(root)
    page = root / "src/Demo.App/Views/ThingPage.xaml"
    page.write_text(page.read_text(encoding="utf-8").replace('Text="{Binding Caption}"', ""), encoding="utf-8")
    (root / "src/Demo.Core").mkdir(parents=True, exist_ok=True)
    (root / "src/Demo.Core/Options.cs").write_text("public sealed record Options { public string? Caption { get; init; } }\n",
                                                   encoding="utf-8")
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
    for reader in ("public class Reader { string A() => ThingViewModel.Caption; }\n",
                   "public class Reader { string A(object o) => o.Caption; }\n"):
        root = _unbound_caption_tree(tmp_path / f"w{abs(hash(reader))}", reader)
        assert "CODE-ONLY-OBSERVABLE" in _codes(root), reader


def test_a_url_in_a_string_literal_is_not_mistaken_for_a_comment(tmp_path):
    root = _unbound_caption_tree(
        tmp_path / "w", 'public class Reader { string A(Vm v) { var u = "http://x"; return v.Caption; } }\n')
    assert "CODE-ONLY-OBSERVABLE" in _codes(root)


def test_a_read_through_a_variable_declared_as_another_type_is_not_a_read_of_this_member(tmp_path):
    """The real #461 shape: `options.ModelId` where `options` is a `RunOptions` parameter, not the ViewModel."""
    root = _unbound_caption_tree(tmp_path / "w", "public class Reader { string A(Options options) => options.Caption; }\n")
    codes = _codes(root)
    assert "UNBOUND-OBSERVABLE" in codes and "CODE-ONLY-OBSERVABLE" not in codes


def test_a_variable_of_unknown_or_owning_type_is_still_a_read(tmp_path):
    for reader in ("public class Reader { string A(ThingViewModel options) => options.Caption; }\n",
                   "public class Reader { string A() { var options = Make(); return options.Caption; } }\n"):
        root = _unbound_caption_tree(tmp_path / f"v{abs(hash(reader))}", reader)
        assert "CODE-ONLY-OBSERVABLE" in _codes(root), reader
