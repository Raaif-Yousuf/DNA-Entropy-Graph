using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

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
    public void Every_hard_rule_10_field_is_present_and_labels_render()
    {
        var request = CloudJobRequestFactory.Create(Options(), "20260101-000000-abcdef", "my-project", "install-1", "0.0.1");

        request.JobId.ShouldBe("20260101-000000-abcdef");
        request.Spec.JobId.ShouldBe(request.JobId);
        request.Spec.ProjectId.ShouldBe("my-project");
        request.Spec.InstallationId.ShouldBe("install-1");
        request.Spec.TerminationAction.ShouldBe("DELETE");
        request.Spec.MaxRunDuration.ShouldBe(TimeSpan.FromMinutes(240));

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
        var request = CloudJobRequestFactory.Create(Options(b => b.AfterTask = action), "job-1", "p", "i", "0.0.1");

        request.AfterTask.ShouldBe(action);
        request.Spec.Lifecycle.ShouldBe(label);
    }

    [Fact]
    public void The_users_max_run_duration_is_the_VM_backstop()
    {
        var request = CloudJobRequestFactory.Create(Options(b => b.MaxRunDurationMinutes = 90), "job-1", "p", "i", "0.0.1");

        request.Spec.MaxRunDuration.ShouldBe(TimeSpan.FromMinutes(90));
    }

    [Theory]
    [InlineData(GpuTier.CheapestAvailable, "g2-standard-8")]
    [InlineData(GpuTier.L4, "g2-standard-8")]
    [InlineData(GpuTier.A100_40, "a2-highgpu-1g")]
    [InlineData(GpuTier.A100_80, "a2-ultragpu-1g")]
    [InlineData(GpuTier.H100, "a3-highgpu-1g")]
    public void Each_gpu_tier_maps_to_a_machine_type(GpuTier tier, string machineType)
    {
        var request = CloudJobRequestFactory.Create(Options(b => b.GpuTier = tier), "job-1", "p", "i", "0.0.1");

        request.Spec.MachineType.ShouldBe(machineType);
    }

    [Fact]
    public void A_zone_preference_is_tried_first_and_never_duplicated()
    {
        var request = CloudJobRequestFactory.Create(Options(b => b.ZonePreference = "us-central1-b"), "job-1", "p", "i", "0.0.1");

        request.Zones[0].ShouldBe("us-central1-b");
        request.Zones.Distinct().Count().ShouldBe(request.Zones.Count);
        request.Zones.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Without_a_zone_preference_the_default_ladder_is_used()
    {
        var request = CloudJobRequestFactory.Create(Options(), "job-1", "p", "i", "0.0.1");

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

public class CloudJobRequestFactoryWorkerImageTests
{
    [Fact]
    public void The_worker_image_flows_to_the_request()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" };

        CloudJobRequestFactory.Create(options, "job-1", "p", "i", "0.0.1", "reg/x@sha256:" + new string('a', 64)).WorkerImage.ShouldBe("reg/x@sha256:" + new string('a', 64));
        CloudJobRequestFactory.Create(options, "job-1", "p", "i", "0.0.1").WorkerImage.ShouldBeNull();
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
