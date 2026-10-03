using System.Collections.Concurrent;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// Issue #458: the app, not a setting, supplies the worker image. A reference
/// outside the pinned list is refused unless developer mode is on, and an
/// empty list is a named failure rather than a VM with no startup script.
/// </summary>
public class PinnedWorkerImageProviderTests
{
    private static readonly string Cuda = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:" + new string('a', 64);
    private static readonly string Cpu = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cpu@sha256:" + new string('b', 64);
    private static readonly string Other = "example.com/dev/worker@sha256:" + new string('c', 64);

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public string? GetString(string key) => _values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value) => _values[key] = value;
    }

    private static string List(string version = "0.1.0") => $$"""{"images":[{"version":"{{version}}","cuda":"{{Cuda}}","cpu":"{{Cpu}}"}]}""";

    private static PinnedWorkerImageProvider Provider(ISettingsStore settings, string json) => new(settings, PinnedWorkerImageList.Parse(json));

    [Fact]
    public void The_pinned_image_for_the_app_version_is_returned_without_any_setting()
    {
        var provider = Provider(new MemorySettings(), List());

        var gpu = provider.Resolve("0.1.0", gpu: true);
        var cpu = provider.Resolve("0.1.0", gpu: false);

        gpu.Status.ShouldBe(WorkerImageStatus.Available);
        gpu.Reference.ShouldBe(Cuda);
        cpu.Reference.ShouldBe(Cpu);
    }

    [Fact]
    public void A_version_with_no_pinned_image_is_None_shipped_never_another_versions_build()
    {
        var r = Provider(new MemorySettings(), List("0.0.9")).Resolve("0.1.0", gpu: true);

        r.Status.ShouldBe(WorkerImageStatus.NoneShipped);
        r.Reference.ShouldBeNull();
    }

    [Fact]
    public void An_override_outside_the_list_is_refused_when_developer_mode_is_off()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Other);

        var r = Provider(settings, List()).Resolve("0.1.0", gpu: true);

        r.Status.ShouldBe(WorkerImageStatus.OverrideRefused);
        r.Reference.ShouldBeNull("a refused override must not silently fall back to the pinned image either: the user asked for something else");
    }

    [Fact]
    public void An_override_outside_the_list_is_used_in_developer_mode()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Other);
        settings.SetString(PinnedWorkerImageProvider.DeveloperModeSettingsKey, "true");

        var r = Provider(settings, """{"images":[]}""").Resolve("0.1.0", gpu: true);

        r.Status.ShouldBe(WorkerImageStatus.Available);
        r.Reference.ShouldBe(Other);
    }

    [Theory]
    [InlineData("ghcr.io/x/y:latest")]
    [InlineData("not a reference")]
    public void An_override_that_is_not_pinned_by_digest_is_refused_even_in_developer_mode(string value)
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, value);
        settings.SetString(PinnedWorkerImageProvider.DeveloperModeSettingsKey, "true");

        Provider(settings, List()).Resolve("0.1.0", gpu: true).Status.ShouldBe(WorkerImageStatus.OverrideRefused);
    }

    [Fact]
    public void An_override_equal_to_this_versions_image_for_this_variant_is_accepted_without_developer_mode()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Cuda);

        var r = Provider(settings, List()).Resolve("0.1.0", gpu: true);

        r.Status.ShouldBe(WorkerImageStatus.Available);
        r.Reference.ShouldBe(Cuda);
        r.IsDeveloperOverride.ShouldBeFalse("an allowlisted image is not a developer override");
    }

    [Fact]
    public void A_cpu_image_is_refused_for_a_gpu_run_because_the_VM_would_boot_an_image_with_no_torch()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Cpu);

        var r = Provider(settings, List()).Resolve("0.1.0", gpu: true);

        r.Status.ShouldBe(WorkerImageStatus.OverrideRefused);
        r.Reference.ShouldBeNull();
    }

    [Fact]
    public void A_cuda_image_is_refused_for_a_cpu_run()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Cuda);

        Provider(settings, List()).Resolve("0.1.0", gpu: false).Status.ShouldBe(WorkerImageStatus.OverrideRefused);
    }

    [Fact]
    public void An_image_pinned_for_another_app_version_is_refused()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Cuda);

        Provider(settings, List("0.0.9")).Resolve("0.1.0", gpu: true).Status.ShouldBe(WorkerImageStatus.OverrideRefused);
    }

    [Fact]
    public void Developer_mode_accepts_the_wrong_variant_and_the_resolution_says_it_is_an_override()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Cpu);
        settings.SetString(PinnedWorkerImageProvider.DeveloperModeSettingsKey, "true");

        var r = Provider(settings, List()).Resolve("0.1.0", gpu: true);

        r.Status.ShouldBe(WorkerImageStatus.Available);
        r.Reference.ShouldBe(Cpu);
        r.IsDeveloperOverride.ShouldBeTrue("developer mode must be visible to the caller so it can be logged");
    }

    [Fact]
    public void Developer_mode_with_no_override_still_uses_the_pinned_list()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.DeveloperModeSettingsKey, "true");

        Provider(settings, List()).Resolve("0.1.0", gpu: true).Reference.ShouldBe(Cuda);
    }

    [Fact]
    public void A_blank_override_is_the_same_as_none()
    {
        var settings = new MemorySettings();
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, "   ");

        Provider(settings, List()).Resolve("0.1.0", gpu: true).Reference.ShouldBe(Cuda);
    }

    [Fact]
    public void The_list_that_ships_with_the_app_is_embedded_and_parses_with_no_dropped_entries()
    {
        var list = PinnedWorkerImageProvider.LoadShippedList();

        list.DroppedCount.ShouldBe(0, "an entry that fails the digest rule would be silently dropped from the allowlist");
    }

    /// <summary>
    /// ISSUE #480 PLACEHOLDER, RED ON PURPOSE LATER. The release pipeline does not write digests into
    /// worker-images.json yet, so the shipped list is empty and every run of this build ends
    /// no_worker_image. This test documents that fact. The day #480 fills the list it goes red:
    /// replace it with a guard that the entry for the app's own version has both a -cuda and a -cpu
    /// reference, and delete this test.
    /// </summary>
    [Fact]
    public void ISSUE_480_the_shipped_list_is_still_empty_replace_this_with_a_real_guard_when_it_is_filled()
    {
        var list = PinnedWorkerImageProvider.LoadShippedList();

        list.IsEmpty.ShouldBeTrue("#480 filled worker-images.json: replace this placeholder with a guard that the app's own version has a -cuda and a -cpu entry");
    }
}
