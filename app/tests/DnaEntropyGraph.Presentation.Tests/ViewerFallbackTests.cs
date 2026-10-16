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
        vm.OpenRun(_dir);
        await vm.FallbackReady;
        vm.HasFallbackChart.ShouldBeFalse();
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
}
