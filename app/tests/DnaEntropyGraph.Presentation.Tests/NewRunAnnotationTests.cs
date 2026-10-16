using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #686: pairing a GFF3 with a FASTA on the New run page (drop, Add GFF3, the pill line, the run's AnnotationPath).</summary>
public sealed class NewRunAnnotationTests : IDisposable
{
    private const string Dna = "ACGTACGTACGTACGT";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-annot-" + Guid.NewGuid().ToString("N"));
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly IJobEngine _jobEngine = Substitute.For<IJobEngine>();
    private readonly List<(string Fasta, string Gff, Topology Topology)> _checks = [];
    private AnnotationCheckResult _result = new(null, 12, []);

    public NewRunAnnotationTests()
    {
        Directory.CreateDirectory(_dir);
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
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

    private NewRunViewModel NewViewModel(bool offer = true)
    {
        var estimates = Substitute.For<ICostEstimateService>();
        estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        return new NewRunViewModel(
            _picker,
            _jobEngine,
            Substitute.For<INavigator>(),
            new Strings(),
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
            estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()),
            Substitute.For<IDiagnosticsLog>(),
            TestGpu.Plan(),
            validateAnnotation: (fasta, gff, topology) =>
            {
                _checks.Add((fasta, gff, topology));
                return _result;
            },
            offerAnnotation: offer);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string Fasta(string name) => Write(name, ">x\n" + Dna + "\n");

    private string Gff(string name) => Write(name, "##gff-version 3\n");

    private static DroppedItems Drop(params string[] paths) => new(paths, 0, false);

    [Fact]
    public async Task Dropping_a_fasta_and_a_gff3_together_pairs_them_by_name_and_makes_one_pill()
    {
        var vm = NewViewModel();
        var fasta = Fasta("plasmid.fasta");
        var gff = Gff("plasmid.gff3");

        await vm.AddDroppedCommand.ExecuteAsync(Drop(gff, fasta));

        var pill = vm.Items.ShouldHaveSingleItem();
        pill.Path.ShouldBe(fasta);
        pill.AnnotationPath.ShouldBe(gff);
        pill.HasAnnotation.ShouldBeTrue();
        pill.AnnotationText.ShouldBe("ann:plasmid.gff3:12 genes");
        _checks.ShouldHaveSingleItem().ShouldBe((fasta, gff, Topology.Auto));
    }

    [Fact]
    public async Task A_single_fasta_takes_a_gff3_with_another_name()
    {
        var vm = NewViewModel();
        var fasta = Fasta("a.fasta");
        var gff = Gff("annotations.gff");

        await vm.AddDroppedCommand.ExecuteAsync(Drop(fasta, gff));

        vm.Items.ShouldHaveSingleItem().AnnotationPath.ShouldBe(gff);
    }

    [Fact]
    public async Task With_two_fasta_files_the_matching_name_wins()
    {
        var vm = NewViewModel();
        var first = Fasta("one.fasta");
        var second = Fasta("two.fasta");
        var gff = Gff("two.gff3");

        await vm.AddDroppedCommand.ExecuteAsync(Drop(first, second, gff));

        vm.Items.Single(i => i.Path == first).HasAnnotation.ShouldBeFalse();
        vm.Items.Single(i => i.Path == second).AnnotationPath.ShouldBe(gff);
    }

    [Fact]
    public async Task With_two_fasta_files_and_no_matching_name_nothing_is_paired_and_the_page_says_to_use_Add_GFF3()
    {
        var vm = NewViewModel();
        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("one.fasta"), Fasta("two.fasta"), Gff("other.gff3")));

        vm.Items.ShouldAllBe(i => !i.HasAnnotation);
        vm.StatusMessage.ShouldBe("NewRunStatusAnnotationAmbiguous");
        _checks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_gff3_with_no_fasta_on_the_page_says_to_add_the_fasta_first_and_adds_no_pill()
    {
        var vm = NewViewModel();

        await vm.AddDroppedCommand.ExecuteAsync(Drop(Gff("lonely.gff3")));

        vm.Items.ShouldBeEmpty();
        vm.StatusMessage.ShouldBe("NewRunStatusAnnotationNoFasta");
    }

