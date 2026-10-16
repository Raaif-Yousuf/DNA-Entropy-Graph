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
            // A junction is removed on its own first: a recursive delete refuses to cross it.
            foreach (var dir in Directory.GetDirectories(_temp).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                Directory.Delete(dir, recursive: false);
            }

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
    [InlineData(@"C:\L\DNAEntropyGraph", @"C:\L\DnaEntropyGraph")]
    [InlineData(@"C:\L\DNAEntropyGraph", @"C:\L\DNAEntropyGraph\sub")]
    [InlineData(@"C:\L\DNAEntropyGraph\", @"C:\L\dnaentropygraph")]
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
    public void The_production_install_root_and_data_folder_do_not_overlap_so_the_question_is_asked()
    {
        // Issue #712: release.yml packs with --packId DnaEntropyGraph.Desktop, so Velopack installs to <LocalAppData>\DnaEntropyGraph.Desktop
        // (the app runs from its "current" subfolder), while the data lives in AppDataRoot.Default(). They used to be the same folder.
        var localAppData = Path.GetDirectoryName(DnaEntropyGraph.Core.AppDataRoot.Default().Path)!;
        var installRoot = Path.Combine(localAppData, "DnaEntropyGraph.Desktop");

        UninstallDataCleanup.Overlaps(DnaEntropyGraph.Core.AppDataRoot.Default().Path, installRoot).ShouldBeFalse();
        UninstallDataPrompt.InstallRootOf(Path.Combine(installRoot, "current") + Path.DirectorySeparatorChar).ShouldBe(installRoot);
    }

    [Fact]
    public void A_junction_that_leads_into_the_install_folder_is_an_overlap()
    {
        var realInstall = Path.Combine(_temp, "DnaEntropyGraph.Desktop");
        Directory.CreateDirectory(Path.Combine(realInstall, "current"));
        var link = Path.Combine(_temp, "DNAEntropyGraphLink");
        MakeJunction(link, realInstall);

        // The apparent paths share nothing; the real ones are the same folder.
        UninstallDataCleanup.Overlaps(link, realInstall).ShouldBeTrue();
        UninstallDataCleanup.Overlaps(Path.Combine(link, "current"), realInstall).ShouldBeTrue();

        var asked = false;
        UninstallDataCleanup.Run(link, realInstall, () => asked = true, _ => { }).ShouldBe(UninstallDataOutcome.SharedWithInstall);
        asked.ShouldBeFalse();
    }

    [Fact]
    public void A_folder_reached_through_a_junction_in_its_ancestors_still_overlaps_and_an_unrelated_junction_does_not()
    {
        var realInstall = Path.Combine(_temp, "DnaEntropyGraph.Desktop");
        Directory.CreateDirectory(realInstall);
        var other = Path.Combine(_temp, "SomewhereElse");
        Directory.CreateDirectory(other);
        var toInstall = Path.Combine(_temp, "ViaInstall");
        var toOther = Path.Combine(_temp, "ViaOther");
        MakeJunction(toInstall, realInstall);
        MakeJunction(toOther, other);

        UninstallDataCleanup.Overlaps(Path.Combine(toInstall, "data-not-created-yet"), realInstall).ShouldBeTrue();
        UninstallDataCleanup.Overlaps(Path.Combine(toOther, "data"), realInstall).ShouldBeFalse();
    }

    [Fact]
    public void An_8_3_short_name_of_the_install_folder_is_the_same_folder()
    {
        var longName = Path.Combine(_temp, "DnaEntropyGraph.Desktop.Long.Folder.Name");
        Directory.CreateDirectory(longName);
        var buffer = new System.Text.StringBuilder(520);
        var length = GetShortPathNameW(longName, buffer, buffer.Capacity);
        var shortName = buffer.ToString();
        if (length == 0 || string.Equals(shortName, longName, StringComparison.OrdinalIgnoreCase))
        {
            return; // 8.3 names are switched off on this volume: nothing to prove here.
        }

        UninstallDataCleanup.Overlaps(shortName, longName).ShouldBeTrue();
    }

    [Fact]
    public void A_non_interactive_uninstall_keeps_the_data_and_asks_nothing()
    {
        var asked = false;
        var deleted = false;

        var outcome = UninstallDataCleanup.Run(Data, Install, () => asked = true, _ => deleted = true, interactive: false);

        outcome.ShouldBe(UninstallDataOutcome.NotInteractive);
        asked.ShouldBeFalse();
        deleted.ShouldBeFalse();
        File.Exists(Path.Combine(Data, "settings.json")).ShouldBeTrue();
    }

    [Fact]
    public void A_yes_renames_the_folder_first_so_the_delete_only_ever_sees_a_clearly_named_leftover()
    {
        string? seen = null;
        var liveFolderExistedDuringDelete = true;

        var outcome = UninstallDataCleanup.Run(Data, Install, () => true, path =>
        {
            seen = path;
            liveFolderExistedDuringDelete = Directory.Exists(Data);
            Directory.Delete(path, recursive: true);
        });

        outcome.ShouldBe(UninstallDataOutcome.Removed);
        seen.ShouldNotBeNull();
        Path.GetFileName(seen).ShouldStartWith("DNAEntropyGraph.removing-");
        liveFolderExistedDuringDelete.ShouldBeFalse();
    }

    [Fact]
    public void A_delete_killed_partway_leaves_a_named_leftover_and_never_a_half_deleted_live_folder()
    {
        var outcome = UninstallDataCleanup.Run(Data, Install, () => true, path =>
        {
            Directory.Delete(Path.Combine(path, "runs"), recursive: true);
            throw new IOException("process killed");
        });

        outcome.ShouldBe(UninstallDataOutcome.Failed);
        Directory.Exists(Data).ShouldBeFalse();
        Directory.GetDirectories(_temp, "DNAEntropyGraph.removing-*").Length.ShouldBe(1);
    }

    [Fact]
    public void A_folder_that_cannot_be_renamed_stays_exactly_as_it_was()
    {
        var deleted = false;

        var outcome = UninstallDataCleanup.Run(Data, Install, () => true, _ => deleted = true, moveFolder: (_, _) => throw new IOException("in use"));

        outcome.ShouldBe(UninstallDataOutcome.Failed);
        deleted.ShouldBeFalse();
        File.Exists(Path.Combine(Data, "settings.json")).ShouldBeTrue();
    }

    [Fact]
    public void A_delete_that_outlasts_the_budget_returns_in_time_instead_of_being_killed_by_the_uninstaller()
    {
        using var release = new ManualResetEventSlim();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var outcome = UninstallDataCleanup.Run(Data, Install, () => true, _ => release.Wait(TimeSpan.FromSeconds(20)), deleteBudget: TimeSpan.FromMilliseconds(200));

        release.Set();
        outcome.ShouldBe(UninstallDataOutcome.Failed);
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void The_prompt_text_is_never_a_resource_key_even_when_the_loader_cannot_resolve_it()
    {
        // ReswStringResourceProvider returns the key itself when the loader fails, which is what the hook (before Application.Start) may see.
        var (title, body) = UninstallDataStrings.Resolve(key => key);

        title.ShouldNotBe(UninstallDataStrings.TitleKey);
        body.ShouldNotBe(UninstallDataStrings.BodyKey);
        title.ShouldNotContain("_");
        body.ShouldContain("DNA Entropy Graph");

        UninstallDataStrings.Resolve(_ => throw new InvalidOperationException("no loader")).Title.ShouldBe(title);
        UninstallDataStrings.Resolve(_ => string.Empty).Body.ShouldBe(body);
        UninstallDataStrings.Resolve(key => "loaded " + key).Title.ShouldBe("loaded " + UninstallDataStrings.TitleKey);
    }

    private static void MakeJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, "mklink /J failed");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, int length);

    [Fact]
    public void A_prompt_that_throws_keeps_the_data()
    {
        var outcome = UninstallDataCleanup.Run(Data, Install, () => throw new InvalidOperationException("no ui"), _ => throw new InvalidOperationException("must not delete"));

        outcome.ShouldBe(UninstallDataOutcome.Kept);
    }
}
