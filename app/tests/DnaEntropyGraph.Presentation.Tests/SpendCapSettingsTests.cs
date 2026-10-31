using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #109: the Settings spending-cap group.</summary>
public sealed class SpendCapSettingsTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    private readonly MemorySettings _settings = new();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly ISpendCapGate _gate = Substitute.For<ISpendCapGate>();

    public SpendCapSettingsTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _gate.MonthToDateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new SpendSummary(3.5, 0)));
    }

    private SpendCapSettingsViewModel Create() => new(_settings, _strings, _gate);

    [Fact]
    public void A_valid_cap_is_saved_under_the_key_the_gate_reads()
    {
        var vm = Create();

        vm.CapText = " 25.50 ";

        SpendCapSettings.ReadCapUsd(_settings).ShouldBe(25.5);
        vm.Status.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("0")]
    public void An_unusable_cap_is_not_saved_and_says_so(string text)
    {
        var vm = Create();

        vm.CapText = text;

        _settings.Values.ShouldNotContainKey(SpendCapSettings.CapUsdKey);
        vm.Status.ShouldBe("SpendCapInvalid");
    }

    [Fact]
    public void Clearing_the_box_turns_the_cap_off()
    {
        _settings.Values[SpendCapSettings.CapUsdKey] = "10";
        var vm = Create();
        vm.CapText.ShouldBe("10");

        vm.CapText = string.Empty;

        SpendCapSettings.ReadCapUsd(_settings).ShouldBeNull();
    }

    [Fact]
    public void The_hard_stop_switch_saves_and_the_gate_key_reads_it()
    {
        var vm = Create();

        vm.IsHardStop = true;

        SpendCapSettings.IsHardStop(_settings).ShouldBeTrue();
        vm.IsHardStop = false;
        SpendCapSettings.IsHardStop(_settings).ShouldBeFalse();
    }

    [Fact]
    public void Opening_the_page_does_not_save_what_it_just_read()
    {
        _settings.Values[SpendCapSettings.CapUsdKey] = "10";
        _settings.Values[SpendCapSettings.HardStopKey] = "true";

        var vm = Create();

        vm.CapText.ShouldBe("10");
        vm.IsHardStop.ShouldBeTrue();
        _settings.Values.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Month_to_date_comes_from_the_gate()
    {
        var vm = Create();

        for (var i = 0; i < 200 && vm.MonthToDateText.Length == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        vm.MonthToDateText.ShouldBe("SpendMonthToDate");
        await _gate.Received().MonthToDateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runs_in_progress_are_named_in_the_month_to_date_line()
    {
        _gate.MonthToDateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new SpendSummary(3.5, 1.25)));
        var vm = Create();

        for (var i = 0; i < 200 && vm.MonthToDateText.Length == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        vm.MonthToDateText.ShouldBe("SpendMonthToDateWithLive");
    }
}
