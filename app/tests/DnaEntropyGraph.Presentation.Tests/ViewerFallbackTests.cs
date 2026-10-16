using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #117: with the WebView2 Runtime missing the viewer area still shows the entropy track (native chart) beside the one named action.</summary>
public sealed class ViewerFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-vfb-" + Guid.NewGuid().ToString("N"));
    private readonly IWebViewRuntimeProbe _probe = Substitute.For<IWebViewRuntimeProbe>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IEntropyOverviewSource _overview = Substitute.For<IEntropyOverviewSource>();

    public ViewerFallbackTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), ">a\nACGT\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.bedgraph"), "a 0 1 2\n");
        _strings.GetString(Arg.Any<string>()).Returns(ci => "[" + ci.Arg<string>() + "]");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static EntropyContig Contig(string name) => new(name, 4, 1, [1f], null);

    private ViewerViewModel Make(params EntropyContig[] contigs)
    {
        _overview.Load(Arg.Any<string>()).Returns(contigs.Length == 0 ? null : new EntropyOverview(contigs));
        return new ViewerViewModel(_probe, _strings, _overview);
    }

    [Fact]
    public async Task Missing_runtime_exposes_the_fallback_chart_and_the_named_action()
    {
        _probe.GetVersion().Returns((string?)null);
        var vm = Make(Contig("a"), Contig("b"));
        vm.OpenRun(_dir);
        await vm.FallbackReady;
        vm.Status.ShouldBe(ViewerStatus.RuntimeMissing);
        vm.HasFallbackChart.ShouldBeTrue();
        vm.FallbackContig!.Name.ShouldBe("a");
        vm.FallbackTitle.ShouldBe("a");
        vm.HasLinkAction.ShouldBeTrue();
        vm.ActionUrl.ShouldBe(ViewerUrls.RuntimeDownloadUrl);
        vm.IsViewerVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task Missing_runtime_with_no_track_has_no_chart_but_still_the_action()
    {
        _probe.GetVersion().Returns((string?)null);
        var vm = Make();
        vm.OpenRun(_dir);
        await vm.FallbackReady;
        vm.HasFallbackChart.ShouldBeFalse();
        vm.HasLinkAction.ShouldBeTrue();
    }

    [Fact]
    public async Task Unreadable_track_is_swallowed()
    {
        _probe.GetVersion().Returns((string?)null);
        _overview.Load(Arg.Any<string>()).Returns(_ => throw new FormatException("bad"));
        var vm = new ViewerViewModel(_probe, _strings, _overview);
        vm.OpenRun(_dir);
        await vm.FallbackReady;
        vm.HasFallbackChart.ShouldBeFalse();
    }

    [Fact]
    public async Task Working_runtime_has_no_fallback_chart()
    {
        _probe.GetVersion().Returns("130.0.1");
        var vm = Make(Contig("a"));
        _overview.ClearReceivedCalls(); // the stub setup in Make itself calls Load
        vm.OpenRun(_dir);
        await vm.FallbackReady;
        vm.HasFallbackChart.ShouldBeFalse();
        // Only the record list's own read: a fallback read would make it two.
        await vm.ContigsReady;
        _overview.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IEntropyOverviewSource.Load)).ShouldBe(1);
    }

    [Fact]
    public async Task Opening_a_good_run_after_a_missing_runtime_clears_the_chart()
    {
        _probe.GetVersion().Returns((string?)null);
        var vm = Make(Contig("a"));
        vm.OpenRun(_dir);
        await vm.FallbackReady;
        vm.HasFallbackChart.ShouldBeTrue();
        _probe.GetVersion().Returns("130.0.1");
        vm.OpenRun(_dir);
        vm.HasFallbackChart.ShouldBeFalse();
    }

    [Fact]
    public async Task Control_init_failure_also_shows_the_chart()
    {
        _probe.GetVersion().Returns("130.0.1");
        var vm = Make(Contig("a"));
        vm.OpenRun(_dir);
        vm.OnViewerInitFailed();
        await vm.FallbackReady;
        vm.Status.ShouldBe(ViewerStatus.ViewerError);
        vm.HasFallbackChart.ShouldBeTrue();
    }

    [Fact]
    public async Task Init_failure_after_a_specific_error_keeps_that_error_and_loads_nothing()
    {
        _probe.GetVersion().Returns("130.0.1");
        var vm = Make(Contig("a"));
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        vm.OpenRun(empty);
        vm.Status.ShouldBe(ViewerStatus.NoSequenceFile);
        vm.OnViewerInitFailed();
        await vm.FallbackReady;
        vm.Status.ShouldBe(ViewerStatus.NoSequenceFile);
        vm.ErrorTitle.ShouldBe("[ViewerNoSequenceFile_Title]");
        vm.HasFallbackChart.ShouldBeFalse();
        _overview.DidNotReceive().Load(Arg.Any<string>());
    }

    [Theory]
    [InlineData("{\"evt\":\"error\"}")]
    public async Task Page_error_also_shows_the_chart_and_keeps_the_contigs(string message)
    {
        _probe.GetVersion().Returns("130.0.1");
        var vm = Make(Contig("a"), Contig("b"));
        vm.OpenRun(_dir);
        await vm.ContigsReady;
        vm.OnViewerMessage(message);
        await vm.FallbackReady;
        vm.Status.ShouldBe(ViewerStatus.ViewerError);
        vm.HasFallbackChart.ShouldBeTrue();
        vm.Contigs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Process_failure_also_shows_the_chart()
    {
        _probe.GetVersion().Returns("130.0.1");
        var vm = Make(Contig("a"));
        vm.OpenRun(_dir);
        vm.OnViewerProcessFailed();
        await vm.FallbackReady;
        vm.HasFallbackChart.ShouldBeTrue();
    }

    [Fact]
    public async Task A_slow_load_for_an_earlier_run_does_not_overwrite_the_newer_run()
    {
        _probe.GetVersion().Returns((string?)null);
        var gate = new TaskCompletionSource();
        _overview.Load("run-a").Returns(_ =>
        {
            gate.Task.Wait();
            return new EntropyOverview([Contig("from-a")]);
        });
        _overview.Load("run-b").Returns(new EntropyOverview([Contig("from-b")]));
        var vm = new ViewerViewModel(_probe, _strings, _overview);
        vm.OpenRun("run-a");
        var slowA = vm.FallbackReady;
        vm.OpenRun("run-b");
        await vm.FallbackReady;
        vm.FallbackContig!.Name.ShouldBe("from-b");
        gate.SetResult();
        await slowA;
        vm.FallbackContig!.Name.ShouldBe("from-b");
    }
}
