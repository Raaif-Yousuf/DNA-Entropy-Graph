using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
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
    public async Task Delete_cloud_is_disabled_with_a_hint_when_the_service_cannot_delete()
    {
        _cloud.CanDelete(Arg.Is<RunRecord>(r => r.JobId == "a")).Returns(false);
        var vm = await Loaded(Row("a"), Row("b"));

        Item(vm, "a").CanDeleteCloud.ShouldBeFalse();
        Item(vm, "a").DeleteCloudHint.ShouldBe("Runs_DeleteCloud_Unavailable_Hint");
        Item(vm, "b").CanDeleteCloud.ShouldBeTrue();
        Item(vm, "b").DeleteCloudHint.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_local_delete_that_throws_or_fails_toasts_the_failure_copy_and_refreshes()
    {
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(_ => throw new IOException("in use"));
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));
        _repository.ClearReceivedCalls();

        await Item(vm, "a").DeleteLocalCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Failed_Title", "Runs_DeleteLocal_Failed_Body");
        await _repository.Received(1).GetAllAsync(Arg.Any<CancellationToken>());

        _toasts.ClearReceivedCalls();
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(LocalDeleteStatus.Failed);
        await Item(vm, "a").DeleteLocalCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Failed_Title", "Runs_DeleteLocal_Failed_Body");
    }

    [Fact]
    public async Task A_rerun_whose_start_throws_toasts_the_failure_copy_and_does_not_navigate()
    {
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\in.gb");
        _engine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" })));

        await Item(vm, "a").RerunCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Rerun_Failed_Title", "Runs_Rerun_Failed_Body");
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task A_redownload_or_cloud_delete_that_throws_toasts_the_failure_copy(Type exception)
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns<Task<CloudResultsStatus>>(_ => throw (Exception)Activator.CreateInstance(exception)!);
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns<Task<CloudResultsStatus>>(_ => throw (Exception)Activator.CreateInstance(exception)!);
        var vm = await Loaded(Row("a"));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_Redownload_Failed_Title", "Runs_Redownload_Failed_Body");

        await Item(vm, "a").DeleteCloudCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body");
    }

    [Fact]
    public async Task A_partial_redownload_has_its_own_copy()
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Partial);
        var vm = await Loaded(Row("a", JobPhase.Failed));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_Redownload_Partial_Title", "Runs_Redownload_Partial_Body");
    }

    [Fact]
    public async Task Open_navigates_to_the_viewer_with_the_output_folder_when_it_exists()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        Item(vm, "a").OpenCommand.Execute(null);

        _navigator.Received(1).NavigateTo(ViewerViewModel.PageKey, @"C:\out\a");
    }

    [Fact]
    public async Task Open_with_the_folder_gone_toasts_the_redownload_action_instead_of_navigating()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(false);
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        Item(vm, "a").OpenCommand.Execute(null);

        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Open_Missing_Title", "Runs_Open_Missing_Body");
    }

    [Fact]
    public async Task Open_on_a_live_run_goes_to_its_progress_page()
    {
        var vm = await Loaded(Row("live", JobPhase.Running));

        Item(vm, "live").OpenCommand.Execute(null);

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
        _toasts.Received(1).ShowToast("Runs_Rerun_NoOptions_Title", "Runs_Rerun_NoOptions_Body");
    }

    [Fact]
    public async Task Rerun_with_no_input_anywhere_does_not_start_a_run()
    {
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns((string?)null);
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" })));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RerunCommand).ExecuteAsync(null);

        await _engine.DidNotReceiveWithAnyArgs().StartRunAsync(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Rerun_NoInput_Title", "Runs_Rerun_NoInput_Body");
    }

    [Fact]
    public async Task Redownload_calls_the_service_for_that_run_toasts_success_and_refreshes()
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Done);
        var vm = await Loaded(Row("a"));
        _repository.ClearReceivedCalls();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RedownloadCommand).ExecuteAsync(null);

        await _cloud.Received(1).RedownloadAsync(Arg.Is<RunRecord>(r => r.JobId == "a"), Arg.Any<CancellationToken>());
        _toasts.Received(1).ShowToast("Runs_Redownload_Done_Title", "Runs_Redownload_Done_Body");
        await _repository.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CloudResultsStatus.Expired, "Runs_Redownload_Expired")]
    [InlineData(CloudResultsStatus.NoCloudCopy, "Runs_Redownload_NoCloudCopy")]
    [InlineData(CloudResultsStatus.ResultNotFound, "Runs_Redownload_ResultNotFound")]
    [InlineData(CloudResultsStatus.Refused, "Runs_Redownload_Refused")]
    [InlineData(CloudResultsStatus.Failed, "Runs_Redownload_Failed")]
    [InlineData(CloudResultsStatus.Partial, "Runs_Redownload_Partial")]
    public async Task Each_redownload_failure_has_its_own_copy(CloudResultsStatus status, string keyPrefix)
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(status);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RedownloadCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast(keyPrefix + "_Title", keyPrefix + "_Body");
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
        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Done_Title", "Runs_DeleteCloud_Done_Body");
    }

    [Fact]
    public async Task A_failed_cloud_delete_toasts_the_failure_copy()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Failed);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body");
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
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(LocalDeleteStatus.Deleted);
        var vm = await Loaded(Row("a", outputDir: @"C:\out\a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteLocalCommand).ExecuteAsync(null);

        _local.Received(1).DeleteOutputFolder(Arg.Is<RunRecord>(r => r.JobId == "a"));
        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Done_Title", "Runs_DeleteLocal_Done_Body");
    }

    [Fact]
    public async Task A_refused_local_delete_says_so_and_names_an_action()
    {
        _local.DeleteOutputFolder(Arg.Any<RunRecord>()).Returns(LocalDeleteStatus.Refused);
        var vm = await Loaded(Row("a", outputDir: @"C:\elsewhere"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteLocalCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteLocal_Refused_Title", "Runs_DeleteLocal_Refused_Body");
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
