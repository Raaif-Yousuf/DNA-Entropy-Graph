using System.Text;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.CloudCli;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.CloudCli;

/// <summary>
/// Issue #759: <c>vm create</c> can attach the worker startup metadata (image, bucket, GPU expectation, gpu-test mode), and two new
/// verbs, <c>bucket ensure</c> and <c>object get</c>, let scripts/cloud_gpu_test.ps1 find the bucket and pull the log back.
/// </summary>
public class CloudCliGpuTestModeTests
{
    private const string Project = "my-lab";
    private const string Image = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string[] BaseCreate =
    [
        "vm", "create", "--project", Project, "--zone", "us-central1-a", "--install", "inst-1", "--job", "job9",
        "--service-account", "w@my-lab.iam.gserviceaccount.com", "--max-run-duration", "20m",
    ];

    private sealed record Outcome(int ExitCode, string Out, string Err);

    private sealed class Sessions(FakeGcp fake) : ICloudCliSessionFactory
    {
        public CloudCliSession Open(string projectId, string? installationId) => new(fake, fake);
    }

    private static async Task<Outcome> RunAsync(FakeGcp fake, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await new CloudCliApp(new Sessions(fake), TimeProvider.System, new Random(759)).RunAsync(args, stdout, stderr, CancellationToken.None);
        return new Outcome(code, stdout.ToString(), stderr.ToString());
    }

    private static string[] Create(params string[] extra) => [.. BaseCreate, .. extra];

    private static VmCreateCommand ParseCreate(params string[] extra)
    {
        var parsed = CloudCliParser.Parse(Create(extra));
        parsed.Error.ShouldBeNull();
        return parsed.Command.ShouldBeOfType<VmCreateCommand>();
    }

    [Fact]
    public void Vm_create_reads_the_worker_image_bucket_startup_mode_and_gpu_expectation()
    {
        var command = ParseCreate("--worker-image", Image, "--bucket", "deg-my-lab-1", "--startup-mode", "gpu-test", "--expect-gpu", "true");

        command.WorkerImage.ShouldBe(Image);
        command.Bucket.ShouldBe("deg-my-lab-1");
        command.StartupMode.ShouldBe("gpu-test");
        command.ExpectGpu.ShouldBeTrue();
    }

    [Fact]
    public void Vm_create_without_a_worker_image_has_no_startup_fields()
    {
        var command = ParseCreate();

        (command.WorkerImage, command.Bucket, command.StartupMode, command.ExpectGpu).ShouldBe((null, null, null, false));
    }

    [Theory]
    [InlineData(new[] { "--worker-image", Image }, "--bucket")]
    [InlineData(new[] { "--bucket", "deg-my-lab-1" }, "--worker-image")]
    [InlineData(new[] { "--startup-mode", "gpu-test" }, "--worker-image")]
    [InlineData(new[] { "--expect-gpu", "true" }, "--worker-image")]
    [InlineData(new[] { "--worker-image", Image, "--bucket", "b-1", "--startup-mode", "turbo" }, "--startup-mode")]
    [InlineData(new[] { "--worker-image", Image, "--bucket", "b-1", "--expect-gpu", "maybe" }, "--expect-gpu")]
    public void Vm_create_startup_flags_that_do_not_fit_together_are_a_usage_error_naming_the_flag(string[] extra, string named)
    {
        var result = CloudCliParser.Parse(Create(extra));

        result.Command.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain(named);
    }

    [Fact]
    public void Object_get_and_bucket_ensure_parse()
    {
        var get = CloudCliParser.Parse(["object", "get", "--project", Project, "--bucket", "b-1", "--key", "jobs/j/logs/gpu-test.log"]);
        var command = get.Command.ShouldBeOfType<ObjectGetCommand>();
        (command.Project, command.Bucket, command.Key).ShouldBe((Project, "b-1", "jobs/j/logs/gpu-test.log"));
        CloudCliParser.Parse(["object", "get", "--project", Project, "--bucket", "b-1"]).Error.ShouldNotBeNull().ShouldContain("--key");
        CloudCliParser.Parse(["object", "get", "--project", Project, "--key", "k"]).Error.ShouldNotBeNull().ShouldContain("--bucket");
        CloudCliParser.Parse(["bucket", "ensure", "--project", Project]).Command.ShouldBeOfType<BucketEnsureCommand>().Project.ShouldBe(Project);
    }

