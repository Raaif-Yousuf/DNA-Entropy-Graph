using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #553: the app's one log sink until Serilog lands. It keeps an area and an error class, never a message.</summary>
public sealed class InMemoryAppLogTests
{
    [Fact]
    public void An_error_is_kept_with_its_area_its_class_and_the_time_it_was_logged()
    {
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var log = new InMemoryAppLog(new FixedTime(now));

        log.Error("NewRun.StartRun", "FileNotFoundException");

        log.Entries.ShouldBe([new AppLogEntry(now, "NewRun.StartRun", "FileNotFoundException")]);
    }

    [Fact]
    public void Only_the_most_recent_entries_are_kept()
    {
        var log = new InMemoryAppLog();

        for (var i = 0; i < InMemoryAppLog.Capacity + 25; i++)
        {
            log.Error("Area", "Class" + i);
        }

        log.Entries.Count.ShouldBe(InMemoryAppLog.Capacity);
        log.Entries[0].ErrorClass.ShouldBe("Class25");
        log.Entries[^1].ErrorClass.ShouldBe("Class" + (InMemoryAppLog.Capacity + 24));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
