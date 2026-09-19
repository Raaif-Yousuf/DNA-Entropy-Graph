using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public class NewRunViewModelTests
{
    private static NewRunViewModel CreateViewModel(
        out IFilePicker filePicker,
        out IJobEngine jobEngine,
        out INavigator navigator)
    {
        filePicker = Substitute.For<IFilePicker>();
        jobEngine = Substitute.For<IJobEngine>();
        navigator = Substitute.For<INavigator>();
        var settingsStore = Substitute.For<ISettingsStore>();

        return new NewRunViewModel(filePicker, settingsStore, jobEngine, navigator);
    }

    [Fact]
    public void StartRun_cannot_execute_with_no_input_selected()
    {
        var viewModel = CreateViewModel(out _, out _, out _);

        viewModel.StartRunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Browsing_a_file_makes_start_run_executable()
    {
        var viewModel = CreateViewModel(out var filePicker, out _, out _);
        filePicker.PickInputFileAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(@"C:\seq.fasta"));

        await viewModel.BrowseCommand.ExecuteAsync(null);

        viewModel.SelectedInputPath.ShouldBe(@"C:\seq.fasta");
        viewModel.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Starting_a_run_navigates_to_run_progress_with_the_new_job_id()
    {
        var viewModel = CreateViewModel(out var filePicker, out var jobEngine, out var navigator);
        filePicker.PickInputFileAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(@"C:\seq.fasta"));
        jobEngine.StartRunAsync(Arg.Any<Core.RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-123"));

        await viewModel.BrowseCommand.ExecuteAsync(null);
        await viewModel.StartRunCommand.ExecuteAsync(null);

        navigator.Received(1).NavigateTo("RunProgress", "job-123");
    }
}
