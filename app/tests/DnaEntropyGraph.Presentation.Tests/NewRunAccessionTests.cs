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
    private readonly ICostEstimateService _estimates = Substitute.For<ICostEstimateService>();
    private readonly ScriptedAccessionLookup _lookup = new();

    public NewRunAccessionTests()
    {
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
    }

    private NewRunViewModel NewViewModel(bool offer = true, IAccessionLookup? lookup = null, bool noLookup = false)
        => new(
            Substitute.For<IFilePicker>(),
            _jobEngine,
            Substitute.For<INavigator>(),
            new Strings(),
            new LocalPastedInputStore(Path.Combine(Path.GetTempPath(), "deg-acc-" + Guid.NewGuid().ToString("N"))),
            _estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()),
            Substitute.For<IDiagnosticsLog>(),
            TestGpu.Plan(),
            offerAccession: offer,
            accessionLookup: noLookup ? null : lookup ?? _lookup);

    private static Task Add(NewRunViewModel vm) => ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AddAccessionCommand).ExecuteAsync(null);

    [Fact]
    public async Task Adding_an_accession_makes_one_valid_genbank_pill_with_no_file_and_clears_the_box()
    {
        var vm = NewViewModel();
        vm.AccessionText = " nc_045512.2 ";

        await Add(vm);

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
        vm.IsCheckingAccession.ShouldBeFalse();
        vm.HasOutputChoices.ShouldBeTrue("the GenBank file choices are shown for it");
    }

    [Fact]
    public async Task The_pill_carries_the_length_NCBI_reported_so_the_cost_estimate_has_a_size()
    {
        var vm = NewViewModel();
        vm.AccessionText = "NC_045512.2";

        await Add(vm);

        var pill = vm.Items.Single();
        pill.TotalBases.ShouldBe(ScriptedAccessionLookup.DefaultLength);
        pill.SummaryText.ShouldBe("NewRunPillAccessionLength");
        _lookup.Asked.ShouldBe([("NC_045512.2", null)]);
        await _estimates.Received().EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), ScriptedAccessionLookup.DefaultLength, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_range_is_looked_up_with_its_range_and_the_pill_has_the_length_of_the_range()
    {
        var vm = NewViewModel();
        vm.AccessionText = "NC_045512.2";
        vm.AccessionStartText = "100";
        vm.AccessionEndText = "500";

        await Add(vm);

        _lookup.Asked.ShouldBe([("NC_045512.2", "100-500")]);
        vm.Items.Single().TotalBases.ShouldBe(401);
    }

    [Fact]
    public async Task A_host_with_no_lookup_still_adds_the_pill_and_the_engine_is_the_gate()
    {
        var vm = NewViewModel(noLookup: true);
        vm.AccessionText = "NC_045512.2";

        await Add(vm);

        var pill = vm.Items.ShouldHaveSingleItem();
        pill.TotalBases.ShouldBeNull();
        pill.SummaryText.ShouldBe("NewRunPillAccessionSummary");
    }

    [Theory]
    [InlineData(AccessionLookupProblem.NotFound)]
    [InlineData(AccessionLookupProblem.RangeOutsideRecord)]
    [InlineData(AccessionLookupProblem.TooLarge)]
    [InlineData(AccessionLookupProblem.RateLimited)]
    [InlineData(AccessionLookupProblem.NcbiTrouble)]
    [InlineData(AccessionLookupProblem.TimedOut)]
    [InlineData(AccessionLookupProblem.Unreachable)]
    public async Task An_accession_NCBI_does_not_have_says_what_to_do_and_adds_no_pill(AccessionLookupProblem problem)
    {
        _lookup.Script["NC_999999.9"] = AccessionLookupResult.Failed(problem);
        var vm = NewViewModel();
        vm.AccessionText = "NC_999999.9";

        await Add(vm);

        vm.Items.ShouldBeEmpty();
        vm.AccessionMessage.ShouldBe("NewRunAccessionLookup_" + problem);
        vm.HasAccessionMessage.ShouldBeTrue();
        vm.AccessionText.ShouldBe("NC_999999.9", "it stays in the box so it can be corrected or tried again");
        vm.IsCheckingAccession.ShouldBeFalse();
    }

    [Fact]
    public async Task While_NCBI_is_being_asked_the_box_says_so_and_Add_waits()
    {
        var release = new TaskCompletionSource<AccessionLookupResult>();
        var vm = NewViewModel(lookup: new GatedLookup(release.Task));
        vm.AccessionText = "NC_045512.2";

        var adding = Add(vm);

        vm.IsCheckingAccession.ShouldBeTrue();
        vm.AddAccessionCommand.CanExecute(null).ShouldBeFalse("a second press would ask NCBI twice");
        release.SetResult(AccessionLookupResult.Found(100, "NC_045512.2"));
        await adding;
        vm.IsCheckingAccession.ShouldBeFalse();
        vm.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_lookup_that_throws_is_reported_as_unreachable_and_not_crashed()
    {
        var vm = NewViewModel(lookup: new ThrowingLookup());
        vm.AccessionText = "NC_045512.2";

        await Add(vm);

        vm.Items.ShouldBeEmpty();
        vm.AccessionMessage.ShouldBe("NewRunAccessionLookup_Unreachable");
        vm.IsCheckingAccession.ShouldBeFalse();
    }

    [Fact]
    public async Task Run_starts_with_the_accession_and_range_and_no_input_path()
    {
        var vm = NewViewModel();
        vm.AccessionText = "NC_045512.2";
        vm.AccessionStartText = "100";
        vm.AccessionEndText = "500";
        await Add(vm);

        await vm.StartRunCommand.ExecuteAsync(null);

        await _jobEngine.Received(1).StartRunAsync(
            Arg.Is<RunOptions>(o => o.Accession == "NC_045512.2" && o.AccessionRange == "100-500" && o.InputPath == null && o.AnnotationPath == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("hello", "", "", "NewRunAccessionProblem_NotAnAccession")]
    [InlineData("NC_045512.2", "100", "", "NewRunAccessionProblem_RangeIncomplete")]
    [InlineData("NC_045512.2", "500", "100", "NewRunAccessionProblem_RangeInvalid")]
    public async Task A_bad_accession_or_range_says_what_to_fix_adds_no_pill_and_asks_NCBI_nothing(string accession, string start, string end, string messageKey)
    {
        var vm = NewViewModel();
        vm.AccessionText = accession;
        vm.AccessionStartText = start;
        vm.AccessionEndText = end;

        await Add(vm);

        vm.Items.ShouldBeEmpty();
        vm.AccessionMessage.ShouldBe(messageKey);
        vm.HasAccessionMessage.ShouldBeTrue();
        vm.AccessionText.ShouldBe(accession, "a mistyped accession stays in the box so it can be corrected");
        _lookup.Asked.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("accession")]
    [InlineData("start")]
    [InlineData("end")]
    public async Task Editing_any_box_clears_the_old_message(string box)
    {
        var vm = NewViewModel();
        vm.AccessionText = "hello";
        await Add(vm);
        vm.HasAccessionMessage.ShouldBeTrue();

        switch (box)
        {
            case "accession":
                vm.AccessionText = "NC_045512.2";
                break;
            case "start":
                vm.AccessionStartText = "1";
                break;
            default:
                vm.AccessionEndText = "9";
                break;
        }

        vm.AccessionMessage.ShouldBeEmpty();
        vm.HasAccessionMessage.ShouldBeFalse();
    }

    [Fact]
    public async Task The_same_accession_and_range_is_not_added_twice_and_NCBI_is_not_asked_again()
    {
        var vm = NewViewModel();
        vm.AccessionText = "NC_045512.2";
        await Add(vm);
        vm.AccessionText = "nc_045512.2";

        await Add(vm);

        vm.Items.Count.ShouldBe(1);
        vm.AccessionMessage.ShouldBe("NewRunAccessionAlreadyAdded");
        _lookup.Asked.Count.ShouldBe(1);
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
        await Add(vm);

        await vm.TreatAsRnaCommand.ExecuteAsync(null);

        vm.Items.ShouldHaveSingleItem().IsValid.ShouldBeTrue();
    }

    private sealed class Strings : IStringResourceProvider
    {
        public string GetString(string key) => key;
    }

    private sealed class GatedLookup(Task<AccessionLookupResult> answer) : IAccessionLookup
    {
        public Task<AccessionLookupResult> LookupAsync(string accession, string? range, CancellationToken cancellationToken) => answer;
    }

    private sealed class ThrowingLookup : IAccessionLookup
    {
        public Task<AccessionLookupResult> LookupAsync(string accession, string? range, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}
