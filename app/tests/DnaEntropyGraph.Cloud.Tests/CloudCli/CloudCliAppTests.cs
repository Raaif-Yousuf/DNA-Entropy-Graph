using System.Text.Json;
using DnaEntropyGraph.CloudCli;
using DnaEntropyGraph.Core.Cloud;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.CloudCli;

/// <summary>Issue #60: the verbs, their output and their exit codes, over <see cref="FakeGcp"/> (no network) and NSubstitute for the refusal paths.</summary>
public class CloudCliAppTests
{
    private const string Project = "my-lab";

    private sealed class FixedSessions(IComputeGateway compute, IStorageGateway storage) : ICloudCliSessionFactory
    {
        public string? OpenedProject { get; private set; }

        public string? OpenedInstallation { get; private set; }

        public CloudCliSession Open(string projectId, string? installationId)
        {
            OpenedProject = projectId;
            OpenedInstallation = installationId;
            return new CloudCliSession(compute, storage);
        }
    }

    private sealed class SignedOutSessions : ICloudCliSessionFactory
    {
        public CloudCliSession Open(string projectId, string? installationId) => throw new CloudCliNotSignedInException();
    }

    private sealed record Outcome(int ExitCode, string Out, string Err);

    private static async Task<Outcome> RunAsync(ICloudCliSessionFactory sessions, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await new CloudCliApp(sessions, TimeProvider.System, new Random(60)).RunAsync(args, stdout, stderr, CancellationToken.None);
        return new Outcome(code, stdout.ToString(), stderr.ToString());
    }

    private static VmSpec Spec(string job, string installation) => new(
        ProjectId: Project, InstallationId: installation, JobId: job, Model: "evo2_7b", AppVersion: "0.1.0", Lifecycle: "test",
        MachineType: "g2-standard-8", MaxRunDuration: TimeSpan.FromMinutes(30), TerminationAction: "DELETE");

    private static async Task<(FakeGcp Fake, FixedSessions Sessions)> SeededAsync()
    {
        var fake = new FakeGcp();
        await fake.CreateVmAsync(Spec("job1", "inst-1"), "us-central1-a", CancellationToken.None);
        await fake.CreateVmAsync(Spec("job2", "inst-2"), "us-east1-b", CancellationToken.None);
        await fake.EnsureBucketAsync(Project, CancellationToken.None);
        return (fake, new FixedSessions(fake, fake));
    }

    private static void ShouldBeAscii(string text)
    {
        text.ShouldAllBe(c => c < 128 && (c >= 32 || c == '\n' || c == '\r' || c == '\t'));
    }

    [Fact]
    public async Task Resources_list_prints_every_labelled_vm_and_bucket_in_a_plain_ascii_table()
    {
        var (_, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "resources", "list", "--project", Project);

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("KIND");
        lines[0].ShouldContain("NAME");
        lines.ShouldContain(l => l.StartsWith("vm") && l.Contains("deg-job1") && l.Contains("us-central1-a") && l.Contains("inst-1"));
        lines.ShouldContain(l => l.StartsWith("vm") && l.Contains("deg-job2") && l.Contains("inst-2"));
        lines.ShouldContain(l => l.StartsWith("bucket") && l.Contains(FakeGcp.BucketName(Project)));
        lines[^1].TrimEnd().ShouldBe("OK: 3 resource(s)");
        ShouldBeAscii(result.Out);
        sessions.OpenedProject.ShouldBe(Project);
    }

    [Fact]
    public async Task Resources_list_with_install_keeps_only_that_installations_vms_and_passes_the_id_to_the_session()
    {
        var (_, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "resources", "list", "--project", Project, "--install", "inst-1", "--kind", "vm");

        result.Out.ShouldContain("deg-job1");
        result.Out.ShouldNotContain("deg-job2");
        result.Out.ShouldNotContain("bucket");
        sessions.OpenedInstallation.ShouldBe("inst-1");
    }

