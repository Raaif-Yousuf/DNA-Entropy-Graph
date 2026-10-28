using System.Text.Json;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Core.Viewers;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #497: a FASTA over the in-page limit loads through a generated .fai kept in an app-owned folder.</summary>
public sealed class ViewerIndexedFastaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-vix-" + Guid.NewGuid().ToString("N"));
    private readonly string _run;
    private readonly string _faiDir;
    private readonly FastaIndexStore _store;
    private readonly IWebViewRuntimeProbe _probe = Substitute.For<IWebViewRuntimeProbe>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IEntropyOverviewSource _overview = Substitute.For<IEntropyOverviewSource>();
    private readonly List<string> _sent = [];

    public ViewerIndexedFastaTests()
    {
        _run = Path.Combine(_root, "run");
        _faiDir = Path.Combine(_root, "fai");
        Directory.CreateDirectory(_run);
        File.WriteAllText(Path.Combine(_run, "s.fasta"), ">a\nACGTACGT\nACGT\n");
        File.WriteAllText(Path.Combine(_run, "s.entropy.bedgraph"), "a 0 1 2\n");
        _store = new FastaIndexStore(_faiDir);
        _probe.GetVersion().Returns("130.0.1");
        _strings.GetString(Arg.Any<string>()).Returns(ci => "[" + ci.Arg<string>() + "]");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static JsonElement Reference(IgvLoadPlan plan) =>
        JsonDocument.Parse(plan.LoadMessageJson!).RootElement.GetProperty("config").GetProperty("reference");

    [Fact]
    public void Over_the_limit_with_a_store_loads_indexed_with_the_index_served_from_the_index_host()
    {
        var plan = IgvLoadPlanner.Plan(_run, maxFastaBytes: 3, indexStore: _store);

        plan.Failure.ShouldBe(IgvPlanFailure.None);
        var reference = Reference(plan);
        reference.GetProperty("indexed").GetBoolean().ShouldBeTrue();
        reference.GetProperty("fastaURL").GetString().ShouldBe("https://run.deg/s.fasta");
        var indexUrl = reference.GetProperty("indexURL").GetString()!;
        indexUrl.ShouldStartWith(ViewerUrls.IndexBaseUrl);
        indexUrl.ShouldEndWith(".fai");
        File.Exists(Path.Combine(_faiDir, indexUrl[ViewerUrls.IndexBaseUrl.Length..])).ShouldBeTrue();
    }

    [Fact]
    public void The_index_is_never_written_into_the_run_folder()
    {
        IgvLoadPlanner.Plan(_run, maxFastaBytes: 3, indexStore: _store);

        Directory.GetFiles(_run).Select(Path.GetFileName).Order().ToArray().ShouldBe(["s.entropy.bedgraph", "s.fasta"]);
    }

    [Fact]
    public void A_fasta_under_the_limit_still_loads_unindexed_and_builds_no_index()
    {
        var plan = IgvLoadPlanner.Plan(_run, indexStore: _store);

        Reference(plan).GetProperty("indexed").GetBoolean().ShouldBeFalse();
        Directory.Exists(_faiDir).ShouldBeFalse();
    }

    [Fact]
    public void Over_the_limit_with_no_store_is_still_SequenceTooLarge_and_names_its_file()
    {
        var plan = IgvLoadPlanner.Plan(_run, maxFastaBytes: 3);

        plan.Failure.ShouldBe(IgvPlanFailure.SequenceTooLarge);
        plan.FastaPath.ShouldBe(Path.Combine(_run, "s.fasta"));
    }

    [Fact]
    public void Over_the_indexed_limit_is_SequenceTooLarge_and_builds_no_index()
    {
        IgvLoadPlanner.Plan(_run, maxFastaBytes: 3, indexStore: _store, maxIndexedBytes: 5).Failure.ShouldBe(IgvPlanFailure.SequenceTooLarge);
        Directory.Exists(_faiDir).ShouldBeFalse();
    }

    [Fact]
    public void A_fasta_the_indexer_refuses_is_IndexFailed()
    {
        File.WriteAllText(Path.Combine(_run, "s.fasta"), ">a\nACGTACGT\nAC\nACGTACGT\n");

        IgvLoadPlanner.Plan(_run, maxFastaBytes: 3, indexStore: _store).Failure.ShouldBe(IgvPlanFailure.IndexFailed);
    }

    [Fact]
    public void An_unwritable_index_folder_is_IndexFailed()
    {
        var store = Substitute.For<IFastaIndexStore>();
        store.EnsureIndex(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => throw new IOException("disk full"));

        IgvLoadPlanner.Plan(_run, maxFastaBytes: 3, indexStore: store).Failure.ShouldBe(IgvPlanFailure.IndexFailed);
    }

    [Fact]
    public void The_indexed_limit_is_a_real_ceiling_above_the_in_page_one()
    {
        (ViewerLimits.MaxIndexedFastaBytes > ViewerLimits.MaxFastaBytes).ShouldBeTrue();
        ViewerUrls.IndexHost.ShouldBe("fai.deg");
    }

    [Fact]
    public void The_viewer_page_may_fetch_from_the_index_host()
    {
        var html = File.ReadAllText(FindViewerAsset("index.html"));
        html.ShouldContain("https://" + ViewerUrls.IndexHost);
    }

    private static string FindViewerAsset(string name)
    {
        // #874: this source file's own tree first (what was compiled), the output directory walk only as a fallback.
        var here = Path.GetDirectoryName(ThisFile())!;
        var fromSource = Path.GetFullPath(Path.Combine(here, "..", "..", "src", "DnaEntropyGraph.App", "Assets", "viewer", name));
        if (File.Exists(fromSource))
        {
            return fromSource;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DnaEntropyGraph.App", "Assets", "viewer", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return fromSource;
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private ViewerViewModel Make(IFastaIndexStore? store, long maxFasta = 3, long maxIndexed = ViewerLimits.MaxIndexedFastaBytes)
    {
        var vm = new ViewerViewModel(_probe, _strings, _overview, maxFasta, store, maxIndexed);
        vm.MessageToViewer += _sent.Add;
        return vm;
    }

    [Fact]
    public async Task The_view_model_indexes_off_the_open_call_then_loads_once_the_page_is_ready()
    {
        var vm = Make(_store);
        vm.OnViewerMessage("{\"evt\":\"ready\"}");

        vm.OpenRun(_run);
        vm.Status.ShouldBe(ViewerStatus.Loading);
        vm.IsLoading.ShouldBeTrue();
        await vm.OpenReady;

        vm.MapRunFolder.ShouldBe(_run);
        var load = _sent.ShouldHaveSingleItem();
        JsonDocument.Parse(load).RootElement.GetProperty("config").GetProperty("reference").GetProperty("indexed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task The_view_model_shows_the_index_failure_with_its_own_copy()
    {
        File.WriteAllText(Path.Combine(_run, "s.fasta"), ">a\nACGTACGT\nAC\nACGTACGT\n");
        var vm = Make(_store);

        vm.OpenRun(_run);
        await vm.OpenReady;

        vm.Status.ShouldBe(ViewerStatus.IndexFailed);
        vm.ErrorTitle.ShouldBe("[ViewerIndexFailed_Title]");
        vm.ErrorBody.ShouldBe("[ViewerIndexFailed_Body]");
        vm.MapRunFolder.ShouldBeNull();
    }

    [Fact]
    public async Task A_slow_index_for_a_run_left_meanwhile_does_not_land_on_the_next_run()
    {
        var gate = new TaskCompletionSource();
        var slow = Substitute.For<IFastaIndexStore>();
        slow.EnsureIndex(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            gate.Task.Wait();
            return Path.Combine(_faiDir, "late.fai");
        });
        var vm = Make(slow);
        vm.OpenRun(_run);
        var first = vm.OpenReady;

        vm.OpenRun(null);
        gate.SetResult();
        await first;

        vm.Status.ShouldBe(ViewerStatus.NoRun);
        vm.MapRunFolder.ShouldBeNull();
    }

    [Fact]
    public async Task Leaving_the_page_cancels_the_index_build_and_applies_nothing()
    {
        var started = new TaskCompletionSource();
        var sawCancel = false;
        var slow = Substitute.For<IFastaIndexStore>();
        slow.EnsureIndex(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            started.SetResult();
            ci.Arg<CancellationToken>().WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            sawCancel = ci.Arg<CancellationToken>().IsCancellationRequested;
            ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Path.Combine(_faiDir, "late.fai");
        });
        var vm = Make(slow);
        vm.OpenRun(_run);
        await started.Task;

        vm.CancelOpen();
        await vm.OpenReady;

        sawCancel.ShouldBeTrue();
        vm.MapRunFolder.ShouldBeNull();
        vm.Status.ShouldBe(ViewerStatus.Loading);
    }

    [Fact]
    public void The_too_large_message_names_the_limit_in_force_taken_from_ViewerLimits()
    {
        _strings.GetString("ViewerSequenceTooLarge_Body").Returns("over {0}");

        var noStore = Make(null, maxFasta: 3);
        noStore.OpenRun(_run);

        ViewerLimits.Describe(ViewerLimits.MaxIndexedFastaBytes).ShouldBe("1 GB");
        ViewerLimits.Describe(ViewerLimits.MaxFastaBytes).ShouldBe("50 MB");
        noStore.ErrorBody.ShouldBe("over 3 bytes");
    }

    [Fact]
    public async Task The_too_large_message_with_an_index_store_names_the_indexed_limit()
    {
        _strings.GetString("ViewerSequenceTooLarge_Body").Returns("over {0}");
        var vm = Make(_store, maxFasta: 3, maxIndexed: 5);

        vm.OpenRun(_run);
        await vm.OpenReady;

        vm.Status.ShouldBe(ViewerStatus.SequenceTooLarge);
        vm.ErrorBody.ShouldBe("over 5 bytes");
    }

    [Fact]
    public void Without_a_store_the_view_model_keeps_refusing_a_large_fasta()
    {
        var vm = Make(null);

        vm.OpenRun(_run);

        vm.Status.ShouldBe(ViewerStatus.SequenceTooLarge);
    }
}
