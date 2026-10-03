using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

public class CloudHousekeepingSettingsTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value) => Values[key] = value;
    }

    public static TheoryData<string?, double?> Cases => new()
    {
        { null, CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
        { "6", 6 },
        { " 24 ", 24 },
        { "0", null },
        { "-5", null },
        { "soon", CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
        { "", CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_idle_limit_reads_the_setting_and_an_unreadable_value_is_the_default_never_off(string? stored, double? hours)
    {
        var settings = new MemorySettings();
        if (stored is not null)
        {
            settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = stored;
        }

        var limit = CloudHousekeepingSettings.IdleStoppedVmLimit(settings);

        limit.ShouldBe(hours is null ? null : TimeSpan.FromHours(hours.Value));
    }

    [Fact]
    public void The_case_table_covers_off_default_and_explicit_values()
    {
        Cases.Count.ShouldBeGreaterThanOrEqualTo(5);
        CloudHousekeepingSettings.DefaultIdleStoppedVmHours.ShouldBeGreaterThan(0);
    }
}
