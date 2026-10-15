using DnaEntropyGraph.App.Startup;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #264: what the uninstall hook does with the user's data folder (default: keep; remove only when the user says so).</summary>
public sealed class UninstallDataCleanupTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "deg-uninstall-" + Guid.NewGuid().ToString("N"));

    private string Data => Path.Combine(_temp, "DNAEntropyGraph");

    private string Install => Path.Combine(_temp, "Elsewhere");

    public UninstallDataCleanupTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "runs", "job-1", "input"));
        File.WriteAllText(Path.Combine(Data, "settings.json"), "{}");
        Directory.CreateDirectory(Install);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Fact]
    public void The_default_answer_keeps_the_data_and_never_deletes()
    {
        var deleted = false;

        var outcome = UninstallDataCleanup.Run(Data, Install, () => false, _ => deleted = true);

        outcome.ShouldBe(UninstallDataOutcome.Kept);
        deleted.ShouldBeFalse();
        File.Exists(Path.Combine(Data, "settings.json")).ShouldBeTrue();
    }

    [Fact]
    public void A_yes_removes_the_whole_data_folder()
    {
        var outcome = UninstallDataCleanup.Run(Data, Install, () => true, path => Directory.Delete(path, recursive: true));

        outcome.ShouldBe(UninstallDataOutcome.Removed);
        Directory.Exists(Data).ShouldBeFalse();
    }

    [Fact]
    public void No_data_folder_means_nothing_to_ask_about()
    {
        var asked = false;

        var outcome = UninstallDataCleanup.Run(Path.Combine(_temp, "nothing-here"), Install, () => asked = true, _ => { });

        outcome.ShouldBe(UninstallDataOutcome.NothingToRemove);
        asked.ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\DNAEntropyGraph", @"C:\Users\x\AppData\Local\DnaEntropyGraph")]
    [InlineData(@"C:\Users\x\AppData\Local\DNAEntropyGraph", @"C:\Users\x\AppData\Local\DNAEntropyGraph\sub")]
    [InlineData(@"C:\Users\x\AppData\Local\DNAEntropyGraph\", @"C:\Users\x\AppData\Local\dnaentropygraph")]
    public void A_data_folder_that_is_the_install_folder_is_not_asked_about(string data, string install)
        => UninstallDataCleanup.Overlaps(data, install).ShouldBeTrue();

    [Fact]
    public void Separate_folders_do_not_overlap_and_a_name_prefix_is_not_an_overlap()
    {
        UninstallDataCleanup.Overlaps(@"C:\L\DNAEntropyGraph", @"C:\L\DnaEntropyGraphApp").ShouldBeFalse();
        UninstallDataCleanup.Overlaps(@"C:\L\DNAEntropyGraph", @"C:\L\Other").ShouldBeFalse();
    }

    [Fact]
    public void An_overlapping_folder_is_left_to_the_uninstaller_without_a_question()
    {
        var asked = false;
        Directory.CreateDirectory(Path.Combine(Data, "current"));

        var outcome = UninstallDataCleanup.Run(Data, Path.Combine(Data.ToUpperInvariant()), () => asked = true, _ => { });

        outcome.ShouldBe(UninstallDataOutcome.SharedWithInstall);
        asked.ShouldBeFalse();
    }

    [Fact]
    public void A_delete_that_fails_is_reported_and_never_thrown()
    {
        var outcome = UninstallDataCleanup.Run(Data, Install, () => true, _ => throw new IOException("locked"));

        outcome.ShouldBe(UninstallDataOutcome.Failed);
    }

    [Fact]
    public void A_prompt_that_throws_keeps_the_data()
    {
        var outcome = UninstallDataCleanup.Run(Data, Install, () => throw new InvalidOperationException("no ui"), _ => throw new InvalidOperationException("must not delete"));

        outcome.ShouldBe(UninstallDataOutcome.Kept);
    }
}
