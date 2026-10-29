using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #514: the result folder is named from <c>RunOptions.NameTemplate</c>, through the one function the New run page's preview also
/// uses (<see cref="RunOutputFolders.RunFolderName"/>). Each test reads the folder a run really created on disk, not a method's return.
/// </summary>
public class CloudJobRunnerRunNameTests
{
    private static VmSpec Spec(string jobId) => new(
        ProjectId: "my-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudJobRequest Request(string jobId, string? template, string? outputFolder = null)
    {
        var request = TestInputs.Request(jobId, Spec(jobId));
        if (template is not null)
        {
            request = request with { Options = request.Options with { NameTemplate = template } };
        }

        return outputFolder is null ? request : request with { OutputFolder = outputFolder };
    }

    private static async Task<string> RunAsync(CloudJobRequest request)
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var clocks = new RunnerClocks();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(60),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        return repo.AllRecordedInOrder.Last(r => r.JobId == request.JobId).OutputDir!;
    }

    [Fact]
    public async Task A_file_and_model_template_names_the_result_folder_after_both()
    {
        var request = Request("job-n1", "{file}_{model}");

        var folder = await RunAsync(request);

        Path.GetFileName(folder).ShouldBe("seq_evo2_7b");
        Path.GetDirectoryName(folder).ShouldBe(request.OutputFolder);
        // The page's preview is this same function, so the same options give the same text.
        Path.GetFileName(folder).ShouldBe(RunOutputFolders.RunFolderName(request.Options, request.Inputs[0], DateTime.Now));
        File.Exists(Path.Combine(folder, "seq", "seq.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_default_template_gives_the_folder_the_input_stem_as_before()
    {
        var folder = await RunAsync(Request("job-n2", template: null));

        Path.GetFileName(folder).ShouldBe("seq");
    }

    [Fact]
    public async Task A_name_already_in_the_output_folder_gets_the_suffix_2_and_the_first_folder_is_left_alone()
    {
        var first = Request("job-n3", "{file}_{model}");
        var firstFolder = await RunAsync(first);

        var secondFolder = await RunAsync(Request("job-n4", "{file}_{model}", first.OutputFolder));

        Path.GetFileName(firstFolder).ShouldBe("seq_evo2_7b");
        Path.GetFileName(secondFolder).ShouldBe("seq_evo2_7b_2");
        File.Exists(Path.Combine(firstFolder, "seq", "seq.bedgraph")).ShouldBeTrue();
    }
}
