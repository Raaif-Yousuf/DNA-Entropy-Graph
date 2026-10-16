using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.PrototypeImport;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #243: the command-line prototype kept <c>%APPDATA%\dna-entropy\config.json</c> (last_zone, ssh_key_file) and wrote each run to
/// <c>%USERPROFILE%\Downloads\&lt;name&gt;\&lt;name&gt;.entropy.bedgraph</c> (worker/legacy/cloud/orchestrator.py, worker/legacy/dna-entropy-v1/cli.py).
/// Finding it only ever reads (Hard Rule 14); importing adds history rows flagged Imported with no cloud objects.
/// </summary>
public sealed class PrototypeImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-proto-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly string _config;

    public PrototypeImportTests()
    {
        _downloads = Path.Combine(_root, "Downloads");
        _config = Path.Combine(_root, "AppData", "dna-entropy", "config.json");
        Directory.CreateDirectory(_downloads);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Run(string name, params string[] files)
    {
        var folder = Path.Combine(_downloads, name);
        Directory.CreateDirectory(folder);
        foreach (var file in files.Length == 0 ? [$"{name}.entropy.bedgraph"] : files)
        {
            File.WriteAllText(Path.Combine(folder, file), "chr1\t0\t1\t1.9\n");
        }

        return folder;
    }

    private void Config(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_config)!);
        File.WriteAllText(_config, json);
    }

    private sealed class MemoryRuns : IRunRepository
    {
        public List<RunRecord> Rows { get; } = [];

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunRecord>>([.. Rows]);

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
        {
            Rows.RemoveAll(r => r.JobId == run.JobId);
            Rows.Add(run);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void The_prototype_locations_are_the_ones_its_code_used()
    {
        PrototypeLocations.ConfigPath(@"C:\Users\x\AppData\Roaming").ShouldBe(Path.Combine(@"C:\Users\x\AppData\Roaming", "dna-entropy", "config.json"));
        PrototypeLocations.Downloads(@"C:\Users\x").ShouldBe(Path.Combine(@"C:\Users\x", "Downloads"));
    }

    [Fact]
    public void Scan_lists_each_download_folder_that_holds_an_entropy_bedgraph()
    {
        var one = Run("plasmid-1");
        Run("plasmid-2");
        Run("only-smoothed", "only-smoothed.entropy.smooth5.bedgraph");
        Run("empty", "notes.txt");
        File.WriteAllText(Path.Combine(_downloads, "loose.entropy.bedgraph"), "x");

        var scan = PrototypeScanner.Scan(_config, _downloads);

        scan.Runs.Select(r => r.Name).ShouldBe(["plasmid-1", "plasmid-2"], ignoreOrder: true);
        scan.Runs.Single(r => r.Name == "plasmid-1").Folder.ShouldBe(one);
        scan.Found.ShouldBeTrue();
    }

    [Fact]
    public void A_folder_this_app_wrote_is_not_a_prototype_run()
    {
        Run("from-this-app", "from-this-app.entropy.bedgraph", "provenance.json");

        PrototypeScanner.Scan(_config, _downloads).Runs.ShouldBeEmpty();
    }

    [Fact]
    public void Scan_does_not_look_inside_sub_folders()
    {
        var folder = Run("outer", "readme.txt");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        File.WriteAllText(Path.Combine(folder, "inner", "inner.entropy.bedgraph"), "x");

        PrototypeScanner.Scan(_config, _downloads).Runs.ShouldBeEmpty();
    }

    [Fact]
    public void Scan_reads_the_last_zone_and_the_ssh_key_note_from_config_json()
    {
        Config("""{"last_zone":"us-central1-a","ssh_key_file":"C:\\Users\\x\\.ssh\\google_compute_engine"}""");

        var scan = PrototypeScanner.Scan(_config, _downloads);

        scan.LastZone.ShouldBe("us-central1-a");
        scan.SshKeyFile.ShouldBe(@"C:\Users\x\.ssh\google_compute_engine");
        scan.Found.ShouldBeTrue();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"last_zone":5,"ssh_key_file":{"a":1}}""")]
    [InlineData("""{"last_zone":"us-central1-a; rm -rf","ssh_key_file":""}""")]
    public void A_config_that_cannot_be_used_is_ignored_not_thrown_on(string json)
    {
        Config(json);

        var scan = PrototypeScanner.Scan(_config, _downloads);

        scan.LastZone.ShouldBeNull();
        scan.SshKeyFile.ShouldBeNull();
    }

    [Fact]
    public void Nothing_on_the_PC_is_not_found_and_does_not_throw()
    {
        var scan = PrototypeScanner.Scan(Path.Combine(_root, "nope", "config.json"), Path.Combine(_root, "no-downloads"));

        scan.Found.ShouldBeFalse();
        scan.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Scanning_and_importing_never_change_a_file_of_the_prototype()
    {
        Config("""{"last_zone":"us-central1-a"}""");
        Run("a");
        Run("b");
        string Snapshot() => string.Join("|", Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order()
            .Select(p => p + "@" + (File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks + ":" + new FileInfo(p).Length : "dir")));
        var before = Snapshot();

        var scan = PrototypeScanner.Scan(_config, _downloads);
        await new PrototypeImporter(new MemoryRuns()).ImportAsync(scan, CancellationToken.None);

        Snapshot().ShouldBe(before);
    }

    [Fact]
    public async Task Import_adds_one_finished_row_per_folder_flagged_imported_with_no_cloud_objects()
    {
        var folder = Run("plasmid-1");
        var runs = new MemoryRuns();

        var added = await new PrototypeImporter(runs).ImportAsync(PrototypeScanner.Scan(_config, _downloads), CancellationToken.None);

        added.ShouldBe(1);
        var row = runs.Rows.ShouldHaveSingleItem();
        row.Name.ShouldBe("plasmid-1");
        row.Phase.ShouldBe(JobPhase.Completed);
        row.Imported.ShouldBeTrue();
        row.Target.ShouldBe(RunTargets.LocalRecord);
        row.OutputDir.ShouldBe(folder);
        row.Bucket.ShouldBeNull();
        row.JobPrefix.ShouldBeNull();
        row.VmName.ShouldBeNull();
        row.JobId.ShouldStartWith("legacy-");
        row.JobId.ShouldMatch("^[a-z0-9-]+$");
        row.CreatedUtc.UtcDateTime.ShouldBe(File.GetLastWriteTimeUtc(Path.Combine(folder, "plasmid-1.entropy.bedgraph")), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Importing_twice_adds_nothing_the_second_time()
    {
        Run("a");
        var runs = new MemoryRuns();
        var importer = new PrototypeImporter(runs);
        var scan = PrototypeScanner.Scan(_config, _downloads);

        (await importer.ImportAsync(scan, CancellationToken.None)).ShouldBe(1);
        (await importer.ImportAsync(PrototypeScanner.Scan(_config, _downloads), CancellationToken.None)).ShouldBe(0);

        runs.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_folder_already_in_history_under_any_id_is_not_added_again()
    {
        var folder = Run("a");
        var runs = new MemoryRuns();
        runs.Rows.Add(new RunRecord("some-real-job", JobPhase.Completed, DateTimeOffset.UtcNow, "a", OutputDir: folder.ToUpperInvariant() + Path.DirectorySeparatorChar));

        (await new PrototypeImporter(runs).ImportAsync(PrototypeScanner.Scan(_config, _downloads), CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public void Scan_is_capped_so_a_huge_Downloads_folder_cannot_stall_the_app()
    {
        for (var i = 0; i < PrototypeScanner.MaxRuns + 5; i++)
        {
            Run($"r{i:D4}");
        }

        PrototypeScanner.Scan(_config, _downloads).Runs.Count.ShouldBe(PrototypeScanner.MaxRuns);
    }
}
