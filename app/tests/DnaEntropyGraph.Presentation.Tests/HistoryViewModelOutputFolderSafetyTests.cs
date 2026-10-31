using CommunityToolkit.Mvvm.Input;
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
/// Issue #934 (a recursive delete requires the run folder's marker; an older folder without one is confirmed once, with its full path, never deleted silently) and
/// issue #954 (a run saved straight to a drive says why History cannot delete or re-download it, and names one action).
/// </summary>
public sealed class HistoryViewModelOutputFolderSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
    private const string Folder = @"C:\People\bob\Documents";

    private sealed class UtcClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly ILocalRunFiles _local = Substitute.For<ILocalRunFiles>();
    private readonly IRunCloudResults _cloud = Substitute.For<IRunCloudResults>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IChoiceDialogService _choice = Substitute.For<IChoiceDialogService>();
    private readonly IToastService _toasts = Substitute.For<IToastService>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IShellLauncher _launcher = Substitute.For<IShellLauncher>();
    private List<RunRecord> _rows = [];

    public HistoryViewModelOutputFolderSafetyTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _strings.GetString(RunsCopy.DeleteLocalUnmarkedConfirmBody).Returns("Not marked. Delete {0} and everything in it?");
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => _rows);
        _dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(true);
        _cloud.IsAvailable(Arg.Any<RunRecord>()).Returns(true);
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(true);
        _launcher.OpenFolder(Arg.Any<string>()).Returns(true);
        _choice.ChooseAsync(default!, default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs((int?)0);
    }

    private HistoryViewModel Make(bool withChoice = true)
        => new(_repository, Substitute.For<IRunHistoryRemover>(), Substitute.For<INavigator>(), _cloud, _local, Substitute.For<IJobEngine>(), _dialogs, _toasts, _strings, _launcher,
            Substitute.For<IGcpAccount>(), new UtcClock(), choiceDialogs: withChoice ? _choice : null);

    private static RunRecord Row(string id = "a", string? outputDir = Folder)
        => new(id, JobPhase.Completed, Now.AddHours(-1), OutputDir: outputDir, OptionsJson: "{}", Bucket: "b", JobPrefix: $"jobs/{id}/");

    private async Task<RunListItem> Loaded(RunRecord row, bool withChoice = true)
    {
        _rows = [row];
        var vm = Make(withChoice);
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm.Groups.SelectMany(g => g.Items).Single();
    }

    private static Task Press(System.Windows.Input.ICommand command) => ((IAsyncRelayCommand)command).ExecuteAsync(null);

    [Fact]
    public async Task A_folder_with_the_runs_marker_gets_the_ordinary_confirmation_and_the_marker_checked_delete()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.Proceed);
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.Deleted, 3));
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        await _dialogs.Received(1).ConfirmAsync(RunsCopy.DeleteLocalConfirmTitle, Arg.Any<string>(), Arg.Any<CancellationToken>());
        _local.Received(1).DeleteOutputFolder(Arg.Is<RunRecord>(r => r.JobId == "a"));
        _local.DidNotReceiveWithAnyArgs().DeleteUnmarkedOutputFolder(default!);
    }

    [Fact]
    public async Task A_folder_with_no_marker_asks_once_naming_the_full_folder_and_deletes_only_after_the_user_confirms()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.AskFirst);
        _local.DeleteUnmarkedOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.Deleted, 3));
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        // The one question names the folder in full and says what will go; the ordinary confirmation is not asked on top of it.
        await _dialogs.Received(1).ConfirmAsync(RunsCopy.DeleteLocalUnmarkedConfirmTitle, "Not marked. Delete " + Folder + " and everything in it?", Arg.Any<CancellationToken>());
        await _dialogs.DidNotReceive().ConfirmAsync(RunsCopy.DeleteLocalConfirmTitle, Arg.Any<string>(), Arg.Any<CancellationToken>());
        _local.Received(1).DeleteUnmarkedOutputFolder(Arg.Is<RunRecord>(r => r.JobId == "a"));
        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Done_Title", "Runs_DeleteLocal_Done_Body", ToastSeverity.Success);
    }

    [Fact]
    public async Task Cancelling_the_question_for_a_folder_with_no_marker_deletes_nothing()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.AskFirst);
        _dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(false);
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        await _dialogs.Received(1).ConfirmAsync(RunsCopy.DeleteLocalUnmarkedConfirmTitle, Arg.Any<string>(), Arg.Any<CancellationToken>());
        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
        _local.DidNotReceiveWithAnyArgs().DeleteUnmarkedOutputFolder(default!);
        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
    }

    [Fact]
    public async Task A_marker_naming_another_run_is_refused_without_a_question_and_offers_to_open_the_folder()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.OtherRunsFolder);
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        await _dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, TestContext.Current.CancellationToken);
        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
        _local.DidNotReceiveWithAnyArgs().DeleteUnmarkedOutputFolder(default!);
        await _choice.Received(1).ChooseAsync("Runs_DeleteLocal_OtherRun_Title", "Runs_DeleteLocal_OtherRun_Body", Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { RunsCopy.OpenFolderLabel, RunsCopy.CloseLabel })), Arg.Any<CancellationToken>());
        _launcher.Received(1).OpenFolder(Folder);
    }

    [Fact]
    public async Task Closing_the_refusal_dialog_opens_nothing()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.OtherRunsFolder);
        _choice.ChooseAsync(default!, default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs((int?)1);
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        _launcher.DidNotReceiveWithAnyArgs().OpenFolder(default!);
    }

    [Fact]
    public async Task Without_a_choice_dialog_the_refusal_is_a_toast_that_names_the_action_in_words()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.OtherRunsFolder);
        var item = await Loaded(Row(), withChoice: false);

        await Press(item.DeleteLocalCommand);

        // A toast has no Open folder button, so the toast variant names the row's own Open button instead.
        _toasts.Received(1).ShowToast("Runs_DeleteLocal_OtherRun_Title", "Runs_DeleteLocal_OtherRun_Toast_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task Without_a_choice_dialog_a_drive_root_delete_and_redownload_toast_the_variant_that_names_the_Open_button()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.DriveRoot);
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.DriveRootRefused);
        var item = await Loaded(Row("a", @"D:\sample"), withChoice: false);

        await Press(item.DeleteLocalCommand);
        await Press(item.RedownloadCommand);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_DriveRoot_Title", "Runs_DeleteLocal_DriveRoot_Toast_Body", ToastSeverity.Error);
        _toasts.Received(1).ShowToast("Runs_Redownload_DriveRoot_Title", "Runs_Redownload_DriveRoot_Toast_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task A_refusal_dialog_that_could_not_be_shown_falls_back_to_the_toast_so_the_user_always_sees_the_reason()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.OtherRunsFolder);
        _choice.ChooseAsync(default!, default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs((int?)null);
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_OtherRun_Title", "Runs_DeleteLocal_OtherRun_Toast_Body", ToastSeverity.Error);
        _launcher.DidNotReceiveWithAnyArgs().OpenFolder(default!);
    }

    [Fact]
    public async Task An_unmarked_folder_that_is_the_users_own_shell_folder_is_refused_without_a_question_and_offers_to_open_it()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.ProtectedFolder);
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        await _dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, TestContext.Current.CancellationToken);
        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
        _local.DidNotReceiveWithAnyArgs().DeleteUnmarkedOutputFolder(default!);
        await _choice.Received(1).ChooseAsync("Runs_DeleteLocal_Protected_Title", "Runs_DeleteLocal_Protected_Body", Arg.Is<IReadOnlyList<string>>(l => l[0] == RunsCopy.OpenFolderLabel), Arg.Any<CancellationToken>());
        _launcher.Received(1).OpenFolder(Folder);
    }

    [Fact]
    public async Task A_protected_folder_refusal_without_a_dialog_toasts_the_variant_that_names_the_Open_button()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.ProtectedFolder);
        var item = await Loaded(Row(), withChoice: false);

        await Press(item.DeleteLocalCommand);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Protected_Title", "Runs_DeleteLocal_Protected_Toast_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task A_confirmed_delete_the_service_refuses_as_a_protected_folder_offers_to_open_it()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.AskFirst);
        _local.DeleteUnmarkedOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.ProtectedFolderRefused));
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        await _choice.Received(1).ChooseAsync("Runs_DeleteLocal_Protected_Title", "Runs_DeleteLocal_Protected_Body", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_drive_root_row_shows_the_reason_on_both_buttons_and_the_open_folder_action_on_press()
    {
        _local.OutputRootIsDriveRoot(Arg.Any<RunRecord>()).Returns(true);
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.DriveRoot);
        var item = await Loaded(Row("a", @"D:\sample"));

        item.DeleteLocalHint.ShouldBe(RunsCopy.DeleteLocalHintDriveRoot);
        item.RedownloadHint.ShouldBe(RunsCopy.RedownloadHintDriveRoot);

        await Press(item.DeleteLocalCommand);

        await _choice.Received(1).ChooseAsync("Runs_DeleteLocal_DriveRoot_Title", "Runs_DeleteLocal_DriveRoot_Body", Arg.Is<IReadOnlyList<string>>(l => l[0] == RunsCopy.OpenFolderLabel), Arg.Any<CancellationToken>());
        _launcher.Received(1).OpenFolder(@"D:\sample");
        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
    }

    [Fact]
    public async Task A_row_that_is_not_on_a_drive_root_has_no_such_hint()
    {
        var item = await Loaded(Row());

        item.DeleteLocalHint.ShouldBeNull();
        item.RedownloadHint.ShouldBeNull();
    }

    [Fact]
    public async Task A_redownload_refused_because_the_run_is_on_a_drive_root_says_why_and_offers_to_open_the_folder()
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.DriveRootRefused);
        var item = await Loaded(Row("a", @"D:\sample"));

        await Press(item.RedownloadCommand);

        await _choice.Received(1).ChooseAsync("Runs_Redownload_DriveRoot_Title", "Runs_Redownload_DriveRoot_Body", Arg.Is<IReadOnlyList<string>>(l => l[0] == RunsCopy.OpenFolderLabel), Arg.Any<CancellationToken>());
        _launcher.Received(1).OpenFolder(@"D:\sample");
    }

    [Fact]
    public async Task A_delete_that_still_finds_no_marker_after_the_plan_said_go_ahead_toasts_instead_of_deleting()
    {
        // The folder changed between the plan and the delete: the service refuses, and the page says to press the button again, never a silent delete.
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.Proceed);
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.NeedsConfirmation));
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_NeedsConfirmation_Title", "Runs_DeleteLocal_NeedsConfirmation_Body", ToastSeverity.Warning);
        _local.DidNotReceiveWithAnyArgs().DeleteUnmarkedOutputFolder(default!);
    }

    [Fact]
    public async Task A_delete_the_service_refuses_as_another_runs_folder_offers_to_open_it()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.Proceed);
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.OtherRunsFolder));
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        await _choice.Received(1).ChooseAsync("Runs_DeleteLocal_OtherRun_Title", "Runs_DeleteLocal_OtherRun_Body", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    // Issue #934 cold review round 3: a link refusal after some files were deleted must not say "nothing was deleted".
    [Fact]
    public async Task A_link_refusal_after_some_files_were_deleted_says_so_and_offers_to_open_the_folder()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.Proceed);
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.LinkRefused, 3, 0));
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        await _choice.Received(1).ChooseAsync("Runs_DeleteLocal_LinkPartial_Title", "Runs_DeleteLocal_LinkPartial_Body", Arg.Is<IReadOnlyList<string>>(l => l[0] == RunsCopy.OpenFolderLabel), Arg.Any<CancellationToken>());
        _launcher.Received(1).OpenFolder(Folder);
        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
    }

    [Fact]
    public async Task A_link_refusal_after_some_files_were_deleted_without_a_dialog_toasts_the_variant_that_names_the_Open_button()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.Proceed);
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.LinkRefused, 3, 0));
        var item = await Loaded(Row(), withChoice: false);

        await Press(item.DeleteLocalCommand);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_LinkPartial_Title", "Runs_DeleteLocal_LinkPartial_Toast_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task A_link_refusal_with_nothing_deleted_keeps_the_nothing_was_deleted_copy()
    {
        _local.PlanDelete(Arg.Any<RunRecord>()).Returns(LocalDeletePlan.Proceed);
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.LinkRefused));
        var item = await Loaded(Row());

        await Press(item.DeleteLocalCommand);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Link_Title", "Runs_DeleteLocal_Link_Body", ToastSeverity.Error);
        await _choice.DidNotReceiveWithAnyArgs().ChooseAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Every_new_refusal_status_has_copy_that_names_an_action()
    {
        foreach (var status in new[] { LocalDeleteStatus.NeedsConfirmation, LocalDeleteStatus.OtherRunsFolder, LocalDeleteStatus.DriveRootRefused })
        {
            var token = status switch { LocalDeleteStatus.NeedsConfirmation => "NeedsConfirmation", LocalDeleteStatus.OtherRunsFolder => "OtherRun", _ => "DriveRoot" };
            var message = RunsCopy.DeleteLocal(status);
            message.Title.ShouldBe($"Runs_DeleteLocal_{token}_Title", "each status has its own keys, not the generic refused copy");
            message.Body.ShouldBe($"Runs_DeleteLocal_{token}_Body");
        }

        RunsCopy.Redownload(CloudResultsStatus.DriveRootRefused).Body.ShouldBe("Runs_Redownload_DriveRoot_Body");
        RunsCopy.AllKeys.ShouldContain(RunsCopy.OpenFolderLabel);
        RunsCopy.AllKeys.ShouldContain(RunsCopy.CloseLabel);
        RunsCopy.AllKeys.ShouldContain(RunsCopy.DeleteLocalUnmarkedConfirmTitle);
        RunsCopy.AllKeys.ShouldContain(RunsCopy.DeleteLocalUnmarkedConfirmBody);
        RunsCopy.AllKeys.ShouldContain(RunsCopy.DeleteLocalHintDriveRoot);
        RunsCopy.AllKeys.ShouldContain(RunsCopy.RedownloadHintDriveRoot);
        RunsCopy.AllKeys.ShouldContain("Runs_DeleteLocal_OtherRun_Body");
        RunsCopy.AllKeys.ShouldContain("Runs_Redownload_DriveRoot_Title");
        foreach (var key in new[] { "Runs_DeleteLocal_Protected_Title", "Runs_DeleteLocal_Protected_Body", "Runs_DeleteLocal_Protected_Toast_Body", "Runs_DeleteLocal_OtherRun_Toast_Body", "Runs_DeleteLocal_DriveRoot_Toast_Body", "Runs_Redownload_DriveRoot_Toast_Body", "Runs_DeleteLocal_LinkPartial_Title", "Runs_DeleteLocal_LinkPartial_Body", "Runs_DeleteLocal_LinkPartial_Toast_Body" })
        {
            RunsCopy.AllKeys.ShouldContain(key);
        }

        RunsCopy.DeleteLocal(LocalDeleteStatus.ProtectedFolderRefused).Body.ShouldBe("Runs_DeleteLocal_Protected_Body");
    }
}
