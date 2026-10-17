using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #699: dragging a range on the Results entropy strip opens the viewer at that range.</summary>
public sealed class OverviewDragSelectTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-drag-" + Guid.NewGuid().ToString("N"));
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IEntropyOverviewSource _source = Substitute.For<IEntropyOverviewSource>();
    private readonly IWebViewRuntimeProbe _probe = Substitute.For<IWebViewRuntimeProbe>();
    private readonly List<string> _sent = [];

    public OverviewDragSelectTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), ">c1\nACGT\n>c2\nACGT\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.bedgraph"), "c1 0 1 2\n");
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _probe.GetVersion().Returns("130.0.1");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static EntropyContig C(string name, int length) => new(name, length, 10, new float[length / 10], null);

    // ---- the pure range -> target decision

    [Theory]
    [InlineData(100.0, 500.0, "c1:101-500")]
    [InlineData(500.0, 100.0, "c1:101-500")]
    [InlineData(100.4, 500.2, "c1:101-501")]
    [InlineData(-50.0, 300.0, "c1:1-300")]
    [InlineData(900.0, 5000.0, "c1:901-1000")]
    public void A_drag_becomes_a_one_based_inclusive_range_clamped_to_the_contig(double a, double b, string locus)
    {
        var target = OverviewSelection.ToTarget("run", C("c1", 1000), new OverviewRange(a, b));

        target.ShouldNotBeNull();
        target.RunFolder.ShouldBe("run");
        target.Locus.ShouldBe(locus);
    }

    [Theory]
    [InlineData(100.0, 100.0)]
    [InlineData(100.0, 100.4)]
    [InlineData(double.NaN, 50.0)]
    [InlineData(2000.0, 3000.0)]
    [InlineData(-30.0, -10.0)]
    public void A_click_or_a_drag_with_nothing_inside_the_contig_selects_nothing(double a, double b)
    {
        OverviewSelection.ToTarget("run", C("c1", 1000), new OverviewRange(a, b)).ShouldBeNull();
    }

    // ---- Results

    private async Task<(ResultsViewModel Vm, INavigator Nav)> Results(EntropyContig? contig)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<RunRecord>
        {
            new("job", JobPhase.Completed, Now.AddHours(-1), Name: "job", OutputDir: _dir, OptionsJson: "{}", Bucket: "b", JobPrefix: "jobs/job/"),
        });
        _source.Load(_dir).Returns(contig is null ? null : new EntropyOverview([contig]));
        var nav = Substitute.For<INavigator>();
        var vm = new ResultsViewModel(repository, new RunOutputReader(), Substitute.For<IShellLauncher>(), nav, _strings, Substitute.For<IExternalViewerOpener>(), _source);
        await vm.LoadAsync("job", TestContext.Current.CancellationToken);
        return (vm, nav);
    }

    [Fact]
    public async Task Selecting_a_range_navigates_to_the_viewer_with_the_run_folder_and_locus()
    {
        var (vm, nav) = await Results(C("c1", 1000));

        vm.SelectOverviewRangeCommand.Execute(new OverviewRange(200, 400));

        nav.Received(1).NavigateTo(ViewerViewModel.PageKey, Arg.Is<object?>(p => p is ViewerRunTarget && ((ViewerRunTarget)p).RunFolder == _dir && ((ViewerRunTarget)p).Locus == "c1:201-400"));
    }

    [Fact]
    public async Task A_click_on_the_strip_does_not_navigate()
    {
        var (vm, nav) = await Results(C("c1", 1000));

        vm.SelectOverviewRangeCommand.Execute(new OverviewRange(200, 200));

        nav.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    [Fact]
    public async Task Without_an_overview_selecting_does_nothing()
    {
        var (vm, nav) = await Results(null);

        vm.SelectOverviewRangeCommand.Execute(new OverviewRange(200, 400));

        nav.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    // ---- the viewer page

    private ViewerViewModel Viewer(params EntropyContig[] contigs)
    {
        var overview = Substitute.For<IEntropyOverviewSource>();
        overview.Load(Arg.Any<string>()).Returns(new EntropyOverview(contigs));
        var vm = new ViewerViewModel(_probe, _strings, overview);
        vm.MessageToViewer += _sent.Add;
        return vm;
    }

    private List<JsonElement> Sent(string cmd) =>
        _sent.Select(s => JsonDocument.Parse(s).RootElement.Clone()).Where(r => r.GetProperty("cmd").GetString() == cmd).ToList();

    [Fact]
    public async Task The_viewer_goes_to_the_requested_locus_once_the_run_is_drawn_and_selects_its_contig()
    {
        var vm = Viewer(C("c1", 1000), C("c2", 500));

        vm.OpenFromNavigation(new ViewerRunTarget(_dir, "c2:11-60"));
        await vm.ContigsReady;
        vm.OnViewerMessage("{\"evt\":\"ready\"}");
        Sent("goto").ShouldBeEmpty();
        vm.OnViewerMessage("{\"evt\":\"loaded\"}");

        Sent("goto").ShouldHaveSingleItem().GetProperty("locus").GetString().ShouldBe("c2:11-60");
        vm.SelectedContig!.Name.ShouldBe("c2");
    }

    [Fact]
    public async Task The_locus_is_applied_once_so_a_retry_keeps_where_the_user_is()
    {
        var vm = Viewer(C("c1", 1000));
        vm.OpenFromNavigation(new ViewerRunTarget(_dir, "c1:11-60"));
        await vm.ContigsReady;
        vm.OnViewerMessage("{\"evt\":\"ready\"}");
        vm.OnViewerMessage("{\"evt\":\"loaded\"}");
        Sent("goto").Count.ShouldBe(1);

        vm.OnViewerMessage("{\"evt\":\"error\"}");
        vm.RetryCommand.Execute(null);
        vm.OnViewerMessage("{\"evt\":\"loaded\"}");

        Sent("goto").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_plain_folder_string_still_opens_the_run_with_no_goto()
    {
        var vm = Viewer(C("c1", 1000));

        vm.OpenFromNavigation(_dir);
        await vm.ContigsReady;
        vm.OnViewerMessage("{\"evt\":\"ready\"}");
        vm.OnViewerMessage("{\"evt\":\"loaded\"}");

        vm.MapRunFolder.ShouldBe(_dir);
        Sent("goto").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_later_plain_open_forgets_the_earlier_locus()
    {
        var vm = Viewer(C("c1", 1000));
        vm.OpenFromNavigation(new ViewerRunTarget(_dir, "c1:11-60"));
        vm.OpenFromNavigation(_dir);
        await vm.ContigsReady;
        vm.OnViewerMessage("{\"evt\":\"ready\"}");
        vm.OnViewerMessage("{\"evt\":\"loaded\"}");

        Sent("goto").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_navigation_parameter_shows_the_no_run_error()
    {
        var vm = Viewer();

        vm.OpenFromNavigation(42);

        vm.Status.ShouldBe(ViewerStatus.NoRun);
    }
}
