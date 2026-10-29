using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issue #141's observable, through the real SQLite file: a note typed on the Results page is there after a relaunch (a new
/// repository and new ViewModels over the same file), and searching a word of it or a tag finds exactly that run.
/// </summary>
public sealed class RunNotesRelaunchTests : IDisposable
{
    private readonly TempPaths _paths = new();

    public void Dispose() => _paths.Dispose();

    private static IStringResourceProvider Strings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        return strings;
    }

    private sealed class UtcClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static HistoryViewModel History(RunRepository repository)
        => new(repository, repository, Substitute.For<INavigator>(), Substitute.For<IRunCloudResults>(), Substitute.For<ILocalRunFiles>(), Substitute.For<IJobEngine>(),
            Substitute.For<IDialogService>(), Substitute.For<IToastService>(), Strings(), Substitute.For<IShellLauncher>(), Substitute.For<IGcpAccount>(), new UtcClock());

    private static ResultsViewModel Results(RunRepository repository)
        => new(repository, Substitute.For<IRunOutputReader>(), Substitute.For<IShellLauncher>(), Substitute.For<INavigator>(), Strings(), Substitute.For<IExternalViewerOpener>(), annotations: repository);

    [Fact]
    public async Task A_note_and_tags_typed_on_the_results_page_survive_a_relaunch_and_the_runs_search_finds_only_that_run()
    {
        var created = DateTimeOffset.UtcNow.AddHours(-1);
        var first = new RunRepository(new SqliteDatabase(_paths.DatabasePath));
        await first.UpsertAsync(new RunRecord("run-1", JobPhase.Completed, created, Name: "plasmid-a"), CancellationToken.None);
        await first.UpsertAsync(new RunRecord("run-2", JobPhase.Completed, created.AddMinutes(1), Name: "plasmid-b"), CancellationToken.None);

        var results = Results(first);
        await results.LoadAsync("run-1", TestContext.Current.CancellationToken);
        results.NotesText = "This was the clone that failed sequencing";
        results.TagsText = "Clone7, redo";
        await results.SaveAnnotationsCommand.ExecuteAsync(null);

        // Relaunch: nothing shared with the first repository but the file.
        var second = new RunRepository(new SqliteDatabase(_paths.DatabasePath));
        var reopened = Results(second);
        await reopened.LoadAsync("run-1", TestContext.Current.CancellationToken);
        reopened.NotesText.ShouldBe("This was the clone that failed sequencing");
        reopened.TagsText.ShouldBe("Clone7, redo");

        var history = History(second);
        await history.RefreshCommand.ExecuteAsync(null);
        IEnumerable<string> Shown() => history.Groups.SelectMany(g => g.Items).Select(i => i.JobId);
        Shown().Count().ShouldBe(2);

        history.SearchText = "FAILED seq";
        Shown().ShouldBe(["run-1"]);
        history.SearchText = "clone7";
        Shown().ShouldBe(["run-1"]);
        history.SearchText = "nothing-like-this";
        Shown().ShouldBeEmpty();
        history.SearchText = string.Empty;
        history.Groups.SelectMany(g => g.Items).Single(i => i.JobId == "run-1").Tags.ShouldBe(["Clone7", "redo"]);
    }
}
