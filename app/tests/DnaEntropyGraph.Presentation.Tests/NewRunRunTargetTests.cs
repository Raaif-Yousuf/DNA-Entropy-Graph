using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issues #120 and #173: the place chosen in the picker is the place the run is sent to. Without this, a saved choice of This PC
/// would be a setting saved and never read (the run would still say Cloud).
/// </summary>
public sealed class NewRunRunTargetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-newrun-target-" + Guid.NewGuid().ToString("N"));
    private readonly IJobEngine _jobEngine = Substitute.For<IJobEngine>();
    private readonly ICostEstimateService _estimates = Substitute.For<ICostEstimateService>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly Dictionary<string, string> _saved = [];

    public NewRunRunTargetTests()
    {
        Directory.CreateDirectory(_dir);
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
    }

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

    private sealed class Settings(Dictionary<string, string> values) : ISettingsStore
    {
        public string? GetString(string key) => values.GetValueOrDefault(key);

        public void SetString(string key, string value) => values[key] = value;
    }

    private NewRunViewModel Create(bool withPicker)
    {
        var settings = new Settings(_saved);
        var picker = withPicker ? new RunTargetPickerViewModel(settings, Substitute.For<ILocalReadinessSource>(), _strings) : null;
        return new NewRunViewModel(
            Substitute.For<IFilePicker>(),
            _jobEngine,
            Substitute.For<INavigator>(),
            _strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
            _estimates,
            new OutputSelectionStore(settings),
            NullDiagnosticsLog.Instance,
            runTarget: picker);
    }

    private async Task<RunOptions> RunAsync(NewRunViewModel vm)
    {
        var path = Path.Combine(_dir, "a.fasta");
        await File.WriteAllTextAsync(path, ">a\n" + string.Concat(Enumerable.Repeat("ACGT", 40)) + "\n", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await vm.AddDroppedCommand.ExecuteAsync(new DroppedItems([path], 0, false));
        await vm.StartRunCommand.ExecuteAsync(null);
        return (RunOptions)_jobEngine.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IJobEngine.StartRunAsync)).GetArguments()[0]!;
    }

    [Fact]
    public async Task A_saved_This_PC_choice_reaches_the_runs_options()
    {
        _saved[RunTargetPickerViewModel.SettingKey] = "This PC";

        (await RunAsync(Create(withPicker: true))).RunTarget.ShouldBe("This PC");
    }

    [Fact]
    public async Task A_saved_Auto_choice_reaches_the_runs_options()
    {
        _saved[RunTargetPickerViewModel.SettingKey] = "Auto";

        (await RunAsync(Create(withPicker: true))).RunTarget.ShouldBe("Auto");
    }

    [Fact]
    public async Task With_nothing_saved_the_run_goes_to_the_cloud_as_before()
    {
        (await RunAsync(Create(withPicker: true))).RunTarget.ShouldBe("Cloud");
    }

    [Fact]
    public async Task A_host_with_no_picker_runs_in_the_cloud()
    {
        (await RunAsync(Create(withPicker: false))).RunTarget.ShouldBe("Cloud");
    }
}
