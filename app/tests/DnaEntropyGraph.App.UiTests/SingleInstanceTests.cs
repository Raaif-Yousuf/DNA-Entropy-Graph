using DnaEntropyGraph.App.Startup;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issues #107/#108: the single-instance key and the bounded, never-crashing redirect to the running copy.</summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void The_key_is_stable_and_ignores_case_and_a_trailing_slash()
    {
        var a = SingleInstance.KeyFor(@"C:\Data\Deg");
        SingleInstance.KeyFor(@"c:\data\deg\").ShouldBe(a);
        a.ShouldStartWith("DnaEntropyGraph-");
        a.Length.ShouldBe("DnaEntropyGraph-".Length + 16);
    }

    [Fact]
    public void Different_data_folders_get_different_keys_so_a_sandbox_never_takes_over_the_real_app()
        => SingleInstance.KeyFor(@"C:\Data\Deg").ShouldNotBe(SingleInstance.KeyFor(@"C:\Data\Sandbox"));

    private static bool Wait(ManualResetEvent done, TimeSpan timeout) => done.WaitOne(timeout);

    [Fact]
    public void A_redirect_that_completes_is_reported_as_handed_over()
        => SingleInstance.TryRedirect(() => Task.CompletedTask, TimeSpan.FromSeconds(5), Wait).ShouldBeTrue();

    [Fact]
    public void A_redirect_that_throws_does_not_crash_the_caller_and_falls_back()
        => SingleInstance.TryRedirect(() => throw new InvalidOperationException("gone"), TimeSpan.FromSeconds(5), Wait).ShouldBeFalse();

    [Fact]
    public void A_redirect_that_throws_asynchronously_falls_back_too()
        => SingleInstance.TryRedirect(async () => { await Task.Yield(); throw new InvalidOperationException("gone"); }, TimeSpan.FromSeconds(5), Wait).ShouldBeFalse();

    [Fact]
    public void A_redirect_that_never_answers_gives_up_after_the_timeout()
    {
        var never = new TaskCompletionSource();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        SingleInstance.TryRedirect(() => never.Task, TimeSpan.FromMilliseconds(200), Wait).ShouldBeFalse();

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        never.SetResult();
    }

    [Fact]
    public void The_real_message_pumping_wait_also_returns_when_signalled_and_when_it_times_out()
    {
        SingleInstance.TryRedirect(() => Task.CompletedTask, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        var never = new TaskCompletionSource();
        SingleInstance.TryRedirect(() => never.Task, TimeSpan.FromMilliseconds(100)).ShouldBeFalse();
        never.SetResult();
    }
}
