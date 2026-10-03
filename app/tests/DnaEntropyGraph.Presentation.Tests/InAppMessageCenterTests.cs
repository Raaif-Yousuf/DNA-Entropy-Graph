using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #585: the in-app message surface behind <see cref="IToastService"/>. Time and thread are both faked, so nothing here sleeps.</summary>
public class InAppMessageCenterTests
{
    private readonly ManualDispatcher _dispatcher = new();
    private readonly ManualTimeProvider _time = new();

    private InAppMessageCenter Create() => new(_dispatcher, _time);

    [Fact]
    public void A_shown_message_becomes_visible_with_its_text_and_severity()
    {
        var center = Create();

        center.ShowToast("Deleted", "3 files removed", ToastSeverity.Success);
        _dispatcher.RunAll();

        center.IsOpen.ShouldBeTrue();
        center.Title.ShouldBe("Deleted");
        center.Message.ShouldBe("3 files removed");
        center.Severity.ShouldBe(ToastSeverity.Success);
    }

    [Fact]
    public void The_two_argument_overload_is_an_info_message()
    {
        var center = Create();

        center.ShowToast("Theme updated", "Dark");
        _dispatcher.RunAll();

        center.Severity.ShouldBe(ToastSeverity.Info);
        center.IsOpen.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ToastSeverity.Info)]
    [InlineData(ToastSeverity.Success)]
    public void Info_and_success_close_themselves_after_the_delay(ToastSeverity severity)
    {
        var center = Create();
        center.ShowToast("t", "b", severity);
        _dispatcher.RunAll();

        _time.Timers.Count.ShouldBe(1, "vacuity guard: an auto-dismiss timer must have been armed");
        _time.Timers[0].Due.ShouldBe(InAppMessageCenter.AutoDismissAfter);
        _time.Timers[0].Fire();
        _dispatcher.RunAll();

        center.IsOpen.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ToastSeverity.Warning)]
    [InlineData(ToastSeverity.Error)]
    public void Warnings_and_errors_stay_until_closed(ToastSeverity severity)
    {
        var center = Create();
        center.ShowToast("t", "b", severity);
        _dispatcher.RunAll();

        _time.Timers.ShouldBeEmpty();
        center.IsOpen.ShouldBeTrue();

        center.IsOpen = false; // what the InfoBar close button does through the two-way binding
        center.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void A_stale_timer_cannot_close_a_newer_message()
    {
        var center = Create();
        center.ShowToast("first", "b", ToastSeverity.Info);
        _dispatcher.RunAll();
        var stale = _time.Timers[0];

        center.ShowToast("second", "b", ToastSeverity.Error);
        _dispatcher.RunAll();
        stale.Fire();
        _dispatcher.RunAll();

        center.IsOpen.ShouldBeTrue();
        center.Title.ShouldBe("second");
    }

    [Fact]
    public async Task A_message_from_a_background_thread_changes_state_only_when_the_dispatcher_runs()
    {
        var center = Create();

        await Task.Run(() => center.ShowToast("t", "b", ToastSeverity.Error), TestContext.Current.CancellationToken);

        center.IsOpen.ShouldBeFalse("the bound state must not change off the UI thread");
        _dispatcher.RunAll();
        center.IsOpen.ShouldBeTrue();
    }
}