    [Fact]
    public async Task Resources_list_with_a_foreign_install_drops_the_bucket_too()
    {
        var (_, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "resources", "list", "--project", Project, "--install", "someone-else", "--json");

        JsonDocument.Parse(result.Out).RootElement.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Resources_list_json_is_an_array_the_leak_scan_can_read_by_name_and_zone()
    {
        var (_, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "resources", "list", "--project", Project, "--install", "inst-1", "--kind", "vm", "--json");

        using var document = JsonDocument.Parse(result.Out);
        var vm = document.RootElement.EnumerateArray().Single();
        vm.GetProperty("kind").GetString().ShouldBe("vm");
        vm.GetProperty("name").GetString().ShouldBe("deg-job1");
        vm.GetProperty("zone").GetString().ShouldBe("us-central1-a");
        vm.GetProperty("installation").GetString().ShouldBe("inst-1");
        vm.GetProperty("job").GetString().ShouldBe("job1");
    }

    [Fact]
    public async Task Resources_list_of_nothing_is_an_empty_table_and_an_empty_json_array()
    {
        var fake = new FakeGcp();
        var sessions = new FixedSessions(fake, fake);

        var table = await RunAsync(sessions, "resources", "list", "--project", Project);
        var json = await RunAsync(sessions, "resources", "list", "--project", Project, "--json");

        table.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        table.Out.ShouldContain("OK: 0 resource(s)");
        table.Out.ShouldNotContain("deg-");
        json.Out.Trim().ShouldBe("[]");
    }

    [Fact]
    public async Task Bucket_describe_prints_name_location_and_labels()
    {
        var (_, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "bucket", "describe", "--project", Project);

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("name: " + FakeGcp.BucketName(Project));
        result.Out.ShouldContain("location:");
        result.Out.ShouldContain("app=dna-entropy-graph");
        ShouldBeAscii(result.Out);
    }

    [Fact]
    public async Task Bucket_describe_of_a_name_that_is_not_there_exits_not_found_and_says_what_to_do()
    {
        var (_, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "bucket", "describe", "--project", Project, "--name", "deg-nothing");

        result.ExitCode.ShouldBe(CloudCliExitCode.NotFound);
        result.Err.ShouldStartWith("ERROR:");
        result.Err.ShouldContain("resources list");
    }

    [Fact]
    public async Task Vm_create_goes_through_VmSpec_so_the_vm_exists_with_every_standard_label()
    {
        var fake = new FakeGcp();
        var sessions = new FixedSessions(fake, fake);

        var result = await RunAsync(
            sessions, "vm", "create", "--project", Project, "--zone", "us-central1-a", "--install", "inst-1", "--job", "job9",
            "--service-account", "w@my-lab.iam.gserviceaccount.com", "--max-run-duration", "20m");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("OK: created deg-job9 in us-central1-a");
        var created = (await fake.ListByInstallationAsync("inst-1", CancellationToken.None)).Single();
        created.Name.ShouldBe("deg-job9");
        created.Labels!.Keys.ShouldBe(["app", "installation-id", "job-id", "model", "app-version", "lifecycle"], ignoreOrder: true);
        created.Labels["lifecycle"].ShouldBe("test");
    }

    [Fact]
    public async Task Vm_create_without_a_job_generates_one_that_follows_the_job_id_convention()
    {
        var fake = new FakeGcp();

        var result = await RunAsync(
            new FixedSessions(fake, fake), "vm", "create", "--project", Project, "--zone", "us-central1-a", "--install", "inst-1",
            "--service-account", "w@my-lab.iam.gserviceaccount.com");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        var name = (await fake.ListByInstallationAsync("inst-1", CancellationToken.None)).Single().Name;
        JobId.MatchesConvention(name["deg-".Length..]).ShouldBeTrue();
    }

    [Fact]
    public async Task Vm_create_with_a_spec_VmSpec_rejects_is_a_usage_error_and_creates_nothing()
    {
        var fake = new FakeGcp();

        var result = await RunAsync(
            new FixedSessions(fake, fake), "vm", "create", "--project", Project, "--zone", "us-central1-a", "--install", "inst-1",
            "--job", "Bad Job", "--service-account", "w@my-lab.iam.gserviceaccount.com");

        result.ExitCode.ShouldBe(CloudCliExitCode.Usage);
        result.Err.ShouldStartWith("ERROR:");
        (await fake.ListByInstallationAsync("inst-1", CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("dna-entropy-box")]
    [InlineData("job1")]
    [InlineData("DEG-job1")]
    public async Task Vm_delete_refuses_a_name_without_the_deg_prefix_before_any_call(string name)
    {
        var compute = Substitute.For<IComputeGateway>();
        var sessions = new FixedSessions(compute, Substitute.For<IStorageGateway>());

        var result = await RunAsync(sessions, "vm", "delete", "--project", Project, "--zone", "us-central1-a", "--name", name);

        result.ExitCode.ShouldBe(CloudCliExitCode.Refused);
        result.Err.ShouldStartWith("ERROR:");
        result.Err.ShouldContain("deg-");
        compute.ReceivedCalls().ShouldBeEmpty();
        sessions.OpenedProject.ShouldBeNull();
    }

    [Fact]
    public async Task Vm_delete_refuses_a_deg_named_vm_that_lacks_the_app_label_and_never_deletes_it()
    {
        var compute = Substitute.For<IComputeGateway>();
        compute.GetVmAsync("deg-stranger", "us-central1-a", Arg.Any<CancellationToken>())
            .Returns(new VmDescriptor("deg-stranger", "us-central1-a", "RUNNING", Labels: new Dictionary<string, string> { ["team"] = "x" }));

        var result = await RunAsync(new FixedSessions(compute, Substitute.For<IStorageGateway>()), "vm", "delete", "--project", Project, "--zone", "us-central1-a", "--name", "deg-stranger");

        result.ExitCode.ShouldBe(CloudCliExitCode.Refused);
        await compute.DidNotReceive().DeleteVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Vm_delete_of_a_vm_whose_labels_are_unknown_is_refused_not_assumed_ours()
    {
        var compute = Substitute.For<IComputeGateway>();
        compute.GetVmAsync("deg-job1", "z", Arg.Any<CancellationToken>()).Returns(new VmDescriptor("deg-job1", "z", "RUNNING", Labels: null));

        var result = await RunAsync(new FixedSessions(compute, Substitute.For<IStorageGateway>()), "vm", "delete", "--project", Project, "--zone", "z", "--name", "deg-job1");

        result.ExitCode.ShouldBe(CloudCliExitCode.Refused);
        await compute.DidNotReceive().DeleteVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Vm_delete_removes_our_labelled_vm_and_leaves_the_others()
    {
        var (fake, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "vm", "delete", "--project", Project, "--zone", "us-central1-a", "--name", "deg-job1");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("OK: deleted deg-job1");
        (await fake.GetVmAsync("deg-job1", "us-central1-a", CancellationToken.None)).ShouldBeNull();
        (await fake.GetVmAsync("deg-job2", "us-east1-b", CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Vm_delete_of_a_vm_that_is_already_gone_succeeds_without_a_delete_call()
    {
        var compute = Substitute.For<IComputeGateway>();
        compute.GetVmAsync("deg-job1", "z", Arg.Any<CancellationToken>()).Returns((VmDescriptor?)null);

        var result = await RunAsync(new FixedSessions(compute, Substitute.For<IStorageGateway>()), "vm", "delete", "--project", Project, "--zone", "z", "--name", "deg-job1");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("not found");
        await compute.DidNotReceive().DeleteVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Not_signed_in_is_exit_3_with_one_ascii_line_naming_the_action_and_nothing_on_stdout()
    {
        var result = await RunAsync(new SignedOutSessions(), "resources", "list", "--project", Project);

        result.ExitCode.ShouldBe(CloudCliExitCode.NotSignedIn);
        result.Out.ShouldBeEmpty();
        result.Err.Trim().ShouldBe("ERROR: Sign in with the app first (open DNA Entropy Graph and choose Sign in), then run this again.");
        ShouldBeAscii(result.Err);
    }

    [Fact]
    public async Task A_cloud_failure_is_exit_1_with_the_api_message_made_ascii_and_an_action()
    {
        var compute = Substitute.For<IComputeGateway>();
        compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VmDescriptor>>>(_ =>
            throw new CloudOperationException(new CloudError("PERMISSION_DENIED", 403, "Not allowed — ask the project owner."), CloudErrorKind.Permission));

        var result = await RunAsync(new FixedSessions(compute, Substitute.For<IStorageGateway>()), "resources", "list", "--project", Project, "--kind", "vm");

        result.ExitCode.ShouldBe(CloudCliExitCode.CloudError);
        result.Err.ShouldStartWith("ERROR:");
        result.Err.ShouldContain("permission");
        ShouldBeAscii(result.Err);
    }

    [Fact]
    public async Task A_usage_error_is_exit_2_with_the_usage_text_on_stderr_and_no_session_opened()
    {
        var (_, sessions) = await SeededAsync();

        var result = await RunAsync(sessions, "resources", "list");

        result.ExitCode.ShouldBe(CloudCliExitCode.Usage);
        result.Err.ShouldStartWith("ERROR:");
        result.Err.ShouldContain("--project");
        result.Out.ShouldBeEmpty();
        sessions.OpenedProject.ShouldBeNull();
    }

    [Fact]
    public void Every_exit_code_is_distinct()
    {
        int[] codes = [CloudCliExitCode.Ok, CloudCliExitCode.CloudError, CloudCliExitCode.Usage, CloudCliExitCode.NotSignedIn, CloudCliExitCode.Refused, CloudCliExitCode.NotFound];

        codes.Distinct().Count().ShouldBe(6);
    }
}
