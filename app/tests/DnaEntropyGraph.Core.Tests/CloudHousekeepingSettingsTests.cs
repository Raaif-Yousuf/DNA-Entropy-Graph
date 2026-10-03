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

    public static TheoryData<string?, double> Cases => new()
    {
        { null, CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
        { "6", 6 },
        { " 24 ", 24 },
        { "0", CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
        { "-5", CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
        { "1", 1 },
        { "720", 720 },
        { "721", 720 },
        { "100000", 720 },
        { "3000000000", 720 },
        { "99999999999999999999999999", 720 },
        { "-99999999999999999999999999", CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
        { "soon", CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
        { "", CloudHousekeepingSettings.DefaultIdleStoppedVmHours },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_idle_limit_reads_the_setting_and_an_unreadable_value_is_the_default_never_off(string? stored, double hours)
    {
        var settings = new MemorySettings();
        if (stored is not null)
        {
            settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = stored;
        }

        var limit = CloudHousekeepingSettings.IdleStoppedVmLimit(settings);

        limit.ShouldBe(TimeSpan.FromHours(hours));
    }

    [Fact]
    public void The_default_is_72_and_the_range_is_1_to_720_with_no_off_switch()
    {
        CloudHousekeepingSettings.DefaultIdleStoppedVmHours.ShouldBe(72);
        CloudHousekeepingSettings.MinIdleStoppedVmHours.ShouldBe(1);
        CloudHousekeepingSettings.MaxIdleStoppedVmHours.ShouldBe(720);
    }

    [Fact]
    public void The_case_table_covers_off_default_and_explicit_values()
    {
        Cases.Count.ShouldBeGreaterThanOrEqualTo(12);
        CloudHousekeepingSettings.DefaultIdleStoppedVmHours.ShouldBeGreaterThan(0);
    }
}
