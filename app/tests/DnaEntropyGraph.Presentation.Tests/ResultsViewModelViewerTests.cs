using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #586: the Results page's Open in IGV and Open in Geneious commands, on top of #102's page.</summary>
public sealed class ResultsViewModelViewerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-results-viewer-tests", Guid.NewGuid().ToString("N"));
    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly IExternalViewerOpener _opener = Substitute.For<IExternalViewerOpener>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public ResultsViewModelViewerTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var folder = Path.Combine(_base, "run");
        Directory.CreateDirectory(Path.Combine(folder, "a"));
        foreach (var name in new[] { "a.fasta", "a.entropy.bedgraph", "a.gb" })
        {
            File.WriteAllText(Path.Combine(folder, "a", name), "x");
        }

        _repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new List<RunRecord> { new("job", JobPhase.Completed, DateTimeOffset.UnixEpoch, OutputDir: folder) });
        _opener.OpenInIgvAsync(Arg.Any<IReadOnlyList<RunOutputFile>>(), Arg.Any<CancellationToken>()).Returns(ExternalViewerOutcome.Opened);
        _opener.OpenInGeneiousAsync(Arg.Any<IReadOnlyList<RunOutputFile>>(), Arg.Any<CancellationToken>()).Returns(ExternalViewerOutcome.Opened);
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

    [Fact]
    public void Neither_command_can_run_before_a_run_is_loaded()
    {
        var vm = Make();

        vm.OpenInIgvCommand.CanExecute(null).ShouldBeFalse();
        vm.OpenInGeneiousCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Open_in_igv_hands_over_the_files_of_the_run_that_is_shown()
    {
        var vm = await Loaded();

        await vm.OpenInIgvCommand.ExecuteAsync(null);

        await _opener.Received(1).OpenInIgvAsync(
            Arg.Is<IReadOnlyList<RunOutputFile>>(f => f.Count == 3 && f.Any(x => x.RelativePath == "a/a.fasta")),
            Arg.Any<CancellationToken>());
        vm.ActionNoticeText.ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_in_geneious_hands_over_the_files_of_the_run_that_is_shown()
    {
        var vm = await Loaded();

        await vm.OpenInGeneiousCommand.ExecuteAsync(null);

        await _opener.Received(1).OpenInGeneiousAsync(
            Arg.Is<IReadOnlyList<RunOutputFile>>(f => f.Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ExternalViewerOutcome.NoFiles, ResultsCopy.IgvNoFiles)]
    [InlineData(ExternalViewerOutcome.NotFound, ResultsCopy.IgvNotFound)]
    [InlineData(ExternalViewerOutcome.LaunchFailed, ResultsCopy.IgvLaunchFailed)]
    [InlineData(ExternalViewerOutcome.IgvRejected, ResultsCopy.IgvRejected)]
    [InlineData(ExternalViewerOutcome.IgvNoReply, ResultsCopy.IgvNoReply)]
    public async Task Every_igv_failure_shows_its_own_message(ExternalViewerOutcome outcome, string key)
    {
        _opener.OpenInIgvAsync(Arg.Any<IReadOnlyList<RunOutputFile>>(), Arg.Any<CancellationToken>()).Returns(outcome);
        var vm = await Loaded();

        await vm.OpenInIgvCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBe(key);
        vm.IsActionNoticeVisible.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ExternalViewerOutcome.NoFiles, ResultsCopy.GeneiousNoFiles)]
    [InlineData(ExternalViewerOutcome.NotFound, ResultsCopy.GeneiousNotFound)]
    [InlineData(ExternalViewerOutcome.LaunchFailed, ResultsCopy.GeneiousLaunchFailed)]
    public async Task Every_geneious_failure_shows_its_own_message(ExternalViewerOutcome outcome, string key)
    {
        _opener.OpenInGeneiousAsync(Arg.Any<IReadOnlyList<RunOutputFile>>(), Arg.Any<CancellationToken>()).Returns(outcome);
        var vm = await Loaded();

        await vm.OpenInGeneiousCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBe(key);
    }

    [Fact]
    public async Task A_success_clears_the_message_of_the_last_failure()
    {
        _opener.OpenInIgvAsync(Arg.Any<IReadOnlyList<RunOutputFile>>(), Arg.Any<CancellationToken>())
            .Returns(ExternalViewerOutcome.NotFound, ExternalViewerOutcome.Opened);
        var vm = await Loaded();

        await vm.OpenInIgvCommand.ExecuteAsync(null);
        vm.ActionNoticeText.ShouldNotBeEmpty();
        await vm.OpenInIgvCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_launch_failed_message_not_an_unobserved_task()
    {
        _opener.OpenInGeneiousAsync(Arg.Any<IReadOnlyList<RunOutputFile>>(), Arg.Any<CancellationToken>())
            .Returns<Task<ExternalViewerOutcome>>(_ => throw new InvalidOperationException("boom"));
        var vm = await Loaded();

        await vm.OpenInGeneiousCommand.ExecuteAsync(null);

        vm.ActionNoticeText.ShouldBe(ResultsCopy.GeneiousLaunchFailed);
    }

    private ResultsViewModel Make()
        => new(_repository, new RunOutputReader(), Substitute.For<IShellLauncher>(), Substitute.For<INavigator>(), _strings, _opener);

    private async Task<ResultsViewModel> Loaded()
    {
        var vm = Make();
        await vm.LoadAsync("job", TestContext.Current.CancellationToken);
        return vm;
    }
}
