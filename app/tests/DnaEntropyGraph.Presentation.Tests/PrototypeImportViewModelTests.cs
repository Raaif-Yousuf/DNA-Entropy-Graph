using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.PrototypeImport;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #243: the first-launch offer to bring in the earlier command-line version's runs, and the Settings button that does it again.</summary>
public sealed class PrototypeImportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-protovm-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly MemoryRuns _runs = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IToastService _toasts = Substitute.For<IToastService>();

    public PrototypeImportViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Downloads"));
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    private sealed class MemoryRuns : IRunRepository
    {
        public List<RunRecord> Rows { get; } = [];

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunRecord>>([.. Rows]);

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
        {
            Rows.RemoveAll(r => r.JobId == run.JobId);
            Rows.Add(run);
            return Task.CompletedTask;
        }
    }

    private PrototypeImportViewModel Make()
        => new(_settings, new PrototypeSearchPaths(Path.Combine(_root, "AppData", "dna-entropy", "config.json"), Path.Combine(_root, "Downloads")), new PrototypeImporter(_runs), _dialogs, _strings, _toasts);

    private void LegacyRun(string name)
    {
        var folder = Path.Combine(_root, "Downloads", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".entropy.bedgraph"), "x");
    }

    [Fact]
    public async Task First_launch_with_prototype_runs_asks_once_and_adds_them_on_yes()
    {
        LegacyRun("a");
        LegacyRun("b");
        var vm = Make();

        await vm.OfferOnFirstLaunchAsync(CancellationToken.None);

        await _dialogs.Received(1).ConfirmAsync("PrototypeImportTitle", Arg.Is<string>(m => m.Contains("PrototypeImportBody")), Arg.Any<CancellationToken>());
        _runs.Rows.Count.ShouldBe(2);
        _runs.Rows.ShouldAllBe(r => r.Imported);
        _settings.Values[PrototypeImportViewModel.DoneKey].ShouldBe("true");
        vm.Status.ShouldBe("PrototypeImportDone");

        await vm.OfferOnFirstLaunchAsync(CancellationToken.None);
        await _dialogs.Received(1).ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saying_no_adds_nothing_and_does_not_ask_again()
    {
        LegacyRun("a");
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var vm = Make();

        await vm.OfferOnFirstLaunchAsync(CancellationToken.None);
        await vm.OfferOnFirstLaunchAsync(CancellationToken.None);

        _runs.Rows.ShouldBeEmpty();
        await _dialogs.Received(1).ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_offer_is_only_marked_done_once_the_user_has_answered()
    {
        LegacyRun("a");
        var answer = new TaskCompletionSource<bool>();
        var asked = new TaskCompletionSource();
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            asked.TrySetResult();
            return answer.Task;
        });
        var vm = Make();

        var offering = vm.OfferOnFirstLaunchAsync(CancellationToken.None);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _settings.Values.ContainsKey(PrototypeImportViewModel.DoneKey).ShouldBeFalse("closing the app with the dialog open must not use up the offer");

        answer.SetResult(false);
        await offering;
        _settings.Values[PrototypeImportViewModel.DoneKey].ShouldBe("true");
    }

    [Fact]
    public async Task A_dialog_that_is_cancelled_by_closing_the_app_leaves_the_offer_for_next_launch()
    {
        LegacyRun("a");
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromCanceled<bool>(CanceledToken()));

        await Make().OfferOnFirstLaunchAsync(CancellationToken.None);

        _settings.Values.ContainsKey(PrototypeImportViewModel.DoneKey).ShouldBeFalse();
    }

    private static CancellationToken CanceledToken()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    [Fact]
    public async Task Nothing_found_asks_nothing_and_is_not_asked_about_again()
    {
        var vm = Make();

        await vm.OfferOnFirstLaunchAsync(CancellationToken.None);

        await _dialogs.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _settings.Values[PrototypeImportViewModel.DoneKey].ShouldBe("true");
    }

    [Fact]
    public async Task The_dialog_text_shows_the_count_the_zone_and_the_key_note()
    {
        LegacyRun("a");
        Directory.CreateDirectory(Path.Combine(_root, "AppData", "dna-entropy"));
        File.WriteAllText(Path.Combine(_root, "AppData", "dna-entropy", "config.json"), """{"last_zone":"us-central1-a","ssh_key_file":"C:\\k"}""");
        _strings.GetString("PrototypeImportBody").Returns("{0} runs");
        _strings.GetString("PrototypeImportZoneLine").Returns("zone {0}");
        _strings.GetString("PrototypeImportSshLine").Returns("key {0}");
        string? shown = null;
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Do<string>(m => shown = m), Arg.Any<CancellationToken>()).Returns(false);

        await Make().OfferOnFirstLaunchAsync(CancellationToken.None);

        shown.ShouldNotBeNull();
        shown.ShouldContain("1 runs");
        shown.ShouldContain("zone us-central1-a");
        shown.ShouldContain("key C:\\k");
    }

    [Fact]
    public async Task The_settings_button_asks_again_even_after_the_first_launch_answer_and_says_when_nothing_is_there()
    {
        _settings.Values[PrototypeImportViewModel.DoneKey] = "true";
        var vm = Make();

        await vm.FindCommand.ExecuteAsync(null);
        vm.Status.ShouldBe("PrototypeImportNothingFound");

        LegacyRun("late");
        await vm.FindCommand.ExecuteAsync(null);

        _runs.Rows.ShouldHaveSingleItem().Name.ShouldBe("late");
    }

    [Fact]
    public async Task Everything_found_already_in_history_says_so_instead_of_asking()
    {
        LegacyRun("a");
        var vm = Make();
        await vm.FindCommand.ExecuteAsync(null);
        _dialogs.ClearReceivedCalls();

        await vm.FindCommand.ExecuteAsync(null);

        await _dialogs.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        vm.Status.ShouldBe("PrototypeImportAlreadyDone");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runs_the_user_removed_are_reported_as_removed_not_as_already_in_Runs(bool anotherIsInHistory)
    {
        // Issue #915 review: "already in Runs" is false for a removed run and names no way back.
        LegacyRun("a");
        var dismissals = new ImportedRunDismissals(_settings);
        dismissals.Add(PrototypeImporter.JobIdFor(Path.Combine(_root, "Downloads", "a")));
        if (anotherIsInHistory)
        {
            LegacyRun("b");
            await new PrototypeImporter(_runs, dismissals).ImportAsync(PrototypeScanner.Scan(Path.Combine(_root, "none.json"), Path.Combine(_root, "Downloads")), CancellationToken.None);
        }

        var vm = new PrototypeImportViewModel(_settings, new PrototypeSearchPaths(Path.Combine(_root, "none.json"), Path.Combine(_root, "Downloads")), new PrototypeImporter(_runs, dismissals), _dialogs, _strings, _toasts);

        await vm.FindCommand.ExecuteAsync(null);

        await _dialogs.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        vm.Status.ShouldBe("PrototypeImportOnlyRemoved");
    }

    [Fact]
    public async Task A_failure_while_importing_names_an_action_and_never_throws()
    {
        LegacyRun("a");
        var runs = Substitute.For<IRunRepository>();
        runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<RunRecord>>>(_ => throw new IOException("db locked"));
        var vm = new PrototypeImportViewModel(_settings, new PrototypeSearchPaths(Path.Combine(_root, "none.json"), Path.Combine(_root, "Downloads")), new PrototypeImporter(runs), _dialogs, _strings, _toasts);

        await Should.NotThrowAsync(() => vm.OfferOnFirstLaunchAsync(CancellationToken.None));

        vm.Status.ShouldBe("PrototypeImportFailed");
    }
}
