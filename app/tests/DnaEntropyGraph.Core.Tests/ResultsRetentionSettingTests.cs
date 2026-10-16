using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

public class ResultsRetentionSettingTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value) => Values[key] = value;
    }

    private sealed class UnavailableSettings : ISettingsStore
    {
        public string? GetString(string key) => throw new SettingsUnavailableException("settings.json is unreadable");

        public void SetString(string key, string value) => throw new SettingsUnavailableException("settings.json is unreadable");
    }

    public static TheoryData<string?, int> Cases => new()
    {
        { null, ResultsBucket.DefaultRetentionDays },
        { "30", 30 },
        { " 7 ", 7 },
        { "1", 1 },
        { "3650", 3650 },
        { "3651", 3650 },
        { "99999999999999999999999999", 3650 },
        { "0", ResultsBucket.DefaultRetentionDays },
        { "-5", ResultsBucket.DefaultRetentionDays },
        { "soon", ResultsBucket.DefaultRetentionDays },
        { "", ResultsBucket.DefaultRetentionDays },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_retention_reads_the_setting_and_an_unusable_value_is_the_default_never_a_throw(string? stored, int days)
    {
        var settings = new MemorySettings();
        if (stored is not null)
        {
            settings.Values[ResultsRetentionSetting.Key] = stored;
        }

        ResultsRetentionSetting.Days(settings).ShouldBe(days);
    }

    [Fact]
    public void A_change_is_seen_on_the_next_read()
    {
        var settings = new MemorySettings();
        ResultsRetentionSetting.Days(settings).ShouldBe(90);
        settings.SetString(ResultsRetentionSetting.Key, "30");
        ResultsRetentionSetting.Days(settings).ShouldBe(30);
    }

    [Fact]
    public void An_unavailable_settings_file_is_the_default()
        => ResultsRetentionSetting.Days(new UnavailableSettings()).ShouldBe(ResultsBucket.DefaultRetentionDays);
}