    [Fact]
    public async Task A_gff3_that_does_not_fit_is_not_paired_and_the_page_names_the_problem()
    {
        var vm = NewViewModel();
        _result = new AnnotationCheckResult(new AnnotationProblem(AnnotationProblemCode.NoNameMatch, "x"), 0, []);

        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("p.fasta"), Gff("p.gff3")));

        vm.Items.ShouldHaveSingleItem().HasAnnotation.ShouldBeFalse();
        vm.StatusMessage.ShouldBe("NewRunAnnotationProblem_NoNameMatch");
    }

    [Fact]
    public async Task The_checkers_notices_show_under_the_pill()
    {
        var vm = NewViewModel();
        _result = new AnnotationCheckResult(null, 3, [new InputNotice(InputNoticeCode.AnnotationUnmatchedNames, 2)]);

        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("p.fasta"), Gff("p.gff3")));

        vm.Items.Single().AnnotationNoticesText.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task When_the_worker_cannot_read_a_gff3_yet_a_dropped_gff3_is_refused_with_one_line_and_no_pill()
    {
        var vm = NewViewModel(offer: false);

        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("p.fasta"), Gff("p.gff3")));

        vm.OffersAnnotation.ShouldBeFalse();
        vm.Items.ShouldHaveSingleItem().HasAnnotation.ShouldBeFalse();
        vm.StatusMessage.ShouldBe("NewRunStatusAnnotationUnavailable");
        vm.AddAnnotationCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Add_GFF3_pairs_the_picked_file_with_the_selected_fasta()
    {
        var vm = NewViewModel();
        var fasta = Fasta("p.fasta");
        await vm.AddDroppedCommand.ExecuteAsync(Drop(fasta));
        var gff = Gff("elsewhere.gff3");
        _picker.PickAnnotationFileAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(gff));

        vm.AddAnnotationCommand.CanExecute(null).ShouldBeTrue();
        await vm.AddAnnotationCommand.ExecuteAsync(null);

        vm.Items.Single().AnnotationPath.ShouldBe(gff);
    }

    [Fact]
    public async Task A_cancelled_Add_GFF3_changes_nothing()
    {
        var vm = NewViewModel();
        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("p.fasta")));
        _picker.PickAnnotationFileAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));

        await vm.AddAnnotationCommand.ExecuteAsync(null);

        vm.Items.Single().HasAnnotation.ShouldBeFalse();
        vm.StatusMessage.ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_GFF3_is_offered_only_for_a_checked_fasta()
    {
        var vm = NewViewModel();
        vm.AddAnnotationCommand.CanExecute(null).ShouldBeFalse();

        var genBank = Write("g.gb", "LOCUS       x                 16 bp    DNA     linear   UNK 01-JAN-2000\nORIGIN\n        1 acgtacgtac gtacgt\n//\n");
        await vm.AddDroppedCommand.ExecuteAsync(Drop(genBank));
        vm.AddAnnotationCommand.CanExecute(null).ShouldBeFalse();

        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("f.fasta")));
        vm.SelectedItem = vm.Items.Single(i => i.Kind == InputKind.Fasta);
        vm.AddAnnotationCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task The_run_carries_the_paired_gff3_as_its_AnnotationPath()
    {
        var vm = NewViewModel();
        var gff = Gff("p.gff3");
        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("p.fasta"), gff));

        await vm.StartRunCommand.ExecuteAsync(null);

        await _jobEngine.Received(1).StartRunAsync(Arg.Is<RunOptions>(o => o.AnnotationPath == gff), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_the_gff3_leaves_the_fasta_and_the_run_has_no_AnnotationPath()
    {
        var vm = NewViewModel();
        await vm.AddDroppedCommand.ExecuteAsync(Drop(Fasta("p.fasta"), Gff("p.gff3")));
        var pill = vm.Items.Single();

        vm.RemoveAnnotationCommand.Execute(pill);
        await vm.StartRunCommand.ExecuteAsync(null);

        pill.HasAnnotation.ShouldBeFalse();
        pill.AnnotationPath.ShouldBeNull();
        await _jobEngine.Received(1).StartRunAsync(Arg.Is<RunOptions>(o => o.AnnotationPath == null), Arg.Any<CancellationToken>());
    }

    private sealed class Strings : IStringResourceProvider
    {
        private static readonly Dictionary<string, string> Templates = new()
        {
            ["NewRunPillKind_Fasta"] = "FASTA",
            ["NewRunPillKind_GenBank"] = "GenBank",
            ["NewRunPillGenesOne"] = "1 gene",
            ["NewRunPillGenesMany"] = "{0} genes",
            ["NewRunPillAnnotationLine"] = "ann:{0}:{1}",
            ["NewRunNotice_AnnotationUnmatchedNames"] = "unmatched {0}",
            ["NewRunNotice_AnnotationUnmatchedNames_One"] = "unmatched one",
        };

        public string GetString(string key) => Templates.GetValueOrDefault(key, key);
    }
}
