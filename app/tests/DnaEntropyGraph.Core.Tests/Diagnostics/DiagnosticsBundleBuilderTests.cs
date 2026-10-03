using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Diagnostics;

/// <summary>
/// Issue #106's observable: grepping the bundle for the sequence or the file name finds nothing. The seeded app data
/// folder holds every kind of thing the real one does, including the ones that must never be copied.
/// </summary>
public class DiagnosticsBundleBuilderTests
{
    private const string Sequence = "ACGTTGCAAGGCTTAACCGGTTAAGGCCTTAAGGCC";
    private const string FileName = "plasmid_pUC19_secret.fasta";
    private const string Email = "jdoe@lab.example.edu";
    private const string Profile = @"C:\Users\jdoe";
    private const string TokenText = "REFRESH-TOKEN-DO-NOT-LEAK-1//0gABC";

    private static readonly DiagnosticsInfo Info = new(
        "0.1.0", "Windows 11 10.0.26300", ".NET 10.0.0", "130.0.1", Profile, [Email, "jdoe"], new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    private sealed class MemorySource(Dictionary<string, string> files) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => files.Keys.ToList();

        public byte[]? TryRead(string relativePath) => files.TryGetValue(relativePath, out var text) ? Encoding.UTF8.GetBytes(text) : null;
    }

