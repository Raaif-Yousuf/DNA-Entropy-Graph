using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #63: the New run page's state and behaviour (files, pills, paste, RNA offer, name template).</summary>
public sealed class NewRunViewModelTests : IDisposable
{
    private const string Dna = "ACGTACGTACGTACGT";
    private const string Rna = "ACGUACGUACGUACGU";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-newrun-" + Guid.NewGuid().ToString("N"));
    private readonly string _appData;
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly IJobEngine _jobEngine = Substitute.For<IJobEngine>();
    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly FakeStrings _strings = new();
    private readonly ICostEstimateService _estimates = Substitute.For<ICostEstimateService>();
    private readonly RecordingLog _log = new();
    private NewRunViewModel _viewModel;

    public NewRunViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _appData = Path.Combine(_dir, "appdata");
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        _viewModel = NewViewModel();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private NewRunViewModel NewViewModel(IPastedInputStore? store = null, Func<string, InputFormat, AmbiguityPolicy, bool, InputValidationResult>? validate = null, IInputFileSystem? files = null) => new(
        _picker,
        _jobEngine,
        _navigator,
        _strings,
        store ?? new LocalPastedInputStore(_appData),
        _estimates,
        new OutputSelectionStore(Substitute.For<ISettingsStore>()),
        _log,
        TestGpu.Plan(),
        validate,
        files);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static DroppedItems Drop(params string[] paths) => new(paths, 0, false);

    private async Task<InputPillItem> AddOne(string path)
    {
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        return _viewModel.Items.Last();
    }

    [Fact]
    public async Task Files_handed_over_by_a_second_launch_arrive_as_the_navigation_parameter_and_are_added()
    {
        // Issue #108: Open with from Explorer navigates here with the files; the page forwards the parameter to this command.
        var path = Write("opened.fasta", ">o\n" + Dna + "\n");

        await _viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(Drop(path));
        await _viewModel.ReceiveNavigationParameterCommand.ExecuteAsync("not a drop");
        await _viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(null);

        _viewModel.Items.Select(i => i.Path).ShouldBe([path]);
    }
    // ---- Start / Browse ----

    [Fact]
    public void Run_cannot_start_with_no_input()
        => _viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();

    [Fact]
    public async Task Browsing_adds_every_picked_file_and_selects_the_first()
    {
        var a = Write("a.fasta", ">a\n" + Dna + "\n");
        var b = Write("b.fasta", ">b\n" + Dna + "\n");
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([a, b]));

        await _viewModel.BrowseCommand.ExecuteAsync(null);

