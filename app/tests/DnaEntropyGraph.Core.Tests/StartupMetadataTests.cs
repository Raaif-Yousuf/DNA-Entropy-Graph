using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #261. <c>worker/vm/startup.sh</c> is a fixed template: every per-job
/// value reaches it as an instance metadata attribute and the script reads it
/// back with <c>meta()</c>, then uses it inside JSON, a docker command and a
/// shell arithmetic expansion. So the C# side cannot "escape" values into the
/// script text; it must reject anything outside the exact shape the script
/// can safely consume, and keep the metadata inside Compute Engine's limits.
/// </summary>
public class StartupMetadataTests
{
    private const string Digest = "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static IReadOnlyDictionary<string, string> Build(
        string bucket = "deg-my-project-abc",
        string jobId = "20261002-101500-abcdef",
        string image = Digest,
        bool expectGpu = true,
        AfterTaskAction lifecycle = AfterTaskAction.Stop,
        TimeSpan? maxRun = null)
        => StartupMetadata.Build(bucket, jobId, image, expectGpu, lifecycle, maxRun ?? TimeSpan.FromMinutes(240));

    private static string RepoStartupScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "worker", "vm", "startup.sh")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("worker/vm/startup.sh must be found above the test binary directory");
        return File.ReadAllText(Path.Combine(dir!.FullName, "worker", "vm", "startup.sh"));
    }

    [Fact]
    public void The_embedded_script_is_the_worker_vm_startup_script_with_unix_line_endings()
    {
        StartupMetadata.Script.ShouldBe(RepoStartupScript().Replace("\r\n", "\n"));
        StartupMetadata.Script.ShouldNotContain("\r");
        StartupMetadata.Script.ShouldStartWith("#!/usr/bin/env bash");
    }

    [Fact]
    public void Build_emits_the_script_and_exactly_the_attributes_the_script_reads()
    {
        var metadata = Build();

        metadata.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(
            ["deg-bucket", "deg-expect-gpu", "deg-job-id", "deg-lifecycle", "deg-max-run-min", "deg-worker-image", "startup-script"]);
        metadata["startup-script"].ShouldBe(StartupMetadata.Script);
        metadata["deg-bucket"].ShouldBe("deg-my-project-abc");
        metadata["deg-job-id"].ShouldBe("20261002-101500-abcdef");
        metadata["deg-worker-image"].ShouldBe(Digest);
        metadata["deg-expect-gpu"].ShouldBe("true");
        metadata["deg-lifecycle"].ShouldBe("stop");
        metadata["deg-max-run-min"].ShouldBe("240");
    }

    [Fact]
    public void Every_attribute_the_script_reads_via_meta_is_one_Build_emits()
    {
        var read = System.Text.RegularExpressions.Regex
            .Matches(StartupMetadata.Script, @"meta instance/attributes/([a-z0-9-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        read.ShouldNotBeEmpty();
        foreach (var attribute in read)
        {
            Build().ContainsKey(attribute).ShouldBeTrue($"startup.sh reads '{attribute}' but StartupMetadata.Build does not set it");
        }
    }

    [Theory]
    [InlineData(AfterTaskAction.Stop, "stop")]
    [InlineData(AfterTaskAction.Delete, "delete")]
    [InlineData(AfterTaskAction.KeepAlive, "keep")]
    public void Lifecycle_maps_to_the_words_the_script_switches_on(AfterTaskAction action, string word)
        => Build(lifecycle: action)["deg-lifecycle"].ShouldBe(word);

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public void Expect_gpu_is_the_literal_the_script_compares_against(bool expect, string literal)
        => Build(expectGpu: expect)["deg-expect-gpu"].ShouldBe(literal);

    [Theory]
    [InlineData(1, "1")]
    [InlineData(90, "90")]
    public void Max_run_is_whole_minutes(int minutes, string expected)
        => Build(maxRun: TimeSpan.FromMinutes(minutes))["deg-max-run-min"].ShouldBe(expected);

    [Fact]
    public void Max_run_rounds_up_to_a_whole_minute()
        => Build(maxRun: TimeSpan.FromSeconds(61))["deg-max-run-min"].ShouldBe("2");

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100001)]
    public void A_max_run_outside_one_minute_to_about_70_days_is_rejected(int minutes)
        => Should.Throw<ArgumentException>(() => Build(maxRun: TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("x$(reboot)")]
    [InlineData("a\"b")]
    [InlineData("a'b")]
    [InlineData("a;b")]
    [InlineData("a`b`")]
    [InlineData("a\nb")]
    [InlineData("../x")]
    [InlineData("JOB")]
    [InlineData("jobé")]
    public void An_unusual_job_id_is_rejected_not_escaped(string jobId)
        => Should.Throw<ArgumentException>(() => Build(jobId: jobId)).Message.ShouldContain("job id");

    [Theory]
    [InlineData("")]
    [InlineData("bucket name")]
    [InlineData("b$(x)")]
    [InlineData("b;rm")]
    [InlineData("Bucket")]
    [InlineData("ab")]
    public void An_unusual_bucket_is_rejected(string bucket)
        => Should.Throw<ArgumentException>(() => Build(bucket: bucket)).Message.ShouldContain("bucket");

    [Theory]
    [InlineData("ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda")]
    [InlineData("ghcr.io/x/y@sha256:abc")]
    [InlineData("ghcr.io/x/y@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef; reboot")]
    [InlineData("--privileged@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("")]
    public void A_worker_image_must_be_a_full_digest_reference(string image)
        => Should.Throw<ArgumentException>(() => Build(image: image)).Message.ShouldContain("image");

    [Fact]
    public void The_real_script_and_attributes_fit_well_inside_the_limits()
    {
        var metadata = Build();

        Should.NotThrow(() => StartupMetadata.EnsureSize(metadata));
        System.Text.Encoding.UTF8.GetByteCount(StartupMetadata.Script).ShouldBeLessThan(StartupMetadata.MaxValueBytes / 4);
    }

    [Fact]
    public void A_single_value_over_256_KiB_is_rejected_naming_the_key()
    {
        var metadata = new Dictionary<string, string> { ["startup-script"] = new string('x', StartupMetadata.MaxValueBytes + 1) };

        var ex = Should.Throw<InvalidOperationException>(() => StartupMetadata.EnsureSize(metadata));

        ex.Message.ShouldContain("startup-script");
    }

    [Fact]
    public void A_value_of_exactly_256_KiB_is_accepted()
        => Should.NotThrow(() => StartupMetadata.EnsureSize(new Dictionary<string, string> { ["k"] = new string('x', StartupMetadata.MaxValueBytes) }));

    [Fact]
    public void Multi_byte_characters_count_as_bytes_not_characters()
    {
        // 90k three-byte characters is 270,000 bytes: over the limit even
        // though the string is only 90,000 characters long.
        var metadata = new Dictionary<string, string> { ["k"] = new string('€', 90_000) };

        Should.Throw<InvalidOperationException>(() => StartupMetadata.EnsureSize(metadata));
    }

    [Fact]
    public void A_total_over_512_KiB_is_rejected_even_when_every_value_fits()
    {
        var metadata = new Dictionary<string, string>
        {
            ["a"] = new string('x', StartupMetadata.MaxValueBytes),
            ["b"] = new string('x', StartupMetadata.MaxValueBytes),
            ["c"] = "x",
        };

        Should.Throw<InvalidOperationException>(() => StartupMetadata.EnsureSize(metadata)).Message.ShouldContain("total");
    }

    [Fact]
    public void A_VmSpec_carrying_oversized_metadata_fails_its_own_preconditions()
    {
        var spec = new VmSpec("p", "i", "job-1", "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(1), "DELETE")
        {
            Metadata = new Dictionary<string, string> { ["startup-script"] = new string('x', StartupMetadata.MaxValueBytes + 1) },
        };

        Should.Throw<InvalidOperationException>(spec.EnsurePreconditions).Message.ShouldContain("startup-script");
    }

    [Fact]
    public void A_VmSpec_with_no_metadata_is_still_valid()
    {
        var spec = new VmSpec("p", "i", "job-1", "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(1), "DELETE");

        Should.NotThrow(spec.EnsurePreconditions);
        spec.Metadata.ShouldBeNull();
    }
}
