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

    private async Task<(ResultsViewModel Vm, IOverviewImageExporter Exporter)> Results(EntropyOverview? overview)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<RunRecord> { Row("job", _dir) });
        _source.Load(_dir).Returns(overview);
        var exporter = Substitute.For<IOverviewImageExporter>();
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