    private static Dictionary<string, string> SeededFolder() => new()
    {
        ["settings.json"] = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Theme"] = "Dark",
            ["installation_id"] = "inst-abc123",
            ["default_output"] = Profile + @"\Downloads",
            ["account_email"] = Email,
        }),
        ["auth/sub-1.token"] = TokenText,
        [$"inputs/job-1/{FileName}"] = $">x\n{Sequence}\n",
        ["pasted/job-2.fasta"] = $">x\n{Sequence}\n",
        ["app.db"] = "SQLite format 3 binary stand-in " + FileName,
        ["logs/app-20261003.log"] = $"12:00 opened {Profile}\\Downloads\\{FileName} for {Email}\n12:01 sequence {Sequence}\n12:02 job-1 ended\n",
        ["runs/job-1/status.json"] = $$$"""{"jobId":"job-1","state":"failed","heartbeatSeq":7,"input":"{{{FileName}}}","error":{"code":"WORKER_FAILED","message":"could not read {{{FileName}}} {{{Sequence}}}"},"vm":{"gpu":"NVIDIA L4","zone":"us-central1-a"}}""",
        ["runs/job-1/progress.jsonl"] = $$"""{"seq":1,"phase":"analysing","text":"{{Sequence}}"}""" + "\nnot json " + Sequence + "\n",
        ["runs/job-1/result.json"] = $$"""{"jobId":"job-1","status":"failed","inputs":[{"name":"{{FileName}}","sequenceLength":36}]}""",
        ["runs/job-1/logs/worker.log"] = $"INFO read {FileName}\nINFO seq {Sequence.ToLowerInvariant()}\nERROR OOM\n",
        ["runs/job-1/outputs/entropy.bedgraph"] = "chr 0 1 1.9\n",
        ["runs/../escape/status.json"] = "{}",
    };

    private static readonly RunRecord Run = new(
        "job-1", JobPhase.Failed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        Name: FileName, ErrorCode: "WORKER_FAILED", ErrorDetail: $"{Sequence} in {FileName} for {Email}",
        OutputDir: $@"{Profile}\Downloads\{FileName}-run", InstallationId: "inst-abc123", MachineType: "g2-standard-8");

    private static Dictionary<string, string> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    private static Dictionary<string, string> BuildSeeded(Dictionary<string, string>? folder = null) =>
        Unzip(DiagnosticsBundleBuilder.Build(new MemorySource(folder ?? SeededFolder()), [Run], Info));

    [Fact]
    public void No_entry_contains_the_sequence_the_file_name_the_email_or_the_profile_name()
    {
        var entries = BuildSeeded();

        foreach (var (name, text) in entries)
        {
            text.ShouldNotContain(Sequence, Case.Insensitive, $"sequence leaked into {name}");
            text.ShouldNotContain(Sequence[..20], Case.Insensitive, $"sequence fragment leaked into {name}");
            text.ShouldNotContain(FileName, Case.Insensitive, $"file name leaked into {name}");
            text.ShouldNotContain("plasmid_pUC19", Case.Insensitive, $"file name stem leaked into {name}");
            text.ShouldNotContain(Email, Case.Insensitive, $"email leaked into {name}");
            text.ShouldNotContain("jdoe", Case.Insensitive, $"profile name leaked into {name}");
            text.ShouldNotContain(TokenText, Case.Insensitive, $"token leaked into {name}");
        }

        DiagnosticsLeakScan.FindLeak(entries).ShouldBeNull();
    }

    [Fact]
    public void Only_allowlisted_files_are_copied_so_tokens_inputs_the_database_and_outputs_are_never_in_the_zip()
    {
        var names = BuildSeeded().Keys;

        names.ShouldNotContain(n => n.StartsWith("auth/", StringComparison.Ordinal));
        names.ShouldNotContain(n => n.Contains("inputs", StringComparison.Ordinal) || n.Contains("pasted", StringComparison.Ordinal));
        names.ShouldNotContain(n => n.Contains("app.db", StringComparison.Ordinal));
        names.ShouldNotContain(n => n.Contains("bedgraph", StringComparison.Ordinal));
        names.ShouldNotContain(n => n.Contains("..", StringComparison.Ordinal) || n.Contains("escape", StringComparison.Ordinal));
        names.ShouldContain("files/logs/app-20261003.log");
        names.ShouldContain("files/runs/job-1/status.json");
        names.ShouldContain("files/runs/job-1/progress.jsonl");
        names.ShouldContain("files/runs/job-1/result.json");
        names.ShouldContain("files/runs/job-1/logs/worker.log");
    }

    [Fact]
    public void Run_history_keeps_ids_states_error_codes_and_timestamps_and_drops_names_and_details()
    {
        var history = BuildSeeded()["run-history.json"];

        using var document = JsonDocument.Parse(history);
        var row = document.RootElement[0];
        row.GetProperty("jobId").GetString().ShouldBe("job-1");
        row.GetProperty("phase").GetString().ShouldBe("Failed");
        row.GetProperty("errorCode").GetString().ShouldBe("WORKER_FAILED");
        row.GetProperty("createdUtc").GetString().ShouldStartWith("2026-10-01");
        row.TryGetProperty("name", out _).ShouldBeFalse();
        row.TryGetProperty("errorDetail", out _).ShouldBeFalse();
        row.TryGetProperty("outputDir", out _).ShouldBeFalse();
    }

    [Fact]
    public void Settings_keep_only_the_known_safe_keys_and_name_the_rest_without_their_values()
    {
        var settings = BuildSeeded()["settings.json"];

        using var document = JsonDocument.Parse(settings);
        document.RootElement.GetProperty("Theme").GetString().ShouldBe("Dark");
        document.RootElement.GetProperty("installation_id").GetString().ShouldBe("inst-abc123");
        document.RootElement.GetProperty("default_output").GetString().ShouldBe("<omitted>");
        document.RootElement.GetProperty("account_email").GetString().ShouldBe("<omitted>");
    }

    [Fact]
    public void Versions_and_the_installation_id_are_recorded()
    {
        using var document = JsonDocument.Parse(BuildSeeded()["versions.json"]);
        var root = document.RootElement;

        root.GetProperty("appVersion").GetString().ShouldBe("0.1.0");
        root.GetProperty("os").GetString().ShouldBe("Windows 11 10.0.26300");
        root.GetProperty("dotNet").GetString().ShouldBe(".NET 10.0.0");
        root.GetProperty("webView2").GetString().ShouldBe("130.0.1");
        root.GetProperty("installationId").GetString().ShouldBe("inst-abc123");
    }

    [Fact]
    public void Log_lines_keep_their_meaning_with_paths_under_the_profile_reduced_to_user()
    {
        var log = BuildSeeded()["files/logs/app-20261003.log"];

        log.ShouldContain("12:02 job-1 ended");
        log.ShouldContain("<user>");
        log.ShouldContain("<sequence>");
    }

    [Fact]
    public void Status_json_keeps_state_and_numbers_but_drops_inputs_and_messages()
    {
        var status = BuildSeeded()["files/runs/job-1/status.json"];

        status.ShouldContain("\"state\": \"failed\"");
        status.ShouldContain("\"heartbeatSeq\": 7");
        status.ShouldContain("WORKER_FAILED");
        status.ShouldContain("us-central1-a");
        status.ShouldNotContain("\"input\"");
        status.ShouldNotContain("\"message\"");
    }

    [Fact]
    public void A_bundle_with_no_logs_at_all_is_still_built()
    {
        var entries = Unzip(DiagnosticsBundleBuilder.Build(new MemorySource([]), [], Info));

        entries.ShouldContainKey("versions.json");
        entries.ShouldContainKey("run-history.json");
        entries.Keys.ShouldNotContain(n => n.StartsWith("files/", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_file_is_skipped_not_fatal()
    {
        var source = new MemorySource(new Dictionary<string, string> { ["logs/gone.log"] = "x" });
        var vanishing = new VanishingSource(source);

        var entries = Unzip(DiagnosticsBundleBuilder.Build(vanishing, [], Info));

        entries.Keys.ShouldNotContain("files/logs/gone.log");
    }

    private sealed class VanishingSource(IDiagnosticsSource inner) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => inner.ListFiles();

        public byte[]? TryRead(string relativePath) => null;
    }

    [Fact]
    public void The_final_scan_names_the_entry_that_still_holds_sequence_like_text()
    {
        DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["a.json"] = "{}", ["b.log"] = "x acgtacgtacgtacgtacgtn y" }).ShouldBe("b.log");
        DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["a.json"] = "ACGTACGTACGTACGTACG" }).ShouldBeNull("19 bases is below the threshold");
    }

    [Fact]
    public void A_leak_the_redactor_cannot_see_refuses_the_zip_and_names_the_entry()
    {
        // A run's JobId is copied as an identifier, so a job id that is itself a DNA run is the one place the redactor
        // does not rewrite: the history is built from fields, and the final scan must still stop it.
        var leaky = Run with { JobId = Sequence };

        var ex = Should.Throw<DiagnosticsLeakException>(() => DiagnosticsBundleBuilder.Build(new MemorySource([]), [leaky], Info));

        ex.EntryName.ShouldBe("run-history.json");
    }
}
