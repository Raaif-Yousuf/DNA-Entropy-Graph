using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.PrototypeImport;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issues #143 and #243: the first-launch tour must not open over the prototype-import question; it starts after the startup offers finish.</summary>
public sealed class ShellStartupOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-startorder-" + Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dialogShown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ShellStartupOrderTests() => Directory.CreateDirectory(Path.Combine(_root, "Downloads"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    private sealed class MemoryRuns : IRunRepository
    {
        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunRecord>>([]);

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private (ShellViewModel Shell, TourViewModel Tour) Make(bool withPrototypeRuns)
    {
        if (withPrototypeRuns)
        {
            var folder = Path.Combine(_root, "Downloads", "a");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.entropy.bedgraph"), "x");
        }

        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _dialogShown.TrySetResult();
            return _answer.Task;
        });
        var proto = new PrototypeImportViewModel(
            new MemorySettings(),
            new PrototypeSearchPaths(Path.Combine(_root, "AppData", "dna-entropy", "config.json"), Path.Combine(_root, "Downloads")),
            new PrototypeImporter(new MemoryRuns()),
            dialogs,
            strings,
            Substitute.For<IToastService>());
        var tour = new TourViewModel(new TourSettings(new MemorySettings()));
        var shell = new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center(), TestMessages.Completion(), prototypeImport: proto, tour: tour);
        return (shell, tour);
    }

    [Fact]
    public async Task The_tour_does_not_start_until_the_prototype_offer_has_been_answered()
    {
        var (shell, tour) = Make(withPrototypeRuns: true);

        var offers = shell.RunStartupOffersAsync(CancellationToken.None);
        var loaded = shell.OnWindowLoadedAsync();
        await _dialogShown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The dialog is open and the question is unanswered: asserted before the gate is released, no timing involved.
        offers.IsCompleted.ShouldBeFalse();
        loaded.IsCompleted.ShouldBeFalse();
        tour.IsNewRunStepOpen.ShouldBeFalse("the tour must not open over the dialog");

        _answer.SetResult(true);
        await offers;
        await loaded;
        tour.IsNewRunStepOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task The_order_of_the_two_calls_does_not_matter_the_tour_still_waits()
    {
        var (shell, tour) = Make(withPrototypeRuns: true);

        var loaded = shell.OnWindowLoadedAsync();
        await _dialogShown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var offers = shell.RunStartupOffersAsync(CancellationToken.None);

        loaded.IsCompleted.ShouldBeFalse();
        tour.IsNewRunStepOpen.ShouldBeFalse("window-loaded ran first and still must not open the tour over the dialog");

        _answer.SetResult(true);
        await offers;
        await loaded;
        tour.IsNewRunStepOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task With_no_offer_to_make_the_tour_starts_straight_away()
    {
        var (shell, tour) = Make(withPrototypeRuns: false);

        await shell.RunStartupOffersAsync(CancellationToken.None);
        await shell.OnWindowLoadedAsync();

        tour.IsNewRunStepOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_offer_still_lets_the_tour_start_and_never_throws_out_of_a_fire_and_forget_call()
    {
        var (shell, tour) = Make(withPrototypeRuns: true);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = shell.RunStartupOffersAsync(cancelled.Token).ContinueWith(static _ => { }, TestContext.Current.CancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
        await shell.OnWindowLoadedAsync();

        tour.IsNewRunStepOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task A_faulted_offer_still_lets_the_tour_start()
    {
        var (shell, tour) = Make(withPrototypeRuns: true);
        _answer.SetException(new InvalidOperationException("boom"));

        _ = shell.RunStartupOffersAsync(CancellationToken.None).ContinueWith(static _ => { }, TestContext.Current.CancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
        await shell.OnWindowLoadedAsync();

        tour.IsNewRunStepOpen.ShouldBeTrue();
    }
}