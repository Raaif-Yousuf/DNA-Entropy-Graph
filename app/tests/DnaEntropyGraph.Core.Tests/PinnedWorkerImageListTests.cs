using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #458: the list of digest-pinned worker images that ships with the app (CLAUDE.md Stack row "Container").</summary>
public class PinnedWorkerImageListTests
{
    private static readonly string Cuda = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:" + new string('a', 64);
    private static readonly string Cpu = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cpu@sha256:" + new string('b', 64);

    private static string Json(string cuda, string cpu, string version = "0.1.0")
        => $$"""{"images":[{"version":"{{version}}","cuda":"{{cuda}}","cpu":"{{cpu}}"}]}""";

    [Fact]
    public void Picks_the_cuda_image_for_a_gpu_tier_and_the_cpu_image_otherwise_for_the_app_version()
    {
        var list = PinnedWorkerImageList.Parse(Json(Cuda, Cpu));

        list.Find("0.1.0", gpu: true).ShouldBe(Cuda);
        list.Find("0.1.0", gpu: false).ShouldBe(Cpu);
    }

    [Fact]
    public void A_version_with_no_entry_finds_nothing_instead_of_falling_back_to_another_build()
        => PinnedWorkerImageList.Parse(Json(Cuda, Cpu)).Find("0.2.0", gpu: true).ShouldBeNull();

    [Fact]
    public void Allows_only_the_exact_reference_pinned_for_this_version_and_variant()
    {
        var list = PinnedWorkerImageList.Parse(Json(Cuda, Cpu, "0.1.0"));

        list.Allows(Cuda, "0.1.0", gpu: true).ShouldBeTrue();
        list.Allows(Cpu, "0.1.0", gpu: false).ShouldBeTrue();
        list.Allows(Cpu, "0.1.0", gpu: true).ShouldBeFalse("a cpu image on a gpu run");
        list.Allows(Cuda, "0.1.0", gpu: false).ShouldBeFalse("a cuda image on a cpu run");
        list.Allows(Cuda, "0.2.0", gpu: true).ShouldBeFalse("another app version");
        list.Allows("evil/x@sha256:" + new string('c', 64), "0.1.0", gpu: true).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_list_is_valid_and_finds_nothing()
    {
        var list = PinnedWorkerImageList.Parse("""{"images":[]}""");

        list.IsEmpty.ShouldBeTrue();
        list.DroppedCount.ShouldBe(0);
        list.Find("0.1.0", gpu: true).ShouldBeNull();
    }

    [Fact]
    public void An_entry_that_is_not_pinned_by_digest_is_dropped_so_it_can_never_be_handed_to_a_VM()
    {
        var list = PinnedWorkerImageList.Parse(Json("ghcr.io/x/y:latest", Cpu));

        list.Find("0.1.0", gpu: true).ShouldBeNull();
        list.Find("0.1.0", gpu: false).ShouldBe(Cpu);
        list.Allows("ghcr.io/x/y:latest", "0.1.0", gpu: true).ShouldBeFalse();
        list.DroppedCount.ShouldBe(1, "a dropped entry is counted so a typo in the shipped list is a failing test, not a silent empty list");
    }

    [Theory]
    [InlineData("""{"images":[{"version":"0.1.0","cud":"x"}]}""")]
    [InlineData("""{"images":[{"version":"0.1.0"}]}""")]
    [InlineData("""{"images":[{"cuda":"x"}]}""")]
    [InlineData("""{"images":[{"version":"0.1.0","cpu":"ghcr.io/x/y@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","extra":"x"}]}""")]
    [InlineData("""{"images":["0.1.0"]}""")]
    public void An_entry_with_a_typo_a_missing_image_or_an_unknown_key_counts_as_dropped_so_the_no_dropped_guard_can_see_it(string json)
        => PinnedWorkerImageList.Parse(json).DroppedCount.ShouldBeGreaterThan(0);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"images":"x"}""")]
    public void Unreadable_content_is_an_error_not_an_empty_allowlist_that_hides_the_problem(string json)
        => Should.Throw<InvalidDataException>(() => PinnedWorkerImageList.Parse(json));
}
