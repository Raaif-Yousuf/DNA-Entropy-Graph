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
    // The Appearance radio buttons' positions: Light, Dark, Use system.
    private const int Light = 0;
    private const int Dark = 1;
    private const int System = 2;

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

        viewModel.ThemeIndex.ShouldBe(System);
    }

    [Fact]
    public void A_locked_settings_file_neither_crashes_opening_settings_nor_changing_the_theme()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns(_ => throw new SettingsUnavailableException("locked"));
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));

        var viewModel = CreateViewModel(settingsStore, out _);
        viewModel.ThemeIndex.ShouldBe(System);

        Should.NotThrow(() => viewModel.ThemeIndex = Dark);
        viewModel.ThemeIndex.ShouldBe(Dark);
    }

    [Fact]
    public void A_failed_theme_save_shows_the_failure_toast_not_theme_updated()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));
        var viewModel = CreateViewModel(settingsStore, out var toastService);

        viewModel.ThemeIndex = Dark;

        viewModel.ThemeIndex.ShouldBe(Dark);
        toastService.Received(1).ShowToast("ThemeNotSaved_Title", "ThemeNotSaved_Body", ToastSeverity.Warning);
        toastService.DidNotReceive().ShowToast("ThemeUpdated_Title", Arg.Any<string>(), Arg.Any<ToastSeverity>());
    }

    [Fact]
    public void Setting_the_theme_writes_it_back_to_the_settings_store()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        var viewModel = CreateViewModel(settingsStore, out _);

        viewModel.ThemeIndex = Dark;

        settingsStore.Received(1).SetString("Theme", "Dark");
        viewModel.ThemeIndex.ShouldBe(Dark);
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

        viewModel.ThemeIndex = Dark;

        toastService.Received(1).ShowToast("Theme updated (from resw)", Arg.Any<string>(), ToastSeverity.Info);
    }

    [Fact]
    public void Choosing_a_theme_applies_it_to_the_window_at_once()
    {
        var applier = Substitute.For<IThemeApplier>();
        var viewModel = CreateViewModel(Substitute.For<ISettingsStore>(), out _, applier);

        viewModel.ThemeIndex = Dark;

        applier.Received(1).Apply("Dark");
    }

    [Fact]
    public void A_locked_settings_file_still_applies_the_theme_for_this_session()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));
        var applier = Substitute.For<IThemeApplier>();
        var viewModel = CreateViewModel(settingsStore, out _, applier);

        viewModel.ThemeIndex = Light;

        applier.Received(1).Apply("Light");
    }

    [Theory]
    [InlineData("Light", Light)]
    [InlineData("Dark", Dark)]
    [InlineData("System", System)]
    [InlineData("garbage", System)]
    public void The_marked_choice_follows_the_saved_theme(string saved, int expected)
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns(saved);
        var viewModel = CreateViewModel(settingsStore, out _);

        viewModel.ThemeIndex.ShouldBe(expected);
    }

    [Fact]
    public void Opening_the_page_neither_applies_nor_saves_the_theme_it_just_read()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns("Dark");
        var applier = Substitute.For<IThemeApplier>();

        CreateViewModel(settingsStore, out _, applier);

        applier.DidNotReceive().Apply(Arg.Any<string>());
        settingsStore.DidNotReceive().SetString(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(Light, "Light")]
    [InlineData(Dark, "Dark")]
    [InlineData(System, "System")]
    public void A_selection_change_applies_and_saves_the_matching_theme(int index, string expected)
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        // Start on a different choice so setting the index is always a real change.
        settingsStore.GetString("Theme").Returns(index == Dark ? "Light" : "Dark");
        var applier = Substitute.For<IThemeApplier>();
        var viewModel = CreateViewModel(settingsStore, out _, applier);

        viewModel.ThemeIndex = index;

        applier.Received(1).Apply(expected);
        settingsStore.Received(1).SetString("Theme", expected);
    }

    [Fact]
    public void No_selection_is_not_a_theme()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        var applier = Substitute.For<IThemeApplier>();
        var viewModel = CreateViewModel(settingsStore, out _, applier);

        viewModel.ThemeIndex = -1;

        applier.DidNotReceive().Apply(Arg.Any<string>());
        settingsStore.DidNotReceive().SetString(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(Light, "Light label from resw")]
    [InlineData(Dark, "Dark label from resw")]
    [InlineData(System, "Use system label from resw")]
    public void The_theme_updated_toast_names_the_choice_in_the_words_of_the_radio_button_not_the_saved_code(int index, string label)
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns(index == Dark ? "Light" : "Dark");
        var toastService = Substitute.For<IToastService>();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("ThemeUpdated_Title").Returns("Theme updated");
        strings.GetString("ThemeChoice_Light").Returns("Light label from resw");
        strings.GetString("ThemeChoice_Dark").Returns("Dark label from resw");
        strings.GetString("ThemeChoice_System").Returns("Use system label from resw");
        var viewModel = new SettingsViewModel(settingsStore, toastService, strings, Substitute.For<IDiagnosticsExporter>(), Substitute.For<IFilePicker>(), Substitute.For<IFolderLauncher>(), TimeProvider.System, Substitute.For<IThemeApplier>());

        viewModel.ThemeIndex = index;

        toastService.Received(1).ShowToast("Theme updated", label, ToastSeverity.Info);
    }
}