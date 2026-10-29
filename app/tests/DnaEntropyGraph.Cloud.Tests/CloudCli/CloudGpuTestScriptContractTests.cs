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

    // #874: walk up from this source file first (the tree actually compiled), then from the output directory.
    private static string ScriptPath([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(thisFile), AppContext.BaseDirectory })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "scripts", "cloud_gpu_test.ps1");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("scripts/cloud_gpu_test.ps1 was not found above " + thisFile + " or " + AppContext.BaseDirectory);
    }

    private sealed record Printed(string[] Create, string[] Delete, string[] List, string[] Leak, string[] Ensure, string[] Get);

    private const string Digest = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task With_an_image_the_scripts_gpu_test_create_ensure_and_get_lists_parse_and_attach_the_startup_metadata()
    {
        var printed = PrintedLists("-Image", Digest, "-Bucket", "deg-my-lab-1");

        var create = CloudCliParser.Parse(printed.Create).Command.ShouldBeOfType<VmCreateCommand>();
        (create.WorkerImage, create.Bucket, create.StartupMode, create.ExpectGpu).ShouldBe((Digest, "deg-my-lab-1", "gpu-test", true));
        CloudCliParser.Parse(printed.Ensure).Command.ShouldBeOfType<BucketEnsureCommand>().Json.ShouldBeTrue();
        var get = CloudCliParser.Parse(printed.Get).Command.ShouldBeOfType<ObjectGetCommand>();
        (get.Bucket, get.Key).ShouldBe(("deg-my-lab-1", "jobs/" + Job + "/logs/gpu-test-result.json"));

        var fake = new FakeGcp();
        var stderr = new StringWriter();
        (await Cli(new FixedSessions(fake)).RunAsync(printed.Create, new StringWriter(), stderr, CancellationToken.None)).ShouldBe(CloudCliExitCode.Ok, stderr.ToString());
        var metadata = fake.CreatedSpecs.Single().Metadata.ShouldNotBeNull();
        (metadata["deg-mode"], metadata["deg-worker-image"], metadata["deg-bucket"], metadata["deg-expect-gpu"], metadata["deg-job-id"])
            .ShouldBe(("gpu-test", Digest, "deg-my-lab-1", "true", Job));
    }

    private static Printed PrintedLists(params string[] extra)
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
        return JsonSerializer.Deserialize<Printed>(stdout, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public async Task The_scripts_vm_create_args_parse_and_create_a_VM_with_the_six_labels_DELETE_and_the_duration()
    {
        var printed = PrintedLists();
        var args = printed.Create;
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

        // The script's own leak-scan, delete and table lists run through the same real CLI: the VM it created is found, then deleted.
        var leakOut = new StringWriter();
        (await Cli(sessions).RunAsync(printed.Leak, leakOut, stderr, CancellationToken.None)).ShouldBe(CloudCliExitCode.Ok, stderr.ToString());
        JsonDocument.Parse(leakOut.ToString()).RootElement.GetArrayLength().ShouldBe(1);
        (await Cli(sessions).RunAsync(printed.Delete, new StringWriter(), stderr, CancellationToken.None)).ShouldBe(CloudCliExitCode.Ok, stderr.ToString());
        (await fake.ListByInstallationAsync("inst123", CancellationToken.None)).ShouldBeEmpty();
        var tableOut = new StringWriter();
        (await Cli(sessions).RunAsync(printed.List, tableOut, stderr, CancellationToken.None)).ShouldBe(CloudCliExitCode.Ok, stderr.ToString());
        tableOut.ToString().TrimEnd().ShouldEndWith("OK: 0 resource(s)");
    }

    [Fact]
    public void The_scripts_delete_and_list_args_parse_with_the_values_the_script_meant()
    {
        var printed = PrintedLists();

        var delete = CloudCliParser.Parse(printed.Delete).Command.ShouldBeOfType<VmDeleteCommand>();
        (delete.Project, delete.Zone, delete.Name).ShouldBe((Project, "us-central1-a", "deg-" + Job));
        var list = CloudCliParser.Parse(printed.List).Command.ShouldBeOfType<ResourcesListCommand>();
        (list.Project, list.Install, list.Kind, list.Json).ShouldBe((Project, "inst123", ResourceKind.Vm, false));
        var leak = CloudCliParser.Parse(printed.Leak).Command.ShouldBeOfType<ResourcesListCommand>();
        (leak.Install, leak.Kind, leak.Json).ShouldBe(("inst123", ResourceKind.Vm, true));
    }

    private static CloudCliApp Cli(ICloudCliSessionFactory sessions) => new(sessions, TimeProvider.System, new Random(619));

    [Fact]
    public void The_scripts_vm_create_args_are_accepted_by_the_parser_with_the_values_the_script_meant()
    {
        var parsed = CloudCliParser.Parse(PrintedLists().Create);

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
