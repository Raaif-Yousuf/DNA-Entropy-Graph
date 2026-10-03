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
    private NewRunViewModel _viewModel;

    public NewRunViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _appData = Path.Combine(_dir, "appdata");
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
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

    private static CostEstimateResult Point(double minutes, double usd) => CostEstimateResult.Of(new CostEstimate(minutes, minutes, usd, usd, EstimateBasis.History));

    private void EstimateReturns(CostEstimateResult result)
        => _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

    /// <summary>The refresh runs without being awaited by the page, so a test waits for the text instead of racing it.</summary>
    private async Task<string> EstimateTextWhen(Func<string, bool> done)
    {
        for (var i = 0; i < 200 && !done(_viewModel.EstimateText); i++)
        {
            await Task.Delay(10);
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
        await _estimates.Received().EstimateAsync("g2-standard-8", false, Dna.Length, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_model_estimate_shows_a_range_in_dollars_and_minutes()
    {
        EstimateReturns(CostEstimateResult.Of(new CostEstimate(5.17, 12.17, 0.0751, 0.1766, EstimateBasis.Model)));

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

    [Fact]
    public async Task A_file_with_a_problem_has_no_estimate_and_the_service_is_not_asked()
    {
        EstimateReturns(Point(9, 0.13));

        await AddOne(Write("bad.fasta", ">x\nACGTXACGTACGT\n"));
        await Task.Delay(50);

        _viewModel.EstimateText.ShouldBeEmpty();
        _viewModel.HasEstimate.ShouldBeFalse();
        await _estimates.DidNotReceive().EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EstimateUnavailable.PriceListUnreadable)]
    [InlineData(EstimateUnavailable.NoPriceForMachine)]
    [InlineData(EstimateUnavailable.NoSpotPrice)]
    public async Task A_price_list_that_cannot_be_used_says_the_one_action(EstimateUnavailable reason)
    {
        EstimateReturns(CostEstimateResult.Unavailable(reason));

        await AddOne(Write("u.fasta", ">a\n" + Dna + "\n"));

        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("No estimate: reinstall the app");
    }

    [Fact]
    public async Task An_unknown_size_says_nothing_instead_of_a_guess()
    {
        EstimateReturns(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize));

        await AddOne(Write("n.fasta", ">a\n" + Dna + "\n"));
        await Task.Delay(50);

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
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns<Task<CostEstimateResult>>(_ => throw new InvalidOperationException("boom"));

        var pill = await AddOne(Write("x.fasta", ">a\n" + Dna + "\n"));
        await Task.Delay(50);

        pill.IsValid.ShouldBeTrue();
        _viewModel.EstimateText.ShouldBeEmpty();
        _viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Only_the_newest_estimate_may_show_when_an_older_one_finishes_last()
    {
        var slow = new TaskCompletionSource<CostEstimateResult>();
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(slow.Task, Task.FromResult(Point(9, 0.13)));

        await AddOne(Write("s1.fasta", ">a\n" + Dna + "\n"));
        await _viewModel.TreatAsRnaCommand.ExecuteAsync(null);
        (await EstimateTextWhen(t => t.Length > 0)).ShouldBe("Estimate: about $0.13, about 9 minutes");

        slow.SetResult(Point(99, 9.99));
        await Task.Delay(50);

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

        _viewModel.StatusMessage.ShouldBe("NewRunStatusStartFailed");
        _viewModel.StartRunCommand.ExecutionTask?.IsFaulted.ShouldNotBe(true);
        _navigator.DidNotReceive().NavigateTo(Arg.Any<string>(), Arg.Any<string>());
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
            Thread.Sleep(50);
            return InputFileValidator.Validate(path, format, policy, rna);
        });
    }

    [Fact]
    public async Task A_cancelled_paste_never_leaves_a_saved_copy_no_pill_owns()
    {
        _viewModel = NewViewModel(validate: (path, format, policy, rna) =>
        {
            _viewModel.AddPastedCommand.Cancel();
            Thread.Sleep(50);
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
            Thread.Sleep(50);
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
        };

        public string GetString(string key) => Templates.GetValueOrDefault(key, key);
    }
}
