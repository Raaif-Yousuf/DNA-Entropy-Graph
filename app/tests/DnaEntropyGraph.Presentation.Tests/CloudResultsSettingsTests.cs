using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #114: the Settings "Cloud results" group writes the retention the storage gateway reads at the start of each run.</summary>
public sealed class CloudResultsSettingsTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public int Writes { get; private set; }

        public bool Locked { get; set; }

        public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value)
        {
            if (Locked)
            {
                throw new SettingsUnavailableException("locked");
            }

            Writes++;
            Values[key] = value;
        }
    }

    private readonly MemorySettings _settings = new();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public CloudResultsSettingsTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() == "CloudRetentionChoiceFormat" ? "{0} days" : call.Arg<string>());
    }

    private CloudResultsSettingsViewModel Create(IMessenger? messenger = null) => new(_settings, _strings, messenger);

    [Fact]
    public void Opening_the_page_shows_the_default_and_saves_nothing()
    {
        var vm = Create();

        vm.SelectedChoice.Days.ShouldBe(90);
        vm.SelectedChoice.Label.ShouldBe("90 days");
        _settings.Writes.ShouldBe(0);
    }

    [Fact]
    public void Choosing_a_length_writes_the_key_the_gateway_reads_and_says_when_it_applies()
    {
        var vm = Create();

        vm.SelectedChoice = vm.Choices.Single(c => c.Days == 30);

        _settings.Values[ResultsRetentionSetting.Key].ShouldBe("30");
        ResultsRetentionSetting.Days(_settings).ShouldBe(30);
        vm.RetentionStatus.ShouldBe("CloudRetentionSaved");
    }

    [Fact]
    public void A_saved_value_that_is_not_one_of_the_choices_is_shown_as_its_own_entry_and_not_rewritten()
    {
        _settings.Values[ResultsRetentionSetting.Key] = "45";

        var vm = Create();

        vm.SelectedChoice.Days.ShouldBe(45);
        vm.Choices.Select(c => c.Days).ShouldBe([7, 30, 45, 90, 180, 365]);
        _settings.Writes.ShouldBe(0);
    }

    [Fact]
    public void A_locked_settings_file_is_reported_with_the_action_and_the_choice_is_not_claimed_saved()
    {
        _settings.Locked = true;
        var vm = Create();

        vm.SelectedChoice = vm.Choices.Single(c => c.Days == 7);

        vm.RetentionStatus.ShouldBe("AdvancedNotSaved");
        _settings.Values.ShouldNotContainKey(ResultsRetentionSetting.Key);
    }

    [Fact]
    public void Reset_all_settings_shows_the_default_again_without_saving()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = Create(messenger);
        vm.SelectedChoice = vm.Choices.Single(c => c.Days == 30);
        _settings.Values.Remove(ResultsRetentionSetting.Key);
        var writes = _settings.Writes;

        messenger.Send(new SettingsResetMessage());

        vm.SelectedChoice.Days.ShouldBe(90);
        _settings.Writes.ShouldBe(writes);
        vm.RetentionStatus.ShouldBeEmpty();
    }
}
