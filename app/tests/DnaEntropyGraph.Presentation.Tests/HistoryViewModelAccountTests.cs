using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #112: the Runs page lists only the active account's runs, follows a switch without a restart, and every line about hidden runs names an action that exists.</summary>
public sealed class HistoryViewModelAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);
    private static readonly AccountInfo A = new("sub-a", "a@lab.org");
    private static readonly AccountInfo B = new("sub-b", "b@lab.org");

    private sealed class UtcClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IToastService _toasts = Substitute.For<IToastService>();
    private readonly List<RunRecord> _rows = [];
    private AccountInfo? _current = A;
    private IReadOnlyList<AccountInfo> _accounts = [A, B];

    public HistoryViewModelAccountTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _strings.GetString(RunsCopy.HiddenOne).Returns("hidden-one");
        _strings.GetString(RunsCopy.HiddenMany).Returns("hidden-many {0}");
        _strings.GetString(RunsCopy.SwitchLabel).Returns("switch {0}");
        _strings.GetString(RunsCopy.HintSwitch).Returns("hint-switch");
        _strings.GetString(RunsCopy.HintSignIn).Returns("hint-signin {0}");
        _strings.GetString(RunsCopy.HintSignInUnknown).Returns("hint-signin-unknown");
        _strings.GetString("SignInNeeded_Joiner").Returns(" or ");
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => _rows.ToList());
        _account.CurrentAccount.Returns(_ => _current);
        _account.Accounts.Returns(_ => _accounts);
    }

    private void Run(string id, string? sub, int hoursAgo = 1, string target = "cloud")
        => _rows.Add(new RunRecord(id, JobPhase.Completed, Now.AddHours(-hoursAgo), Name: id, AccountSub: sub, Target: target));

    private HistoryViewModel Make(IDispatcher? dispatcher = null) => new(
        _repository, Substitute.For<IRunHistoryRemover>(), Substitute.For<INavigator>(), Substitute.For<IRunCloudResults>(), Substitute.For<ILocalRunFiles>(), Substitute.For<IJobEngine>(),
        Substitute.For<IDialogService>(), _toasts, _strings, Substitute.For<IShellLauncher>(), _account, new UtcClock(), dispatcher: dispatcher);

    private static List<string> Ids(HistoryViewModel vm) => [.. vm.Groups.SelectMany(g => g.Items).Select(i => i.Run.JobId).Order()];

    private void SwitchTo(AccountInfo? account)
    {
        _current = account;
        _account.AccountChanged += Raise.Event();
    }

    [Fact]
    public async Task A_second_account_sees_an_empty_Runs_page_until_it_runs_something()
    {
        Run("run-a", "sub-a");
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Ids(vm).ShouldBe(["run-a"]);

        SwitchTo(B);
        await vm.AccountRefresh;

        Ids(vm).ShouldBeEmpty();
        vm.IsEmpty.ShouldBeTrue();

        Run("run-b", "sub-b");
        await vm.RefreshCommand.ExecuteAsync(null);
        Ids(vm).ShouldBe(["run-b"]);
    }

    [Fact]
    public async Task With_account_A_active_only_As_runs_show_and_the_line_says_one_is_hidden()
    {
        Run("run-a", "sub-a");
        Run("run-b", "sub-b");
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        Ids(vm).ShouldBe(["run-a"]);
        vm.HasHiddenRuns.ShouldBeTrue();
        vm.HiddenRunsText.ShouldBe("hidden-one");
    }

    [Fact]
    public async Task Switching_the_account_shows_only_Bs_runs_without_refreshing_by_hand_and_As_in_flight_run_comes_back_with_A()
    {
        Run("run-a", "sub-a");
        _rows[0] = _rows[0] with { Phase = JobPhase.Running };
        Run("run-b1", "sub-b");
        Run("run-b2", "sub-b", hoursAgo: 2);
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Ids(vm).ShouldBe(["run-a"]);

        SwitchTo(B);
        await vm.AccountRefresh;
        Ids(vm).ShouldBe(["run-b1", "run-b2"]);

        SwitchTo(A);
        await vm.AccountRefresh;
        Ids(vm).ShouldBe(["run-a"]);
        vm.Groups.SelectMany(g => g.Items).Single().Run.Phase.ShouldBe(JobPhase.Running);
    }

    [Fact]
    public async Task The_hidden_count_names_how_many_when_there_are_several()
    {
        Run("run-a", "sub-a");
        Run("run-b1", "sub-b");
        Run("run-b2", "sub-b");
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.HiddenRunsText.ShouldBe("hidden-many 2");
    }

    [Fact]
    public async Task A_run_with_no_recorded_account_shows_under_every_account_and_hides_nothing()
    {
        Run("old-run", null);
        Run("pc-run", null, target: "local");
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);
        Ids(vm).ShouldBe(["old-run", "pc-run"]);

        SwitchTo(B);
        await vm.AccountRefresh;
        Ids(vm).ShouldBe(["old-run", "pc-run"]);
        vm.HasHiddenRuns.ShouldBeFalse();
        vm.HiddenRunsText.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_nobody_signed_in_every_run_is_listed_and_nothing_is_called_hidden()
    {
        Run("run-a", "sub-a");
        Run("run-b", "sub-b");
        _current = null;
        _accounts = [];
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        Ids(vm).ShouldBe(["run-a", "run-b"]);
        vm.HasHiddenRuns.ShouldBeFalse();
    }

    [Fact]
    public async Task Hidden_runs_of_a_signed_in_account_get_a_Switch_button_and_a_hint_that_names_it()
    {
        Run("run-a", "sub-a");
        Run("run-b", "sub-b");
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SwitchAccounts.Select(s => s.Label).ShouldBe(["switch b@lab.org"]);
        vm.HintText.ShouldBe("hint-switch");
        vm.HasSignInAgain.ShouldBeFalse();
    }

    [Fact]
    public async Task Hidden_runs_of_a_signed_out_account_get_a_Sign_in_again_button_naming_the_email_and_no_dead_Switch_hint()
    {
        _accounts = [A, B with { NeedsSignIn = true }];
        Run("run-a", "sub-a");
        Run("run-b", "sub-b");
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SwitchAccounts.ShouldBeEmpty();
        vm.HintText.ShouldBe("hint-signin b@lab.org");
        vm.HasSignInAgain.ShouldBeTrue();
        vm.HintText.ShouldNotContain("hint-switch");
    }

    [Fact]
    public async Task Hidden_runs_of_an_account_this_PC_has_forgotten_say_so_without_an_email_and_still_offer_Sign_in_again()
    {
        _accounts = [A];
        Run("run-a", "sub-a");
        Run("run-gone", "sub-gone");
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SwitchAccounts.ShouldBeEmpty();
        vm.HintText.ShouldBe("hint-signin-unknown");
        vm.HasSignInAgain.ShouldBeTrue();
    }

    [Fact]
    public async Task Sign_in_again_starts_the_Google_sign_in_and_a_failure_is_a_toast_naming_what_to_do()
    {
        _accounts = [A];
        Run("run-gone", "sub-gone");
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.SignInAgainCommand.ExecuteAsync(null);
        await _account.Received(1).SignInAsync(Arg.Any<CancellationToken>());

        _account.SignInAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AccountAuthException("denied"));
        await vm.SignInAgainCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast(RunsCopy.SignInFailed.Title, RunsCopy.SignInFailed.Body, ToastSeverity.Error);
    }

    [Fact]
    public async Task The_Switch_button_switches_the_account_and_a_failed_switch_is_a_toast()
    {
        Run("run-b", "sub-b");
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.SwitchAccounts[0].SwitchCommand.ExecuteAsync(null);
        await _account.Received(1).SwitchAccountAsync("sub-b", Arg.Any<CancellationToken>());

        _account.SwitchAccountAsync("sub-b", Arg.Any<CancellationToken>()).ThrowsAsync(new AccountAuthException(AuthErrorCodes.AccountNotFound));
        await vm.SwitchAccounts[0].SwitchCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast(RunsCopy.SwitchFailed.Title, RunsCopy.SwitchFailed.Body, ToastSeverity.Error);
    }

    [Fact]
    public async Task A_page_that_was_never_loaded_does_not_load_on_an_account_change()
    {
        Run("run-a", "sub-a");
        var vm = Make();

        SwitchTo(B);
        await vm.AccountRefresh;

        await _repository.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference LoadAndForgetAPage()
    {
        var vm = Make();
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return new WeakReference(vm);
    }

    [Fact]
    public void A_page_that_was_collected_stops_listening()
    {
        // The account is a singleton and the page ViewModel is transient: the subscription must not keep the page alive.
        Run("run-a", "sub-a");
        var weak = LoadAndForgetAPage();

        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        weak.IsAlive.ShouldBeFalse();
        _account.DidNotReceive().AccountChanged -= Arg.Any<EventHandler>();
        _account.AccountChanged += Raise.Event();
        _account.Received(1).AccountChanged -= Arg.Any<EventHandler>(); // the handler found its page gone and removed itself
    }
    [Fact]
    public async Task The_reload_goes_through_the_dispatcher_when_there_is_one()
    {
        Run("run-a", "sub-a");
        Run("run-b", "sub-b");
        var queued = new List<Action>();
        var dispatcher = Substitute.For<IDispatcher>();
        dispatcher.When(d => d.Enqueue(Arg.Any<Action>())).Do(call => queued.Add(call.Arg<Action>()));
        var vm = Make(dispatcher);
        await vm.RefreshCommand.ExecuteAsync(null);
        Ids(vm).ShouldBe(["run-a"]);

        SwitchTo(B);
        queued.Count.ShouldBe(1);
        Ids(vm).ShouldBe(["run-a"]); // nothing reloads until the dispatcher runs the action

        queued[0]();
        await vm.AccountRefresh;

        Ids(vm).ShouldBe(["run-b"]);
    }

    [Fact]
    public async Task After_Deactivate_an_account_change_loads_nothing()
    {
        Run("run-a", "sub-a");
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        _repository.ClearReceivedCalls();

        vm.Deactivate();
        SwitchTo(B);
        await vm.AccountRefresh;

        await _repository.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
        _account.Received(1).AccountChanged -= Arg.Any<EventHandler>();
    }

    [Fact]
    public async Task A_failed_Switch_reloads_so_the_Sign_in_again_button_the_toast_names_is_on_screen()
    {
        Run("run-b", "sub-b");
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SwitchAccounts.Count.ShouldBe(1);
        vm.HasSignInAgain.ShouldBeFalse();

        // B was forgotten after the page loaded; the failed switch raises no AccountChanged.
        _accounts = [A];
        _account.SwitchAccountAsync("sub-b", Arg.Any<CancellationToken>()).ThrowsAsync(new AccountAuthException(AuthErrorCodes.AccountNotFound));
        _toasts.When(t => t.ShowToast(RunsCopy.SwitchFailed.Title, RunsCopy.SwitchFailed.Body, ToastSeverity.Error)).Do(_ => vm.HasSignInAgain.ShouldBeTrue("the button must exist when the toast tells the person to press it"));

        await vm.SwitchAccounts[0].SwitchCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast(RunsCopy.SwitchFailed.Title, RunsCopy.SwitchFailed.Body, ToastSeverity.Error);
        vm.SwitchAccounts.ShouldBeEmpty();
        vm.HasSignInAgain.ShouldBeTrue();
    }

    [Fact]
    public async Task With_one_owner_signed_in_and_another_signed_out_the_hint_covers_both_buttons()
    {
        var c = new AccountInfo("sub-c", "c@lab.org", NeedsSignIn: true);
        _accounts = [A, B, c];
        Run("run-a", "sub-a");
        Run("run-b", "sub-b");
        Run("run-c", "sub-c");
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SwitchAccounts.Select(s => s.Label).ShouldBe(["switch b@lab.org"]);
        vm.HasSignInAgain.ShouldBeTrue();
        vm.HintText.ShouldContain("hint-switch");
        vm.HintText.ShouldContain("hint-signin c@lab.org");
    }
}