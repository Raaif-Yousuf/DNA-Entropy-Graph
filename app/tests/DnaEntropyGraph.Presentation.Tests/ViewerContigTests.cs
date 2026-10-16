using System.Text.Json;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #116: the viewer's contig selector, the goto command it sends, and the seam markers.</summary>
public sealed class ViewerContigTests : IDisposable
{
    private const string ReadyMsg = "{\"evt\":\"ready\"}";
    private const string LoadedMsg = "{\"evt\":\"loaded\"}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-vct-" + Guid.NewGuid().ToString("N"));
    private readonly IWebViewRuntimeProbe _probe = Substitute.For<IWebViewRuntimeProbe>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IEntropyOverviewSource _overview = Substitute.For<IEntropyOverviewSource>();
    private readonly List<string> _sent = [];

    public ViewerContigTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), ">a\nACGT\n>b\nACGT\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.bedgraph"), "a 0 1 2\n");
        _probe.GetVersion().Returns("130.0.1");
        _strings.GetString(Arg.Any<string>()).Returns(ci => "[" + ci.Arg<string>() + "]");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static EntropyContig Contig(string name, int length, int? seam = null)
        => new(name, length, 1, [1f], seam);

    private ViewerViewModel Make(params EntropyContig[] contigs)
    {
        _overview.Load(Arg.Any<string>()).Returns(contigs.Length == 0 ? null : new EntropyOverview(contigs));
        var vm = new ViewerViewModel(_probe, _strings, _overview);
        vm.MessageToViewer += _sent.Add;
        return vm;
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private List<JsonElement> Sent(string cmd) => _sent.Select(Root).Where(r => r.GetProperty("cmd").GetString() == cmd).ToList();

    private async Task<ViewerViewModel> ReadyViewer(params EntropyContig[] contigs)
    {
        var vm = Make(contigs);
        vm.OpenRun(_dir);
        await vm.ContigsReady;
        vm.OnViewerMessage(ReadyMsg);
        vm.OnViewerMessage(LoadedMsg);
        return vm;
    }

    [Fact]
    public async Task Two_contigs_fill_the_selector_with_the_first_selected_and_no_goto_sent()
    {
        var vm = await ReadyViewer(Contig("a", 100), Contig("b", 200));

        vm.HasContigs.ShouldBeTrue();
        vm.Contigs.Select(c => c.Name).ToArray().ShouldBe(["a", "b"]);
        vm.SelectedContig!.Name.ShouldBe("a");
        Sent("goto").ShouldBeEmpty();
    }

    [Fact]
    public async Task Choosing_a_contig_sends_goto_with_its_name()
    {
        var vm = await ReadyViewer(Contig("a", 100), Contig("b", 200));

        vm.SelectedContig = vm.Contigs[1];

        var gotoCmd = Sent("goto").ShouldHaveSingleItem();
        gotoCmd.GetProperty("locus").GetString().ShouldBe("b");
    }

    [Fact]
    public async Task Choosing_a_contig_before_the_viewer_is_ready_sends_nothing()
    {
        var vm = Make(Contig("a", 100), Contig("b", 200));
        vm.OpenRun(_dir);
        await vm.ContigsReady;

        vm.SelectedContig = vm.Contigs[1];

        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_contig_or_no_overview_hides_the_selector()
    {
        (await ReadyViewer(Contig("a", 100))).HasContigs.ShouldBeFalse();
        (await ReadyViewer()).HasContigs.ShouldBeFalse();
    }

    [Fact]
    public async Task Loaded_sends_a_seam_for_each_contig_that_has_one_with_a_plain_label()
    {
        await ReadyViewer(Contig("a", 100, 40), Contig("b", 200), Contig("c", 300, 25));

        var seams = Sent("seams").ShouldHaveSingleItem();
        seams.GetProperty("label").GetString().ShouldBe("[ViewerSeamLabel]");
        var items = seams.GetProperty("seams").EnumerateArray()
            .Select(s => (s.GetProperty("chr").GetString(), s.GetProperty("pos").GetInt32()))
            .ToArray();
        items.ShouldBe([("a", 40), ("c", 25)]);
    }

    [Fact]
    public async Task No_seams_means_no_seams_command()
    {
        await ReadyViewer(Contig("a", 100), Contig("b", 200));

        Sent("seams").ShouldBeEmpty();
    }

    [Fact]
    public async Task Seams_are_sent_when_the_contig_list_arrives_after_loaded()
    {
        var gate = new TaskCompletionSource();
        _overview.Load(Arg.Any<string>()).Returns(_ =>
        {
            gate.Task.Wait();
            return new EntropyOverview([Contig("a", 100, 40), Contig("b", 200)]);
        });
        var vm = new ViewerViewModel(_probe, _strings, _overview);
        vm.MessageToViewer += _sent.Add;
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        vm.OnViewerMessage(LoadedMsg);
        Sent("seams").ShouldBeEmpty();

        gate.SetResult();
        await vm.ContigsReady;

        Sent("seams").ShouldHaveSingleItem();
    }
}
