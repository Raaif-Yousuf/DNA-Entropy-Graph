using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #102: the Results page for a finished run. The decisive tests use the REAL <see cref="RunOutputReader"/> on real
/// folders, so the numbers on screen provably come from the run's own summary file and from nowhere else.
/// </summary>
public sealed class ResultsViewModelTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-results-vm-tests", Guid.NewGuid().ToString("N"));
    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly IShellLauncher _launcher = Substitute.For<IShellLauncher>();
    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private List<RunRecord> _rows = [];

    public ResultsViewModelTests()
    {
        Directory.CreateDirectory(_base);

        // Keys come back as themselves, except the format strings, which come back as the shipped .resw text.
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _strings.GetString(ResultsCopy.Bits).Returns("{0:F3} bits");
        _strings.GetString(ResultsCopy.Length).Returns("{0:N0} nt");
        _strings.GetString(ResultsCopy.SizeBytes).Returns("{0:N0} bytes");
        _strings.GetString(ResultsCopy.SizeKilobytes).Returns("{0:N1} KB");
        _strings.GetString(ResultsCopy.SizeMegabytes).Returns("{0:N1} MB");
        _strings.GetString(ResultsCopy.Headline).Returns("mean {0:F3}, lowest {1:F3}, highest {2:F3}, {3:N0} nt in {4}");
        _strings.GetString(ResultsCopy.StatsUnreadable).Returns("unreadable {0}");
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => _rows);
        _launcher.OpenFile(Arg.Any<string>()).Returns(true);
        _launcher.ShowInFolder(Arg.Any<string>()).Returns(true);
        _launcher.OpenFolder(Arg.Any<string>()).Returns(true);
        _launcher.CopyText(Arg.Any<string>()).Returns(true);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string SummaryText(string name, string meanAll, string meanContig)
        => "DNA-Entropy summary\n"
           + $"name:               {name}\n"
           + "records:            1\n"
           + "total length:       32 nt\n"
           + $"entropy mean (all): {meanAll} bits\n"
           + "entropy min (all):  0.5000 bits\n"
           + "entropy max (all):  1.9000 bits\n"
           + "\n"
           + $"[{name}_1]\n"
           + "  length:           32 nt\n"
           + "  coordinate start: 1\n"
           + $"  entropy mean:     {meanContig} bits\n"
           + "  entropy min:      0.5000 bits (position 3)\n"
           + "  entropy max:      1.9000 bits (position 9)\n"
           + "  direction:          both-combined\n"
           + "  seam:               n/a\n";

    private string MakeRunFolder(string name, string? summary, params string[] otherFiles)
    {
        var folder = Path.Combine(_base, name);
        Directory.CreateDirectory(Path.Combine(folder, name));
        foreach (var other in otherFiles)
        {
            File.WriteAllText(Path.Combine(folder, name, other), "x");
        }

        if (summary is not null)
        {
            File.WriteAllText(Path.Combine(folder, name, name + ".summary.txt"), summary);
        }

        return folder;
    }

    private static RunRecord Row(string id, string? outputDir, string? name = null)
        => new(id, JobPhase.Completed, DateTimeOffset.UnixEpoch, Name: name, OutputDir: outputDir);

    private ResultsViewModel Make(IRunOutputReader? reader = null) => new(_repository, reader ?? new RunOutputReader(), _launcher, _navigator, _strings);

    private async Task<ResultsViewModel> Loaded(string jobId, IRunOutputReader? reader = null)
    {
        var vm = Make(reader);
        await vm.LoadAsync(jobId, TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task The_numbers_shown_are_read_from_that_runs_own_summary_file()
    {
        var a = MakeRunFolder("alpha", SummaryText("alpha", "1.1111", "1.2222"), "alpha.gb");
        var b = MakeRunFolder("beta", SummaryText("beta", "1.7777", "1.8888"), "beta.gb");
        _rows = [Row("job-a", a), Row("job-b", b)];

        var vm = await Loaded("job-b");

        var group = vm.Groups.ShouldHaveSingleItem();
        group.Headline.ShouldStartWith("mean 1.778");
        var contig = group.Contigs.ShouldHaveSingleItem();
        contig.Name.ShouldBe("beta_1");
        contig.LengthText.ShouldBe("32 nt");
        contig.MeanText.ShouldBe("1.889 bits");
        contig.MinText.ShouldBe("0.500 bits");
        contig.MaxText.ShouldBe("1.900 bits");
        contig.DirectionText.ShouldBe("both-combined");
        vm.Groups.SelectMany(g => g.Headline.Split(' ')).ShouldNotContain("1.111");
    }

    [Fact]
    public async Task One_row_per_file_in_the_run_folder_with_its_size()
    {
        var folder = MakeRunFolder("alpha", SummaryText("alpha", "1.1111", "1.2222"), "alpha.gb", "alpha.fasta");
        _rows = [Row("job-a", folder)];

        var vm = await Loaded("job-a");

        vm.Files.Select(f => f.Name).ShouldBe(["alpha/alpha.fasta", "alpha/alpha.gb", "alpha/alpha.summary.txt"]);
        vm.Files[0].SizeText.ShouldBe("1 bytes");
        vm.HasFiles.ShouldBeTrue();
        vm.IsNoticeVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task Loading_a_different_run_replaces_the_files_and_numbers_it_does_not_add_to_them()
    {
        var a = MakeRunFolder("alpha", SummaryText("alpha", "1.1111", "1.2222"), "alpha.gb");
        var b = MakeRunFolder("beta", SummaryText("beta", "1.7777", "1.8888"));
        _rows = [Row("job-a", a), Row("job-b", b)];
        var vm = await Loaded("job-a");

        await vm.LoadAsync("job-b", TestContext.Current.CancellationToken);

        vm.Files.Select(f => f.Name).ShouldBe(["beta/beta.summary.txt"]);
        vm.Groups.ShouldHaveSingleItem().Contigs.ShouldHaveSingleItem().Name.ShouldBe("beta_1");
    }

    [Fact]
    public async Task The_title_is_the_run_name_or_else_its_id()
    {
        var folder = MakeRunFolder("alpha", null);
        _rows = [Row("job-a", folder, name: "My plasmid"), Row("job-b", folder)];

        (await Loaded("job-a")).Title.ShouldBe("My plasmid");
        (await Loaded("job-b")).Title.ShouldBe("job-b");
    }

    [Fact]
    public async Task An_unknown_run_says_so_and_offers_the_runs_page()
    {
        _rows = [];

        var vm = await Loaded("nope");

        vm.NoticeText.ShouldBe(ResultsCopy.NoRun);
        vm.IsNoticeVisible.ShouldBeTrue();
        vm.Files.ShouldBeEmpty();
        vm.OpenFolderCommand.CanExecute(null).ShouldBeFalse();
        vm.ShowRunsCommand.Execute(null);
        _navigator.Received(1).NavigateTo("Runs", null);
    }

    [Fact]
    public async Task A_run_with_no_recorded_folder_says_the_results_are_not_on_this_pc()
    {
        _rows = [Row("job-a", null)];

        var vm = await Loaded("job-a");

        vm.NoticeText.ShouldBe(ResultsCopy.FolderMissing);
        vm.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_folder_that_was_deleted_says_the_results_are_not_on_this_pc()
    {
        _rows = [Row("job-a", Path.Combine(_base, "deleted"))];

        var vm = await Loaded("job-a");

        vm.NoticeText.ShouldBe(ResultsCopy.FolderMissing);
        vm.Files.ShouldBeEmpty();
        vm.Groups.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reader_that_throws_shows_the_read_failed_notice_instead_of_escaping()
    {
        _rows = [Row("job-a", _base)];
        var reader = Substitute.For<IRunOutputReader>();
        reader.Read(Arg.Any<string>()).Returns(_ => throw new IOException("disk"));

        var vm = await Loaded("job-a", reader);

        vm.NoticeText.ShouldBe(ResultsCopy.ReadFailed);
    }

    [Fact]
    public async Task A_history_database_that_throws_shows_the_read_failed_notice_instead_of_escaping()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<RunRecord>>(_ => throw new InvalidOperationException("db"));

        var vm = await Loaded("job-a");

        vm.NoticeText.ShouldBe(ResultsCopy.ReadFailed);
    }

    [Fact]
    public async Task A_run_with_no_summary_file_lists_its_files_and_says_there_are_no_numbers()
    {
        var folder = MakeRunFolder("alpha", null, "alpha.gb");
        _rows = [Row("job-a", folder)];

        var vm = await Loaded("job-a");

        vm.Groups.ShouldBeEmpty();
        vm.StatsNoticeText.ShouldBe(ResultsCopy.StatsNone);
        vm.IsStatsNoticeVisible.ShouldBeTrue();
        vm.Files.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_summary_that_cannot_be_read_names_the_file_and_never_shows_blank_numbers()
    {
        var folder = MakeRunFolder("alpha", "garbage");
        _rows = [Row("job-a", folder)];

        var vm = await Loaded("job-a");

        vm.Groups.ShouldBeEmpty();
        vm.StatsNoticeText.ShouldBe("unreadable alpha/alpha.summary.txt");
    }

    [Fact]
    public async Task Several_summaries_each_get_a_titled_group_and_a_single_one_does_not()
    {
        var folder = MakeRunFolder("alpha", SummaryText("alpha", "1.1111", "1.2222"));
        Directory.CreateDirectory(Path.Combine(folder, "beta"));
        File.WriteAllText(Path.Combine(folder, "beta", "beta.summary.txt"), SummaryText("beta", "1.7777", "1.8888"));
        _rows = [Row("job-a", folder)];

        var vm = await Loaded("job-a");

        vm.Groups.Select(g => g.Title).ShouldBe(["alpha/alpha.summary.txt", "beta/beta.summary.txt"]);
        vm.Groups.ShouldAllBe(g => g.HasTitle);
    }

    [Fact]
    public async Task A_single_summary_group_has_no_title()
    {
        var folder = MakeRunFolder("alpha", SummaryText("alpha", "1.1111", "1.2222"));
        _rows = [Row("job-a", folder)];

        (await Loaded("job-a")).Groups.ShouldHaveSingleItem().HasTitle.ShouldBeFalse();
    }

    [Fact]
    public async Task Each_file_row_opens_shows_and_copies_that_exact_file()
    {
        var folder = MakeRunFolder("alpha", null, "alpha.gb");
        _rows = [Row("job-a", folder)];
        var vm = await Loaded("job-a");
        var row = vm.Files.Single();
        var full = Path.Combine(folder, "alpha", "alpha.gb");

        row.OpenCommand.Execute(null);
        row.ShowInFolderCommand.Execute(null);
        row.CopyPathCommand.Execute(null);

        _launcher.Received(1).OpenFile(full);
        _launcher.Received(1).ShowInFolder(full);
        _launcher.Received(1).CopyText(full);
        vm.IsActionNoticeVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_action_that_fails_says_so_on_the_page_and_a_later_success_clears_it()
    {
        var folder = MakeRunFolder("alpha", null, "alpha.gb");
        _rows = [Row("job-a", folder)];
        var vm = await Loaded("job-a");
        _launcher.OpenFile(Arg.Any<string>()).Returns(false);

        vm.Files.Single().OpenCommand.Execute(null);

        vm.ActionNoticeText.ShouldBe(ResultsCopy.FileActionFailed);
        vm.IsActionNoticeVisible.ShouldBeTrue();

        vm.Files.Single().CopyPathCommand.Execute(null);
        vm.IsActionNoticeVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task Open_folder_opens_the_run_folder_and_a_failure_is_reported()
    {
        var folder = MakeRunFolder("alpha", null);
        _rows = [Row("job-a", folder)];
        var vm = await Loaded("job-a");

        vm.OpenFolderCommand.Execute(null);
        _launcher.Received(1).OpenFolder(folder);
        vm.IsActionNoticeVisible.ShouldBeFalse();

        _launcher.OpenFolder(Arg.Any<string>()).Returns(false);
        vm.OpenFolderCommand.Execute(null);
        vm.ActionNoticeText.ShouldBe(ResultsCopy.OpenFolderFailed);
    }

    [Fact]
    public async Task Open_in_viewer_goes_to_the_existing_viewer_with_the_run_folder()
    {
        var folder = MakeRunFolder("alpha", null);
        _rows = [Row("job-a", folder)];
        var vm = await Loaded("job-a");

        vm.OpenInViewerCommand.Execute(null);

        _navigator.Received(1).NavigateTo(ViewerViewModel.PageKey, folder);
    }

    [Fact]
    public void Before_a_run_is_loaded_nothing_can_be_opened()
    {
        var vm = Make();

        vm.OpenFolderCommand.CanExecute(null).ShouldBeFalse();
        vm.OpenInViewerCommand.CanExecute(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(10L, "10 bytes")]
    [InlineData(2048L, "2.0 KB")]
    [InlineData(3L * 1024 * 1024, "3.0 MB")]
    public async Task File_sizes_use_a_unit_a_person_can_read(long bytes, string expected)
    {
        var folder = MakeRunFolder("alpha", null);
        var reader = Substitute.For<IRunOutputReader>();
        reader.Read(folder).Returns(new RunOutputSnapshot([new RunOutputFile("a.bin", Path.Combine(folder, "a.bin"), bytes)], []));
        _rows = [Row("job-a", folder)];

        var vm = await Loaded("job-a", reader);

        vm.Files.Single().SizeText.ShouldBe(expected);
    }
}
