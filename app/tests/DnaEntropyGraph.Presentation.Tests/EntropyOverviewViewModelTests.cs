using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #118: the overview strip on Results, its PNG export, and the sparklines on the Runs cards.</summary>
public sealed class EntropyOverviewViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-overview-vm", Guid.NewGuid().ToString("N"));
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IShellLauncher _launcher = Substitute.For<IShellLauncher>();
    private readonly IEntropyOverviewSource _source = Substitute.For<IEntropyOverviewSource>();

    public EntropyOverviewViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _launcher.ShowInFolder(Arg.Any<string>()).Returns(true);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static EntropyOverview Overview(string name = "c1", int? seam = null, params float[] means)
        => new([new EntropyContig(name, means.Length * 16, 16, means.Length == 0 ? [1f, 1.5f, 2f] : means, seam)]);

    private static RunRecord Row(string id, string? outputDir, JobPhase phase = JobPhase.Completed)
        => new(id, phase, Now.AddHours(-1), Name: id, OutputDir: outputDir, OptionsJson: "{}", Bucket: "b", JobPrefix: $"jobs/{id}/");

    private async Task<(ResultsViewModel Vm, IOverviewImageExporter Exporter)> Results(EntropyOverview? overview, IOverviewImageExporter? exporterOverride = null)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<RunRecord> { Row("job", _dir) });
        _source.Load(_dir).Returns(overview);
        var exporter = exporterOverride ?? Substitute.For<IOverviewImageExporter>();
        var vm = new ResultsViewModel(repository, new RunOutputReader(), _launcher, Substitute.For<INavigator>(), _strings, Substitute.For<IExternalViewerOpener>(), _source, exporter);
        await vm.LoadAsync("job", TestContext.Current.CancellationToken);
        return (vm, exporter);
    }

    [Fact]
    public async Task A_run_with_an_entropy_track_shows_its_first_contig_in_the_overview()
    {
        var (vm, _) = await Results(Overview("chr1", seam: 7));

        vm.HasOverview.ShouldBeTrue();
        vm.Overview!.Name.ShouldBe("chr1");
        vm.Overview.Seam.ShouldBe(7);
        vm.OverviewTitle.ShouldBe("chr1");
    }

    [Fact]
    public async Task A_run_without_a_track_has_no_overview_and_no_export()
    {
        var (vm, _) = await Results(null);

        vm.HasOverview.ShouldBeFalse();
        vm.Overview.ShouldBeNull();
        vm.ExportOverviewPngCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_writes_a_png_in_the_run_folder_and_shows_it()
    {
        var (vm, exporter) = await Results(Overview("chr1"));
        var expected = Path.Combine(_dir, "chr1.entropy.png");
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), expected).Returns(true);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        exporter.Received(1).Export(Arg.Is<EntropyContig>(c => c.Name == "chr1"), Arg.Any<OverviewImageLabels>(), expected);
        _launcher.Received(1).ShowInFolder(expected);
        vm.ActionNoticeText.ShouldBeEmpty();
    }

    private async Task<ResultsViewModel> ResultsFor(RunRecord run, string exportParent, IOverviewImageExporter exporter, bool appOwnedRoot = false)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<RunRecord> { run });
        _source.Load(run.OutputDir!).Returns(Overview("chr1"));
        var vm = new ResultsViewModel(repository, new RunOutputReader(), _launcher, Substitute.For<INavigator>(), _strings, Substitute.For<IExternalViewerOpener>(), _source, exporter, exportRoot: new ResultsExportRoot(() => exportParent, appOwnedRoot));
        await vm.LoadAsync(run.JobId, TestContext.Current.CancellationToken);
        return vm;
    }

    private static IOverviewImageExporter WritingExporter()
    {
        var exporter = Substitute.For<IOverviewImageExporter>();
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(call =>
        {
            File.WriteAllBytes(call.Arg<string>(), [1, 2, 3]);
            return true;
        });
        return exporter;
    }

    [Fact]
    public async Task An_imported_runs_png_goes_to_a_new_app_folder_and_its_own_folder_is_left_exactly_as_it_was()
    {
        // Hard Rule 14: the folder of a run imported from the earlier version is the user's own; nothing is written into it.
        var own = Path.Combine(_dir, "proto");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "proto.entropy.bedgraph"), "x");
        var before = Directory.EnumerateFileSystemEntries(own, "*", SearchOption.AllDirectories).Order().ToList();
        var exports = Path.Combine(_dir, "exports");
        Directory.CreateDirectory(exports); // issue #922: the export root is the user's own folder and must already be there
        var vm = await ResultsFor(Row("legacy-1", own) with { Imported = true, Target = "local" }, exports, WritingExporter());

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        Directory.EnumerateFileSystemEntries(own, "*", SearchOption.AllDirectories).Order().ToList().ShouldBe(before);
        var written = Directory.EnumerateFiles(exports, "*.png", SearchOption.AllDirectories).ShouldHaveSingleItem();
        Path.GetDirectoryName(Path.GetDirectoryName(written)).ShouldBe(exports);
        vm.ActionNoticeText.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_export_never_overwrites_the_first()
    {
        var own = Path.Combine(_dir, "mine");
        Directory.CreateDirectory(own);
        var vm = await ResultsFor(Row("job2", own), Path.Combine(_dir, "unused"), WritingExporter());
        var first = Path.Combine(own, "chr1.entropy.png");
        File.WriteAllBytes(first, [9, 9, 9]);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        File.ReadAllBytes(first).ShouldBe([9, 9, 9]);
        File.Exists(Path.Combine(own, "chr1.entropy (2).png")).ShouldBeTrue();
    }

    [Fact]
    public async Task An_imported_run_with_no_export_folder_configured_exports_nothing_and_says_so()
    {
        var own = Path.Combine(_dir, "proto2");
        Directory.CreateDirectory(own);
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<RunRecord> { Row("legacy-2", own) with { Imported = true, Target = "local" } });
        _source.Load(own).Returns(Overview("chr1"));
        var exporter = WritingExporter();
        var vm = new ResultsViewModel(repository, new RunOutputReader(), _launcher, Substitute.For<INavigator>(), _strings, Substitute.For<IExternalViewerOpener>(), _source, exporter);
        await vm.LoadAsync("legacy-2", TestContext.Current.CancellationToken);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        exporter.DidNotReceiveWithAnyArgs().Export(default!, default!, default!);
        vm.ActionNoticeText.ShouldBe(ResultsCopy.ExportPngNoFolder, "the failure text about the results folder would name the wrong action");
    }

    [Fact]
    public async Task A_run_downloaded_from_the_cloud_keeps_its_picture_in_its_own_results_folder_like_any_run()
    {
        var own = Path.Combine(_dir, "downloaded");
        Directory.CreateDirectory(own);
        var exports = Path.Combine(_dir, "exports");
        var vm = await ResultsFor(Row("cloud-1", own) with { Imported = true, Target = "cloud" }, exports, WritingExporter());

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(own, "chr1.entropy.png")).ShouldBeTrue();
        Directory.Exists(exports).ShouldBeFalse();
    }

    [Fact]
    public async Task A_templated_runs_picture_goes_into_its_templated_results_folder_and_makes_no_exports_folder()
    {
        // Issue #955: only an earlier-version import gets an exports folder (named from its own name, no template exists for it); a templated run's picture
        // goes into the run folder the template named, so no second folder is made and none needs the run marker.
        var own = Path.Combine(_dir, "seq_evo2_7b");
        Directory.CreateDirectory(own);
        var exports = Path.Combine(_dir, "exports955");
        Directory.CreateDirectory(exports);
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", InputPath = @"C:\data\seq.fasta", NameTemplate = "{file}_{model}" };
        var vm = await ResultsFor(Row("cloud-955", own) with { Target = "cloud", OptionsJson = RunOptionsJson.Serialize(options) }, exports, WritingExporter());

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(own, "chr1.entropy.png")).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(exports).ShouldBeEmpty();
        Directory.EnumerateDirectories(_dir).Select(Path.GetFileName).ShouldNotContain(n => n!.Contains("exports", StringComparison.Ordinal) && n.Contains("seq", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_pictures_of_an_earlier_version_run_share_one_exports_folder_and_the_second_is_numbered()
    {
        var own = Path.Combine(_dir, "proto3");
        Directory.CreateDirectory(own);
        var exports = Path.Combine(_dir, "exports3");
        Directory.CreateDirectory(exports); // issue #922: the export root is the user's own folder and must already be there
        var vm = await ResultsFor(Row("legacy-3", own) with { Imported = true, Target = "local" }, exports, WritingExporter());

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);
        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        var folder = Directory.EnumerateDirectories(exports).ShouldHaveSingleItem();
        Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order().ShouldBe(["chr1.entropy (2).png", "chr1.entropy.png"]);
    }

    [Fact]
    public async Task A_failed_export_of_an_earlier_version_run_leaves_no_empty_folder_behind()
    {
        var own = Path.Combine(_dir, "proto4");
        Directory.CreateDirectory(own);
        var exports = Path.Combine(_dir, "exports4");
        Directory.CreateDirectory(exports); // issue #922: the export root is the user's own folder and must already be there
        var failing = Substitute.For<IOverviewImageExporter>();
        failing.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(false);
        var vm = await ResultsFor(Row("legacy-4", own) with { Imported = true, Target = "local" }, exports, failing);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBe(ResultsCopy.ExportPngFailedInExports, "the exports folder is the place to check, not the results folder");
        (Directory.Exists(exports) ? Directory.EnumerateFileSystemEntries(exports) : []).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_export_root_is_not_recreated_and_the_export_fails_with_the_exports_notice()
    {
        // Issue #922: a Downloads the user deleted is not brought back, with or without a picture in it.
        var own = Path.Combine(_dir, "proto5");
        Directory.CreateDirectory(own);
        var root = Path.Combine(_dir, "deep", "exports5");
        var exporter = WritingExporter();
        var vm = await ResultsFor(Row("legacy-5", own) with { Imported = true, Target = "local" }, root, exporter);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(_dir, "deep")).ShouldBeFalse();
        vm.ActionNoticeText.ShouldBe(ResultsCopy.ExportPngFailedInExports);
        exporter.DidNotReceiveWithAnyArgs().Export(default!, default!, default!);
    }

    [Fact]
    public async Task An_export_root_the_app_owns_is_made_on_first_use_so_a_fresh_portable_install_can_save_a_picture()
    {
        // Portable mode: <stick>\outputs is the app's own folder and nothing creates it at startup.
        var own = Path.Combine(_dir, "proto6");
        Directory.CreateDirectory(own);
        var root = Path.Combine(_dir, "stick", "outputs");
        var vm = await ResultsFor(Row("legacy-6", own) with { Imported = true, Target = "local" }, root, WritingExporter(), appOwnedRoot: true);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).ShouldHaveSingleItem();
        vm.ActionNoticeText.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_app_owned_export_root_made_for_a_failed_picture_is_taken_away_again()
    {
        var own = Path.Combine(_dir, "proto7");
        Directory.CreateDirectory(own);
        var root = Path.Combine(_dir, "stick7", "outputs");
        var failing = Substitute.For<IOverviewImageExporter>();
        failing.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(false);
        var vm = await ResultsFor(Row("legacy-7", own) with { Imported = true, Target = "local" }, root, failing, appOwnedRoot: true);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(_dir, "stick7")).ShouldBeFalse();
        vm.ActionNoticeText.ShouldBe(ResultsCopy.ExportPngFailedInExports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_results_folder_the_user_deleted_is_not_recreated_and_the_export_fails_with_the_results_folder_notice(bool cloudDownload)
    {
        var own = Path.Combine(_dir, cloudDownload ? "gone-cloud" : "gone");
        Directory.CreateDirectory(own);
        var run = cloudDownload ? Row("gone-1", own) with { Imported = true, Target = "cloud" } : Row("gone-1", own);
        var exporter = WritingExporter();
        var vm = await ResultsFor(run, Path.Combine(_dir, "unused-root"), exporter);
        Directory.Delete(own);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBe(ResultsCopy.ExportPngFailed);
        Directory.Exists(own).ShouldBeFalse("a folder the user deleted or moved must not come back with a lone picture in it");
        exporter.DidNotReceiveWithAnyArgs().Export(default!, default!, default!);
    }

    [Fact]
    public async Task The_swallowed_exporter_exception_leaves_a_trace_with_its_class_name_and_no_path()
    {
        var listener = new RecordingListener();
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            var failing = Substitute.For<IOverviewImageExporter>();
            failing.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns<bool>(_ => throw new InvalidOperationException("C:\\secret\\place\\file.png"));
            var (vm, _) = await Results(Overview("chr1"), failing);

            await vm.ExportOverviewPngCommand.ExecuteAsync(null);
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }

        listener.Text.ShouldContain("InvalidOperationException");
        listener.Text.ShouldNotContain("secret");
        listener.Text.ShouldNotContain("file.png");
    }

    private sealed class RecordingListener : System.Diagnostics.TraceListener
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(string? message)
        {
            lock (_text)
            {
                _text.Append(message);
            }
        }

        public override void WriteLine(string? message) => Write(message + "\n");
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Any_exception_from_the_exporter_becomes_the_failure_notice_not_a_crash(Type exception)
    {
        var failing = Substitute.For<IOverviewImageExporter>();
        failing.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns<bool>(_ => throw (Exception)Activator.CreateInstance(exception)!);
        var (vm, _) = await Results(Overview("chr1"), failing);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBe(ResultsCopy.ExportPngFailed);
    }

    [Fact]
    public async Task A_folder_named_like_the_picture_counts_as_taken()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "chr1.entropy.png"));
        var (vm, exporter) = await Results(Overview("chr1"));
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(true);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        Path.GetFileName((string)exporter.ReceivedCalls().Single().GetArguments()[2]!).ShouldBe("chr1.entropy (2).png");
    }

    [Fact]
    public async Task Save_picture_never_overwrites_a_file_that_is_already_there()
    {
        var (vm, exporter) = await Results(Overview("chr1"));
        File.WriteAllText(Path.Combine(_dir, "chr1.entropy.png"), "another run's picture");
        File.WriteAllText(Path.Combine(_dir, "chr1.entropy (2).png"), "and another");
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(true);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        var path = (string)exporter.ReceivedCalls().Single().GetArguments()[2]!;
        Path.GetFileName(path).ShouldBe("chr1.entropy (3).png");
        File.ReadAllText(Path.Combine(_dir, "chr1.entropy.png")).ShouldBe("another run's picture");
        _launcher.Received(1).ShowInFolder(path);
    }

    [Fact]
    public async Task A_second_save_of_the_same_run_gets_its_own_name_too()
    {
        var (vm, exporter) = await Results(Overview("chr1"));
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(ci =>
        {
            File.WriteAllText(ci.ArgAt<string>(2), "png");
            return true;
        });

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);
        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        exporter.ReceivedCalls().Select(c => Path.GetFileName((string)c.GetArguments()[2]!)).ShouldBe(["chr1.entropy.png", "chr1.entropy (2).png"]);
    }

    [Fact]
    public async Task The_picture_is_titled_with_the_run_and_contig_and_labelled_in_bits()
    {
        var (vm, exporter) = await Results(Overview("chr1"));
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(true);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        var labels = (OverviewImageLabels)exporter.ReceivedCalls().Single().GetArguments()[1]!;
        labels.Title.ShouldBe(ResultsCopy.ExportPngTitle, "the title is the localised format string, formatted with the run name and contig");
        labels.XAxis.ShouldBe(ResultsCopy.ExportPngXAxis);
        labels.YAxis.ShouldBe(ResultsCopy.ExportPngYAxis);
    }

    [Fact]
    public async Task A_contig_name_that_is_not_a_file_name_cannot_choose_where_the_png_goes()
    {
        var (vm, exporter) = await Results(Overview("..\\..\\evil:name"));
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(true);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        var path = (string)exporter.ReceivedCalls().Single().GetArguments()[2]!;
        Path.GetDirectoryName(path).ShouldBe(_dir);
        Path.GetFileName(path).ShouldNotContain("..");
    }

    [Fact]
    public async Task A_failed_export_says_so()
    {
        var (vm, exporter) = await Results(Overview("chr1"));
        exporter.Export(Arg.Any<EntropyContig>(), Arg.Any<OverviewImageLabels>(), Arg.Any<string>()).Returns(false);

        await vm.ExportOverviewPngCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBe(ResultsCopy.ExportPngFailed);
        _launcher.DidNotReceive().ShowInFolder(Arg.Any<string>());
    }

    private async Task<HistoryViewModel> History(params RunRecord[] rows)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => rows.ToList());
        var local = Substitute.For<ILocalRunFiles>();
        local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        var cloud = Substitute.For<IRunCloudResults>();
        var vm = new HistoryViewModel(
            repository, Substitute.For<IRunHistoryRemover>(), Substitute.For<INavigator>(), cloud, local, Substitute.For<IJobEngine>(),
            Substitute.For<IDialogService>(), Substitute.For<IToastService>(), _strings, _launcher, Substitute.For<IGcpAccount>(), TimeProvider.System, _source);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.SparklineWork;
        return vm;
    }

    [Fact]
    public async Task A_finished_run_with_a_track_gets_a_sparkline_of_at_most_the_card_width()
    {
        _source.Load("done").Returns(Overview("c1", null, [.. Enumerable.Range(0, 500).Select(i => (float)(i % 3))]));

        var vm = await History(Row("a", "done"));

        var item = vm.Groups.Single().Items.Single();
        item.HasSparkline.ShouldBeTrue();
        item.Sparkline!.Length.ShouldBe(RunListItem.SparklinePoints);
    }

    [Fact]
    public async Task A_run_whose_folder_has_no_track_has_no_sparkline()
    {
        _source.Load("none").Returns((EntropyOverview?)null);

        var vm = await History(Row("a", "none"));

        vm.Groups.Single().Items.Single().HasSparkline.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_or_running_run_is_not_read_at_all()
    {
        await History(Row("a", "x", JobPhase.Failed), Row("b", "y", JobPhase.Running));

        _source.DidNotReceiveWithAnyArgs().Load(default!);
    }

    [Fact]
    public async Task A_run_with_no_output_folder_recorded_is_not_read()
    {
        await History(Row("a", null));

        _source.DidNotReceiveWithAnyArgs().Load(default!);
    }

    [Fact]
    public async Task A_source_that_throws_costs_only_that_runs_sparkline()
    {
        _source.Load("bad").Returns(_ => throw new InvalidOperationException("boom"));
        _source.Load("good").Returns(Overview());

        var vm = await History(Row("a", "bad"), Row("b", "good"));

        var items = vm.Groups.SelectMany(g => g.Items).ToDictionary(i => i.JobId);
        items["a"].HasSparkline.ShouldBeFalse();
        items["b"].HasSparkline.ShouldBeTrue();
    }
}
