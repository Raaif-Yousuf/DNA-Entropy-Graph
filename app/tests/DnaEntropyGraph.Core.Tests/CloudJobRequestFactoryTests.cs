using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>The pre-#460 call shape: tests of labels, zones and machine types do not care about transfers, so they get one staged input.</summary>
internal static class FactoryCall
{
    public static CloudJobRequest Create(RunOptions options, string jobId, string projectId, string installationId, string appVersion, string? workerImage = null)
        => CloudJobRequestFactory.Create(options, jobId, new CloudScope(projectId), installationId, appVersion, workerImage, [new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.gb", "a.gb")], @"C:\out");
}

/// <summary>
/// Issue #428: JobEngine turns a user's <see cref="RunOptions"/> into the
/// <see cref="CloudJobRequest"/> <see cref="CloudJobRunner"/> consumes. The
/// factory is pure so every Hard Rule 10 field (labels, maxRunDuration,
/// termination action) is provable without a gateway.
/// </summary>
public class CloudJobRequestFactoryTests
{
    private static RunOptions Options(Action<RunOptionsBuilder>? tweak = null)
    {
        var builder = new RunOptionsBuilder();
        tweak?.Invoke(builder);
        return builder.Build();
    }

    private sealed class RunOptionsBuilder
    {
        public string? ZonePreference { get; set; }

        public GpuTier GpuTier { get; set; } = GpuTier.CheapestAvailable;

        public AfterTaskAction AfterTask { get; set; } = AfterTaskAction.Stop;

        public int MaxRunDurationMinutes { get; set; } = 240;

        public RunOptions Build() => new()
        {
            ModelId = "evo2_7b",
            RunTarget = "Cloud",
            ZonePreference = ZonePreference,
            GpuTier = GpuTier,
            AfterTask = AfterTask,
            MaxRunDurationMinutes = MaxRunDurationMinutes,
        };
    }

    [Fact]
    public void The_request_carries_the_scope_it_was_created_in_and_the_spec_names_that_scopes_project()
    {
        var scope = new CloudScope("my-project", "sub-a");

        var request = CloudJobRequestFactory.Create(Options(), "20260101-000000-abcdef", scope, "install-1", "0.0.1", null, [new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.gb", "a.gb")], @"C:\out");

        request.Scope.ShouldBe(scope);
        request.ResolvedScope.ShouldBe(scope);
        request.Spec.ProjectId.ShouldBe("my-project");
    }

    [Fact]
    public void A_request_built_without_a_scope_never_guesses_one_from_its_spec()
    {
        // Issue #830: a silent "the spec's project as the current account" fallback would let a run act in the wrong account.
        var request = CloudJobRequestFactory.Create(Options(), "20260101-000000-abcdef", new CloudScope("my-project", "sub-a"), "install-1", "0.0.1", null, [new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.gb", "a.gb")], @"C:\out") with { Scope = null };

        Should.Throw<InvalidOperationException>(() => request.ResolvedScope);
    }

    [Fact]
    public void The_default_region_is_the_region_of_every_default_zone_a_run_will_try()
    {
        var request = FactoryCall.Create(Options(), "20260101-000000-abcdef", "my-project", "install-1", "0.0.1");

        request.Zones.ShouldNotBeEmpty();
        request.Zones.ShouldAllBe(z => ZoneCandidates.RegionOf(z) == CloudJobRequestFactory.DefaultRegion);
    }

    [Fact]
    public void Every_hard_rule_10_field_is_present_and_labels_render()
    {
        var request = FactoryCall.Create(Options(), "20260101-000000-abcdef", "my-project", "install-1", "0.0.1");

        request.JobId.ShouldBe("20260101-000000-abcdef");
        request.Spec.JobId.ShouldBe(request.JobId);
        request.Spec.ProjectId.ShouldBe("my-project");
        request.Spec.InstallationId.ShouldBe("install-1");
        request.Spec.TerminationAction.ShouldBe("DELETE");
        request.Spec.MaxRunDuration.ShouldBe(TimeSpan.FromMinutes(255));

        var labels = request.Spec.ToLabels();
        labels["app"].ShouldBe("dna-entropy-graph");
        labels["model"].ShouldBe("evo2_7b");
        labels["lifecycle"].ShouldBe("stop");
        labels["app-version"].ShouldBe("0-0-1");
    }

    [Theory]
    [InlineData(AfterTaskAction.Stop, "stop")]
    [InlineData(AfterTaskAction.Delete, "delete")]
    [InlineData(AfterTaskAction.KeepAlive, "keep")]
    public void After_task_flows_to_the_request_and_the_lifecycle_label(AfterTaskAction action, string label)
    {
        var request = FactoryCall.Create(Options(b => b.AfterTask = action), "job-1", "p", "i", "0.0.1");

        request.AfterTask.ShouldBe(action);
        request.Spec.Lifecycle.ShouldBe(label);
    }

    [Fact]
    public void The_VM_backstop_is_the_users_cap_plus_15_minutes()
    {
        var request = FactoryCall.Create(Options(b => b.MaxRunDurationMinutes = 90), "job-1", "p", "i", "0.0.1");

        // The worker enforces the user's cap itself (manifest limits.maxRunSeconds); the platform's deadline sits 15 minutes later (#89).
        request.Spec.MaxRunDuration.ShouldBe(TimeSpan.FromMinutes(105));
        request.Spec.TerminationAction.ShouldBe("DELETE");
    }

    [Theory]
    [InlineData(GpuTier.CheapestAvailable, "g2-standard-8")]
    [InlineData(GpuTier.L4, "g2-standard-8")]
    [InlineData(GpuTier.A100_40, "a2-highgpu-1g")]
    [InlineData(GpuTier.A100_80, "a2-ultragpu-1g")]
    [InlineData(GpuTier.H100, "a3-highgpu-1g")]
    public void Each_gpu_tier_maps_to_a_machine_type(GpuTier tier, string machineType)
    {
        var request = FactoryCall.Create(Options(b => b.GpuTier = tier), "job-1", "p", "i", "0.0.1");

        request.Spec.MachineType.ShouldBe(machineType);
    }

    [Fact]
    public void A_zone_preference_is_tried_first_and_never_duplicated()
    {
        var request = FactoryCall.Create(Options(b => b.ZonePreference = "us-central1-b"), "job-1", "p", "i", "0.0.1");

        request.Zones[0].ShouldBe("us-central1-b");
        request.Zones.Distinct().Count().ShouldBe(request.Zones.Count);
        request.Zones.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Without_a_zone_preference_the_default_ladder_is_used()
    {
        var request = FactoryCall.Create(Options(), "job-1", "p", "i", "0.0.1");

        request.Zones.ShouldNotBeEmpty();
    }
}

public class InstallationIdTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value) => Values[key] = value;
    }

    [Fact]
    public void The_first_call_generates_a_label_safe_id_and_persists_it()
    {
        var settings = new MemorySettings();

        var id = InstallationId.GetOrCreate(settings);

        id.ShouldMatch("^[a-z0-9_-]{1,63}$");
        settings.Values.Values.ShouldContain(id);
    }

    [Fact]
    public void Later_calls_return_the_same_id()
    {
        var settings = new MemorySettings();

        InstallationId.GetOrCreate(settings).ShouldBe(InstallationId.GetOrCreate(settings));
    }

    [Fact]
    public void A_stored_value_that_is_not_a_valid_label_is_replaced()
    {
        var settings = new MemorySettings();
        settings.SetString(InstallationId.SettingsKey, "Not A Label!");

        var id = InstallationId.GetOrCreate(settings);

        id.ShouldMatch("^[a-z0-9_-]{1,63}$");
    }
}

public class CloudJobRequestFactoryTransferTests
{
    [Fact]
    public void The_staged_inputs_options_and_output_folder_flow_to_the_request()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" };
        var inputs = new[] { new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.gb", "a.gb") };

        var request = CloudJobRequestFactory.Create(options, "job-1", new CloudScope("p"), "i", "0.0.1", null, inputs, @"C:\out");

        request.Inputs.ShouldBe(inputs);
        request.Options.ShouldBeSameAs(options);
        request.OutputFolder.ShouldBe(@"C:\out");
    }

    [Fact]
    public void A_request_with_no_inputs_is_refused_instead_of_becoming_a_run_that_uploads_nothing()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" };

        Should.Throw<ArgumentException>(() => CloudJobRequestFactory.Create(options, "job-1", new CloudScope("p"), "i", "0.0.1", null, [], @"C:\out"));
    }
}

public class CloudJobRequestFactoryWorkerImageTests
{
    [Fact]
    public void The_worker_image_flows_to_the_request()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" };

        FactoryCall.Create(options, "job-1", "p", "i", "0.0.1", "reg/x@sha256:" + new string('a', 64)).WorkerImage.ShouldBe("reg/x@sha256:" + new string('a', 64));
        FactoryCall.Create(options, "job-1", "p", "i", "0.0.1").WorkerImage.ShouldBeNull();
    }
}

public class InstallationIdConcurrencyTests
{
    private sealed class SlowSettings : ISettingsStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _values = new();

        public string? GetString(string key)
        {
            string? value;
            lock (_gate)
            {
                value = _values.TryGetValue(key, out var v) ? v : null;
            }

            Thread.Sleep(20); // widen the read-then-write window
            return value;
        }

        public void SetString(string key, string value)
        {
            lock (_gate)
            {
                _values[key] = value;
            }
        }
    }

    [Fact]
    public void Concurrent_first_calls_all_get_the_same_id()
    {
        var settings = new SlowSettings();

        var ids = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(16).Select(_ => InstallationId.GetOrCreate(settings)).ToList();

        ids.Distinct().Count().ShouldBe(1, "two first runs must never label VMs with different installation ids (Hard Rule 9)");
    }
}
