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

    private sealed class ThrowingSettings : ISettingsStore
    {
        public string? GetString(string key) => throw new IOException("disk gone");

        public void SetString(string key, string value) => throw new IOException("disk gone");
    }

    private sealed class MemoryRuns : IRunRepository
    {
        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunRecord>>([]);

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private (ShellViewModel Shell, TourViewModel Tour) Make(bool withPrototypeRuns, ISettingsStore? offerSettings = null)
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
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _dialogShown.TrySetResult();
            // Like the real dialog, ends when its token is cancelled.
            return _answer.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        var proto = new PrototypeImportViewModel(
            offerSettings ?? new MemorySettings(),
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
    public async Task A_settings_read_that_throws_an_IOException_never_escapes_the_startup_offers_and_the_tour_still_starts()
    {
        var (shell, tour) = Make(withPrototypeRuns: true, new ThrowingSettings());

        // MainWindow awaits this unguarded inside an async void handler: it must complete normally.
        await shell.RunStartupOffersAsync(CancellationToken.None);
        await shell.OnWindowLoadedAsync();

        tour.IsNewRunStepOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task The_offers_do_not_take_their_lifetime_from_the_first_callers_token_a_cancelled_token_only_ends_that_callers_wait()
    {
        var (shell, tour) = Make(withPrototypeRuns: true);
        using var cancelled = new CancellationTokenSource();

        var first = shell.RunStartupOffersAsync(cancelled.Token);
        await _dialogShown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);

        // The question is still open and the tour still waits for it.
        var loaded = shell.OnWindowLoadedAsync();
        loaded.IsCompleted.ShouldBeFalse();
        tour.IsNewRunStepOpen.ShouldBeFalse();

        _answer.SetResult(true);
        await loaded;
        tour.IsNewRunStepOpen.ShouldBeTrue();
    }
}
