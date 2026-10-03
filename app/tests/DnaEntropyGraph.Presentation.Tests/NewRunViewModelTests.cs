using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
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
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();
    private readonly FakeStrings _strings = new();
    private NewRunViewModel _viewModel;

    public NewRunViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _appData = Path.Combine(_dir, "appdata");
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
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

    private NewRunViewModel NewViewModel(IPastedInputStore? store = null, Func<string, InputFormat, AmbiguityPolicy, bool, InputValidationResult>? validate = null) => new(
        _picker,
        _settings,
        _jobEngine,
        _navigator,
        _strings,
        store ?? new LocalPastedInputStore(_appData),
        new FixedTimeProvider(new DateTimeOffset(2026, 10, 3, 14, 7, 0, TimeSpan.Zero)),
        validate);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private async Task<InputPillItem> AddOne(string path)
    {
        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { path });
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
        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { path });
        _viewModel.NameTemplate = "{file}_{model}";

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        await _jobEngine.Received(1).StartRunAsync(
            Arg.Is<RunOptions>(o => o.InputPath == path && o.NameTemplate == "{file}_{model}" && !o.TreatAsRna && o.ModelId == "evo2_7b"),
            Arg.Any<CancellationToken>());
        _navigator.Received(1).NavigateTo("RunProgress", "job-1");
        _settings.Received().SetString("LastModelId", "evo2_7b");
        _settings.Received().SetString("NameTemplate", "{file}_{model}");
    }

    [Fact]
    public void The_name_template_the_user_last_used_is_read_back()
    {
        _settings.GetString("NameTemplate").Returns("{date}_{file}");

        NewViewModel().NameTemplate.ShouldBe("{date}_{file}");
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
        pill.NoticesText.ShouldBe("NewRunNotice_RepeatedIds:1");
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

        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { path });
        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { path });

        _viewModel.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_dropped_folder_contributes_its_sequence_files()
    {
        Write(Path.Combine("folder", "a.fasta"), ">a\n" + Dna + "\n");
        Write(Path.Combine("folder", "b.fa"), ">b\n" + Dna + "\n");
        Write(Path.Combine("folder", "notes.docx"), "not a sequence");

        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { Path.Combine(_dir, "folder") });

        _viewModel.Items.Select(i => i.DisplayName).ShouldBe(["a.fasta", "b.fa"]);
    }

    [Fact]
    public async Task A_dropped_folder_with_no_sequence_files_says_so()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));

        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { Path.Combine(_dir, "empty") });

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.StatusMessage.ShouldBe("NewRunStatusFolderEmpty:" + Path.Combine(_dir, "empty"));
    }

    [Fact]
    public async Task Adding_never_writes_next_to_the_users_file()
    {
        var path = Write(Path.Combine("lab", "a.fasta"), ">a\n" + Dna + "\n");
        var before = Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!);

        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { path });

        Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!).ShouldBe(before);
        File.ReadAllText(path).ShouldBe(">a\n" + Dna + "\n");
    }

    [Fact]
    public async Task Removing_a_pill_selects_a_neighbour_and_disables_run_when_none_is_left()
    {
        var a = await AddOne(Write("a.fasta", ">a\n" + Dna + "\n"));
        var b = await AddOne(Write("b.fasta", ">b\n" + Dna + "\n"));

        _viewModel.RemoveItemCommand.Execute(a);
        _viewModel.SelectedItem.ShouldBeSameAs(b);

        _viewModel.RemoveItemCommand.Execute(b);
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

        _viewModel.RemoveItemCommand.Execute(a);
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

    // ---- Run name ----

    [Fact]
    public async Task The_run_name_preview_follows_the_template_and_the_selected_file()
    {
        await AddOne(Write("SetTnpB.fasta", ">a\n" + Dna + "\n"));

        _viewModel.RunNamePreview.ShouldBe("SetTnpB");

        _viewModel.NameTemplate = "{file}_{date:yyyy-MM-dd}_{model}";
        _viewModel.RunNamePreview.ShouldBe("SetTnpB_2026-10-03_evo2_7b");
    }

    [Fact]
    public async Task A_pasted_sequence_is_named_pasted()
    {
        _viewModel.PasteText = Dna;
        await _viewModel.AddPastedCommand.ExecuteAsync(null);

        _viewModel.RunNamePreview.ShouldBe("pasted");
    }

    [Fact]
    public void With_nothing_selected_the_preview_is_empty()
        => _viewModel.RunNamePreview.ShouldBe(string.Empty);


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

        var adding = _viewModel.AddPathsCommand.ExecuteAsync(new[] { path });
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

        await _viewModel.AddPathsCommand.ExecuteAsync(new[] { Write("a.fasta", ">a\n" + Dna + "\n") });

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

        _viewModel.RemoveItemCommand.Execute(_viewModel.Items[0]);
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

        _viewModel.RemoveItemCommand.Execute(pasted);
        _viewModel.RemoveItemCommand.Execute(users);

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
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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
        };

        public string GetString(string key) => Templates.GetValueOrDefault(key, key);
    }
}
