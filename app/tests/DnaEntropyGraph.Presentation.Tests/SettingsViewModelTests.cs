using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public class SettingsViewModelTests
{
    private static SettingsViewModel CreateViewModel(ISettingsStore settingsStore, out IToastService toastService, IThemeApplier? themeApplier = null)
    {
        toastService = Substitute.For<IToastService>();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());
        return new SettingsViewModel(settingsStore, toastService, strings, Substitute.For<IDiagnosticsExporter>(), Substitute.For<IFilePicker>(), Substitute.For<IFolderLauncher>(), TimeProvider.System, themeApplier ?? Substitute.For<IThemeApplier>());
    }

    [Fact]
    public void Theme_defaults_to_system_when_nothing_was_ever_saved()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns((string?)null);
        var viewModel = CreateViewModel(settingsStore, out _);

        viewModel.Theme.ShouldBe("System");
    }

    [Fact]
    public void A_locked_settings_file_neither_crashes_opening_settings_nor_changing_the_theme()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns(_ => throw new SettingsUnavailableException("locked"));
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));

        var viewModel = CreateViewModel(settingsStore, out _);
        viewModel.Theme.ShouldBe("System");

        Should.NotThrow(() => viewModel.SetThemeCommand.Execute("Dark"));
        viewModel.Theme.ShouldBe("Dark");
    }

    [Fact]
    public void A_failed_theme_save_shows_the_failure_toast_not_theme_updated()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));
        var viewModel = CreateViewModel(settingsStore, out var toastService);

        viewModel.SetThemeCommand.Execute("Dark");

        viewModel.Theme.ShouldBe("Dark");
        toastService.Received(1).ShowToast("ThemeNotSaved_Title", "ThemeNotSaved_Body");
        toastService.DidNotReceive().ShowToast("ThemeUpdated_Title", Arg.Any<string>());
    }

    [Fact]
    public void Setting_the_theme_writes_it_back_to_the_settings_store()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        var viewModel = CreateViewModel(settingsStore, out _);

        viewModel.SetThemeCommand.Execute("Dark");

        settingsStore.Received(1).SetString("Theme", "Dark");
        viewModel.Theme.ShouldBe("Dark");
    }

    [Fact]
    public void Setting_the_theme_resolves_its_toast_title_from_the_string_resource_provider_not_a_literal()
    {
        // The mutation this guards against: SettingsViewModel reverting to a
        // literal "Theme updated" string (Hard Rule 13 / issue #71) would
        // still pass every other assertion in this file - only asserting
        // that IStringResourceProvider.GetString was actually consulted
        // catches it.
        var settingsStore = Substitute.For<ISettingsStore>();
        var toastService = Substitute.For<IToastService>();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("ThemeUpdated_Title").Returns("Theme updated (from resw)");
        var viewModel = new SettingsViewModel(settingsStore, toastService, strings, Substitute.For<IDiagnosticsExporter>(), Substitute.For<IFilePicker>(), Substitute.For<IFolderLauncher>(), TimeProvider.System, Substitute.For<IThemeApplier>());

        viewModel.SetThemeCommand.Execute("Dark");

        toastService.Received(1).ShowToast("Theme updated (from resw)", "Dark");
    }

    [Fact]
    public void Choosing_a_theme_applies_it_to_the_window_at_once()
    {
        var applier = Substitute.For<IThemeApplier>();
        var viewModel = CreateViewModel(Substitute.For<ISettingsStore>(), out _, applier);

        viewModel.SetThemeCommand.Execute("Dark");

        applier.Received(1).Apply("Dark");
    }

    [Fact]
    public void A_locked_settings_file_still_applies_the_theme_for_this_session()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));
        var applier = Substitute.For<IThemeApplier>();
        var viewModel = CreateViewModel(settingsStore, out _, applier);

        viewModel.SetThemeCommand.Execute("Light");

        applier.Received(1).Apply("Light");
    }

    [Theory]
    [InlineData("Light", true, false, false)]
    [InlineData("Dark", false, true, false)]
    [InlineData("System", false, false, true)]
    [InlineData("garbage", false, false, true)]
    public void Exactly_one_choice_is_marked_and_it_follows_the_saved_theme(string saved, bool light, bool dark, bool system)
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns(saved);
        var viewModel = CreateViewModel(settingsStore, out _);

        (viewModel.IsLightTheme, viewModel.IsDarkTheme, viewModel.IsSystemTheme).ShouldBe((light, dark, system));
    }

    [Fact]
    public void The_marked_choice_and_its_change_notification_follow_a_new_choice()
    {
        var viewModel = CreateViewModel(Substitute.For<ISettingsStore>(), out _);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.SetThemeCommand.Execute("Dark");

        viewModel.IsDarkTheme.ShouldBeTrue();
        viewModel.IsSystemTheme.ShouldBeFalse();
        changed.ShouldContain(nameof(SettingsViewModel.IsDarkTheme));
        changed.ShouldContain(nameof(SettingsViewModel.IsSystemTheme));
    }
}
