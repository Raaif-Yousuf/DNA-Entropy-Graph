using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #107: AppNotifier, driven through a fake of the Windows notification manager.</summary>
public sealed class AppNotifierTests
{
    private readonly IAppWindow _window = Substitute.For<IAppWindow>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly FakeSink _sink = new();

    public AppNotifierTests() => _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());

    private AppNotifier Notifier() => new(_window, _strings, _sink);

    [Fact]
    public void A_run_notification_carries_the_run_in_its_body_and_its_button()
    {
        Notifier().Notify("T", "B", "job-9");

        var shown = _sink.Shown.ShouldHaveSingleItem();
        shown.Title.ShouldBe("T");
        shown.Body.ShouldBe("B");
        shown.RunId.ShouldBe("job-9");
        shown.OpenLabel.ShouldBe("RunToast_OpenResults");
    }

    [Fact]
    public void A_notification_with_no_run_has_no_link_and_no_button()
    {
        Notifier().Notify("T", "B", null);

        var shown = _sink.Shown.ShouldHaveSingleItem();
        shown.RunId.ShouldBeNull();
        shown.OpenLabel.ShouldBeNull();
    }

    [Fact]
    public void Nothing_is_shown_while_the_window_is_in_front()
    {
        _window.IsInForeground.Returns(true);

        Notifier().Notify("T", "B", "job-9");

        _sink.Shown.ShouldBeEmpty();
    }

    [Fact]
    public void Nothing_is_shown_when_notifications_are_not_supported()
    {
        _sink.Supported = false;

        Notifier().Notify("T", "B", "job-9");

        _sink.Shown.ShouldBeEmpty();
    }

    [Fact]
    public void A_job_id_that_is_not_a_plain_id_is_refused_before_anything_is_shown()
    {
        Should.Throw<ArgumentException>(() => Notifier().Notify("T", "B", "../x"));

        _sink.Shown.ShouldBeEmpty();
    }

    private sealed class FakeSink : INotificationSink
    {
        public bool Supported { get; set; } = true;

        public List<NotificationContent> Shown { get; } = [];

        public bool IsSupported() => Supported;

        public void Show(NotificationContent content) => Shown.Add(content);
    }
}
