using CommunityToolkit.Mvvm.Messaging;
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
    private readonly IShellLauncher _launcher = Substitute.For<IShellLauncher>();
    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IRunCostSource _costs = Substitute.For<IRunCostSource>();
    private Dictionary<string, double> _booked = [];
    private List<RunRecord> _rows = [];

    public HistoryViewModelTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => _rows);
        _costs.GetBookedAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyDictionary<string, double>>(_booked));
        _dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(true);
        _cloud.IsAvailable(Arg.Any<RunRecord>()).Returns(true);
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(true);
    }

    private HistoryViewModel Make() => new(_repository, _remover, _navigator, _cloud, _local, _engine, _dialogs, _toasts, _strings, _launcher, _account, new UtcClock(), costs: _costs);

    private static RunRecord Row(string id, JobPhase phase = JobPhase.Completed, DateTimeOffset? created = null, string? name = null, string? outputDir = null, string? options = null, string? notes = null)
        => new(id, phase, created ?? Now.AddHours(-1), Name: name, OutputDir: outputDir, OptionsJson: options ?? "{}", Notes: notes, Bucket: "b", JobPrefix: $"jobs/{id}/");

    [Fact]
    public async Task A_run_started_from_a_preset_shows_its_name_on_the_card_and_a_hand_set_run_shows_none()
    {
        _strings.GetString(RunsCopy.PresetLine).Returns("Preset: {0}");
        var options = DnaEntropyGraph.Core.Runs.RunOptionsJson.Serialize(
            DnaEntropyGraph.Core.Presets.BuiltInPresets.PlasmidQc.ApplyTo(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }));

        var vm = await Loaded(Row("preset", options: options), Row("plain"));

        Item(vm, "preset").PresetText.ShouldBe("Preset: Plasmid QC");
        Item(vm, "preset").HasPreset.ShouldBeTrue();
        Item(vm, "plain").HasPreset.ShouldBeFalse();
    }

    [Fact]
    public async Task A_row_shows_the_booked_cost_short_and_a_row_that_booked_nothing_shows_none()
    {
        _strings.GetString(ResultsCopy.CostShort).Returns("about ${0}");
        _strings.GetString(ResultsCopy.CostShortNoCloud).Returns("none");

        _booked = new() { ["paid"] = 0.5 };
        var vm = await Loaded(Row("paid"), Row("unbooked"), Row("pc") with { Target = "local" });

        Item(vm, "paid").CostText.ShouldBe("about $0.50");
        Item(vm, "paid").HasCost.ShouldBeTrue();
        Item(vm, "unbooked").HasCost.ShouldBeFalse();
        Item(vm, "pc").CostText.ShouldBe("none");
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    [Fact]
    public async Task A_run_that_ends_while_the_page_is_open_gets_its_cost_on_the_row_without_leaving_the_page()
    {
        _strings.GetString(ResultsCopy.CostShort).Returns("about ${0}");
        CommunityToolkit.Mvvm.Messaging.IMessenger messenger = new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger();
        _rows = [Row("live", JobPhase.Running)];
        var vm = new HistoryViewModel(_repository, _remover, _navigator, _cloud, _local, _engine, _dialogs, _toasts, _strings, _launcher, _account, new UtcClock(), costs: _costs, messenger: messenger, dispatcher: new InlineDispatcher());
        await vm.RefreshCommand.ExecuteAsync(null);
        Item(vm, "live").HasCost.ShouldBeFalse();

        _rows = [Row("live")];
        _booked = new() { ["live"] = 0.5 };
        messenger.Send(new DnaEntropyGraph.Presentation.Messaging.RunPhaseChangedMessage("live", JobPhase.Completed));
        for (var i = 0; i < 200 && !Item(vm, "live").HasCost; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Item(vm, "live").CostText.ShouldBe("about $0.50");
    }

    private sealed class CountingDispatcher : IDispatcher
    {
        public int Enqueued { get; private set; }

        public void Enqueue(Action action)
        {
            Enqueued++;
            action();
        }
    }

    private HistoryViewModel MakeListening(CommunityToolkit.Mvvm.Messaging.IMessenger messenger, IDispatcher dispatcher)
        => new(_repository, _remover, _navigator, _cloud, _local, _engine, _dialogs, _toasts, _strings, _launcher, _account, new UtcClock(), costs: _costs, messenger: messenger, dispatcher: dispatcher);

    [Fact]
    public async Task A_phase_change_that_is_not_an_end_does_not_reload_the_list_but_an_end_does()
    {
        // The reload is awaited through a signal the repository raises when it is read again, never assumed to have finished by the time Send returns.
        var reads = 0;
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref reads) > 1)
            {
                reloaded.TrySetResult();
            }

            return Task.FromResult<IReadOnlyList<RunRecord>>(_rows);
        });
        CommunityToolkit.Mvvm.Messaging.IMessenger messenger = new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger();
        _rows = [Row("live", JobPhase.Running)];
        var dispatcher = new CountingDispatcher();
        var vm = MakeListening(messenger, dispatcher);
        await vm.RefreshCommand.ExecuteAsync(null);
        reads.ShouldBe(1);

        messenger.Send(new DnaEntropyGraph.Presentation.Messaging.RunPhaseChangedMessage("live", JobPhase.Running));
        dispatcher.Enqueued.ShouldBe(0, "only an ended run changes what a row shows");

        messenger.Send(new DnaEntropyGraph.Presentation.Messaging.RunPhaseChangedMessage("live", JobPhase.Completed));
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        dispatcher.Enqueued.ShouldBe(1, "an end asks the UI thread for one reload, and the earlier non-end asked for none");
        Volatile.Read(ref reads).ShouldBe(2, "the reload read the table once");
    }
    [Fact]
    public async Task A_page_that_was_left_stops_reloading_on_a_run_end_while_a_page_still_open_does()
    {
        CommunityToolkit.Mvvm.Messaging.IMessenger messenger = new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger();
        _rows = [Row("live", JobPhase.Running)];
        var leftDispatcher = new CountingDispatcher();
        var openDispatcher = new CountingDispatcher();
        var left = MakeListening(messenger, leftDispatcher);
        var open = MakeListening(messenger, openDispatcher);
        await left.RefreshCommand.ExecuteAsync(null);
        await open.RefreshCommand.ExecuteAsync(null);
        left.Deactivate();

        messenger.Send(new DnaEntropyGraph.Presentation.Messaging.RunPhaseChangedMessage("live", JobPhase.Completed));

        leftDispatcher.Enqueued.ShouldBe(0, "a page the user left must not reload the whole table on every run end");
        openDispatcher.Enqueued.ShouldBe(1);
        GC.KeepAlive(left);
        GC.KeepAlive(open);
    }

    [Fact]
    public async Task A_Spot_run_on_a_machine_with_no_Spot_price_shows_the_on_demand_wording_on_its_row()
    {
        _strings.GetString(ResultsCopy.CostShort).Returns("about ${0}");
        _strings.GetString(ResultsCopy.CostShortSpot).Returns("about spot ${0}");
        _rows = [Row("spot") with { IsSpot = true, MachineType = "g2-standard-8" }];
        _booked = new() { ["spot"] = 1.0 };
        var noSpot = DnaEntropyGraph.Core.Cost.PricingTable.Parse("""
            {"schema":1,"asOf":"2026-10-01","currency":"USD","region":"us-central1","source":"test","disk":{"usdPerGbMonth":0.073},
             "machines":{"g2-standard-8":{"accelerator":"L4","onDemandUsdPerHour":1.0}}}
            """);
        var pricing = Substitute.For<DnaEntropyGraph.Core.Cost.IPricingSource>();
        pricing.Load().Returns(noSpot);
        var vm = new HistoryViewModel(_repository, _remover, _navigator, _cloud, _local, _engine, _dialogs, _toasts, _strings, _launcher, _account, new UtcClock(), pricing: pricing, costs: _costs);

        await vm.RefreshCommand.ExecuteAsync(null);

        Item(vm, "spot").CostText.ShouldBe("about $1.00");
    }

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

    [Theory]
    [InlineData("NC_045512.2", "c3")]
    [InlineData("nc_045512", "c3")]
    [InlineData("045512.2", "c3")]
    [InlineData("100-500", "c3")]
    [InlineData("MN908947", "d4")]
    public async Task Text_search_finds_an_accession_run_by_its_accession_or_range(string text, string expected)
    {
        // Issue #684: an accession run has no input file, so the file-name match alone could never find it.
        var vm = await Loaded(
            Row("c3", options: """{"Accession":"NC_045512.2","AccessionRange":"100-500","ModelId":"m","RunTarget":"cloud"}"""),
            Row("d4", options: """{"Accession":"MN908947.3","ModelId":"m","RunTarget":"cloud"}"""),
            Row("e5", options: """{"InputPath":"C:\\data\\Lambda.gb","ModelId":"m","RunTarget":"cloud"}"""));

        vm.SearchText = text;

        vm.Groups.SelectMany(g => g.Items).Select(i => i.JobId).ShouldBe([expected]);
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

    // ---- Issue #929: a renamed-aside file is visible on EVERY path that recorded one, whatever the primary code is ----

    public static TheoryData<JobPhase, string> EveryPathThatRenamedAFileAside() => new()
    {
        { JobPhase.Completed, RunErrorCodes.ChangedFilesKeptAside },
        { JobPhase.Completed, RunErrorCodes.LifecycleUnverified },
        { JobPhase.PartiallyCompleted, RunErrorCodes.LifecycleUnverified },
        { JobPhase.Failed, RunErrorCodes.WorkerFailed },
        { JobPhase.Failed, RunErrorCodes.DownloadFailed },
        { JobPhase.Failed, RunErrorCodes.VmEndUnconfirmed },
    };

    [Theory]
    [MemberData(nameof(EveryPathThatRenamedAFileAside))]
    public async Task A_row_that_renamed_files_aside_shows_the_count_and_an_open_folder_button_whatever_its_code(JobPhase phase, string code)
    {
        _strings.GetString(RunsCopy.KeptAsideMany).Returns("{0} files were kept under new names.");
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        _launcher.OpenFolder(Arg.Any<string>()).Returns(true);
        var detail = KeptAsideDetail.Append("Some diagnostics.", 2);

        var vm = await Loaded(Row("aside", phase, outputDir: @"C:\out\aside") with { ErrorCode = code, ErrorDetail = detail });

        var item = Item(vm, "aside");
        item.HasKeptAside.ShouldBeTrue();
        item.KeptAsideText.ShouldBe("2 files were kept under new names.");
        item.ReasonText.ShouldBe(RunErrorCodes.ResourceKey(code), "the primary code stays the headline");
        item.OpenOutputFolderCommand.ShouldNotBeNull();
        await item.OpenOutputFolderCommand!.ExecuteAsync(null);
        _launcher.Received(1).OpenFolder(@"C:\out\aside");
    }

    [Fact]
    public async Task One_renamed_file_uses_the_singular_text_and_a_row_with_no_mark_shows_no_line_and_no_button()
    {
        _strings.GetString(RunsCopy.KeptAsideOne).Returns("one file kept");
        _strings.GetString(RunsCopy.KeptAsideMany).Returns("{0} files kept");
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);

        var vm = await Loaded(
            Row("one", JobPhase.Failed, outputDir: @"C:\out\one") with { ErrorCode = RunErrorCodes.WorkerFailed, ErrorDetail = KeptAsideDetail.Append("x", 1) },
            Row("none", JobPhase.Failed, outputDir: @"C:\out\none") with { ErrorCode = RunErrorCodes.WorkerFailed, ErrorDetail = "x" },
            Row("run", JobPhase.Running, outputDir: @"C:\out\run") with { ErrorDetail = KeptAsideDetail.Append("x", 1) });

        Item(vm, "one").KeptAsideText.ShouldBe("one file kept");
        Item(vm, "none").HasKeptAside.ShouldBeFalse();
        Item(vm, "none").OpenOutputFolderCommand.ShouldBeNull();
        Item(vm, "run").HasKeptAside.ShouldBeFalse("a run still going has not finished downloading");
    }

    [Fact]
    public async Task The_open_folder_button_on_a_row_whose_folder_has_gone_says_so_instead_of_doing_nothing()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(false);
        var vm = await Loaded(Row("gone", JobPhase.Failed, outputDir: @"C:\out\gone") with { ErrorCode = RunErrorCodes.WorkerFailed, ErrorDetail = KeptAsideDetail.Append("x", 1) });

        await Item(vm, "gone").OpenOutputFolderCommand!.ExecuteAsync(null);

        _launcher.DidNotReceive().OpenFolder(Arg.Any<string>());
        _toasts.Received(1).ShowToast("Runs_Open_Missing_Title", "Runs_Open_Missing_Body", ToastSeverity.Warning);
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
    public async Task An_imported_run_offers_no_local_delete_and_says_why_and_the_command_does_nothing()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        _strings.GetString(RunsCopy.DeleteLocalHintImported).Returns("imported-hint");
        var imported = Row("legacy-1", outputDir: @"C:\Users\me\Downloads\proto") with { Imported = true, Target = DnaEntropyGraph.Core.LocalEngine.RunTargets.LocalRecord, Bucket = null, JobPrefix = null };
        var vm = await Loaded(imported, Row("own", outputDir: @"C:\out\own"));

        Item(vm, "legacy-1").CanDeleteLocal.ShouldBeFalse();
        Item(vm, "legacy-1").DeleteLocalHint.ShouldBe("imported-hint");
        Item(vm, "own").CanDeleteLocal.ShouldBeTrue();
        Item(vm, "own").DeleteLocalHint.ShouldBeNull();

        await Item(vm, "legacy-1").DeleteLocalCommand.ExecuteAsync(null);
        _local.DidNotReceive().DeleteOutputFolder(Arg.Is<RunRecord>(r => r.JobId == "legacy-1"));
        await _dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_prototype_import_row_has_no_cloud_badge_and_the_earlier_version_hint()
    {
        // Built exactly as PrototypeImport builds it: Target local, no installation id, no bucket.
        var proto = new RunRecord("legacy-p", JobPhase.Completed, Now, "p", Target: DnaEntropyGraph.Core.LocalEngine.RunTargets.LocalRecord, OutputDir: @"C:\Users\me\Downloads\proto", FinishedAt: Now, Imported: true);
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        _strings.GetString(RunsCopy.DeleteLocalHintImported).Returns("imported-hint");
        _strings.GetString(RunsCopy.ImportedBadge).Returns("badge {0}");
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(false); // as the real RunCloudResults answers for any Imported row
        var vm = await Loaded(proto);

        Item(vm, "legacy-p").IsImported.ShouldBeFalse();
        Item(vm, "legacy-p").ImportedText.ShouldBeNull();
        Item(vm, "legacy-p").HasImportedText.ShouldBeFalse();
        Item(vm, "legacy-p").DeleteLocalHint.ShouldBe("imported-hint");
        Item(vm, "legacy-p").CanDeleteLocal.ShouldBeFalse();
        Item(vm, "legacy-p").DeleteCloudHint.ShouldBe(RunsCopy.DeleteCloudHintPrototype, "no cloud copy, and the named action (Open) exists; Run again is off for it");
        Item(vm, "legacy-p").CanDeleteCloud.ShouldBeFalse();
    }

    [Fact]
    public async Task A_cloud_found_row_has_the_cloud_badge_and_not_the_earlier_version_hint()
    {
        // Built exactly as RunImporter builds it: cloud target, installation id, bucket, Imported.
        var found = new RunRecord("20261014-aaaa", JobPhase.Completed, Now, "f", Bucket: "b", JobPrefix: "jobs/20261014-aaaa/", InstallationId: "inst-pc-a-long", Imported: true);
        _strings.GetString(RunsCopy.DeleteLocalHintImported).Returns("imported-hint");
        _strings.GetString(RunsCopy.ImportedBadge).Returns("badge {0}");
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(false); // as the real RunCloudResults answers for any Imported row
        var vm = await Loaded(found);

        Item(vm, "20261014-aaaa").IsImported.ShouldBeTrue();
        Item(vm, "20261014-aaaa").ImportedText.ShouldBe("badge inst-pc-");
        Item(vm, "20261014-aaaa").DeleteLocalHint.ShouldBeNull();
        Item(vm, "20261014-aaaa").DeleteCloudHint.ShouldBe(RunsCopy.DeleteCloudHintImported);
    }

    [Fact]
    public async Task An_imported_run_offers_no_redownload_or_cloud_delete_and_run_again_says_it_has_no_options_and_starts_nothing()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        _cloud.IsAvailable(Arg.Any<RunRecord>()).Returns(false);
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(false);
        var imported = Row("legacy-1", outputDir: @"C:\Users\me\Downloads\proto") with { Imported = true, Target = DnaEntropyGraph.Core.LocalEngine.RunTargets.LocalRecord, Bucket = null, JobPrefix = null };
        var vm = await Loaded(imported);

        Item(vm, "legacy-1").CanRedownload.ShouldBeFalse();
        Item(vm, "legacy-1").CanDeleteCloud.ShouldBeFalse();

        await Item(vm, "legacy-1").RerunCommand.ExecuteAsync(null);
        await _engine.DidNotReceiveWithAnyArgs().StartRunAsync(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Rerun_NoOptions_Title", "Runs_Rerun_NoOptions_Body", ToastSeverity.Warning);
    }

    [Fact]
    public async Task An_imported_run_that_somehow_has_options_but_no_input_file_says_so_and_starts_nothing()
    {
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(true);
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns((string?)null);
        var options = DnaEntropyGraph.Core.Runs.RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" });
        var imported = Row("legacy-2", outputDir: @"C:\Users\me\Downloads\proto", options: options) with { Imported = true };
        var vm = await Loaded(imported);

        await Item(vm, "legacy-2").RerunCommand.ExecuteAsync(null);

        await _engine.DidNotReceiveWithAnyArgs().StartRunAsync(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Rerun_NoInput_Title", "Runs_Rerun_NoInput_Body", ToastSeverity.Warning);
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

    /// <summary>Issue #914: whatever a Delete cloud copy hint tells the user to choose must be a control that is on for that row.</summary>
    [Fact]
    public async Task Every_delete_cloud_hint_names_an_action_that_is_enabled_for_that_row()
    {
        // Answers as the real RunCloudResults does: an imported row can never be deleted from here, but its cloud copy is "available".
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(ci => ci.Arg<RunRecord>() is { Imported: false, CloudResultsDeleted: false, Bucket: not null });
        _cloud.IsAvailable(Arg.Any<RunRecord>()).Returns(ci => ci.Arg<RunRecord>() is { CloudResultsDeleted: false, Bucket: not null });
        _local.OutputFolderExists(Arg.Any<RunRecord>()).Returns(ci => ci.Arg<RunRecord>().OutputDir is not null);
        var gone = Row("gone") with { CloudResultsDeleted = true };
        var failedGone = Row("failed-gone", JobPhase.Failed) with { CloudResultsDeleted = true };
        var cloudLinked = Row("cloud-linked") with { Imported = true, InstallationId = "inst-other" };
        var cloudNoLink = Row("cloud-nolink") with { Imported = true, InstallationId = "inst-other", JobPrefix = null };
        var protoFolder = Row("legacy-1", outputDir: @"C:\Users\me\Downloads\p1") with { Imported = true, Target = DnaEntropyGraph.Core.LocalEngine.RunTargets.LocalRecord, Bucket = null, JobPrefix = null };
        var protoNoFolder = Row("legacy-2") with { Imported = true, Target = DnaEntropyGraph.Core.LocalEngine.RunTargets.LocalRecord, Bucket = null, JobPrefix = null };
        var vm = await Loaded(gone, failedGone, cloudLinked, cloudNoLink, protoFolder, protoNoFolder);

        var checkedKinds = new List<string>();
        foreach (var item in vm.Groups.SelectMany(g => g.Items))
        {
            item.CanDeleteCloud.ShouldBeFalse(item.JobId);
            var named = item.DeleteCloudHint switch
            {
                RunsCopy.DeleteCloudHintNoCopy => item.RerunCommand.CanExecute(null),
                RunsCopy.DeleteCloudHintImported => item.ShowStoragePage && item.OpenStoragePageCommand!.CanExecute(null),
                RunsCopy.DeleteCloudHintImportedNoLink => item.CanRemove,
                RunsCopy.DeleteCloudHintPrototype => item.HasLocalFiles && item.OpenCommand.CanExecute(null),
                RunsCopy.DeleteCloudHintPrototypeNoFolder => item.CanRemove,
                _ => false,
            };
            named.ShouldBeTrue($"{item.JobId}: hint {item.DeleteCloudHint} names an action that is off for this row");
            checkedKinds.Add($"{item.JobId}={item.DeleteCloudHint}");
        }

        checkedKinds.Order().ShouldBe(
        [
            "cloud-linked=Runs_DeleteCloud_Hint_Imported",
            "cloud-nolink=Runs_DeleteCloud_Hint_ImportedNoLink",
            "failed-gone=Runs_DeleteCloud_Hint_NoCopy",
            "gone=Runs_DeleteCloud_Hint_NoCopy",
            "legacy-1=Runs_DeleteCloud_Hint_Prototype",
            "legacy-2=Runs_DeleteCloud_Hint_PrototypeNoFolder",
        ]);
    }

    [Fact]
    public async Task A_row_found_in_the_bucket_with_a_storage_link_offers_the_storage_page_button_at_once()
    {
        _cloud.CanDelete(Arg.Any<RunRecord>()).Returns(false);
        var vm = await Loaded(Row("cloud-linked") with { Imported = true }, Row("own"));

        Item(vm, "cloud-linked").ShowStoragePage.ShouldBeTrue();
        Item(vm, "own").ShowStoragePage.ShouldBeFalse();
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

        _toasts.DidNotReceive().ShowToast(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToastSeverity>());
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
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns<Task<CloudDeleteResult>>(_ => throw (Exception)Activator.CreateInstance(exception)!);
        var vm = await Loaded(Row("a"));

        await Item(vm, "a").RedownloadCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_Redownload_Failed_Title", "Runs_Redownload_Failed_Body", ToastSeverity.Error);

        await Item(vm, "a").DeleteCloudCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_DeleteCloud_FailedOther_Title", "Runs_DeleteCloud_FailedOther_Body", ToastSeverity.Error);
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
    public async Task Rerun_of_an_accession_run_starts_again_from_the_same_accession_with_no_input_file()
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", Accession = "NC_045512.2", AccessionRange = "100-500" };
        _engine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns("new-job");
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(recorded)));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RerunCommand).ExecuteAsync(null);

        await _engine.Received(1).StartRunAsync(Arg.Is<RunOptions>(o => o.Accession == "NC_045512.2" && o.AccessionRange == "100-500" && o.InputPath == null), Arg.Any<CancellationToken>());
        _navigator.Received(1).NavigateTo("RunProgress", "new-job");
    }

    [Fact]
    public async Task Rerun_of_a_paired_run_carries_the_gff3_the_app_kept()
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", InputPath = @"C:\gone\in.fasta", AnnotationPath = @"C:\gone\in.gff3" };
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\app\runs\a\input\in.fasta");
        _local.FindRerunAnnotation(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\app\runs\a\annotation\in.gff3");
        _engine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns("new-job");
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(recorded)));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RerunCommand).ExecuteAsync(null);

        await _engine.Received(1).StartRunAsync(
            recorded with { InputPath = @"C:\app\runs\a\input\in.fasta", AnnotationPath = @"C:\app\runs\a\annotation\in.gff3" },
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rerun_of_a_paired_run_whose_gff3_is_gone_does_not_start_without_it()
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", AnnotationPath = @"C:\gone\in.gff3" };
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\app\runs\a\input\in.fasta");
        _local.FindRerunAnnotation(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns((string?)null);
        var vm = await Loaded(Row("a", options: RunOptionsJson.Serialize(recorded)));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").RerunCommand).ExecuteAsync(null);

        await _engine.DidNotReceiveWithAnyArgs().StartRunAsync(default!, TestContext.Current.CancellationToken);
        _toasts.Received(1).ShowToast("Runs_Rerun_NoInput_Title", "Runs_Rerun_NoInput_Body", ToastSeverity.Warning);
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
    [InlineData(CloudResultsStatus.OutputFolderGone, "Runs_Redownload_FolderGone", ToastSeverity.Error)]
    [InlineData(CloudResultsStatus.OtherRunsFolderRefused, "Runs_Redownload_OtherRun", ToastSeverity.Error)]
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
    public async Task A_cloud_delete_that_stopped_partway_toasts_the_partial_copy_and_not_done()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, 3));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_Partial_Body", ToastSeverity.Warning);
        _toasts.DidNotReceive().ShowToast("Runs_DeleteCloud_Done_Title", Arg.Any<string>(), Arg.Any<ToastSeverity>());
    }

    [Fact]
    public async Task A_partial_cloud_delete_with_an_unknown_count_says_so_without_a_number()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, null));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        // The count is unknown, so the title must not assert "partly deleted" either: it is the "may be" one.
        _toasts.Received(1).ShowToast("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialUnknown_Body", ToastSeverity.Warning);
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission, "Runs_DeleteCloud_PartialKnown_Body_Permission")]
    [InlineData(CloudErrorKind.OrgPolicy, "Runs_DeleteCloud_PartialKnown_Body_OrgPolicy")]
    [InlineData(CloudErrorKind.Billing, "Runs_DeleteCloud_PartialKnown_Body_Billing")]
    [InlineData(CloudErrorKind.ApiDisabled, "Runs_DeleteCloud_PartialKnown_Body_Api")]
    [InlineData(CloudErrorKind.Quota, "Runs_DeleteCloud_PartialKnown_Body_Quota")]
    public async Task A_partial_delete_with_a_known_count_and_a_cause_with_its_own_action_states_the_count_and_that_action(CloudErrorKind kind, string body)
    {
        // "Choose Delete cloud copy again" alone will not help a refused permission, and a known count must not be dropped for the unknown-count copy ("may").
        _strings.GetString(body).Returns("left:{0}");
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, 3, kind));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Partial_Title", "left:3", ToastSeverity.Warning);
        _toasts.DidNotReceive().ShowToast(Arg.Any<string>(), "Runs_DeleteCloud_Partial_Body", Arg.Any<ToastSeverity>());
        _toasts.DidNotReceive().ShowToast("Runs_DeleteCloud_PartialMaybe_Title", Arg.Any<string>(), Arg.Any<ToastSeverity>());
    }

    [Fact]
    public async Task A_partial_delete_with_one_file_left_and_a_cause_with_its_own_action_still_states_the_count()
    {
        _strings.GetString("Runs_DeleteCloud_PartialKnown_Body_Permission").Returns("left:{0}");
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, 1, CloudErrorKind.Permission));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Partial_Title", "left:1", ToastSeverity.Warning);
    }

    [Fact]
    public async Task A_delete_that_finished_but_could_not_be_confirmed_empty_says_so_and_does_not_claim_it_stopped()
    {
        // Truth table cell: sweep completed = yes, count known = no, cause = none.
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, null, null, SweepCompleted: true));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_PartialUnconfirmed_Title", "Runs_DeleteCloud_PartialUnconfirmed_Body", ToastSeverity.Warning);
        _toasts.DidNotReceive().ShowToast(Arg.Any<string>(), "Runs_DeleteCloud_PartialUnknown_Body", Arg.Any<ToastSeverity>());
    }

    [Theory]
    [InlineData(CloudErrorKind.Network)]
    [InlineData(CloudErrorKind.Other)]
    public async Task A_partial_delete_with_a_known_count_and_a_retryable_cause_keeps_the_count_copy(CloudErrorKind kind)
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, 3, kind));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_Partial_Body", ToastSeverity.Warning);
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission, "Runs_DeleteCloud_PartialMaybe_Body_Permission")]
    [InlineData(CloudErrorKind.OrgPolicy, "Runs_DeleteCloud_PartialMaybe_Body_OrgPolicy")]
    [InlineData(CloudErrorKind.Billing, "Runs_DeleteCloud_PartialMaybe_Body_Billing")]
    [InlineData(CloudErrorKind.ApiDisabled, "Runs_DeleteCloud_PartialMaybe_Body_Api")]
    [InlineData(CloudErrorKind.Quota, "Runs_DeleteCloud_PartialMaybe_Body_Quota")]
    [InlineData(CloudErrorKind.Network, "Runs_DeleteCloud_PartialMaybe_Body_Network")]
    [InlineData(CloudErrorKind.Other, "Runs_DeleteCloud_PartialMaybe_Body_Other")]
    public async Task A_possibly_partial_cloud_delete_with_a_known_cause_says_some_files_may_be_gone_and_names_that_causes_action(CloudErrorKind kind, string body)
    {
        // Issue #262 review round 2 (M): never "nothing was deleted" when the sweep may have removed some.
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, null, kind));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_PartialMaybe_Title", body, ToastSeverity.Warning);
        _toasts.DidNotReceive().ShowToast(Arg.Any<string>(), Arg.Is<string>(b => b.StartsWith("Runs_DeleteCloud_Failed", StringComparison.Ordinal)), Arg.Any<ToastSeverity>());
    }

    [Fact]
    public async Task A_partial_cloud_delete_with_one_file_left_uses_the_singular_copy()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, 1));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_Partial_Body_One", ToastSeverity.Warning);
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission, "Runs_DeleteCloud_FailedPermission_Title", "Runs_DeleteCloud_FailedPermission_Body")]
    [InlineData(CloudErrorKind.Billing, "Runs_DeleteCloud_FailedBilling_Title", "Runs_DeleteCloud_FailedBilling_Body")]
    [InlineData(CloudErrorKind.ApiDisabled, "Runs_DeleteCloud_FailedApi_Title", "Runs_DeleteCloud_FailedApi_Body")]
    [InlineData(CloudErrorKind.Network, "Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body")]
    [InlineData(CloudErrorKind.OrgPolicy, "Runs_DeleteCloud_FailedOrgPolicy_Title", "Runs_DeleteCloud_FailedOrgPolicy_Body")]
    [InlineData(CloudErrorKind.Quota, "Runs_DeleteCloud_FailedQuota_Title", "Runs_DeleteCloud_FailedQuota_Body")]
    [InlineData(CloudErrorKind.Other, "Runs_DeleteCloud_FailedOther_Title", "Runs_DeleteCloud_FailedOther_Body")]
    public async Task A_classified_cloud_delete_failure_toasts_its_own_action(CloudErrorKind kind, string title, string body)
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(CloudResultsStatus.Failed, null, kind));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast(title, body, ToastSeverity.Error);
    }

    [Theory]
    [InlineData(CloudResultsStatus.Failed, null, "Runs_DeleteCloud_ListingStuckNothing_Body")]
    [InlineData(CloudResultsStatus.PartiallyDeleted, null, "Runs_DeleteCloud_ListingStuck_Body")]
    [InlineData(CloudResultsStatus.PartiallyDeleted, 3, "Runs_DeleteCloud_ListingStuck_Body")]
    public async Task A_cloud_delete_whose_listing_never_finished_toasts_the_stuck_copy_not_a_try_again_one(CloudResultsStatus status, int? left, string body)
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new CloudDeleteResult(status, left, CloudErrorKind.Other, ListingStuck: true));
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_ListingStuck_Title", body, ToastSeverity.Warning);
    }

    [Fact]
    public void Delete_cloud_failure_copy_without_a_known_kind_is_the_general_other_copy_not_the_network_one()
    {
        RunsCopy.DeleteCloudFailed(null).Body.ShouldBe("Runs_DeleteCloud_FailedOther_Body");
        RunsCopy.DeleteCloudFailed(CloudErrorKind.OrgPolicy).Body.ShouldBe("Runs_DeleteCloud_FailedOrgPolicy_Body");
        RunsCopy.DeleteCloudFailed(CloudErrorKind.Network).Body.ShouldBe("Runs_DeleteCloud_Failed_Body");
    }

    [Fact]
    public async Task A_cloud_delete_of_another_computers_run_toasts_the_other_computer_copy_and_offers_the_storage_page()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.OtherComputer);
        var vm = await Loaded(Row("a"));
        Item(vm, "a").ShowStoragePage.ShouldBeFalse();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_OtherComputer_Title", "Runs_DeleteCloud_OtherComputer_Body", ToastSeverity.Warning);
        Item(vm, "a").ShowStoragePage.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cloud_delete_that_could_not_confirm_ownership_toasts_its_own_copy()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.NotConfirmed);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_NotConfirmed_Title", "Runs_DeleteCloud_NotConfirmed_Body", ToastSeverity.Warning);
        Item(vm, "a").ShowStoragePage.ShouldBeTrue();
    }

    [Fact]
    public async Task Open_storage_page_opens_this_runs_own_folder_on_googles_storage_page()
    {
        _launcher.OpenUrl(Arg.Any<string>()).Returns(true);
        var vm = await Loaded(Row("a"));

        Item(vm, "a").OpenStoragePageCommand!.Execute(null);

        _launcher.Received(1).OpenUrl("https://console.cloud.google.com/storage/browser/b/jobs/a/");
    }

    [Theory]
    [InlineData("jobs/../x/")]
    [InlineData("jobs/a/b/")]
    [InlineData("jobs/a?x=1/")]
    [InlineData("jobs/a")]
    public async Task A_row_whose_stored_folder_is_not_a_plain_job_folder_gets_no_storage_button_and_no_link(string prefix)
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.OtherComputer);
        var vm = await Loaded(Row("a") with { JobPrefix = prefix });

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);
        Item(vm, "a").OpenStoragePageCommand!.Execute(null);

        Item(vm, "a").ShowStoragePage.ShouldBeFalse();
        _launcher.DidNotReceiveWithAnyArgs().OpenUrl(default!);
    }

    [Fact]
    public async Task A_cloud_delete_in_a_missing_bucket_toasts_storage_not_found_without_the_storage_button()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.StorageNotFound);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_StorageNotFound_Title", "Runs_DeleteCloud_StorageNotFound_Body", ToastSeverity.Warning);
        Item(vm, "a").ShowStoragePage.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_cloud_delete_toasts_the_failure_copy()
    {
        _cloud.DeleteAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(CloudResultsStatus.Failed);
        var vm = await Loaded(Row("a"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Item(vm, "a").DeleteCloudCommand).ExecuteAsync(null);

        _toasts.Received(1).ShowToast("Runs_DeleteCloud_FailedOther_Title", "Runs_DeleteCloud_FailedOther_Body", ToastSeverity.Error);
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

    [Fact]
    public async Task A_run_that_failed_for_actAs_offers_a_copy_request_naming_the_role_the_worker_email_and_the_project()
    {
        _strings.GetString(RunsCopy.OwnerRequestActAs).Returns("{0}|{1}|{2}|{3}");
        _strings.GetString(RunsCopy.OwnerRequestGrantee).Returns("<{0}>");
        _account.CurrentAccount.Returns(new AccountInfo("sub", "ann@lab.edu"));
        _launcher.CopyText(Arg.Any<string>()).Returns(true);
        var vm = await Loaded(Row("denied", JobPhase.Failed) with { ErrorCode = RunErrorCodes.PermissionActAs, ProjectId = "my-lab" });
        var item = Item(vm, "denied");

        item.HasOwnerRequest.ShouldBeTrue();
        item.CopyOwnerRequestCommand!.Execute(null);

        _launcher.Received(1).CopyText("roles/iam.serviceAccountUser|dna-entropy-worker@my-lab.iam.gserviceaccount.com|my-lab|<ann@lab.edu>");
        _toasts.Received(1).ShowToast(RunsCopy.OwnerRequestCopied.Title, RunsCopy.OwnerRequestCopied.Body, ToastSeverity.Success);
    }

    [Fact]
    public async Task A_run_whose_actAs_refusal_ended_with_an_unconfirmed_VM_still_offers_the_copy_request()
    {
        // Issue #665.
        var vm = await Loaded(Row("both", JobPhase.Failed) with
        {
            ErrorCode = RunErrorCodes.VmEndUnconfirmed,
            ProjectId = "my-lab",
            ErrorDetail = "refused [vm-account=dna-entropy-worker@my-lab.iam.gserviceaccount.com] Original code: permission_actas. (VM end not confirmed)",
        });

        Item(vm, "both").HasOwnerRequest.ShouldBeTrue();
    }

    [Fact]
    public async Task The_request_names_the_account_the_row_says_the_VM_ran_as_and_leaves_the_grantee_out_when_nobody_is_signed_in()
    {
        _strings.GetString(RunsCopy.OwnerRequestActAs).Returns("{0}|{1}|{2}|{3}");
        _launcher.CopyText(Arg.Any<string>()).Returns(true);
        _account.CurrentAccount.Returns((AccountInfo?)null);
        var vm = await Loaded(Row("denied", JobPhase.Failed) with
        {
            ErrorCode = RunErrorCodes.PermissionActAs,
            ProjectId = "my-lab",
            ErrorDetail = "refused [vm-account=123456789012-compute@developer.gserviceaccount.com]",
        });

        Item(vm, "denied").CopyOwnerRequestCommand!.Execute(null);

        _launcher.Received(1).CopyText("roles/iam.serviceAccountUser|123456789012-compute@developer.gserviceaccount.com|my-lab|");
    }

    [Fact]
    public async Task A_failed_clipboard_says_so_and_names_an_action()
    {
        _launcher.CopyText(Arg.Any<string>()).Returns(false);
        var vm = await Loaded(Row("denied", JobPhase.Failed) with { ErrorCode = RunErrorCodes.PermissionActAs, ProjectId = "my-lab" });

        Item(vm, "denied").CopyOwnerRequestCommand!.Execute(null);

        _toasts.Received(1).ShowToast(RunsCopy.OwnerRequestCopyFailed.Title, RunsCopy.OwnerRequestCopyFailed.Body, ToastSeverity.Error);
    }

    [Theory]
    [InlineData("permission", "my-lab")]
    [InlineData(RunErrorCodes.PermissionActAs, null)]
    [InlineData(RunErrorCodes.WorkerDefaultAccountMissing, "my-lab")]
    public async Task Only_an_actAs_failure_with_a_known_project_offers_the_copy_request(string code, string? project)
    {
        var vm = await Loaded(Row("other", JobPhase.Failed) with { ErrorCode = code, ProjectId = project });

        Item(vm, "other").HasOwnerRequest.ShouldBeFalse();
        Item(vm, "other").CopyOwnerRequestCommand.ShouldBeNull();
    }

    [Fact]
    public async Task A_cancelled_run_with_a_cloud_copy_offers_Download_partial_results_and_the_other_runs_keep_Download_again()
    {
        var vm = await Loaded(Row("stopped", JobPhase.Cancelled), Row("done"));

        Item(vm, "stopped").CanRedownload.ShouldBeTrue();
        Item(vm, "stopped").RedownloadLabel.ShouldBe("Runs_RedownloadPartial_Label");
        Item(vm, "done").RedownloadLabel.ShouldBe("Runs_RedownloadAgain_Label");
    }

    [Fact]
    public async Task A_cancelled_run_with_no_cloud_copy_offers_nothing_to_download()
    {
        _cloud.IsAvailable(Arg.Any<RunRecord>()).Returns(false);

        var vm = await Loaded(Row("stopped", JobPhase.Cancelled));

        Item(vm, "stopped").CanRedownload.ShouldBeFalse();
    }

    [Fact]
    public async Task Partial_results_of_a_cancelled_run_are_reported_as_expected_not_as_a_warning_and_the_button_calls_the_service_for_that_run()
    {
        _cloud.RedownloadAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(new RedownloadResult(CloudResultsStatus.Partial, 0));
        var vm = await Loaded(Row("stopped", JobPhase.Cancelled), Row("done"));

        await Item(vm, "stopped").RedownloadCommand.ExecuteAsync(null);
        await _cloud.Received(1).RedownloadAsync(Arg.Is<RunRecord>(r => r.JobId == "stopped"), Arg.Any<CancellationToken>());
        _toasts.Received(1).ShowToast("Runs_Redownload_CancelledPartial_Title", "Runs_Redownload_CancelledPartial_Body", ToastSeverity.Info);

        await Item(vm, "done").RedownloadCommand.ExecuteAsync(null);
        _toasts.Received(1).ShowToast("Runs_Redownload_Partial_Title", "Runs_Redownload_Partial_Body", ToastSeverity.Warning);
    }

    // ---- Issue #92: the stockout card offers "wait for a graphics card" ----

    private static readonly string StockoutCode = RunErrorCodes.For(CloudErrorKind.Stockout);

    [Fact]
    public async Task A_run_that_failed_for_want_of_a_graphics_card_offers_waiting_and_choosing_it_starts_the_same_run_with_WaitForGpu_on()
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", InputPath = @"C:\gone\in.gb", OutputFolder = @"D:\lab" };
        _local.FindRerunInput(Arg.Any<RunRecord>(), Arg.Any<RunOptions>()).Returns(@"C:\app\runs\a\input\in.gb");
        _engine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns("waiting-job");
        var vm = await Loaded(Row("a", JobPhase.Failed, options: RunOptionsJson.Serialize(recorded)) with { ErrorCode = StockoutCode });

        var item = Item(vm, "a");
        item.HasWaitForGpu.ShouldBeTrue();
        await item.WaitForGpuCommand!.ExecuteAsync(null);

        await _engine.Received(1).StartRunAsync(recorded with { InputPath = @"C:\app\runs\a\input\in.gb", WaitForGpu = true }, Arg.Any<CancellationToken>());
        _navigator.Received(1).NavigateTo("RunProgress", "waiting-job");
    }

    [Theory]
    [InlineData("quota")]
    [InlineData("billing")]
    [InlineData("vm_unhealthy")]
    public async Task A_run_that_failed_for_any_other_reason_offers_no_waiting(string code)
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud" };
        var vm = await Loaded(Row("a", JobPhase.Failed, options: RunOptionsJson.Serialize(recorded)) with { ErrorCode = code });

        Item(vm, "a").HasWaitForGpu.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_that_already_waited_and_still_got_no_card_offers_no_second_wait_and_says_so()
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", WaitForGpu = true };
        var vm = await Loaded(Row("a", JobPhase.Failed, options: RunOptionsJson.Serialize(recorded)) with { ErrorCode = StockoutCode });

        Item(vm, "a").HasWaitForGpu.ShouldBeFalse();
        Item(vm, "a").ReasonText.ShouldBe("RunError_stockout_waited");
    }

    [Fact]
    public async Task A_stockout_run_that_did_not_wait_keeps_the_ordinary_stockout_text()
    {
        var recorded = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud" };
        var vm = await Loaded(Row("a", JobPhase.Failed, options: RunOptionsJson.Serialize(recorded)) with { ErrorCode = StockoutCode });

        Item(vm, "a").ReasonText.ShouldBe("RunError_stockout");
    }

    [Fact]
    public async Task A_completed_run_and_a_stockout_run_with_unreadable_options_offer_no_waiting()
    {
        var vm = await Loaded(Row("done", JobPhase.Completed), Row("bad", JobPhase.Failed, options: "garbage") with { ErrorCode = StockoutCode });

        Item(vm, "done").HasWaitForGpu.ShouldBeFalse();
        Item(vm, "bad").HasWaitForGpu.ShouldBeFalse();
    }
}
