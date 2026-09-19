using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void Theme_defaults_to_system_when_nothing_was_ever_saved()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("Theme").Returns((string?)null);
        var viewModel = new SettingsViewModel(settingsStore, Substitute.For<IToastService>());

        viewModel.Theme.ShouldBe("System");
    }

    [Fact]
    public void Setting_the_theme_writes_it_back_to_the_settings_store()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        var viewModel = new SettingsViewModel(settingsStore, Substitute.For<IToastService>());

        viewModel.SetThemeCommand.Execute("Dark");

        settingsStore.Received(1).SetString("Theme", "Dark");
        viewModel.Theme.ShouldBe("Dark");
    }
}
