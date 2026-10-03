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
    private readonly NewRunViewModel _viewModel;

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

    private NewRunViewModel NewViewModel() => new(
        _picker,
        _settings,
        _jobEngine,
        _navigator,
        _strings,
        new LocalPastedInputStore(_appData),
        new FixedTimeProvider(new DateTimeOffset(2026, 10, 3, 14, 7, 0, TimeSpan.Zero)));

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
    public async Task The_notices_of_the_validator_are_shown_verbatim()
    {
        var pill = await AddOne(Write("two.fasta", ">a\n" + Dna + "\n>b\n" + Dna + "\n"));

        pill.HasNotices.ShouldBeTrue();
        pill.NoticesText.ShouldContain("Read 2 record(s) from the FASTA (all processed).");
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
        pill.NoticesText.ShouldContain("Converted 4 U->T (RNA input).");
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
        };

        public string GetString(string key) => Templates.GetValueOrDefault(key, key);
    }
}