        _viewModel.Items.Select(i => i.Path).ShouldBe([a, b]);
        _viewModel.SelectedItem.ShouldBeSameAs(_viewModel.Items[0]);
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_picker_adds_nothing()
    {
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));

        await _viewModel.BrowseCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Starting_a_run_sends_the_selected_file_and_options_and_opens_run_progress()
    {
        var path = Write("SetTnpB.fasta", ">x\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        await _jobEngine.Received(1).StartRunAsync(
            Arg.Is<RunOptions>(o => o.InputPath == path && !o.TreatAsRna && o.ModelId == "evo2_7b"),
            Arg.Any<CancellationToken>());
        _navigator.Received(1).NavigateTo("RunProgress", "job-1");
    }

    // ---- Run name (issue #514) ----

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly DateTimeOffset NameClock = new(2026, 10, 29, 14, 5, 0, TimeSpan.Zero);

    private NewRunViewModel NamedViewModel() => new(
        _picker,
        _jobEngine,
        _navigator,
        _strings,
        new LocalPastedInputStore(_appData),
        _estimates,
        new OutputSelectionStore(Substitute.For<ISettingsStore>()),
        _log,
        TestGpu.Plan(),
        clock: new FixedClock(NameClock));

    [Fact]
    public async Task The_run_name_preview_follows_the_template_and_the_selected_file()
    {
        _viewModel = NamedViewModel();
        var a = Write("SetTnpB.fasta", ">x\n" + Dna + "\n");
        var b = Write("Other.fasta", ">y\n" + Dna + "\n");
        _viewModel.RunNamePreview.ShouldBe(string.Empty, "nothing selected, nothing to name");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(a, b));
        var changed = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _viewModel.NameTemplate.ShouldBe("{file}");
        _viewModel.RunNamePreview.ShouldBe("SetTnpB");
        _viewModel.NameTemplate = "{file}_{model}_{date}";

        changed.ShouldContain(nameof(NewRunViewModel.RunNamePreview));
        _viewModel.RunNamePreview.ShouldBe("SetTnpB_evo2_7b_2026-10-29");
        _viewModel.SelectedItem = _viewModel.Items[1];
        _viewModel.RunNamePreview.ShouldBe("Other_evo2_7b_2026-10-29");
    }

    [Fact]
    public async Task Starting_a_run_refreshes_the_preview_so_the_time_shown_is_the_time_taken()
    {
        _viewModel = NamedViewModel();
        var path = Write("SetTnpB.fasta", ">x\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        _viewModel.NameTemplate = "{file}_{time}";
        var raised = 0;
        var raisedWhenTheEngineWasCalled = -1;
        _viewModel.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(NewRunViewModel.RunNamePreview) ? 1 : 0;
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            raisedWhenTheEngineWasCalled = raised;
            return Task.FromResult("job-1");
        });

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        raisedWhenTheEngineWasCalled.ShouldBeGreaterThan(0, "the preview is raised before the options are built, so the page shows the time the run takes");
    }

    [Fact]
    public async Task Run_sends_the_typed_template_and_the_preview_is_the_name_the_runner_will_use()
    {
        _viewModel = NamedViewModel();
        var path = Write("SetTnpB.fasta", ">x\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        _viewModel.NameTemplate = "{file}_{model}";
        var preview = _viewModel.RunNamePreview;

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        var sent = (RunOptions)_jobEngine.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IJobEngine.StartRunAsync)).GetArguments()[0]!;
        sent.NameTemplate.ShouldBe("{file}_{model}");
        // The runner names the folder with this function over the staged copy, which keeps the file name.
        RunOutputFolders.RunFolderName(sent, new DnaEntropyGraph.Core.Contract.StagedInput(path, Path.GetFileName(path)), NameClock.DateTime).ShouldBe(preview);
        preview.ShouldBe("SetTnpB_evo2_7b");
    }

    [Fact]
    public async Task After_a_run_starts_the_page_is_empty_for_the_next_one()
    {
        // Issue #527: the ViewModel outlives the page, so a started run must leave a clean New run page behind.
        var path = Write("started.fasta", ">x\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        _viewModel.PasteText = "half typed";
        _viewModel.IsTreatingAsRna = true;

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.SelectedItem.ShouldBeNull();
        _viewModel.PasteText.ShouldBe(string.Empty);
        _viewModel.IsTreatingAsRna.ShouldBeFalse();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();
        File.Exists(path).ShouldBeTrue("the user's own file is never touched (Hard Rule 14)");
    }

    [Fact]
    public async Task After_a_run_starts_no_stale_status_line_or_open_paste_box_is_left_behind()
    {
        var path = Write("clean.fasta", ">x\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        _viewModel.IsPasteOpen = true;
        _viewModel.PasteText = Path.Combine(_dir, "no-such-file.fasta");
        await _viewModel.AddPastedCommand.ExecuteAsync(null);
        _viewModel.HasStatusMessage.ShouldBeTrue("the setup: a path-not-found line is showing");

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.ShouldBeEmpty();
        _viewModel.HasStatusMessage.ShouldBeFalse();
        _viewModel.IsPasteOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task A_navigation_parameter_is_taken_only_on_a_new_navigation_not_when_Back_returns_to_the_page()
    {
        // Issue #527: Back re-delivers the Open-with files the page was first opened with; they were already run, so they must not return.
        var path = Write("openwith.fasta", ">o\n" + Dna + "\n");

        await _viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(new NavigationArrival(Drop(path), IsNewNavigation: false));
        _viewModel.Items.ShouldBeEmpty();

        await _viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(new NavigationArrival(Drop(path), IsNewNavigation: true));
        _viewModel.Items.Select(i => i.Path).ShouldBe([path]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void Only_the_New_navigation_mode_counts_as_a_new_navigation(int mode, bool expectedNew)
        => NavigationArrival.From("p", mode).IsNewNavigation.ShouldBe(expectedNew);

    [Fact]
    public async Task Back_to_the_page_still_attaches_it_to_the_intake()
    {
        var intake = new NewRunIntake();
        var viewModel = new NewRunViewModel(_picker, _jobEngine, _navigator, _strings, new LocalPastedInputStore(_appData), _estimates, new OutputSelectionStore(Substitute.For<ISettingsStore>()), _log, TestGpu.Plan(), intake: intake);
        var path = Write("later.fasta", ">l\n" + Dna + "\n");

        await viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(new NavigationArrival(null, IsNewNavigation: false));

        intake.TryAdd(Drop(path)).ShouldBeTrue("a Back arrival is still 'this screen is showing'");
    }

    [Fact]
    public async Task A_start_that_fails_keeps_the_files_so_the_user_can_try_again()
    {
        var path = Write("again.fasta", ">x\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new InvalidOperationException("no"));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.Items.Select(i => i.Path).ShouldBe([path]);
        _viewModel.SelectedItem.ShouldNotBeNull();
    }

    [Fact]
    public async Task Leaving_and_coming_back_keeps_the_files_and_the_selection()
    {
        var a = Write("a.fasta", ">a\n" + Dna + "\n");
        var b = Write("b.fasta", ">b\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(a, b));
        _viewModel.SelectedItem = _viewModel.Items[1];

        _viewModel.DetachFromIntake();
        await _viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(null);

        _viewModel.Items.Select(i => i.Path).ShouldBe([a, b]);
        _viewModel.SelectedItem.ShouldBeSameAs(_viewModel.Items[1]);
    }

    [Fact]
    public async Task After_a_run_starts_the_other_pasted_copies_are_deleted_but_the_started_ones_stays_for_its_run()
    {
        // The started run's input is staged before StartRunAsync returns, but a retry restages from the recorded path, so that copy stays.
        _viewModel.PasteText = Dna;
        await _viewModel.AddPastedCommand.ExecuteAsync(null);
        var started = _viewModel.Items.Single();
        _viewModel.PasteText = Dna + "ACGT";
        await _viewModel.AddPastedCommand.ExecuteAsync(null);
        var other = _viewModel.Items.Last();
        _viewModel.SelectedItem = started;

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldBeEmpty();
        File.Exists(started.Path).ShouldBeTrue();
        File.Exists(other.Path).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_with_a_problem_cannot_be_run()
    {
        await AddOne(Write("bad.fasta", ">x\nACGTXACGTACGT\n"));

        _viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();
    }

    // ---- Pills ----

    [Fact]
    public async Task A_FASTA_pill_shows_kind_records_and_bases()
    {
        var pill = await AddOne(Write("two.fasta", ">a\n" + Dna + "\n>b\n" + Dna + "\n"));

        pill.IsValid.ShouldBeTrue();
        pill.DisplayName.ShouldBe("two.fasta");
        pill.KindText.ShouldBe("FASTA");
        pill.SummaryText.ShouldBe("2 records, 32 bases");
    }

    [Fact]
    public async Task A_GenBank_pill_also_shows_its_genes()
    {
        var gb = "LOCUS       T1                     24 bp    DNA     linear   UNK 01-JAN-1980\n"
            + "FEATURES             Location/Qualifiers\n     gene            1..12\n     gene            13..24\nORIGIN\n"
            + "        1 acgtacgtac gtacgtacgt acgt\n//\n";

        var pill = await AddOne(Write("t.gb", gb));

        pill.KindText.ShouldBe("GenBank");
        pill.SummaryText.ShouldBe("1 record, 24 bases, 2 genes");
    }

    [Fact]
    public async Task A_notice_about_repeated_ids_is_shown_as_copy_with_its_number()
    {
        var pill = await AddOne(Write("dup.fasta", ">a\n" + Dna + "\n>a\n" + Dna + "\n"));

        pill.HasNotices.ShouldBeTrue();
        pill.NoticesText.ShouldBe("NewRunNotice_RepeatedIds_One");
    }

    [Fact]
    public async Task A_notice_about_two_repeated_ids_uses_the_plural_copy()
    {
        var pill = await AddOne(Write("dup2.fasta", ">a\n" + Dna + "\n>a\n" + Dna + "\n>b\n" + Dna + "\n>b\n" + Dna + "\n"));

        pill.NoticesText.ShouldBe("NewRunNotice_RepeatedIds:2");
    }

    [Fact]
    public async Task A_gene_with_an_unreadable_position_is_a_valid_file_with_a_notice_and_one_gene_fewer()
    {
        // Issues #543 and #788: the worker leaves the gene out and runs the rest, so the pill must not refuse the file.
        var gb = "LOCUS       T1                     24 bp    DNA     linear   UNK 01-JAN-1980\n"
            + "FEATURES             Location/Qualifiers\n     gene            abc\n     gene            13..24\nORIGIN\n"
            + "        1 acgtacgtac gtacgtacgt acgt\n//\n";

        var pill = await AddOne(Write("bad-position.gb", gb));

        pill.IsValid.ShouldBeTrue();
        pill.HasNotices.ShouldBeTrue();
        pill.NoticesText.ShouldBe("NewRunNotice_GenBankFeaturesDropped_One");
        pill.SummaryText.ShouldBe("1 record, 24 bases, 1 gene");
    }

    [Fact]
    public async Task An_error_pill_names_the_problem_in_resw_copy_and_where_it_is()
    {
        var pill = await AddOne(Write("bad.fasta", ">x\nACGTXACGTACGT\n"));

        pill.IsValid.ShouldBeFalse();
        pill.HasError.ShouldBeTrue();
        pill.ErrorText.ShouldBe(RunErrorCodes.ResourceKey(RunErrorCodes.InputInvalidCharacter) + " (record 1, base 5)");
    }

    [Fact]
    public async Task A_missing_file_gets_an_error_pill_instead_of_being_dropped()
    {
        var pill = await AddOne(Path.Combine(_dir, "gone.fasta"));

        pill.HasError.ShouldBeTrue();
        pill.ErrorText.ShouldBe(RunErrorCodes.ResourceKey(RunErrorCodes.InputMissing));
    }

    [Fact]
    public async Task The_same_file_added_twice_is_one_pill()
    {
        var path = Write("a.fasta", ">a\n" + Dna + "\n");

        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));

        _viewModel.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_dropped_folder_contributes_its_sequence_files()
    {
        Write(Path.Combine("folder", "a.fasta"), ">a\n" + Dna + "\n");
        Write(Path.Combine("folder", "b.fa"), ">b\n" + Dna + "\n");
        Write(Path.Combine("folder", "notes.docx"), "not a sequence");

        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(Path.Combine(_dir, "folder")));

        _viewModel.Items.Select(i => i.DisplayName).ShouldBe(["a.fasta", "b.fa"]);
    }

    [Fact]
    public async Task A_dropped_folder_with_no_sequence_files_says_so()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));

        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(Path.Combine(_dir, "empty")));

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.StatusMessage.ShouldBe("NewRunStatusFolderEmpty:" + Path.Combine(_dir, "empty"));
    }

    [Fact]
    public async Task Adding_never_writes_next_to_the_users_file()
    {
        var path = Write(Path.Combine("lab", "a.fasta"), ">a\n" + Dna + "\n");
        var before = Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!);

        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));

        Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!).ShouldBe(before);
        File.ReadAllText(path).ShouldBe(">a\n" + Dna + "\n");
    }

    [Fact]
    public async Task Removing_a_pill_selects_a_neighbour_and_disables_run_when_none_is_left()
    {
        var a = await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        var b = await AddOne(Write("b.fasta", ">b\n" + Dna + "\n"));

        await _viewModel.RemoveItemCommand.ExecuteAsync(a);
        _viewModel.SelectedItem.ShouldBeSameAs(b);

        await _viewModel.RemoveItemCommand.ExecuteAsync(b);
        _viewModel.SelectedItem.ShouldBeNull();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_pill_with_a_problem_shows_no_counts()
    {
        var pill = await AddOne(Write("bad.fasta", ">x\nACGTXACGTACGT\n"));

        pill.SummaryText.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task The_list_clearing_its_selection_after_a_removal_does_not_leave_the_page_with_none_selected()
    {
        // MEASURED 2026-10-03 in the real app: after Remove, the ListView pushed a null SelectedItem back through
        // the TwoWay binding, so the neighbour was not selected and Run stayed disabled.
        var a = await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        var b = await AddOne(Write("b.fasta", ">b\n" + Dna + "\n"));
        var raised = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await _viewModel.RemoveItemCommand.ExecuteAsync(a);
        _viewModel.SelectedItem = null;

        _viewModel.SelectedItem.ShouldBeSameAs(b);
        raised.ShouldContain(nameof(NewRunViewModel.SelectedItem));
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void Nothing_selected_is_accepted_when_there_are_no_pills()
    {
        _viewModel.SelectedItem = null;

        _viewModel.SelectedItem.ShouldBeNull();
    }
    [Fact]
    public void Remove_cannot_run_with_nothing_selected()
        => _viewModel.RemoveItemCommand.CanExecute(null).ShouldBeFalse();

    // ---- RNA: the observable of the issue ----

    [Fact]
    public async Task A_file_with_a_U_shows_the_RNA_notice_on_its_pill_and_offers_Treat_as_RNA()
    {
        var pill = await AddOne(Write("rna.fasta", ">r\n" + Rna + "\n"));

        pill.IsValid.ShouldBeFalse();
        pill.NeedsRnaChoice.ShouldBeTrue();
        pill.ErrorText.ShouldStartWith("NewRunPillRnaNotice");
        _viewModel.OfferTreatAsRna.ShouldBeTrue();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Choosing_Treat_as_RNA_makes_the_file_runnable_and_the_run_carries_the_flag()
    {
        var pill = await AddOne(Write("rna.fasta", ">r\n" + Rna + "\n"));

        await _viewModel.TreatAsRnaCommand.ExecuteAsync(null);

        pill.IsValid.ShouldBeTrue();
        pill.NoticesText.ShouldBe("NewRunNotice_RnaConverted:4");
        _viewModel.OfferTreatAsRna.ShouldBeFalse();
        _viewModel.IsTreatingAsRna.ShouldBeTrue();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();

        await _viewModel.StartRunCommand.ExecuteAsync(null);
        await _jobEngine.Received(1).StartRunAsync(Arg.Is<RunOptions>(o => o.TreatAsRna), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Turning_RNA_treatment_off_checks_the_files_again()
    {
        var pill = await AddOne(Write("rna.fasta", ">r\n" + Rna + "\n"));
        await _viewModel.TreatAsRnaCommand.ExecuteAsync(null);

        await _viewModel.StopTreatingAsRnaCommand.ExecuteAsync(null);

        pill.NeedsRnaChoice.ShouldBeTrue();
        _viewModel.OfferTreatAsRna.ShouldBeTrue();
    }

    [Fact]
    public async Task No_offer_is_made_for_a_file_without_a_U()
    {
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));

        _viewModel.OfferTreatAsRna.ShouldBeFalse();
    }

    // ---- Paste ----

    [Fact]
    public void The_paste_counter_follows_the_text()
    {
        _viewModel.PasteCountText.ShouldBe("NewRunPasteCount:0");

        _viewModel.PasteText = ">h\nAC GT\n12 AC";

        _viewModel.PasteCountText.ShouldBe("NewRunPasteCount:6");
    }

    [Fact]
    public void Add_is_available_only_when_something_was_pasted()
    {
        _viewModel.AddPastedCommand.CanExecute(null).ShouldBeFalse();

        _viewModel.PasteText = "  \n ";
        _viewModel.AddPastedCommand.CanExecute(null).ShouldBeFalse();

        _viewModel.PasteText = Dna;
        _viewModel.AddPastedCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task A_pasted_sequence_becomes_a_pill_saved_under_app_data()
    {
        _viewModel.PasteText = Dna;

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        var pill = _viewModel.Items.ShouldHaveSingleItem();
        pill.IsPasted.ShouldBeTrue();
        pill.DisplayName.ShouldBe("NewRunPastedName");
        pill.IsValid.ShouldBeTrue();
        pill.Path.ShouldStartWith(Path.Combine(_appData, "pasted"));
        _viewModel.PasteText.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task A_pasted_sequence_with_a_U_gets_the_RNA_notice()
    {
        _viewModel.PasteText = Rna;

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.Items.Single().NeedsRnaChoice.ShouldBeTrue();
        _viewModel.OfferTreatAsRna.ShouldBeTrue();
    }

    [Fact]
    public async Task Pasting_the_path_of_an_existing_file_adds_that_file_not_a_copy_of_the_text()
    {
        var path = Write("a.fasta", ">a\n" + Dna + "\n");
        _viewModel.PasteText = "\"" + path + "\"";

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.Items.Single().Path.ShouldBe(path);
    }

    [Fact]
    public async Task Pasting_a_path_that_does_not_exist_says_so_and_adds_nothing()
    {
        var missing = Path.Combine(_dir, "nope.fasta");
        _viewModel.PasteText = missing;

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.StatusMessage.ShouldBe("NewRunStatusPathNotFound:" + missing);
    }

    // ---- Review of #63 ----

    /// <summary>A validator whose first call waits at a gate, so a later call can finish before it.</summary>
    private sealed class GatedValidator
    {
        private readonly ManualResetEventSlim _gate = new(false);
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new(false);

        public void Release() => _gate.Set();

        public InputValidationResult Validate(string path, InputFormat format, AmbiguityPolicy policy, bool treatAsRna)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                _gate.Wait(TimeSpan.FromSeconds(20));
            }

            return InputFileValidator.Validate(path, format, policy, treatAsRna);
        }
    }

    [Fact]
    public async Task An_older_validation_that_finishes_last_does_not_overwrite_a_newer_one()
    {
        var gated = new GatedValidator();
        _viewModel = NewViewModel(validate: gated.Validate);
        var path = Write("rna.fasta", ">r\n" + Rna + "\n");

        var adding = _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        gated.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        await _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        var pill = _viewModel.Items.Single();
        pill.IsValid.ShouldBeTrue();

        gated.Release();
        await adding;

        pill.IsValid.ShouldBeTrue();
        pill.NeedsRnaChoice.ShouldBeFalse();
        _viewModel.OfferTreatAsRna.ShouldBeFalse();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task A_check_that_is_cancelled_leaves_the_checking_state_with_one_action()
    {
        _viewModel = NewViewModel(validate: (_, _, _, _) => throw new OperationCanceledException());

        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(Write("a.fasta", ">a\n" + Dna + "\n")));

        var pill = _viewModel.Items.Single();
        pill.IsChecking.ShouldBeFalse();
        pill.IsValid.ShouldBeFalse();
        pill.ErrorText.ShouldBe("NewRunPillCheckStopped");
    }

    [Fact]
    public async Task A_second_drop_while_the_first_is_still_being_checked_is_not_lost()
    {
        var gated = new GatedValidator();
        _viewModel = NewViewModel(validate: gated.Validate);
        var first = Write("first.fasta", ">a\n" + Dna + "\n");
        var second = Write("second.fasta", ">b\n" + Dna + "\n");
        var third = Write("third.fasta", ">c\n" + Dna + "\n");

        // A command that disallows concurrent runs cancels the running one when a second drop arrives, which
        // would stop the checks still queued behind the first file of the first drop.
        _viewModel.AddDroppedCommand.Execute(new DroppedItems([first, second], 0, false));
        var firstDrop = _viewModel.AddDroppedCommand.ExecutionTask!;
        gated.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        _viewModel.AddDroppedCommand.Execute(new DroppedItems([third], 0, false));
        var secondDrop = _viewModel.AddDroppedCommand.ExecutionTask!;
        // MEASURED 2026-10-29 (#841, 3 crashes in about 125 runs of this class): the page owns Items on one UI thread, but here the two drops run on thread-pool threads, so the first drop adding
        // second.fasta while the second drop was still in its duplicate check made `Items.Any` throw "Collection was modified" and take the test host down. The third pill is on the page before
        // anything is released, so the only thread that touches Items afterwards is the first drop's.
        // Items.Count == 2 only proves the third pill was added; the second drop still enumerates Items afterwards (duplicate check, RNA offer, output choices). Only the first file is gated, so
        // the second drop can run to completion on its own; wait for that before the first drop is allowed to add second.fasta.
        await secondDrop.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        _viewModel.Items.Count.ShouldBe(2);
        gated.Release();
        await firstDrop;
        await secondDrop;

        _viewModel.Items.Select(i => i.DisplayName).ShouldBe(["first.fasta", "second.fasta", "third.fasta"], ignoreOrder: true);
        _viewModel.Items.ShouldAllBe(i => i.IsValid);
    }

    [Fact]
    public async Task A_drop_with_virtual_items_adds_the_real_files_and_tells_the_user_one_action()
    {
        var path = Write("a.fasta", ">a\n" + Dna + "\n");

        await _viewModel.AddDroppedCommand.ExecuteAsync(new DroppedItems([path], SkippedVirtual: 2, Failed: false));

        _viewModel.Items.Single().Path.ShouldBe(path);
        _viewModel.StatusMessage.ShouldBe("NewRunStatusDropVirtual");
    }

    [Fact]
    public async Task A_drop_that_could_not_be_read_says_so_and_adds_nothing()
    {
        await _viewModel.AddDroppedCommand.ExecuteAsync(new DroppedItems([], SkippedVirtual: 0, Failed: true));

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.StatusMessage.ShouldBe("NewRunStatusDropFailed");
    }

    [Fact]
    public async Task A_clean_drop_clears_the_status_line()
    {
        await _viewModel.AddDroppedCommand.ExecuteAsync(new DroppedItems([], SkippedVirtual: 1, Failed: false));
        await _viewModel.AddDroppedCommand.ExecuteAsync(new DroppedItems([Write("a.fasta", ">a\n" + Dna + "\n")], 0, false));

        _viewModel.StatusMessage.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task The_page_says_run_runs_only_the_selected_file_once_there_are_several()
    {
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        _viewModel.HasSeveralItems.ShouldBeFalse();

        await AddOne(Write("b.fasta", ">b\n" + Dna + "\n"));
        _viewModel.HasSeveralItems.ShouldBeTrue();

        await _viewModel.RemoveItemCommand.ExecuteAsync(_viewModel.Items[0]);
        _viewModel.HasSeveralItems.ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_file_shows_the_kind_its_extension_implies_or_none()
    {
        (await AddOne(Path.Combine(_dir, "gone.fasta"))).KindText.ShouldBe("FASTA");
        (await AddOne(Path.Combine(_dir, "gone.gb"))).KindText.ShouldBe("GenBank");
        (await AddOne(Path.Combine(_dir, "gone.xyz"))).KindText.ShouldBe(string.Empty);
    }

    private sealed class ThreadRecordingStore(string root) : IPastedInputStore
    {
        public int SaveThread { get; private set; }

        public string Save(string text)
        {
            SaveThread = Environment.CurrentManagedThreadId;
            return new LocalPastedInputStore(root).Save(text);
        }

        public int DeleteThread { get; private set; }

        public void Delete(string path)
        {
            DeleteThread = Environment.CurrentManagedThreadId;
            new LocalPastedInputStore(root).Delete(path);
        }
    }

    [Fact]
    public void Saving_a_paste_does_not_run_on_the_thread_that_called_the_command()
    {
        var store = new ThreadRecordingStore(_appData);
        _viewModel = NewViewModel(store);
        _viewModel.PasteText = Dna;
        var callerThread = 0;

        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            _viewModel.AddPastedCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        });
        caller.Start();
        caller.Join();

        store.SaveThread.ShouldNotBe(0);
        store.SaveThread.ShouldNotBe(callerThread);
    }

    [Fact]
    public async Task A_paste_that_fails_validation_keeps_the_text_leaves_no_pill_and_names_the_problem()
    {
        _viewModel.PasteText = "ACGTXACGTACGTAC";

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.PasteText.ShouldBe("ACGTXACGTACGTAC");
        _viewModel.Items.ShouldBeEmpty();
        _viewModel.StatusMessage.ShouldStartWith(RunErrorCodes.ResourceKey(RunErrorCodes.InputInvalidCharacter));
        Directory.GetFiles(Path.Combine(_appData, "pasted")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_paste_with_a_U_clears_the_box_and_keeps_its_pill_for_the_RNA_offer()
    {
        _viewModel.PasteText = Rna;

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.PasteText.ShouldBe(string.Empty);
        _viewModel.Items.Single().NeedsRnaChoice.ShouldBeTrue();
    }

    private sealed class FailingStore : IPastedInputStore
    {
        public string Save(string text) => throw new IOException("disk full");

        public void Delete(string path)
        {
        }
    }

    [Fact]
    public async Task A_paste_that_cannot_be_saved_keeps_the_text_and_says_so()
    {
        _viewModel = NewViewModel(new FailingStore());
        _viewModel.PasteText = Dna;

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.PasteText.ShouldBe(Dna);
        _viewModel.Items.ShouldBeEmpty();
        _viewModel.StatusMessage.ShouldBe("NewRunStatusPasteFailed");
    }

    [Fact]
    public async Task Removing_a_pasted_pill_deletes_its_saved_file_but_never_a_users_file()
    {
        _viewModel.PasteText = Dna;
        await _viewModel.AddPastedCommand.ExecuteAsync(null);
        var pasted = _viewModel.Items.Single();
        var users = await AddOne(Write("mine.fasta", ">a\n" + Dna + "\n"));

        await _viewModel.RemoveItemCommand.ExecuteAsync(pasted);
        await _viewModel.RemoveItemCommand.ExecuteAsync(users);

        File.Exists(pasted.Path).ShouldBeFalse();
        File.Exists(users.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task Notices_are_resw_copy_chosen_by_code_not_the_validators_english()
    {
        var pill = await AddOne(Write("rna.fasta", ">r\n" + Rna + "\n"));
        await _viewModel.TreatAsRnaCommand.ExecuteAsync(null);

        pill.NoticesText.ShouldBe("NewRunNotice_RnaConverted:4");
        pill.NoticesText.ShouldNotContain("U->T");
    }

    [Fact]
    public async Task The_notices_the_pill_summary_already_covers_are_not_repeated()
    {
        var pill = await AddOne(Write("two.fasta", ">a\n" + Dna + "\n>b\n" + Dna + "\n"));

        pill.HasNotices.ShouldBeFalse();
    }
    // ---- Round 2 of #63 ----

    /// <summary>Blocks the validator on one file until released, so a test decides when that check finishes.</summary>
    private sealed class BlockableValidator
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public volatile string? BlockSuffix;

        public ManualResetEventSlim Entered { get; } = new(false);

        public void Release() => _gate.Set();

        public InputValidationResult Validate(string path, InputFormat format, AmbiguityPolicy policy, bool treatAsRna)
        {
            if (BlockSuffix is { } suffix && path.EndsWith(suffix, StringComparison.Ordinal))
            {
                Entered.Set();
                _gate.Wait(TimeSpan.FromSeconds(20));
            }

            return InputFileValidator.Validate(path, format, policy, treatAsRna);
        }
    }

    [Fact]
    public async Task A_cancelled_Treat_as_RNA_stops_the_file_it_was_on_and_never_starts_a_check_on_the_next_one()
    {
        var blockable = new BlockableValidator();
        _viewModel = NewViewModel(validate: blockable.Validate);
        var a = await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        var b = await AddOne(Write("b.fasta", ">b\n" + Dna + "\n"));
        blockable.BlockSuffix = "a.fasta";

        var treating = _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        blockable.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        _viewModel.TreatAsRnaCommand.Cancel();
        try
        {
            await treating;
        }
        catch (OperationCanceledException)
        {
            // the command may report its own cancellation
        }

        // A cancelled check must never write state, and a cancelled command must not begin checks it cannot finish:
        // b keeps its result instead of showing "stopped".
        b.IsValid.ShouldBeTrue();
        b.ErrorText.ShouldBe(string.Empty);
        a.ErrorText.ShouldBe("NewRunPillCheckStopped");
        blockable.Release();
    }

    [Fact]
    public async Task A_cancelled_check_that_a_newer_one_replaced_leaves_the_newer_result()
    {
        var blockable = new BlockableValidator();
        _viewModel = NewViewModel(validate: blockable.Validate);
        var a = await AddOne(Write("a.fasta", ">a\n" + Rna + "\n"));
        a.NeedsRnaChoice.ShouldBeTrue();
        blockable.BlockSuffix = "a.fasta";

        var treating = _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        blockable.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        blockable.BlockSuffix = null;
        var stopping = _viewModel.StopTreatingAsRnaCommand.ExecuteAsync(null);
        await stopping;
        try
        {
            await treating;
        }
        catch (OperationCanceledException)
        {
            // the superseded command may report its own cancellation
        }

        a.ErrorText.ShouldNotBe("NewRunPillCheckStopped");
        a.NeedsRnaChoice.ShouldBeTrue();
        _viewModel.IsTreatingAsRna.ShouldBeFalse();
        blockable.Release();
    }

    [Fact]
    public async Task Run_follows_the_selected_pill_not_the_first_one()
    {
        var a = await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        var b = await AddOne(Write("b.fasta", ">b\n" + Dna + "\n"));
        var bad = await AddOne(Write("bad.fasta", ">x\nACGTXACGTACGT\n"));
        _viewModel.SelectedItem.ShouldBeSameAs(a);

        _viewModel.SelectedItem = b;
        await _viewModel.StartRunCommand.ExecuteAsync(null);
        await _jobEngine.Received(1).StartRunAsync(Arg.Is<RunOptions>(o => o.InputPath == b.Path), Arg.Any<CancellationToken>());

        _viewModel.SelectedItem = bad;
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();

        _viewModel.SelectedItem = a;
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    /// <summary>Validator calls whose first N calls each wait at their own gate.</summary>
    private sealed class CallGates(int gatedCalls)
    {
        private int _calls;

        public ManualResetEventSlim[] Entered { get; } = Enumerable.Range(0, gatedCalls).Select(_ => new ManualResetEventSlim(false)).ToArray();

        public ManualResetEventSlim[] Gate { get; } = Enumerable.Range(0, gatedCalls).Select(_ => new ManualResetEventSlim(false)).ToArray();

        public InputValidationResult Validate(string path, InputFormat format, AmbiguityPolicy policy, bool treatAsRna)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            if (call < Gate.Length)
            {
                Entered[call].Set();
                Gate[call].Wait(TimeSpan.FromSeconds(20));
            }

            return InputFileValidator.Validate(path, format, policy, treatAsRna);
        }
    }

    [Fact]
    public async Task A_pasted_sequence_is_kept_when_its_check_is_replaced_by_a_newer_one()
    {
        var gates = new CallGates(2);
        _viewModel = NewViewModel(validate: gates.Validate);
        _viewModel.PasteText = "ACGTXACGTACGTAC";

        var adding = _viewModel.AddPastedCommand.ExecuteAsync(null);
        gates.Entered[0].Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        var treating = _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        gates.Entered[1].Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();

        // The first check reports last-but-one: its answer is already out of date, so it may not remove the pill or its saved copy.
        gates.Gate[0].Set();
        await adding;

        var pill = _viewModel.Items.ShouldHaveSingleItem();
        File.Exists(pill.Path).ShouldBeTrue();
        gates.Gate[1].Set();
        await treating;
        File.Exists(pill.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task A_pasted_sequence_whose_own_check_failed_is_still_removed_with_its_saved_copy()
    {
        _viewModel.PasteText = "ACGTXACGTACGTAC";

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldBeEmpty();
        Directory.GetFiles(Path.Combine(_appData, "pasted")).ShouldBeEmpty();
    }

    /// <summary>Records the thread of every disk question the page asks.</summary>
    private sealed class RecordingFiles : IInputFileSystem
    {
        private readonly LocalInputFileSystem _inner = new();

        public List<(string Call, int Thread)> Calls { get; } = [];

        public bool DirectoryExists(string path)
        {
            Calls.Add((nameof(DirectoryExists), Environment.CurrentManagedThreadId));
            return _inner.DirectoryExists(path);
        }

        public IReadOnlyList<string> SequenceFiles(string folder)
        {
            Calls.Add((nameof(SequenceFiles), Environment.CurrentManagedThreadId));
            return _inner.SequenceFiles(folder);
        }

        public InputResolution Resolve(string? text)
        {
            Calls.Add((nameof(Resolve), Environment.CurrentManagedThreadId));
            return _inner.Resolve(text);
        }
    }

    private static void RunOnAnotherThread(Func<Task> action, out int callerThread)
    {
        var id = 0;
        var thread = new Thread(() =>
        {
            id = Environment.CurrentManagedThreadId;
            action().GetAwaiter().GetResult();
        });
        thread.Start();
        thread.Join();
        callerThread = id;
    }

    [Fact]
    public void A_dropped_folder_is_listed_off_the_thread_that_called_the_command()
    {
        var files = new RecordingFiles();
        _viewModel = NewViewModel(files: files);
        Write(Path.Combine("folder", "a.fasta"), ">a\n" + Dna + "\n");

        RunOnAnotherThread(() => _viewModel.AddDroppedCommand.ExecuteAsync(Drop(Path.Combine(_dir, "folder"))), out var caller);

        files.Calls.Select(c => c.Call).ShouldBe([nameof(RecordingFiles.DirectoryExists), nameof(RecordingFiles.SequenceFiles)]);
        files.Calls.ShouldAllBe(c => c.Thread != caller);
        _viewModel.Items.Count.ShouldBe(1);
    }

    [Fact]
    public void What_a_paste_means_is_decided_off_the_thread_that_called_the_command()
    {
        var files = new RecordingFiles();
        _viewModel = NewViewModel(files: files);
        _viewModel.PasteText = Dna;

        RunOnAnotherThread(() => _viewModel.AddPastedCommand.ExecuteAsync(null), out var caller);

        files.Calls.Select(c => c.Call).ShouldBe([nameof(RecordingFiles.Resolve)]);
        files.Calls.ShouldAllBe(c => c.Thread != caller);
    }

    [Fact]
    public async Task Removing_a_pasted_pill_deletes_its_saved_copy_off_the_thread_that_called_the_command()
    {
        var store = new ThreadRecordingStore(_appData);
        _viewModel = NewViewModel(store);
        _viewModel.PasteText = Dna;
        await _viewModel.AddPastedCommand.ExecuteAsync(null);
        var pasted = _viewModel.Items.Single();

        RunOnAnotherThread(() => _viewModel.RemoveItemCommand.ExecuteAsync(pasted), out var caller);

        store.DeleteThread.ShouldNotBe(0);
        store.DeleteThread.ShouldNotBe(caller);
        File.Exists(pasted.Path).ShouldBeFalse();
    }

    // ---- Issue #98: the pre-run estimate ----

    private static CostEstimateResult Point(double minutes, double usd) => CostEstimateResult.Of(new CostEstimate(minutes, minutes, usd, usd, EstimateBasis.History, StoppedDiskUsdPerDay: 0.49));

    private void EstimateReturns(CostEstimateResult result)
        => _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

    /// <summary>
    /// The refresh runs without being awaited by the page, so a test waits for the text instead of racing it. It waits on the view model's own PropertyChanged signal (#841), not on a poll with a short deadline;
    /// the 20 s bound is only a safety net that turns a lost signal into a failed assertion on the returned text.
    /// </summary>
    private async Task<string> EstimateTextWhen(Func<string, bool> done)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, System.ComponentModel.PropertyChangedEventArgs? args)
        {
            if (done(_viewModel.EstimateText))
            {
                reached.TrySetResult();
            }
        }

        _viewModel.PropertyChanged += Check;
        try
        {
            Check(null, null);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // Fall through: the caller's assertion on the text names what never arrived.
        }
        finally
        {
            _viewModel.PropertyChanged -= Check;
        }

        return _viewModel.EstimateText;
    }

    [Fact]
    public async Task A_valid_file_shows_the_point_estimate_asked_for_the_l4_machine_and_the_files_own_base_count()
    {
        EstimateReturns(Point(9, 0.13));

        await AddOne(Write("e.fasta", ">a\n" + Dna + "\n"));

        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.13, about 9 minutes");
        _viewModel.HasEstimate.ShouldBeTrue();
        await _estimates.Received().EstimateAsync("g2-standard-8", false, Dna.Length, 150, Arg.Any<CancellationToken>());
    }

    private sealed class QueueDispatcher : IDispatcher
    {
        public List<Action> Queued { get; } = [];

        public void Enqueue(Action action) => Queued.Add(action);
    }

    [Fact]
    public async Task A_price_change_signalled_after_the_estimate_was_shown_asks_for_the_estimate_again_through_the_dispatcher()
    {
        // Issue #214 review round 2 (M): the first estimate of the day is made from the shipped prices; the background read finishing must reach the page.
        var dispatcher = new QueueDispatcher();
        _viewModel = new NewRunViewModel(_picker, _jobEngine, _navigator, _strings, new LocalPastedInputStore(_appData), _estimates, new OutputSelectionStore(Substitute.For<ISettingsStore>()), _log, TestGpu.Plan(), dispatcher: dispatcher);
        EstimateReturns(Point(9, 0.13));
        await AddOne(Write("p.fasta", ">a\n" + Dna + "\n"));
        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.13, about 9 minutes");

        EstimateReturns(Point(9, 0.26));
        _estimates.PricesChanged += Raise.Event<Action>();

        _viewModel.EstimateText.ShouldBe("Estimate: about $0.13, about 9 minutes", "the signal arrives on a pool thread; nothing touches the page until the dispatcher runs it");
        dispatcher.Queued.Count.ShouldBe(1);
        dispatcher.Queued[0]();
        (await EstimateTextWhen(t => t.Contains("0.26", StringComparison.Ordinal))).ShouldBe("Estimate: about $0.26, about 9 minutes");
    }

    [Fact]
    public async Task A_model_estimate_shows_a_range_in_dollars_and_minutes()
    {
        EstimateReturns(CostEstimateResult.Of(new CostEstimate(5.17, 12.17, 0.0751, 0.1766, EstimateBasis.Model, StoppedDiskUsdPerDay: 0.49)));

        await AddOne(Write("r.fasta", ">a\n" + Dna + "\n"));

        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.08 to $0.18, about 5 to 12 minutes");
    }

    [Fact]
    public async Task One_minute_is_singular_and_a_tiny_cost_never_reads_as_zero()
    {
        EstimateReturns(Point(0.4, 0.001));

        await AddOne(Write("t.fasta", ">a\n" + Dna + "\n"));

        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.01, about 1 minute");
    }

    private async Task<string> SpendCapTextWhen(Func<string, bool> done)
    {
        for (var i = 0; i < 200 && !done(_viewModel.SpendCapText); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return _viewModel.SpendCapText;
    }

    [Fact]
    public async Task A_warning_from_the_spend_gate_shows_the_cap_card_and_leaves_Run_allowed()
    {
        EstimateReturns(Point(9, 0.13));
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.Warning, 8.0, 10.0)));

        await AddOne(Write("w.fasta", ">a\n" + Dna + "\n"));

        (await SpendCapTextWhen(t => t.Length > 0)).ShouldBe("NewRunSpendCapWarning:$8.00:$10.00");
        _viewModel.HasSpendCap.ShouldBeTrue();
        _viewModel.IsSpendCapBlocked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_block_from_the_spend_gate_shows_the_blocked_card()
    {
        EstimateReturns(Point(9, 0.13));
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.Blocked, 12.0, 10.0)));

        await AddOne(Write("b.fasta", ">a\n" + Dna + "\n"));

        (await SpendCapTextWhen(t => t.Length > 0)).ShouldBe("NewRunSpendCapBlocked:$12.00:$10.00");
        _viewModel.IsSpendCapBlocked.ShouldBeTrue();
    }

    [Fact]
    public async Task Choosing_the_A100_in_the_planner_makes_the_estimate_and_the_card_price_a2_highgpu_1g_and_Run_start_the_same_machine()
    {
        EstimateReturns(Point(9, 0.13));
        _viewModel.Gpu.SelectedGpu = _viewModel.Gpu.Gpus.Single(g => g.Tier == GpuTier.A100_40);
        var spot = _viewModel.Gpu.RequiresSpot;

        await AddOne(Write("spot.fasta", ">a\n" + Dna + "\n"));
        await EstimateTextWhen(t => t.Length > 0);

        await _estimates.Received().EstimateAsync("a2-highgpu-1g", spot, Dna.Length, 150, Arg.Any<CancellationToken>());
        await _viewModel.StartRunCommand.ExecuteAsync(null);
        await _jobEngine.Received().StartRunAsync(
            Arg.Is<RunOptions>(o => o.SpotVm == spot && o.GpuTier == GpuTier.A100_40),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_blocked_card_also_disables_Run_and_a_warning_does_not()
    {
        EstimateReturns(Point(9, 0.13));
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.Warning, 8.0, 10.0)));
        await AddOne(Write("w2.fasta", ">a\n" + Dna + "\n"));
        await SpendCapTextWhen(t => t.Length > 0);
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();

        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.Blocked, 12.0, 10.0)));
        _viewModel.ReceiveNavigationParameterCommand.Execute(null);
        await SpendCapTextWhen(t => t.StartsWith("NewRunSpendCapBlocked", StringComparison.Ordinal));

        _viewModel.IsSpendCapBlocked.ShouldBeTrue();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task The_card_says_when_runs_in_progress_are_part_of_the_month()
    {
        EstimateReturns(Point(9, 0.13));
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.Warning, 8.0, 10.0, InProgressUsd: 1.5)));

        await AddOne(Write("l.fasta", ">a\n" + Dna + "\n"));

        (await SpendCapTextWhen(t => t.Length > 0)).ShouldBe("NewRunSpendCapWarning:$8.00:$10.00 NewRunSpendCapInProgress");
    }

    [Fact]
    public async Task Showing_the_page_again_asks_the_cap_again_so_a_changed_cap_shows_at_once()
    {
        EstimateReturns(Point(9, 0.13));
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.None, 1.0, null)));
        await AddOne(Write("s.fasta", ">a\n" + Dna + "\n"));
        await EstimateTextWhen(t => t.Length > 0);
        _viewModel.HasSpendCap.ShouldBeFalse();

        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.Warning, 1.0, 1.0)));
        _viewModel.ReceiveNavigationParameterCommand.Execute(null);

        (await SpendCapTextWhen(t => t.Length > 0)).ShouldStartWith("NewRunSpendCapWarning");
    }

    [Fact]
    public async Task With_no_file_chosen_a_cap_already_reached_still_shows()
    {
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.Blocked, 12.0, 10.0)));

        _viewModel.ReceiveNavigationParameterCommand.Execute(null);

        (await SpendCapTextWhen(t => t.Length > 0)).ShouldBe("NewRunSpendCapBlocked:$12.00:$10.00");
    }

    [Fact]
    public async Task A_cap_check_that_throws_does_not_take_the_estimate_away()
    {
        EstimateReturns(Point(9, 0.13));
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns<Task<SpendCapStatus?>>(_ => throw new InvalidOperationException("ledger locked"));

        await AddOne(Write("t2.fasta", ">a\n" + Dna + "\n"));

        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.13, about 9 minutes");
        _viewModel.HasSpendCap.ShouldBeFalse();
    }

    [Fact]
    public async Task No_cap_means_no_card()
    {
        EstimateReturns(Point(9, 0.13));
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SpendCapStatus?>(new SpendCapStatus(SpendCapLevel.None, 1.0, null)));

        await AddOne(Write("n.fasta", ">a\n" + Dna + "\n"));
        await EstimateTextWhen(t => t.Length > 0);

        _viewModel.HasSpendCap.ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_with_a_problem_has_no_estimate_and_the_service_is_not_asked()
    {
        EstimateReturns(Point(9, 0.13));

        await AddOne(Write("bad.fasta", ">x\nACGTXACGTACGT\n"));
        // Negative evidence, real time (#841): nothing may change, so a window that is too short can only hide a regression and never fail correct code.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _viewModel.EstimateText.ShouldBeEmpty();
        _viewModel.HasEstimate.ShouldBeFalse();
        await _estimates.DidNotReceive().EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EstimateUnavailable.PriceListUnreadable, "No estimate: reinstall the app")]
    [InlineData(EstimateUnavailable.NoPriceForMachine, "No estimate: no price for this machine yet")]
    [InlineData(EstimateUnavailable.NoSpotPrice, "No estimate: no Spot price for this machine yet")]
    public async Task Every_reason_there_is_no_estimate_says_what_is_true_and_only_an_unreadable_list_says_reinstall(EstimateUnavailable reason, string expected)
    {
        EstimateReturns(CostEstimateResult.Unavailable(reason));

        await AddOne(Write("u.fasta", ">a\n" + Dna + "\n"));

        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe(expected);
        _viewModel.EstimateDiskText.ShouldBeEmpty("no estimate means no stopped-disk line either");
    }

    [Fact]
    public async Task A_model_range_that_collapses_after_rounding_is_not_called_based_on_earlier_runs()
    {
        EstimateReturns(CostEstimateResult.Of(new CostEstimate(5.0, 5.001, 0.0750, 0.0751, EstimateBasis.Model, StoppedDiskUsdPerDay: 0.49)));

        await AddOne(Write("m.fasta", ">a\n" + Dna + "\n"));

        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.08, about 5 minutes, no earlier runs");
    }

    [Fact]
    public async Task The_estimate_names_the_stopped_disk_cost_per_day_and_asks_for_the_runs_own_disk_size()
    {
        EstimateReturns(Point(9, 0.13));

        await AddOne(Write("d.fasta", ">a\n" + Dna + "\n"));
        await EstimateTextWhen(t => t.Length > 0);

        _viewModel.EstimateDiskText.ShouldBe("Stopped disk: about $0.49 a day");
        await _estimates.Received().EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }.BootDiskGb, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_check_clears_the_old_estimate_so_a_checking_file_never_shows_it()
    {
        var blockable = new BlockableValidator();
        _viewModel = NewViewModel(validate: blockable.Validate);
        EstimateReturns(Point(9, 0.13));
        await AddOne(Write("stale.fasta", ">a\n" + Dna + "\n"));
        (await EstimateTextWhen(t => t.Length > 0)).ShouldNotBeEmpty();
        _viewModel.EstimateDiskText.ShouldNotBeEmpty();
        blockable.BlockSuffix = "stale.fasta";

        var treating = _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        blockable.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();

        _viewModel.SelectedItem!.IsChecking.ShouldBeTrue();
        _viewModel.EstimateText.ShouldBeEmpty();
        _viewModel.EstimateDiskText.ShouldBeEmpty();
        _viewModel.HasEstimate.ShouldBeFalse();

        blockable.Release();
        await treating;
        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.13, about 9 minutes");
    }

    [Fact]
    public async Task An_unknown_size_says_nothing_instead_of_a_guess()
    {
        EstimateReturns(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize));

        await AddOne(Write("n.fasta", ">a\n" + Dna + "\n"));
        // Negative evidence, real time (#841): nothing may change, so a window that is too short can only hide a regression and never fail correct code.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _viewModel.EstimateText.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_the_last_file_clears_the_estimate()
    {
        EstimateReturns(Point(9, 0.13));
        var pill = await AddOne(Write("c.fasta", ">a\n" + Dna + "\n"));
        await EstimateTextWhen(t => t.Length > 0);

        await _viewModel.RemoveItemCommand.ExecuteAsync(pill);

        (await EstimateTextWhen(t => t.Length == 0)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_service_that_throws_leaves_the_page_working_with_no_estimate()
    {
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<CostEstimateResult>>(_ => throw new InvalidOperationException("boom"));

        var pill = await AddOne(Write("x.fasta", ">a\n" + Dna + "\n"));
        // Negative evidence, real time (#841): nothing may change, so a window that is too short can only hide a regression and never fail correct code.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        pill.IsValid.ShouldBeTrue();
        _viewModel.EstimateText.ShouldBeEmpty();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Only_the_newest_estimate_may_show_when_an_older_one_finishes_last()
    {
        var slow = new TaskCompletionSource<CostEstimateResult>();
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(slow.Task, Task.FromResult(Point(9, 0.13)));

        await AddOne(Write("s1.fasta", ">a\n" + Dna + "\n"));
        await _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.13, about 9 minutes");

        slow.SetResult(Point(99, 9.99));
        // Negative evidence, real time (#841): nothing may change, so a window that is too short can only hide a regression and never fail correct code.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _viewModel.EstimateText.ShouldBe("Estimate: about $0.13, about 9 minutes");
    }

    // ---- Round 3 of #63 ----

    [Fact]
    public async Task A_run_that_fails_to_start_says_one_action_and_leaves_no_faulted_command()
    {
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("boom")));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.ShouldBe("NewRunStatusStartFailedOther");
        _viewModel.StartRunCommand.ExecutionTask?.IsFaulted.ShouldNotBe(true);
        _navigator.DidNotReceive().NavigateTo(Arg.Any<string>(), Arg.Any<string>());
    }

    private sealed class ThrowingLog : IDiagnosticsLog
    {
        public void Warning(string source, string? jobId, string errorClass) => throw new InvalidOperationException("log broke");
    }

    [Fact]
    public async Task A_log_that_throws_neither_hides_the_start_failure_line_nor_escapes_the_command()
    {
        _viewModel = new NewRunViewModel(
            _picker, _jobEngine, _navigator, _strings, new LocalPastedInputStore(_appData), _estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()), new ThrowingLog(), TestGpu.Plan());
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("boom")));

        await Should.NotThrowAsync(() => _viewModel.StartRunCommand.ExecuteAsync(null));

        _viewModel.StatusMessage.ShouldBe("NewRunStatusStartFailedOther");
    }

    [Fact]
    public async Task A_log_that_throws_neither_hides_the_picker_failure_line_nor_escapes_the_command()
    {
        _viewModel = new NewRunViewModel(
            _picker, _jobEngine, _navigator, _strings, new LocalPastedInputStore(_appData), _estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()), new ThrowingLog(), TestGpu.Plan());
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("COM")));

        await Should.NotThrowAsync(() => _viewModel.BrowseCommand.ExecuteAsync(null));

        _viewModel.StatusMessage.ShouldBe("NewRunStatusBrowseFailed");
    }

    private sealed class RecordingLog : IDiagnosticsLog
    {
        public List<(string Source, string? JobId, string ErrorClass)> Entries { get; } = [];

        public void Warning(string source, string? jobId, string errorClass) => Entries.Add((source, jobId, errorClass));
    }

    // ---- #553: a failed start is classified by what threw, logged by class, and posted like every other line ----

    public static TheoryData<Exception, string, string> StartFailures => new()
    {
        { new FileNotFoundException("gone"), "NewRunStatusStartFailedFile", "FileNotFoundException" },
        { new DirectoryNotFoundException("gone"), "NewRunStatusStartFailedFile", "DirectoryNotFoundException" },
        { new IOException("disk full"), "NewRunStatusStartFailedOther", "IOException" },
        { new UnauthorizedAccessException("denied"), "NewRunStatusStartFailedFile", "UnauthorizedAccessException" },
        { new CloudOperationException(new CloudError("QUOTA_EXCEEDED", 403, "quota"), CloudErrorKind.Quota), "NewRunStatusStartFailedCloud", "CloudOperationException:Quota" },
        { new CloudOperationException(new CloudError(null, 403, "denied"), CloudErrorKind.Permission), "NewRunStatusStartFailedCloud", "CloudOperationException:Permission" },
        { new AccountAuthException("sign_in_required"), "NewRunStatusStartFailedCloud", "AccountAuthException" },
        { new InvalidOperationException("boom"), "NewRunStatusStartFailedOther", "InvalidOperationException" },
        { new SettingsUnavailableException("settings", new IOException("x")), "NewRunStatusStartFailedOther", "SettingsUnavailableException" },
    };

    [Theory]
    [MemberData(nameof(StartFailures))]
    public async Task A_run_that_fails_to_start_says_the_action_for_what_failed_and_logs_only_its_class(Exception failure, string expectedKey, string expectedClass)
    {
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<string>(failure));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.ShouldBe(expectedKey);
        _log.Entries.ShouldBe([("new-run", null, expectedClass)]);
    }

    [Fact]
    public void The_start_failure_theory_covers_the_local_cloud_and_other_copy()
        => StartFailures.Select(row => row.Data.Item2).Distinct().Order().ShouldBe(
            ["NewRunStatusStartFailedCloud", "NewRunStatusStartFailedFile", "NewRunStatusStartFailedOther"]);

    [Fact]
    public async Task The_log_of_a_failed_start_never_carries_the_file_name_or_the_exception_message()
    {
        var path = Write("secret-sample-name.fasta", ">a\n" + Dna + "\n");
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(path));
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new FileNotFoundException("no such file " + path, path)));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _log.Entries.ShouldNotBeEmpty();
        _log.Entries.Select(e => $"{e.Source} {e.JobId} {e.ErrorClass}").ShouldAllBe(line => !line.Contains("secret-sample-name") && !line.Contains("no such file"));
    }

    [Fact]
    public async Task A_cancelled_start_is_not_logged_as_a_failure()
    {
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new OperationCanceledException()));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _log.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_start_and_a_failed_picker_do_not_wipe_a_line_another_add_has_already_said()
    {
        var blockable = new BlockableValidator();
        _viewModel = NewViewModel(validate: blockable.Validate);
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        blockable.BlockSuffix = "b.fasta";
        var adding = _viewModel.AddDroppedCommand.ExecuteAsync(Drop(Write("b.fasta", ">b\n" + Dna + "\n")));
        blockable.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();

        // Another add, while b is still being checked, says a line.
        var missing = Path.Combine(_dir, "nope.fasta");
        _viewModel.PasteText = missing;
        await _viewModel.AddPastedCommand.ExecuteAsync(null);
        var said = "NewRunStatusPathNotFound:" + missing;
        _viewModel.StatusMessage.ShouldBe(said);

        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("boom")));
        await _viewModel.StartRunCommand.ExecuteAsync(null);
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("COM")));
        await _viewModel.BrowseCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.Split('\n').ShouldBe([said, "NewRunStatusStartFailedOther", "NewRunStatusBrowseFailed"]);
        blockable.Release();
        await adding;
    }

    [Fact]
    public async Task A_failed_start_on_an_idle_page_replaces_a_line_left_by_an_earlier_action()
    {
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        _viewModel.PasteText = Path.Combine(_dir, "nope.fasta");
        await _viewModel.AddPastedCommand.ExecuteAsync(null);
        _viewModel.StatusMessage.ShouldStartWith("NewRunStatusPathNotFound");
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("boom")));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.ShouldBe("NewRunStatusStartFailedOther");
    }

    [Fact]
    public async Task A_failed_picker_is_logged_by_class()
    {
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("COM")));

        await _viewModel.BrowseCommand.ExecuteAsync(null);

        _log.Entries.ShouldBe([("new-run", null, "InvalidOperationException")]);
    }

    // A check that Treat as RNA started on the pill an add is still checking belongs to the Treat as RNA click, not to the add.
    private sealed class TwoStageValidator
    {
        private readonly ManualResetEventSlim _firstGate = new(false);
        private readonly ManualResetEventSlim _secondGate = new(false);
        private int _calls;

        public ManualResetEventSlim FirstEntered { get; } = new(false);

        public ManualResetEventSlim SecondEntered { get; } = new(false);

        public void ReleaseSecond() => _secondGate.Set();

        public void ReleaseFirst() => _firstGate.Set();

        public InputValidationResult Validate(string path, InputFormat format, AmbiguityPolicy policy, bool treatAsRna)
        {
            switch (Interlocked.Increment(ref _calls))
            {
                case 1:
                    FirstEntered.Set();
                    _firstGate.Wait(TimeSpan.FromSeconds(20));
                    break;
                case 2:
                    SecondEntered.Set();
                    _secondGate.Wait(TimeSpan.FromSeconds(20));
                    break;
            }

            return InputFileValidator.Validate(path, format, policy, treatAsRna);
        }
    }

    [Fact]
    public async Task A_cancelled_add_never_marks_the_check_Treat_as_RNA_started_on_the_same_pill_as_stopped()
    {
        var validator = new TwoStageValidator();
        _viewModel = NewViewModel(validate: validator.Validate);
        var path = Write("rna.fasta", ">r\n" + Rna + "\n");
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([path]));

        var browsing = _viewModel.BrowseCommand.ExecuteAsync(null);
        validator.FirstEntered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        var pill = _viewModel.Items.Single();
        var treating = _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        validator.SecondEntered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        pill.IsChecking.ShouldBeTrue();

        _viewModel.BrowseCommand.Cancel();
        try
        {
            await browsing;
        }
        catch (OperationCanceledException)
        {
            // the command may report its own cancellation
        }

        // The Treat as RNA check is still running: its pill must say so, and Run stays blocked until it lands.
        pill.IsChecking.ShouldBeTrue("the add's cancel must not end a check the add does not own");
        pill.ErrorText.ShouldNotBe("NewRunPillCheckStopped");
        pill.HasError.ShouldBeFalse();

        validator.ReleaseSecond();
        await treating;
        validator.ReleaseFirst();

        pill.IsValid.ShouldBeTrue();
        pill.ErrorText.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task An_add_cancelled_before_its_check_began_still_ends_the_checking_state_with_one_action()
    {
        var gated = new GatedValidator();
        _viewModel = NewViewModel(validate: gated.Validate);
        var a = Write("a.fasta", ">a\n" + Dna + "\n");
        var b = Write("b.fasta", ">b\n" + Dna + "\n");
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([a, b]));

        var browsing = _viewModel.BrowseCommand.ExecuteAsync(null);
        gated.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        _viewModel.BrowseCommand.Cancel();
        gated.Release();
        try
        {
            await browsing;
        }
        catch (OperationCanceledException)
        {
            // the command may report its own cancellation
        }

        _viewModel.Items.ShouldAllBe(item => !item.IsChecking);
    }

    [Fact]
    public async Task A_run_start_that_is_cancelled_says_nothing()
    {
        await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new OperationCanceledException()));

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.ShouldBe(string.Empty);
        _navigator.DidNotReceive().NavigateTo(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task A_file_picker_that_fails_says_one_action_and_adds_nothing()
    {
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("COM")));

        await _viewModel.BrowseCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.ShouldBe("NewRunStatusBrowseFailed");
        _viewModel.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_picker_that_is_cancelled_says_nothing()
    {
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<string>>(new OperationCanceledException()));

        await _viewModel.BrowseCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Cancelling_an_add_after_the_first_file_leaves_no_pill_stuck_on_checking()
    {
        BrowseCancelsOnFirstCheck(out var a, out var b);
        _picker.PickInputFilesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([a, b]));

        await _viewModel.BrowseCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldNotBeEmpty();
        _viewModel.Items.ShouldAllBe(item => !item.IsChecking);
        _viewModel.Items.Select(i => i.Path).ShouldNotContain(b);
    }

    private void BrowseCancelsOnFirstCheck(out string a, out string b)
    {
        a = Write("a.fasta", ">a\n" + Dna + "\n");
        b = Write("b.fasta", ">b\n" + Dna + "\n");
        _viewModel = NewViewModel(validate: (path, format, policy, rna) =>
        {
            _viewModel.BrowseCommand.Cancel();
            return InputFileValidator.Validate(path, format, policy, rna);
        });
    }

    [Fact]
    public async Task A_cancelled_paste_never_leaves_a_saved_copy_no_pill_owns()
    {
        _viewModel = NewViewModel(validate: (path, format, policy, rna) =>
        {
            _viewModel.AddPastedCommand.Cancel();
            return InputFileValidator.Validate(path, format, policy, rna);
        });
        _viewModel.PasteText = Dna;

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        var owned = _viewModel.Items.Select(i => i.Path).ToHashSet();
        Directory.GetFiles(Path.Combine(_appData, "pasted")).ShouldAllBe(file => owned.Contains(file));
        _viewModel.Items.ShouldAllBe(item => !item.IsChecking);
    }

    private sealed class CancellingStore(string root, Action cancel) : IPastedInputStore
    {
        public string Save(string text)
        {
            var path = new LocalPastedInputStore(root).Save(text);
            cancel();
            return path;
        }

        public void Delete(string path) => new LocalPastedInputStore(root).Delete(path);
    }

    [Fact]
    public async Task A_paste_cancelled_after_it_was_saved_deletes_the_saved_copy_and_keeps_the_text()
    {
        _viewModel = NewViewModel(new CancellingStore(_appData, () => _viewModel.AddPastedCommand.Cancel()));
        _viewModel.PasteText = Dna;

        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.PasteText.ShouldBe(Dna);
        Directory.GetFiles(Path.Combine(_appData, "pasted")).ShouldBeEmpty();
    }
    [Fact]
    public async Task A_later_drop_does_not_wipe_what_an_earlier_one_said_while_another_is_still_running()
    {
        var gated = new BlockableValidator { BlockSuffix = "slow.fasta" };
        _viewModel = NewViewModel(validate: gated.Validate);
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        var slow = Write("slow.fasta", ">a\n" + Dna + "\n");
        var quick = Write("quick.fasta", ">b\n" + Dna + "\n");

        _viewModel.AddDroppedCommand.Execute(Drop(slow));
        var running = _viewModel.AddDroppedCommand.ExecutionTask!;
        gated.Entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ShouldBeTrue();
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(Path.Combine(_dir, "empty")));
        _viewModel.StatusMessage.ShouldBe("NewRunStatusFolderEmpty:" + Path.Combine(_dir, "empty"));
        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(quick));
        gated.Release();
        await running;

        _viewModel.StatusMessage.ShouldBe("NewRunStatusFolderEmpty:" + Path.Combine(_dir, "empty"));
    }
    [Fact]
    public async Task Two_problems_in_one_drop_are_both_said()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty1"));
        Directory.CreateDirectory(Path.Combine(_dir, "empty2"));

        await _viewModel.AddDroppedCommand.ExecuteAsync(Drop(Path.Combine(_dir, "empty1"), Path.Combine(_dir, "empty2")));

        _viewModel.StatusMessage.ShouldContain("empty1");
        _viewModel.StatusMessage.ShouldContain("empty2");
    }

    [Fact]
    public async Task The_count_of_a_large_paste_is_not_worked_out_on_the_thread_that_typed_it()
    {
        _viewModel.PasteText = new string('A', 1_000_000);

        _viewModel.PasteCountText.ShouldBe("NewRunPasteCount:0");
        await WaitFor(() => _viewModel.PasteCountText == "NewRunPasteCount:1000000");
    }

    [Fact]
    public async Task A_late_count_of_an_older_large_paste_never_replaces_the_count_of_a_newer_one()
    {
        _viewModel.PasteText = new string('A', 1_000_000);
        _viewModel.PasteText = "ACGT";

        _viewModel.PasteCountText.ShouldBe("NewRunPasteCount:4");
        // Negative evidence, real time (#841): nothing may change, so a window that is too short can only hide a regression and never fail correct code.
        await Task.Delay(700, TestContext.Current.CancellationToken);
        _viewModel.PasteCountText.ShouldBe("NewRunPasteCount:4");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().ShouldBeTrue();
    }

    private sealed class FakeStrings : IStringResourceProvider
    {
        private static readonly Dictionary<string, string> Templates = new()
        {
            ["NewRunPillKind_Fasta"] = "FASTA",
            ["NewRunPillKind_GenBank"] = "GenBank",
            ["NewRunPillKind_Paste"] = "Plain sequence",
            ["NewRunPillRecordsOne"] = "1 record",
            ["NewRunPillRecordsMany"] = "{0} records",
            ["NewRunPillBases"] = "{0} bases",
            ["NewRunPillGenesOne"] = "1 gene",
            ["NewRunPillGenesMany"] = "{0} genes",
            ["NewRunPillWhereRecordBase"] = "(record {0}, base {1})",
            ["NewRunPillWhereRecord"] = "(record {0})",
            ["NewRunPasteCount"] = "NewRunPasteCount:{0}",
            ["NewRunStatusPathNotFound"] = "NewRunStatusPathNotFound:{0}",
            ["NewRunStatusFolderEmpty"] = "NewRunStatusFolderEmpty:{0}",
            ["NewRunNotice_RnaConverted"] = "NewRunNotice_RnaConverted:{0}",
            ["NewRunNotice_RepeatedIds"] = "NewRunNotice_RepeatedIds:{0}",
            ["NewRunEstimateHistory"] = "Estimate: about {0}, about {1}",
            ["NewRunEstimateRange"] = "Estimate: about {0} to {1}, about {2} to {3} minutes",
            ["NewRunEstimateMoney"] = "${0}",
            ["NewRunEstimateMinutesOne"] = "1 minute",
            ["NewRunEstimateMinutesMany"] = "{0} minutes",
            ["NewRunEstimateUnavailable"] = "No estimate: reinstall the app",
            ["NewRunEstimateNoPrice"] = "No estimate: no price for this machine yet",
            ["NewRunEstimateNoSpotPrice"] = "No estimate: no Spot price for this machine yet",
            ["NewRunEstimateModelPoint"] = "Estimate: about {0}, about {1}, no earlier runs",
            ["NewRunEstimateStoppedDisk"] = "Stopped disk: about {0} a day",
            ["NewRunSpendCapWarning"] = "NewRunSpendCapWarning:{0}:{1}",
            ["NewRunSpendCapBlocked"] = "NewRunSpendCapBlocked:{0}:{1}",
            ["NewRunSpendCapInProgress"] = "NewRunSpendCapInProgress",
        };

        public string GetString(string key) => Templates.GetValueOrDefault(key, key);
    }
}
