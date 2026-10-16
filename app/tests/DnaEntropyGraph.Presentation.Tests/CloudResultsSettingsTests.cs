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

        vm.SelectedChoice = vm.Choices.Single(c => c.Days == 180);

        _settings.Values[ResultsRetentionSetting.Key].ShouldBe("180");
        ResultsRetentionSetting.Days(_settings).ShouldBe(180);
        vm.RetentionStatus.ShouldBe("CloudRetentionSaved");
    }

    [Fact]
    public void Every_choice_shows_the_one_status_that_is_true_whatever_the_previous_choice_or_the_bucket_holds()
    {
        // Review of #114: the bucket's age is not known here, so no choice may claim more than the one message says. 90 -> 7 -> 30 and a
        // bucket at 365 with 180 chosen both get it; the text itself (resw) carries the "never shortens a longer rule" part.
        var vm = Create();
        var seen = new List<string>();

        foreach (var days in new[] { 7, 30, 365, 180 })
        {
            vm.SelectedChoice = vm.Choices.Single(c => c.Days == days);
            seen.Add(vm.RetentionStatus);
            _settings.Values[ResultsRetentionSetting.Key].ShouldBe(days.ToString());
        }

        seen.ShouldAllBe(s => s == "CloudRetentionSaved");
    }

    [Fact]
    public void A_failed_save_does_not_change_what_a_later_successful_save_says()
    {
        _settings.Locked = true;
        var vm = Create();
        vm.SelectedChoice = vm.Choices.Single(c => c.Days == 7);
        vm.RetentionStatus.ShouldBe("AdvancedNotSaved");

        _settings.Locked = false;
        vm.SelectedChoice = vm.Choices.Single(c => c.Days == 30);

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
