using System.Text;
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

/// <summary>Issue #111: the Runs page's "Find runs from other computers" action, through the real <see cref="RunImporter"/>.</summary>
public sealed class HistoryViewModelImportTests
{
    private const string Bucket = "deg-proj";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);

    private sealed class UtcClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly IStorageGateway _storage = Substitute.For<IStorageGateway>();
    private readonly IRunCloudResults _cloud;
    private readonly Dictionary<string, string> _settings = [];
    private readonly ISettingsStore _settingsStore = Substitute.For<ISettingsStore>();
    private readonly IRunHistoryRemover _remover = Substitute.For<IRunHistoryRemover>();
    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly Dictionary<string, string> _objects = [];
    private readonly List<RunRecord> _rows = [];

    public HistoryViewModelImportTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => _rows.ToList());
        _repository.UpsertAsync(Arg.Any<RunRecord>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _rows.RemoveAll(r => r.JobId == ci.Arg<RunRecord>().JobId);
            _rows.Add(ci.Arg<RunRecord>());
            return Task.CompletedTask;
        });
        _account.SelectedProjectId.Returns("proj");
        _account.SelectedScope().Returns(TestScopes.Of("proj"));
        var deleter = Substitute.For<IJobObjectDeleter>();
        deleter.IsAvailable.Returns(true);
        _cloud = new RunCloudResults(_storage, deleter, _repository, () => Path.GetTempPath(), new UtcClock());
        _settingsStore.GetString(Arg.Any<string>()).Returns(ci => _settings.GetValueOrDefault(ci.Arg<string>()));
        _settingsStore.When(x => x.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(ci => _settings[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1));
        _remover.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _rows.RemoveAll(r => r.JobId == ci.Arg<string>());
            return Task.CompletedTask;
        });
        _storage.ListBucketsAsync(new CloudScope("proj"), Arg.Any<CancellationToken>()).Returns([new BucketDescriptor(Bucket, "US", new Dictionary<string, string>(), null)]);
        _storage.ListChildPrefixesAsync(Arg.Any<CloudScope>(), Bucket, "jobs/", Arg.Any<CancellationToken>()).Returns(_ => _objects.Keys.Select(k => string.Join('/', k.Split('/').Take(2)) + "/").Distinct().ToList());
        _storage.TryDownloadAsync(Arg.Any<CloudScope>(), Bucket, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Task.FromResult<Stream?>(_objects.TryGetValue(ci.ArgAt<string>(2), out var text) ? new MemoryStream(Encoding.UTF8.GetBytes(text)) : null));
    }

    private void PutForeignRun(string id, string installation)
    {
        _objects[$"jobs/{id}/manifest.json"] = $$$"""{"schema":1,"jobId":"{{{id}}}","createdAt":"2026-10-14T08:30:00Z","createdBy":{"installationId":"{{{installation}}}","appVersion":"0.1.0"},"worker":{"version":"0.1.0"},"inputs":[{"id":"in1","name":"sample-{{{id}}}","path":"inputs/a.fa"}]}""";
        _objects[$"jobs/{id}/result.json"] = $$$"""{"schema":1,"status":"done","inputs":[{"id":"in1","status":"done","files":[{"path":"output/a.bedgraph","sha256":null,"bytes":1}]}]}""";
    }

    private HistoryViewModel Make(IRunImporter? importer = null) => new(
        _repository, _remover, Substitute.For<INavigator>(), _cloud, Substitute.For<ILocalRunFiles>(), Substitute.For<IJobEngine>(),
        Confirming(), Substitute.For<IToastService>(), _strings, Substitute.For<IShellLauncher>(), _account, new UtcClock(),
        importer: importer ?? new RunImporter(_storage, _repository, new ImportedRunDismissals(_settingsStore)),
        dismissals: new ImportedRunDismissals(_settingsStore));

    private static IDialogService Confirming()
    {
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(true);
        return dialogs;
    }

    private static List<RunListItem> Items(HistoryViewModel vm) => [.. vm.Groups.SelectMany(g => g.Items)];

    [Fact]
    public async Task Finding_runs_from_other_computers_adds_two_read_only_rows_to_the_list()
    {
        PutForeignRun("20261014-aaaa", "inst-pc-a");
        PutForeignRun("20261014-bbbb", "inst-pc-b");
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.IsEmpty.ShouldBeTrue();

        await vm.ImportRunsCommand.ExecuteAsync(null);

        var items = Items(vm);
        items.Select(i => i.JobId).OrderBy(x => x).ShouldBe(["20261014-aaaa", "20261014-bbbb"]);
        items.ShouldAllBe(i => i.IsImported && !i.CanDeleteCloud && i.CanRedownload && !i.RerunCommand.CanExecute(null));
        items.ShouldAllBe(i => i.ImportedText == "Runs_Imported_Badge");
        vm.ImportStatusText.ShouldBe("Runs_Import_Done_Many");
        vm.IsImporting.ShouldBeFalse();
    }

    [Fact]
    public async Task One_found_run_and_none_found_have_their_own_status_lines()
    {
        var vm = Make();
        await vm.ImportRunsCommand.ExecuteAsync(null);
        vm.ImportStatusText.ShouldBe("Runs_Import_None");

        PutForeignRun("20261014-aaaa", "inst-pc-a");
        await vm.ImportRunsCommand.ExecuteAsync(null);
        vm.ImportStatusText.ShouldBe("Runs_Import_Done_One");
    }

    [Fact]
    public async Task A_locally_known_run_is_not_flagged_imported()
    {
        _rows.Add(new RunRecord("20261014-aaaa", JobPhase.Completed, Now.AddHours(-1), Bucket: Bucket, JobPrefix: "jobs/20261014-aaaa/"));
        PutForeignRun("20261014-aaaa", "inst-pc-a");
        var vm = Make();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.ImportRunsCommand.ExecuteAsync(null);

        Items(vm).Single().IsImported.ShouldBeFalse();
        vm.ImportStatusText.ShouldBe("Runs_Import_None");
    }

    [Fact]
    public async Task With_no_project_chosen_it_says_so_and_does_not_look()
    {
        _account.SelectedProjectId.Returns((string?)null);
        _account.SelectedScope().Returns(TestScopes.Of(null));
        var importer = Substitute.For<IRunImporter>();
        var vm = Make(importer);

        await vm.ImportRunsCommand.ExecuteAsync(null);

        vm.ImportStatusText.ShouldBe("Runs_Import_NoProject");
        await importer.DidNotReceiveWithAnyArgs().ImportAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_failed_scan_names_what_to_do_and_leaves_the_list_alone()
    {
        _storage.ListBucketsAsync(new CloudScope("proj"), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<BucketDescriptor>>(_ => throw new IOException("offline"));
        var vm = Make();

        await vm.ImportRunsCommand.ExecuteAsync(null);

        vm.ImportStatusText.ShouldBe("Runs_Import_Failed");
        vm.IsImporting.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_removed_from_the_list_does_not_come_back_on_the_next_scan()
    {
        PutForeignRun("20261014-aaaa", "inst-pc-a");
        var vm = Make();
        await vm.ImportRunsCommand.ExecuteAsync(null);
        Items(vm).Count.ShouldBe(1);

        await Items(vm).Single().RemoveCommand.ExecuteAsync(null);
        Items(vm).ShouldBeEmpty();
        await vm.ImportRunsCommand.ExecuteAsync(null);

        Items(vm).ShouldBeEmpty();
        vm.ImportStatusText.ShouldBe("Runs_Import_None");
    }

    public static TheoryData<Exception, string> Failures => new()
    {
        { new CloudOperationException(new CloudError("PERMISSION_DENIED", 403, "denied"), CloudErrorKind.Permission), "Runs_Import_Failed_Permission" },
        { new CloudOperationException(new CloudError("ORG", 403, "org"), CloudErrorKind.OrgPolicy), "Runs_Import_Failed_Permission" },
        { new CloudOperationException(new CloudError("BILLING", 403, "off"), CloudErrorKind.Billing), "Runs_Import_Failed_Setup" },
        { new CloudOperationException(new CloudError("API", 403, "off"), CloudErrorKind.ApiDisabled), "Runs_Import_Failed_Setup" },
        { new CloudOperationException(new CloudError("NET", null, "down"), CloudErrorKind.Network), "Runs_Import_Failed_Network" },
        { new AccountAuthException("expired"), "Runs_Import_Failed_SignIn" },
        { new InvalidOperationException("boom"), "Runs_Import_Failed" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_scan_shows_the_message_for_its_kind(Exception failure, string expectedKey)
    {
        var importer = Substitute.For<IRunImporter>();
        importer.ImportAsync(Arg.Any<DnaEntropyGraph.Core.Cloud.CloudScope>(), Arg.Any<CancellationToken>()).Returns<RunImportResult>(_ => throw failure);
        var vm = Make(importer);

        await vm.ImportRunsCommand.ExecuteAsync(null);

        vm.ImportStatusText.ShouldBe(expectedKey);
    }
}
