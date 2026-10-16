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

/// <summary>Issue #108: files handed over by Open with go into the New run screen that is already showing, so a draft there is not thrown away.</summary>
public sealed class NewRunIntakeTests : IDisposable
{
    private const string Dna = "ACGTACGTACGTACGTACGTACGTACGTACGTACGTACGT";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-intake", Guid.NewGuid().ToString("N"));
    private readonly NewRunIntake _intake = new();

    public NewRunIntakeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private NewRunViewModel NewViewModel()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var estimates = Substitute.For<ICostEstimateService>();
        estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        return new NewRunViewModel(
            Substitute.For<IFilePicker>(),
            Substitute.For<IJobEngine>(),
            Substitute.For<INavigator>(),
            strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
            estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()),
            Substitute.For<IDiagnosticsLog>(),
            intake: _intake);
    }

    private string Write(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, ">x\n" + Dna + "\n");
        return path;
    }

    [Fact]
    public void With_no_New_run_screen_showing_nothing_is_taken()
        => _intake.TryAdd(new DroppedItems([Write("a.fasta")], 0, false)).ShouldBeFalse();

    [Fact]
    public async Task A_showing_New_run_screen_takes_the_files_and_keeps_what_was_already_there()
    {
        var viewModel = NewViewModel();
        var first = Write("first.fasta");
        var second = Write("second.fasta");
        await viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(new DroppedItems([first], 0, false));

        _intake.TryAdd(new DroppedItems([second], 0, false)).ShouldBeTrue();

        // AddDropped runs on the command's task; wait for the item to show.
        SpinWait.SpinUntil(() => viewModel.Items.Count == 2, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        viewModel.Items.Select(i => i.Path).ShouldBe([first, second]);
    }

    [Fact]
    public async Task After_the_screen_is_left_the_files_are_no_longer_taken()
    {
        var viewModel = NewViewModel();
        await viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(null);

        viewModel.DetachFromIntake();

        _intake.TryAdd(new DroppedItems([Write("a.fasta")], 0, false)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_screen_that_was_replaced_cannot_detach_its_successor()
    {
        var old = NewViewModel();
        var current = NewViewModel();
        await old.ReceiveNavigationParameterCommand.ExecuteAsync(null);
        await current.ReceiveNavigationParameterCommand.ExecuteAsync(null);

        old.DetachFromIntake();

        _intake.TryAdd(new DroppedItems([Write("a.fasta")], 0, false)).ShouldBeTrue();
    }
}
