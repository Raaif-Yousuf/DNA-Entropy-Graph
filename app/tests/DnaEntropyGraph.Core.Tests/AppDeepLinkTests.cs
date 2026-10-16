using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #107: the argument a Windows notification (or a second launch) carries to open one run's Results page.</summary>
public class AppDeepLinkTests
{
    [Fact]
    public void A_run_link_round_trips_through_its_argument_text()
    {
        var text = AppDeepLink.ForRun("20261015-abc_1");

        AppDeepLink.TryGetRunId(text, out var jobId).ShouldBeTrue();
        jobId.ShouldBe("20261015-abc_1");
    }

    [Theory]
    [InlineData("action=open;run=job-9")]
    [InlineData("run=job-9&action=open")]
    [InlineData("  run=job-9  ")]
    public void The_run_is_found_among_other_arguments(string text)
    {
        AppDeepLink.TryGetRunId(text, out var jobId).ShouldBeTrue();
        jobId.ShouldBe("job-9");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("run=")]
    [InlineData("other=1")]
    [InlineData("run=..\\..\\secret")]
    [InlineData("run=a/b")]
    [InlineData("run=has space")]
    public void Anything_that_is_not_a_plain_job_id_is_ignored(string? text)
    {
        // The text arrives from outside the app (a notification, another process), so it is checked before it reaches a page.
        AppDeepLink.TryGetRunId(text, out var jobId).ShouldBeFalse();
        jobId.ShouldBeEmpty();
    }

    [Fact]
    public void A_job_id_longer_than_any_the_app_makes_is_ignored()
    {
        AppDeepLink.TryGetRunId("run=" + new string('a', 200), out _).ShouldBeFalse();
    }

    [Fact]
    public void Making_a_link_for_an_unusable_id_is_refused()
    {
        Should.Throw<ArgumentException>(() => AppDeepLink.ForRun("../x"));
    }
}
