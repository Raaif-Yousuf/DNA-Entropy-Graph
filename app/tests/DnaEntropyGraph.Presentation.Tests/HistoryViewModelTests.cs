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

/// <summary>Issue #101: the Runs page's state and actions.</summary>
public sealed class HistoryViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

    private sealed class UtcClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly IRunHistoryRemover _remover = Substitute.For<IRunHistoryRemover>();
    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly IRunCloudResults _cloud = Substitute.For<IRunCloudResults>();
    private readonly ILocalRunFiles _local = Substitute.For<ILocalRunFiles>();
    private readonly IJobEngine _engine = Substitute.For<IJobEngine>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IToastService _toasts = Substitute.For<IToastService>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private List<RunRecord> _rows = [];

    public HistoryViewModelTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => _rows);
        _dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(true);
        _cloud.IsAvailable(Arg.Any<RunRecord>()).Returns(true);
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(true);
    }

    private HistoryViewModel Make() => new(_repository, _remover, _navigator, _cloud, _local, _engine, _dialogs, _toasts, _strings, new UtcClock());

    private static RunRecord Row(string id, JobPhase phase = JobPhase.Completed, DateTimeOffset? created = null, string? name = null, string? outputDir = null, string? options = null, string? notes = null)
        => new(id, phase, created ?? Now.AddHours(-1), Name: name, OutputDir: outputDir, OptionsJson: options ?? "{}", Notes: notes, Bucket: "b", JobPrefix: $"jobs/{id}/");

    private async Task<HistoryViewModel> Loaded(params RunRecord[] rows)
    {
        _rows = [.. rows];
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm;
    }

    private static RunListItem Item(HistoryViewModel vm, string id) => vm.Groups.SelectMany(g => g.Items).Single(i => i.JobId == id);

    [Fact]
    public async Task Runs_are_grouped_by_local_day_newest_day_first_and_newest_run_first_within_a_day()
    {
        var vm = await Loaded(
            Row("old", created: Now.AddDays(-10)),
            Row("today-early", created: Now.AddHours(-5)),
            Row("today-late", created: Now.AddHours(-1)),
            Row("yday", created: Now.AddDays(-1)));

        vm.Groups.Select(g => g.Header).ShouldBe(["Runs_Group_Today", "Runs_Group_Yesterday", Now.AddDays(-10).ToString("D", System.Globalization.CultureInfo.CurrentCulture)]);
        vm.Groups[0].Items.Select(i => i.JobId).ShouldBe(["today-late", "today-early"]);
        vm.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task No_runs_is_empty_with_the_no_runs_copy()
    {
        var vm = await Loaded();

        vm.IsEmpty.ShouldBeTrue();
        vm.EmptyText.ShouldBe("Runs_Empty_None");
        vm.Groups.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(RunStatusFilter.Completed, new[] { "done", "part" })]
    [InlineData(RunStatusFilter.Failed, new[] { "bad" })]
    [InlineData(RunStatusFilter.Cancelled, new[] { "stop" })]
    [InlineData(RunStatusFilter.Active, new[] { "live", "prov" })]
    [InlineData(RunStatusFilter.All, new[] { "done", "part", "bad", "stop", "live", "prov" })]
    public async Task The_status_filter_keeps_only_that_status(RunStatusFilter filter, string[] expected)
    {
        var vm = await Loaded(
            Row("done"), Row("part", JobPhase.PartiallyCompleted), Row("bad", JobPhase.Failed), Row("stop", JobPhase.Cancelled),
            Row("live", JobPhase.Running), Row("prov", JobPhase.Provisioning));
        expected.Length.ShouldBeGreaterThan(0);

        vm.SelectedStatusFilter = vm.StatusFilters.Single(f => f.Value == filter);

        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).OrderBy(x => x).ShouldBe(expected.OrderBy(x => x));
    }

    [Fact]
    public async Task Text_search_matches_name_id_notes_and_input_file_name_ignoring_case_and_empties_groups()
    {
        var vm = await Loaded(
            Row("a1", name: "Plasmid pUC19"),
            Row("b2", notes: "repeat with seed 3"),
            Row("c3", options: """{"InputPath":"C:\\data\\Lambda.gb","ModelId":"m","RunTarget":"cloud"}"""),
            Row("zz9"));

        vm.SearchText = "puc";
        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).ShouldBe(["a1"]);
        vm.SearchText = "SEED";
        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).ShouldBe(["b2"]);
        vm.SearchText = "lambda";
        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).ShouldBe(["c3"]);
        vm.SearchText = "zz9";
        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).ShouldBe(["zz9"]);

        vm.SearchText = "no such run";
        vm.Groups.ShouldBeEmpty();
        vm.IsEmpty.ShouldBeTrue();
        vm.EmptyText.ShouldBe("Runs_Empty_NoMatch");

        vm.SearchText = string.Empty;
        vm.Groups.SelectMany(g => g.Items).Count().ShouldBe(4);
    }

    [Fact]
    public async Task Item_flags_reflect_local_folder_cloud_copy_and_phase()
    {
        _local.OutputFolderExists(Arg.Is<RunRecord>(r => r.JobId == "has")).Returns(true);
        _cloud.IsAvailable(Arg.Is<RunRecord>(r => r.JobId == "nocloud")).Returns(false);
        var vm = await Loaded(Row("has"), Row("nocloud"), Row("live", JobPhase.Running));

        Item(vm, "has").HasLocalFiles.ShouldBeTrue();
        Item(vm, "nocloud").HasLocalFiles.ShouldBeFalse();
        Item(vm, "has").CanRedownload.ShouldBeTrue();
        Item(vm, "nocloud").CanRedownload.ShouldBeFalse();
        Item(vm, "nocloud").CanDeleteCloud.ShouldBeFalse();
        Item(vm, "live").CanRedownload.ShouldBeFalse();
        Item(vm, "live").CanRemove.ShouldBeFalse();
        Item(vm, "has").CanRemove.ShouldBeTrue();
    }

    public static TheoryData<string> EveryRunErrorCode() => [.. RunErrorCodes.All];

    [Theory]
    [MemberData(nameof(EveryRunErrorCode))]
    public async Task A_failed_run_shows_the_resource_text_for_its_code_and_never_the_raw_detail(string code)
    {
        RunErrorCodes.All.ShouldContain(code);
        var vm = await Loaded(Row("bad", JobPhase.Failed) with { ErrorCode = code, ErrorDetail = "RAW-EXCEPTION-TEXT" });

        var item = Item(vm, "bad");
        item.HasReason.ShouldBeTrue();
        item.ReasonText.ShouldBe(RunErrorCodes.ResourceKey(code));
        item.ReasonText!.ShouldNotContain("RAW-EXCEPTION-TEXT");
    }

    [Fact]
    public void The_theory_is_fed_every_code_so_an_empty_list_cannot_pass_it()
        => EveryRunErrorCode().Count.ShouldBe(RunErrorCodes.All.Count);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a_code_from_a_newer_version")]
    public async Task A_failed_run_with_no_or_unknown_code_shows_the_generic_message(string? code)
    {
        var vm = await Loaded(Row("bad", JobPhase.Failed) with { ErrorCode = code, ErrorDetail = "RAW-EXCEPTION-TEXT" });

        Item(vm, "bad").ReasonText.ShouldBe("RunError_other");
    }

    [Fact]
    public async Task A_completed_run_with_a_code_shows_it_as_a_warning_and_one_without_shows_nothing()
    {
        var vm = await Loaded(
            Row("warn", JobPhase.Completed) with { ErrorCode = RunErrorCodes.LifecycleUnverified },
            Row("clean", JobPhase.Completed),
            Row("blank", JobPhase.Completed) with { ErrorCode = " " });

        Item(vm, "warn").ReasonText.ShouldBe("RunError_lifecycle_unverified");
        Item(vm, "warn").HasReason.ShouldBeTrue();
        Item(vm, "clean").HasReason.ShouldBeFalse();
        Item(vm, "clean").ReasonText.ShouldBeNull();
        Item(vm, "blank").HasReason.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_still_going_shows_no_reason_even_with_a_code()
    {
        var vm = await Loaded(Row("live", JobPhase.Running) with { ErrorCode = RunErrorCodes.Other });

        Item(vm, "live").HasReason.ShouldBeFalse();
    }

    [Fact]
    public async Task A_live_run_never_offers_local_deletion_and_the_command_does_nothing()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        var vm = await Loaded(Row("live", JobPhase.Running, outputDir: @"C:\out\live"));

        Item(vm, "live").HasLocalFiles.ShouldBeFalse();
        await Item(vm, "live").DeleteLocalCommand.ExecuteAsync(null);

        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
        await _dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delete_cloud_is_disabled_with_the_reason_that_applies_and_enabled_with_no_hint()
    {
        _cloud.CanDelete(Arg.Is<RunRecord>(r => r.JobId != "ok")).Returns(false);
        _cloud.IsAvailable(Arg.Is<RunRecord>(r => r.JobId == "nocopy")).Returns(false);
        var vm = await Loaded(Row("ok"), Row("live", JobPhase.Running), Row("nocopy"), Row("offline"));

        Item(vm, "ok").CanDeleteCloud.ShouldBeTrue();
        Item(vm, "ok").DeleteCloudHint.ShouldBeNull();
        Item(vm, "live").CanDeleteCloud.ShouldBeFalse();
        Item(vm, "live").DeleteCloudHint.ShouldBe("Runs_DeleteCloud_Hint_Running");
        Item(vm, "nocopy").CanDeleteCloud.ShouldBeFalse();
        Item(vm, "nocopy").DeleteCloudHint.ShouldBe("Runs_DeleteCloud_Hint_NoCopy");
        Item(vm, "offline").CanDeleteCloud.ShouldBeFalse();
        Item(vm, "offline").DeleteCloudHint.ShouldBe("Runs_DeleteCloud_Hint_NotConnected");
    }

    [Fact]
    public async Task A_live_run_whose_deleter_is_also_unavailable_gets_the_running_hint_first()
    {
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(false);
        var vm = await Loaded(Row("live", JobPhase.Running));

        Item(vm, "live").DeleteCloudHint.ShouldBe("Runs_DeleteCloud_Hint_Running");
    }

    [Fact]
    public async Task Refreshing_when_the_history_cannot_be_read_toasts_the_refresh_failure_and_does_not_throw()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<RunRecord>>>(_ => throw new InvalidOperationException("db locked"));
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Refresh_Failed_Title", "Runs_Refresh_Failed_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task Refreshing_when_a_folder_probe_throws_toasts_the_refresh_failure_and_does_not_throw()
    {
        _rows = [Row("a")];
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(_ => throw new IOException("network drive gone"));
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Refresh_Failed_Title", "Runs_Refresh_Failed_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task A_cancelled_refresh_is_silent()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<RunRecord>>>(_ => throw new OperationCanceledException(cancelled.Token));
        var vm = Make();

        await vm.RefreshCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!);
    }

    [Fact]
    public async Task A_local_delete_that_throws_or_is_in_use_toasts_the_in_use_copy_and_refreshes()
    {
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(_ => throw new IOException("in use"));
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));
        _repository.ClearReceivedCalls();

        await Item(vm, "a").DeleteLocalCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_InUse_Title", "Runs_DeleteLocal_InUse_Body", ToastSeverity.Error);
        await _repository.Received(1).GetAllAsync(Arg.Any<CancellationToken>());

        _toasts.ClearReceivedCalls();
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.InUse));
        await Item(vm, "a").DeleteLocalCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_DeleteLocal_InUse_Title", "Runs_DeleteLocal_InUse_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task A_partial_local_delete_says_what_was_deleted_and_what_was_not()
    {
        _strings.GetString("Runs_DeleteLocal_Partial_Body").Returns("{0} deleted, {1} left");
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.Partial, 5, 2));
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        await Item(vm, "a").DeleteLocalCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Partial_Title", "5 deleted, 2 left", ToastSeverity.Warning);
        _toasts.DidNotReceive().ShowToast("Runs_DeleteLocal_InUse_Title", Arg.Any<string>(), Arg.Any<ToastSeverity>());
    }

    [Fact]
    public async Task A_permission_denied_local_delete_has_its_own_copy_distinct_from_in_use()
    {
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.AccessDenied));
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        await Item(vm, "a").DeleteLocalCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_AccessDenied_Title", "Runs_DeleteLocal_AccessDenied_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task A_running_run_cannot_be_run_again_from_its_row()
    {
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\in.gb");
        var vm = await Loaded(Row("live", JobPhase.Running, options: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" })));

        Item(vm, "live").RerunCommand.CanExecute(null).ShouldBeFalse();
        await Item(vm, "live").RerunCommand.ExecuteAsync(null);

        await _engine.DidNotReceiveWithAnyArgs().StartRunAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_finished_run_can_be_run_again()
    {
        var vm = await Loaded(Row("done"));

        Item(vm, "done").RerunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Clearing_the_status_filter_selection_shows_all_runs_instead_of_throwing()
    {
        var vm = await Loaded(Row("a"), Row("b", JobPhase.Failed));
        vm.SelectedStatusFilter = vm.StatusFilters.Single(f => f.Value == RunStatusFilter.Failed);

        vm.SelectedStatusFilter = null;

        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).OrderBy(x => x).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task An_older_slow_load_that_finishes_last_does_not_overwrite_the_newer_list()
    {
        var vm = await Loaded(Row("a"));
        var slow = new TaskCompletionSource<IReadOnlyList<RunRecord>>();
        var calls = 0;
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Interlocked.Increment(ref calls) == 1
            ? slow.Task
            : Task.FromResult<IReadOnlyList<RunRecord>>([Row("new")]));

        // The page's Refresh is still reading when a row action finishes and reloads (that path does not go through the command).
        var older = vm.RefreshCommand.ExecuteAsync(null);
        await Item(vm, "a").RemoveCommand.ExecuteAsync(null);
        slow.SetResult([Row("stale")]);
        await older;

        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).ShouldBe(["new"]);
    }

    [Fact]
    public async Task An_older_load_that_faults_after_a_newer_one_wrote_the_list_shows_no_refresh_failure()
    {
        var vm = await Loaded(Row("a"));
        var slow = new TaskCompletionSource<IReadOnlyList<RunRecord>>();
        var calls = 0;
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Interlocked.Increment(ref calls) == 1
            ? slow.Task
            : Task.FromResult<IReadOnlyList<RunRecord>>([Row("new")]));

        var older = vm.RefreshCommand.ExecuteAsync(null);
        await Item(vm, "a").RemoveCommand.ExecuteAsync(null);
        slow.SetException(new InvalidOperationException("db locked"));
        await older;

        _toasts.DidNotReceive().ShowToast("Runs_Refresh_Failed_Title", Arg.Any<string>(), Arg.Any<ToastSeverity>());
        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).ShouldBe(["new"]);
    }

    [Fact]
    public async Task A_failed_redownload_that_already_kept_files_aside_still_says_how_many()
    {
        _strings.GetString("Runs_Redownload_Changed_Body").Returns("{0} kept");
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new RedownloadResult(CloudResultsStatus.Failed, 3));
        var vm = await Loaded(Row("a"));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Redownload_Changed_Title", "3 kept", ToastSeverity.Info);
        _toasts.Received(1).ShowToast("Runs_Redownload_Failed_Title", "Runs_Redownload_Failed_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task One_changed_file_kept_aside_uses_the_singular_copy()
    {
        _strings.GetString("Runs_Redownload_Changed_Body_One").Returns("a file kept");
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new RedownloadResult(CloudResultsStatus.Done, 1));
        var vm = await Loaded(Row("a"));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Redownload_Changed_Title", "a file kept", ToastSeverity.Info);
    }

    [Fact]
    public async Task Redownload_that_kept_changed_files_aside_says_how_many_in_a_second_toast()
    {
        _strings.GetString("Runs_Redownload_Changed_Body").Returns("{0} kept");
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new RedownloadResult(CloudResultsStatus.Done, 2));
        var vm = await Loaded(Row("a"));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Redownload_Changed_Title", "2 kept", ToastSeverity.Info);
        _toasts.Received(1).ShowToast("Runs_Redownload_Done_Title", "Runs_Redownload_Done_Body", ToastSeverity.Success);
    }

    [Fact]
    public async Task Redownload_that_changed_nothing_shows_no_changed_files_toast()
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Done);
        var vm = await Loaded(Row("a"));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);

        _toasts.DidNotReceive().ShowToast("Runs_Redownload_Changed_Title", Arg.Any<string>(), Arg.Any<ToastSeverity>());
    }

    [Fact]
    public async Task Open_probes_the_disk_off_the_calling_thread()
    {
        using var gate = new ManualResetEventSlim();
        _rows = [Row("a", outputDir: @"C:\out\a")];
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(3));
            return true;
        });

        var opening = Item(vm, "a").OpenCommand.ExecuteAsync(null);

        opening.IsCompleted.ShouldBeFalse("the folder probe ran on the calling thread");
        gate.Set();
        await opening;
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "a");
    }

    [Fact]
    public async Task A_rerun_whose_start_throws_toasts_the_failure_copy_and_does_not_navigate()
    {
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\in.gb");
        _engine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" })));

        await Item(vm, "a").RerunCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Rerun_Failed_Title", "Runs_Rerun_Failed_Body", ToastSeverity.Error);
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task A_redownload_or_cloud_delete_that_throws_toasts_the_failure_copy(Type exception)
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns<Task<RedownloadResult>>(_ => throw (Exception)Activator.CreateInstance(exception)!);
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns<Task<CloudResultsStatus>>(_ => throw (Exception)Activator.CreateInstance(exception)!);
        var vm = await Loaded(Row("a"));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_Redownload_Failed_Title", "Runs_Redownload_Failed_Body", ToastSeverity.Error);

        await Item(vm, "a").DeleteCloudCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task A_partial_redownload_has_its_own_copy()
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Partial);
        var vm = await Loaded(Row("a", JobPhase.Failed));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Redownload_Partial_Title", "Runs_Redownload_Partial_Body", ToastSeverity.Warning);
    }

    [Fact]
    public async Task Open_navigates_to_the_results_page_for_the_run_when_its_folder_exists()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        await Item(vm, "a").OpenCommand.ExecuteAsync(null);

        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "a");
    }

    [Fact]
    public async Task Open_with_the_folder_gone_toasts_the_redownload_action_instead_of_navigating()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(false);
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        await Item(vm, "a").OpenCommand.ExecuteAsync(null);

        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Open_Missing_Title", "Runs_Open_Missing_Body", ToastSeverity.Warning);
    }

    [Fact]
    public async Task Open_on_a_live_run_goes_to_its_progress_page()
    {
        var vm = await Loaded(Row("live", JobPhase.Running));

        await Item(vm, "live").OpenCommand.ExecuteAsync(null);

        _navigator.Received(1).NavigateTo("RunProgress", "live");
    }

    [Fact]
    public async Task Rerun_starts_a_new_run_with_the_recorded_options_and_the_resolved_input_then_opens_its_progress()
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", SpotVm = true, Seed = 9, InputPath = @"C:\gone\in.gb", OutputFolder = @"D:\lab" };
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\app\runs\a\input\in.gb");
        _engine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns("new-job");
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(recorded)));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RerunCommand).ExecuteAsync(null);

        await _engine.Received(1).StartRunAsync(recorded with { InputPath = @"C:\app\runs\a\input\in.gb" }, Arg.Any<CancellationToken>());
        _navigator.Received(1).NavigateTo("RunProgress", "new-job");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("garbage")]
    public async Task Rerun_with_unreadable_options_does_not_start_a_run_and_toasts_the_new_run_action(string options)
    {
        var vm = await Loaded(Row("a", options: options));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RerunCommand).ExecuteAsync(null);

        await _engine.DidNotReceiveWithAnyArgs().StartRunAsync(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Rerun_NoOptions_Title", "Runs_Rerun_NoOptions_Body", ToastSeverity.Warning);
    }

    [Fact]
    public async Task Rerun_with_no_input_anywhere_does_not_start_a_run()
    {
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns((string?)null);
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" })));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RerunCommand).ExecuteAsync(null);

        await _engine.DidNotReceiveWithAnyArgs().StartRunAsync(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Rerun_NoInput_Title", "Runs_Rerun_NoInput_Body", ToastSeverity.Warning);
    }

    [Fact]
    public async Task Redownload_calls_the_service_for_that_run_toasts_success_and_refreshes()
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Done);
        var vm = await Loaded(Row("a"));
        _repository.ClearReceivedCalls();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RedownloadCommand).ExecuteAsync(null);

        await _cloud.Received(1).RedownloadAsync(Arg.Is<RunRecord>(r => r.JobId == "a"), Arg.Any<CancellationToken>());
        _toasts.Received(1).ShowToast("Runs_Redownload_Done_Title", "Runs_Redownload_Done_Body", ToastSeverity.Success);
        await _repository.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CloudResultsStatus.Expired, "Runs_Redownload_Expired", ToastSeverity.Warning)]
    [InlineData(CloudResultsStatus.NoCloudCopy, "Runs_Redownload_NoCloudCopy", ToastSeverity.Info)]
    [InlineData(CloudResultsStatus.ResultNotFound, "Runs_Redownload_ResultNotFound", ToastSeverity.Error)]
    [InlineData(CloudResultsStatus.Refused, "Runs_Redownload_Refused", ToastSeverity.Error)]
    [InlineData(CloudResultsStatus.Failed, "Runs_Redownload_Failed", ToastSeverity.Error)]
    [InlineData(CloudResultsStatus.Partial, "Runs_Redownload_Partial", ToastSeverity.Warning)]
    public async Task Each_redownload_failure_has_its_own_copy(CloudResultsStatus status, string keyPrefix, ToastSeverity severity)
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(status);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RedownloadCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast(keyPrefix + "_Title", keyPrefix + "_Body", severity);
    }

    [Fact]
    public async Task Delete_cloud_results_asks_first_and_does_nothing_when_declined()
    {
        _dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(false);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        await _cloud.DidNotReceiveWithAnyArgs().DeleteAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delete_cloud_results_after_confirming_deletes_that_run_and_refreshes()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Done);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        await _cloud.Received(1).DeleteAsync(Arg.Is<RunRecord>(r => r.JobId == "a"), Arg.Any<CancellationToken>());
        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Done_Title", "Runs_DeleteCloud_Done_Body", ToastSeverity.Success);
    }

    [Fact]
    public async Task A_failed_cloud_delete_toasts_the_failure_copy()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Failed);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task Delete_local_files_asks_first_and_never_deletes_when_declined()
    {
        _dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(false);
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteLocalCommand).ExecuteAsync(null);

        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
    }

    [Fact]
    public async Task Delete_local_files_after_confirming_deletes_through_the_service_and_toasts()
    {
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.Deleted, 3));
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteLocalCommand).ExecuteAsync(null);

        _local.Received(1).DeleteOutputFolder(Arg.Is<RunRecord>(r => r.JobId == "a"));
        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Done_Title", "Runs_DeleteLocal_Done_Body", ToastSeverity.Success);
    }

    [Fact]
    public async Task A_refused_local_delete_says_so_and_names_an_action()
    {
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(new LocalDeleteResult(LocalDeleteStatus.Refused));
        var vm = await Loaded(Row("a", outputDir: @"C:\elsewhere"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteLocalCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Refused_Title", "Runs_DeleteLocal_Refused_Body", ToastSeverity.Error);
    }

    [Fact]
    public async Task Remove_after_confirming_removes_only_the_history_row_and_touches_no_files_or_cloud()
    {
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"), Row("b"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RemoveCommand).ExecuteAsync(null);

        await _remover.Received(1).DeleteAsync("a", Arg.Any<CancellationToken>());
        _local.DidNotReceiveWithAnyArgs().DeleteOutputFolder(default!);
        await _cloud.DidNotReceiveWithAnyArgs().DeleteAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Remove_declined_keeps_the_row()
    {
        _dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(false);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RemoveCommand).ExecuteAsync(null);

        await _remover.DidNotReceiveWithAnyArgs().DeleteAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_live_run_cannot_be_removed()
    {
        var vm = await Loaded(Row("live", JobPhase.Running));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "live").RemoveCommand).ExecuteAsync(null);

        await _remover.DidNotReceiveWithAnyArgs().DeleteAsync(default!, TestContext.Current.CancellationToken);
    }
}
