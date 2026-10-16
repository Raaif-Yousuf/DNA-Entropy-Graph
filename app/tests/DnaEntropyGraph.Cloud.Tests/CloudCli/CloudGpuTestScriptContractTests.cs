using System.Diagnostics;
using System.Text.Json;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.CloudCli;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.CloudCli;

/// <summary>
/// Issue #619: the argument list scripts/cloud_gpu_test.ps1 hands to <c>vm create</c> must be one the real
/// <see cref="CloudCliParser"/> accepts. The script prints its list with <c>-PrintApplyArgs</c> (no cloud call); this feeds that exact
/// list through the real CLI over <see cref="FakeGcp"/> so a renamed flag on either side fails here instead of at the first real run.
/// </summary>
public class CloudGpuTestScriptContractTests
{
    private const string Project = "my-lab";
    private const string Job = "20261015-120000-ab3xy9";

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "cloud_gpu_test.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("scripts/cloud_gpu_test.ps1 was not found above " + AppContext.BaseDirectory);
    }

    private static string[] PrintedArgs(params string[] extra)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var token in new[]
        {
            "-NoProfile", "-File", ScriptPath(), "-ProjectId", Project, "-ServiceAccount", "w@my-lab.iam.gserviceaccount.com",
            "-Zone", "us-central1-a", "-JobId", Job, "-InstallationId", "inst123", "-MaxRunDurationSeconds", "1200", "-PrintApplyArgs",
        }.Concat(extra))
        {
            start.ArgumentList.Add(token);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, stderr);
        return JsonSerializer.Deserialize<string[]>(stdout)!;
    }

    [Fact]
    public async Task The_scripts_vm_create_args_parse_and_create_a_VM_with_the_six_labels_DELETE_and_the_duration()
    {
        var args = PrintedArgs();
        var fake = new FakeGcp();
        var sessions = new FixedSessions(fake);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await new CloudCliApp(sessions, TimeProvider.System, new Random(619)).RunAsync(args, stdout, stderr, CancellationToken.None);

        code.ShouldBe(CloudCliExitCode.Ok, stderr.ToString());
        var spec = fake.CreatedSpecs.Single();
        spec.VmName.ShouldBe("deg-" + Job);
        spec.TerminationAction.ShouldBe("DELETE");
        spec.MaxRunDuration.ShouldBe(TimeSpan.FromSeconds(1200));
        spec.ToLabels().Keys.ShouldBe(["app", "installation-id", "job-id", "model", "app-version", "lifecycle"], ignoreOrder: true);
        spec.ToLabels()["installation-id"].ShouldBe("inst123");
        spec.ToLabels()["job-id"].ShouldBe(Job);
    }

    [Fact]
    public void The_scripts_vm_create_args_are_accepted_by_the_parser_with_the_values_the_script_meant()
    {
        var parsed = CloudCliParser.Parse(PrintedArgs());

        parsed.Error.ShouldBeNull();
        var create = parsed.Command.ShouldBeOfType<VmCreateCommand>();
        (create.Project, create.Zone, create.Install, create.Job).ShouldBe((Project, "us-central1-a", "inst123", Job));
        create.ServiceAccount.ShouldBe("w@my-lab.iam.gserviceaccount.com");
        create.MaxRunDuration.ShouldBe(TimeSpan.FromSeconds(1200));
    }

    private sealed class FixedSessions(FakeGcp fake) : ICloudCliSessionFactory
    {
        public CloudCliSession Open(string project, string? installationId) => new(fake, fake);
    }
}
