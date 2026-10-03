using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #59: a reattached run uploads the app's own copy of its input, found again by job id, never the user's original (Hard Rule 14).</summary>
public sealed class LocalRunInputStoreFindTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-input-find-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Finds_the_copy_a_run_staged_even_after_the_original_is_gone()
    {
        var original = Path.Combine(_root, "user", "SetTnpB.gb");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "LOCUS x");
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));
        var staged = await store.StageAsync("job-1", original, CancellationToken.None);
        File.Delete(original);

        var found = await new LocalRunInputStore(Path.Combine(_root, "appdata")).FindAsync("job-1", CancellationToken.None);

        found.ShouldNotBeNull();
        found.LocalPath.ShouldBe(staged.LocalPath);
        found.FileName.ShouldBe("SetTnpB.gb");
        File.ReadAllText(found.LocalPath).ShouldBe("LOCUS x");
    }

    [Fact]
    public async Task Returns_null_for_a_job_with_no_staged_input()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));

        (await store.FindAsync("job-none", CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Returns_null_for_a_job_id_that_is_not_a_plain_name(string jobId)
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));

        (await store.FindAsync(jobId, CancellationToken.None)).ShouldBeNull();
    }
}
