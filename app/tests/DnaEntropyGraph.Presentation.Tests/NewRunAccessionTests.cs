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

/// <summary>Issue #684: fetching an NCBI record by accession on the New run page (the box, the pill, the run's Accession).</summary>
public sealed class NewRunAccessionTests
{
    private readonly IJobEngine _jobEngine = Substitute.For<IJobEngine>();

    public NewRunAccessionTests()
        => _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));

    private NewRunViewModel NewViewModel(bool offer = true)
    {
        var estimates = Substitute.For<ICostEstimateService>();
        estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        return new NewRunViewModel(
            Substitute.For<IFilePicker>(),
            _jobEngine,
            Substitute.For<INavigator>(),
            new Strings(),
            new LocalPastedInputStore(Path.Combine(Path.GetTempPath(), "deg-acc-" + Guid.NewGuid().ToString("N"))),
            estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()),
            Substitute.For<IDiagnosticsLog>(),
            offerAccession: offer);
    }

    [Fact]
    public void Adding_an_accession_makes_one_valid_genbank_pill_with_no_file_and_clears_the_box()
    {
        var vm = NewViewModel();
        vm.AccessionText = " nc_045512.2 ";

        vm.AddAccessionCommand.Execute(null);

        var pill = vm.Items.ShouldHaveSingleItem();
        pill.IsAccession.ShouldBeTrue();
        pill.Accession.ShouldBe("NC_045512.2");
        pill.AccessionRange.ShouldBeNull();
        pill.IsValid.ShouldBeTrue();
        pill.Kind.ShouldBe(InputKind.GenBank);
        pill.DisplayName.ShouldBe("NC_045512.2");
        pill.KindText.ShouldBe("NewRunPillKind_Ncbi");
        vm.SelectedItem.ShouldBeSameAs(pill);
        vm.AccessionText.ShouldBeEmpty();
        vm.AccessionMessage.ShouldBeEmpty();
        vm.HasOutputChoices.ShouldBeTrue("the GenBank file choices are shown for it");
    }

    [Fact]
    public async Task Run_starts_with_the_accession_and_range_and_no_input_path()
    {
        var vm = NewViewModel();
        vm.AccessionText = "NC_045512.2";
        vm.AccessionStartText = "100";
        vm.AccessionEndText = "500";
        vm.AddAccessionCommand.Execute(null);

        await vm.StartRunCommand.ExecuteAsync(null);

        await _jobEngine.Received(1).StartRunAsync(
            Arg.Is<RunOptions>(o => o.Accession == "NC_045512.2" && o.AccessionRange == "100-500" && o.InputPath == null && o.AnnotationPath == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("hello", "", "", "NewRunAccessionProblem_NotAnAccession")]
    [InlineData("NC_045512.2", "100", "", "NewRunAccessionProblem_RangeIncomplete")]
    [InlineData("NC_045512.2", "500", "100", "NewRunAccessionProblem_RangeInvalid")]
    public void A_bad_accession_or_range_says_what_to_fix_and_adds_no_pill(string accession, string start, string end, string messageKey)
    {
        var vm = NewViewModel();
        vm.AccessionText = accession;
        vm.AccessionStartText = start;
        vm.AccessionEndText = end;

        vm.AddAccessionCommand.Execute(null);

        vm.Items.ShouldBeEmpty();
        vm.AccessionMessage.ShouldBe(messageKey);
        vm.HasAccessionMessage.ShouldBeTrue();
        vm.AccessionText.ShouldBe(accession, "a mistyped accession stays in the box so it can be corrected");
    }

    [Fact]
    public void The_same_accession_and_range_is_not_added_twice()
    {
        var vm = NewViewModel();
        vm.AccessionText = "NC_045512.2";
        vm.AddAccessionCommand.Execute(null);
        vm.AccessionText = "nc_045512.2";

        vm.AddAccessionCommand.Execute(null);

        vm.Items.Count.ShouldBe(1);
        vm.AccessionMessage.ShouldBe("NewRunAccessionAlreadyAdded");
    }

    [Fact]
    public void Add_waits_for_something_typed_and_for_a_worker_that_reads_accessions()
    {
        var vm = NewViewModel();
        vm.AddAccessionCommand.CanExecute(null).ShouldBeFalse();
        vm.AccessionText = "NC_045512.2";
        vm.AddAccessionCommand.CanExecute(null).ShouldBeTrue();

        var without = NewViewModel(offer: false);
        without.OffersAccession.ShouldBeFalse();
        without.AccessionText = "NC_045512.2";
        without.AddAccessionCommand.CanExecute(null).ShouldBeFalse("a worker that ignores the field would run nothing");
    }

    [Fact]
    public async Task Treat_as_RNA_does_not_try_to_read_a_file_for_an_accession_pill()
    {
        var vm = NewViewModel();
        vm.AccessionText = "NC_045512.2";
        vm.AddAccessionCommand.Execute(null);

        await vm.TreatAsRnaCommand.ExecuteAsync(null);

        vm.Items.ShouldHaveSingleItem().IsValid.ShouldBeTrue();
    }

    private sealed class Strings : IStringResourceProvider
    {
        public string GetString(string key) => key;
    }
}