    [Fact]
    public async Task Vm_create_with_a_worker_image_attaches_the_startup_script_and_the_gpu_test_mode()
    {
        var fake = new FakeGcp();

        var result = await RunAsync(fake, Create("--worker-image", Image, "--bucket", "deg-my-lab-1", "--startup-mode", "gpu-test", "--expect-gpu", "true"));

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok, result.Err);
        var metadata = fake.CreatedSpecs.Single().Metadata.ShouldNotBeNull();
        metadata[StartupMetadata.ScriptKey].ShouldBe(StartupMetadata.Script);
        metadata["deg-worker-image"].ShouldBe(Image);
        metadata["deg-bucket"].ShouldBe("deg-my-lab-1");
        metadata["deg-job-id"].ShouldBe("job9");
        metadata["deg-expect-gpu"].ShouldBe("true");
        metadata["deg-lifecycle"].ShouldBe("delete");
        metadata["deg-max-run-min"].ShouldBe("20");
        metadata["deg-mode"].ShouldBe("gpu-test");
    }

    [Fact]
    public async Task Vm_create_with_a_worker_image_in_the_default_mode_sets_no_gpu_test_marker()
    {
        var fake = new FakeGcp();

        var result = await RunAsync(fake, Create("--worker-image", Image, "--bucket", "deg-my-lab-1"));

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok, result.Err);
        var metadata = fake.CreatedSpecs.Single().Metadata.ShouldNotBeNull();
        metadata.ContainsKey("deg-mode").ShouldBeFalse();
        metadata["deg-expect-gpu"].ShouldBe("false");
    }

    [Fact]
    public async Task Vm_create_without_a_worker_image_attaches_no_metadata()
    {
        var fake = new FakeGcp();

        (await RunAsync(fake, Create())).ExitCode.ShouldBe(CloudCliExitCode.Ok);

        fake.CreatedSpecs.Single().Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task Vm_create_with_an_image_not_pinned_by_digest_is_refused_before_any_VM_exists()
    {
        var fake = new FakeGcp();

        var result = await RunAsync(fake, Create("--worker-image", "ghcr.io/raaif-yousuf/dna-entropy-worker:latest", "--bucket", "deg-my-lab-1"));

        result.ExitCode.ShouldBe(CloudCliExitCode.Usage);
        result.Err.ShouldStartWith("ERROR:");
        result.Err.ShouldContain("digest");
        fake.CreatedSpecs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bucket_ensure_prints_the_bucket_name_in_text_and_json()
    {
        var fake = new FakeGcp();

        var text = await RunAsync(fake, "bucket", "ensure", "--project", Project);
        var json = await RunAsync(fake, "bucket", "ensure", "--project", Project, "--json");

        text.ExitCode.ShouldBe(CloudCliExitCode.Ok, text.Err);
        text.Out.ShouldContain(FakeGcp.BucketName(Project));
        text.Out.TrimEnd().ShouldStartWith("OK:");
        json.Out.Trim().ShouldBe($"{{\"name\":\"{FakeGcp.BucketName(Project)}\"}}");
    }

    [Fact]
    public async Task Object_get_prints_the_object_and_a_missing_object_is_exit_5_naming_an_action()
    {
        var fake = new FakeGcp();
        var bucket = FakeGcp.BucketName(Project);
        fake.PutObject(bucket, "jobs/job9/logs/gpu-test.log", Encoding.UTF8.GetBytes("collected 3 items\n3 passed ✓\n"));

        var found = await RunAsync(fake, "object", "get", "--project", Project, "--bucket", bucket, "--key", "jobs/job9/logs/gpu-test.log");
        var missing = await RunAsync(fake, "object", "get", "--project", Project, "--bucket", bucket, "--key", "jobs/job9/logs/none.log");

        found.ExitCode.ShouldBe(CloudCliExitCode.Ok, found.Err);
        found.Out.ShouldBe("collected 3 items\n3 passed ?\n".Replace("\n", Environment.NewLine));
        missing.ExitCode.ShouldBe(CloudCliExitCode.NotFound);
        missing.Err.ShouldStartWith("ERROR:");
        missing.Err.ShouldContain("not there yet");
        missing.Out.ShouldBeEmpty();
    }
}
