using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #141: notes and tags on the Results page, and their chips and search on the Runs page.</summary>
public sealed class RunNotesTagsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

    private sealed class UtcClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly IRunAnnotations _annotations = Substitute.For<IRunAnnotations>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private List<RunRecord> _rows = [];

    public RunNotesTagsViewModelTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => _rows);
    }

    private ResultsViewModel Results() => new(_repository, Substitute.For<IRunOutputReader>(), Substitute.For<IShellLauncher>(), Substitute.For<INavigator>(), _strings, Substitute.For<IExternalViewerOpener>(), annotations: _annotations);

    private HistoryViewModel History() => new(_repository, Substitute.For<IRunHistoryRemover>(), Substitute.For<INavigator>(), Substitute.For<IRunCloudResults>(), Substitute.For<ILocalRunFiles>(), Substitute.For<IJobEngine>(),
        Substitute.For<IDialogService>(), Substitute.For<IToastService>(), _strings, Substitute.For<IShellLauncher>(), Substitute.For<IGcpAccount>(), new UtcClock());

    private static RunRecord Row(string id, string? notes = null, string? tags = null, string? name = null)
        => new(id, JobPhase.Completed, Now.AddHours(-1), Name: name, Notes: notes, TagsJson: tags);

    [Fact]
    public async Task Load_shows_the_stored_note_and_tags_even_when_the_run_folder_is_gone()
    {
        _rows = [Row("r", "first note", """["a","b"]""")];
        var vm = Results();

        await vm.LoadAsync("r", TestContext.Current.CancellationToken);

        vm.NotesText.ShouldBe("first note");
        vm.TagsText.ShouldBe("a, b");
        vm.CanEditAnnotations.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_run_offers_no_note_editor()
    {
        var vm = Results();
        await vm.LoadAsync("nope", TestContext.Current.CancellationToken);

        vm.CanEditAnnotations.ShouldBeFalse();
        vm.SaveAnnotationsCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Save_writes_the_trimmed_note_and_the_normalised_tags_for_this_run_only_and_tidies_the_box()
    {
        _rows = [Row("r")];
        var vm = Results();
        await vm.LoadAsync("r", TestContext.Current.CancellationToken);
        vm.NotesText = "  hello  ";
        vm.TagsText = " x ;X, y ";

        await vm.SaveAnnotationsCommand.ExecuteAsync(null);

        await _annotations.Received(1).UpdateAsync("r", "hello", """["x","y"]""", Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().UpsertAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>());
        vm.TagsText.ShouldBe("x, y");
        vm.AnnotationsStatusText.ShouldBe(ResultsCopy.NotesSaved);
    }

    [Fact]
    public async Task A_blank_note_and_no_tags_are_stored_as_null()
    {
        _rows = [Row("r", "old", """["t"]""")];
        var vm = Results();
        await vm.LoadAsync("r", TestContext.Current.CancellationToken);
        vm.NotesText = "   ";
        vm.TagsText = string.Empty;

        await vm.SaveAnnotationsCommand.ExecuteAsync(null);

        await _annotations.Received(1).UpdateAsync("r", null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_save_says_so_and_keeps_what_was_typed()
    {
        _rows = [Row("r")];
        _annotations.UpdateAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("locked")));
        var vm = Results();
        await vm.LoadAsync("r", TestContext.Current.CancellationToken);
        vm.NotesText = "keep me";

        await vm.SaveAnnotationsCommand.ExecuteAsync(null);

        vm.AnnotationsStatusText.ShouldBe(ResultsCopy.NotesSaveFailed);
        vm.NotesText.ShouldBe("keep me");
    }

    [Fact]
    public async Task Runs_rows_show_tag_chips_and_the_note_and_the_search_matches_tags_notes_and_names_ignoring_case()
    {
        _rows = [Row("a", "Failed SEQUENCING", """["clone7"]""", name: "alpha"), Row("b", null, """["Control"]""", name: "beta"), Row("c", name: "gamma")];
        var vm = History();
        await vm.RefreshCommand.ExecuteAsync(null);
        IEnumerable<RunListItem> Shown() => vm.Groups.SelectMany(g => g.Items);

        var a = Shown().Single(i => i.JobId == "a");
        a.Tags.ShouldBe(["clone7"]);
        a.HasTags.ShouldBeTrue();
        a.NotesText.ShouldBe("Failed SEQUENCING");
        Shown().Single(i => i.JobId == "c").HasTags.ShouldBeFalse();

        vm.SearchText = "CLONE7";
        Shown().Select(i => i.JobId).ShouldBe(["a"]);
        vm.SearchText = "control";
        Shown().Select(i => i.JobId).ShouldBe(["b"]);
        vm.SearchText = "failed seq";
        Shown().Select(i => i.JobId).ShouldBe(["a"]);
        vm.SearchText = "GAMMA";
        Shown().Select(i => i.JobId).ShouldBe(["c"]);
    }
}
