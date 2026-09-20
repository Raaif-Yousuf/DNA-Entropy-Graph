using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public class SettingsViewModelTests
{
    private static SettingsViewModel CreateViewModel(ISettingsStore settingsStore, out IToastService toastService)
    {
        toastService = Substitute.For<IToastService>();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());
        return new SettingsViewModel(settingsStore, toastService, strings);
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
        var viewModel = new SettingsViewModel(settingsStore, toastService, strings);

        viewModel.SetThemeCommand.Execute("Dark");

        toastService.Received(1).ShowToast("Theme updated (from resw)", "Dark");
    }
}
