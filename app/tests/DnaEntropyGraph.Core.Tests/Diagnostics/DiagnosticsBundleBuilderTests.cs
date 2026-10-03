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
/// Issue #106's observable: grepping the bundle for the sequence, a file name, a contig name, a path or a token finds
/// nothing. The seeded app data folder holds every kind of thing the real one does, including the ones that must never be copied.
/// </summary>
public class DiagnosticsBundleBuilderTests
{
    private const string Sequence = "ACGTTGCAAGGCTTAACCGGTTAAGGCCTTAAGGCC";
    private const string FileName = "plasmid_pUC19_secret.fasta";
    private const string SpacedFileName = "My Plasmid.fasta";
    private const string Email = "jdoe@lab.example.edu";
    private const string Profile = @"C:\Users\jdoe";
    private const string TokenText = "REFRESH-TOKEN-DO-NOT-LEAK-1//0gABC";
    private const string Contig = "SetTnpB_3";
    private const string OtherContig = "contig_secret_alpha";
    private const string PatientKey = "my_patient_42";
    private const string Project = "lab-proj-9931";
    private const string Bucket = "bkt-secret-4477";
    private const string Vm = "deg-vm-secret-55";

    // Credentials in the shapes Google and the app print them.
    private static readonly string[] Credentials =
    [
        "ya29.a0AfH6SMBxZ1234567890abcdefGHIJ",
        "1//0gABCdefGHIjklmnopqrstuVWXyz",
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcDEF123_xyz",
        "abcDEF123456789xyzTOKEN",
        "abc123def456ghi789jkl",
        "GOCSPX-abcdefghijklmnop12",
    ];

    private static readonly DiagnosticsInfo Info = new(
        "0.1.0", "Windows 11 10.0.26300", ".NET 10.0.0", "130.0.1", Profile, [Email, "jdoe"], new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    private sealed class MemorySource(Dictionary<string, string> files, int? cap = null) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => files.Keys.ToList();

        public DiagnosticsFile? TryRead(string relativePath, long maxBytes)
        {
            if (!files.TryGetValue(relativePath, out var text))
            {
                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            var limit = Math.Min(maxBytes, cap ?? long.MaxValue);
            return bytes.Length > limit ? new DiagnosticsFile(bytes[^(int)limit..], true) : new DiagnosticsFile(bytes, false);
        }
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
        ["logs/app-20261003.log"] = string.Join(
            '\n',
            $"12:00 opened {Profile}\\Downloads\\{FileName} for {Email}",
            $"12:01 sequence {Sequence}",
            "12:02 job-1 ended",
            $"12:03 contig {Contig} from D:\\Lab\\Patient42\\{SpacedFileName}",
            $"12:04 contig {OtherContig} bucket {Bucket} vm {Vm} project {Project}",
            $"12:05 record {PatientKey} scored",
            @"12:06 share \\labserver\data\runs\batch7.gb read",
            $"12:07 access token {Credentials[0]} and refresh {Credentials[1]}",
            $"12:08 id {Credentials[2]}",
            $"12:09 Authorization: Bearer {Credentials[3]}",
            $"12:10 {{\"access_token\":\"{Credentials[4]}\"}} client_secret={Credentials[5]}",
            "12:11 rna ACGUUGCAAGGCUUAACCGGUUAAGGCC",
            "12:12 iupac ACGTRYKMSWBDHVNACGTRYKMSWBDHVN",
            "12:13 gapped ACGT-ACGT-ACGT-ACGT-ACGT*",
            "LOCUS       pUC19",
            "ORIGIN",
            "        1 acgttgcaag gcttaaccgg ttaaggcctt aaggccaatt",
            "       41 ccggttaagg ttccaaggtt",
            "//",
            "12:14 wrapped",
            "ACGTTGCAAGGCTTAACCGGTTAAGGCCTTAAGGCCTTAACCGGTTAAGGCCTTAAGGCC",
            "TTGCAAGGCTTAACCGGTTAAGGCCTT",
            "12:15 split",
            "GGATCCGATA",
            "CGCGATATCG",
            "12:16 done"),
        ["runs/job-1/status.json"] = JsonSerializer.Serialize(new
        {
            schema = 1,
            jobId = "job-1",
            stage = "failed",
            percent = 42.5,
            heartbeatSeq = 7,
            detail = new { input = FileName, contig = Contig, window = 5, windows = 11, direction = "forward" },
            input = FileName,
            vm = new { name = Vm, zone = "us-central1-a", gpu = "NVIDIA L4" },
            error = new { code = "WORKER_FAILED", message = $"could not read {FileName} {Sequence}", remediation = "x" },
        }),
        ["runs/job-1/progress.jsonl"] =
            JsonSerializer.Serialize(new { seq = 1, ts = "2026-10-01T08:00:00Z", stage = "analysing", level = "info", message = $"{Contig} {Sequence}", data = new { input = FileName } })
            + "\n" + JsonSerializer.Serialize(new { seq = 2, stage = "ACGUUGCAAGGCUUAACCGGUUAAGGCC" })
            + "\nnot json " + Sequence + "\n",
        ["runs/job-1/result.json"] = JsonSerializer.Serialize(new
        {
            schema = 1,
            jobId = "job-1",
            status = "failed",
            perContig = new Dictionary<string, double> { [PatientKey] = 1.9 },
            inputs = new[]
            {
                new
                {
                    id = "in1",
                    status = "failed",
                    name = FileName,
                    contigs = new[] { new { id = OtherContig, name = Contig } },
                    stats = new { contigs = 1, totalNt = 36, meanEntropy = 1.34 },
                },
            },
            timing = new { startedAt = "2026-10-01T08:00:00Z", finishedAt = "2026-10-01T09:00:00Z" },
        }),
        ["runs/job-1/logs/worker.log"] = $"INFO read {FileName}\nINFO seq {Sequence.ToLowerInvariant()}\nERROR OOM\n",
        ["runs/job-1/outputs/entropy.bedgraph"] = "chr 0 1 1.9\n",
        ["runs/../escape/status.json"] = "{}",
    };

    private static readonly RunRecord Run = new(
        "job-1", JobPhase.Failed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        Name: "Patient run", ErrorCode: "WORKER_FAILED", ErrorDetail: $"{Sequence} in {FileName} for {Email}",
        OutputDir: $@"{Profile}\Downloads\{FileName}-run", InstallationId: "inst-abc123", MachineType: "g2-standard-8",
        ProjectId: Project, Bucket: Bucket, VmName: Vm,
        ManifestJson: JsonSerializer.Serialize(new { inputs = new[] { new { id = "in1", name = FileName, path = $@"D:\Lab\Patient42\{SpacedFileName}" } } }),
        OptionsJson: "{}");

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

    // The test's own view of "is a sequence in there": letters only, upper case, U read as T, across line breaks.
    private static string Squash(string text) =>
        new string(text.Where(char.IsLetter).ToArray()).ToUpperInvariant().Replace('U', 'T');

    [Fact]
    public void No_entry_contains_the_sequence_names_paths_email_or_any_credential()
    {
        var entries = BuildSeeded();
        entries.Count.ShouldBeGreaterThan(5, "vacuity: the bundle was actually built");

        string[] forbidden =
        [
            Sequence, Sequence[..20], FileName, "plasmid_pUC19", SpacedFileName, "Plasmid.fasta", "Patient42", "Patient run", Email, "jdoe", TokenText,
            Contig, OtherContig, PatientKey, "perContig", Project, Bucket, Vm, "labserver", "batch7", .. Credentials,
        ];
        foreach (var (name, text) in entries)
        {
            foreach (var value in forbidden)
            {
                text.ShouldNotContain(value, Case.Insensitive, $"'{value}' leaked into {name}");
            }

            // Sequence in any layout: RNA, IUPAC, wrapped, numbered, spaced, split over lines.
            var squashed = Squash(text);
            foreach (var fragment in new[] { "ACGTTGCAAGGCTTAACC", "AAGGCCAATTCCGGTTAAGG", "GGATCCGATACGCGATATCG", "RYKMSWBDHVN", "TTGCAAGGCTTAACCGGTTAAGG" })
            {
                squashed.ShouldNotContain(fragment, Case.Sensitive, $"sequence fragment {fragment} leaked into {name}");
            }
        }

        DiagnosticsLeakScan.FindLeak(entries).ShouldBeNull();
    }

    [Fact]
    public void The_reviewers_line_loses_its_path_its_spaced_file_name_and_its_contig_but_keeps_its_shape()
    {
        var log = BuildSeeded()["files/logs/app-20261003.log"];

        var line = log.Split('\n').Single(l => l.StartsWith("12:03", StringComparison.Ordinal));
        line.ShouldBe("12:03 contig <redacted> from <path>");
    }

    [Fact]
    public void Any_windows_path_becomes_path_and_a_profile_path_becomes_user()
    {
        var log = BuildSeeded()["files/logs/app-20261003.log"];

        log.Split('\n').Single(l => l.StartsWith("12:06", StringComparison.Ordinal)).ShouldBe("12:06 share <path> read");
        log.Split('\n').Single(l => l.StartsWith("12:00", StringComparison.Ordinal)).ShouldBe("12:00 opened <user> for <redacted>");
    }

    [Fact]
    public void Credentials_are_replaced_wherever_they_appear_in_a_log()
    {
        var log = BuildSeeded()["files/logs/app-20261003.log"];

        log.ShouldContain("12:07 access token <token> and refresh <token>");
        log.ShouldContain("12:09 Authorization: <token>");
        log.ShouldContain("client_secret=<token>");
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
        foreach (var dropped in new[] { "name", "errorDetail", "outputDir", "projectId", "bucket", "vmName", "manifestJson" })
        {
            row.TryGetProperty(dropped, out _).ShouldBeFalse(dropped);
        }
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
    public void Log_lines_keep_their_meaning()
    {
        var log = BuildSeeded()["files/logs/app-20261003.log"];

        log.ShouldContain("12:02 job-1 ended");
        log.ShouldContain("12:16 done");
        log.ShouldContain("<sequence>");
    }

    [Fact]
    public void Status_json_keeps_only_allowlisted_keys_with_their_numbers_and_codes()
    {
        var status = BuildSeeded()["files/runs/job-1/status.json"];

        status.ShouldContain("\"stage\": \"failed\"");
        status.ShouldContain("\"heartbeatSeq\": 7");
        status.ShouldContain("\"window\": 5");
        status.ShouldContain("WORKER_FAILED");
        status.ShouldContain("us-central1-a");
        status.ShouldContain("NVIDIA L4");
        foreach (var dropped in new[] { "\"input\"", "\"contig\"", "\"message\"", "\"remediation\"", "\"name\"" })
        {
            status.ShouldNotContain(dropped);
        }
    }

    [Fact]
    public void Result_json_drops_unknown_keys_whole_so_a_data_named_key_cannot_pass()
    {
        // {"perContig":{"my_patient_42":1.9}}: the key names the user's data, so the key is the leak.
        var result = BuildSeeded()["files/runs/job-1/result.json"];

        result.ShouldNotContain("perContig");
        result.ShouldNotContain(PatientKey);
        result.ShouldContain("\"meanEntropy\": 1.34");
        result.ShouldNotContain("contigs\": [");
    }

    [Fact]
    public void Progress_keeps_only_allowlisted_keys_and_a_sequence_in_a_kept_value_is_redacted()
    {
        var progress = BuildSeeded()["files/runs/job-1/progress.jsonl"];

        progress.ShouldContain("\"stage\":\"analysing\"");
        progress.ShouldNotContain("message");
        progress.ShouldContain("<unparseable line omitted>");
        progress.ShouldContain("\"stage\":\"<redacted>\"");
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
        var vanishing = new VanishingSource(new MemorySource(new Dictionary<string, string> { ["logs/gone.log"] = "x" }));

        var entries = Unzip(DiagnosticsBundleBuilder.Build(vanishing, [], Info));

        entries.Keys.ShouldNotContain("files/logs/gone.log");
    }

    private sealed class VanishingSource(IDiagnosticsSource inner) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => inner.ListFiles();

        public DiagnosticsFile? TryRead(string relativePath, long maxBytes) => null;
    }

    [Fact]
    public void A_file_over_the_cap_is_read_as_its_tail_and_marked_truncated_and_an_oversized_json_file_is_left_out()
    {
        var folder = new Dictionary<string, string>
        {
            ["logs/big.log"] = "partial first line\nline two\nline three\n",
            ["runs/job-1/status.json"] = "{\"schema\":1,\"stage\":\"running\"}",
        };

        var entries = Unzip(DiagnosticsBundleBuilder.Build(new MemorySource(folder, cap: 20), [], Info));

        entries["files/logs/big.log"].ShouldStartWith("[truncated:");
        entries["files/logs/big.log"].ShouldContain("line three");
        entries["files/logs/big.log"].ShouldNotContain("partial first line");
        entries["files/runs/job-1/status.json"].ShouldBe("<omitted: file too large>");
    }

    [Fact]
    public void The_final_scan_is_stricter_than_the_redactor_and_names_the_entry()
    {
        DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["a.json"] = "{}", ["b.log"] = "x acgtacgtacgtacgtacgtn y" }).ShouldBe("b.log");
        DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["a.json"] = "ACGTACGTACGTACGTACG" }).ShouldBeNull("19 bases is below the threshold");

        // Spaced, numbered, wrapped, split over lines, RNA, IUPAC, gapped.
        foreach (var layout in new[]
                 {
                     "1 acgttgcaag gcttaaccgg ttaag",
                     "ACGTTGCAAG\nGCTTAACCGG\n",
                     "ACGUUGCAAGGCUUAACCGGUUAA",
                     "RYKMSWBDHVNRYKMSWBDHVNAC",
                     "ACGT-ACGT-ACGT-ACGT-ACGT",
                 })
        {
            DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["x.log"] = layout }).ShouldBe("x.log", layout);
        }

        // Entry names are checked too, and so are credentials.
        DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["files/ACGTTGCAAGGCTTAACCGGTTAAGG.log"] = "ok" }).ShouldBe("files/ACGTTGCAAGGCTTAACCGGTTAAGG.log");
        foreach (var credential in Credentials)
        {
            DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["x.log"] = "Authorization: Bearer " + credential + " " + credential }).ShouldBe("x.log");
        }

        DiagnosticsLeakScan.FindLeak(new Dictionary<string, string> { ["x.log"] = "refresh_token=abcdefghijkl1234" }).ShouldBe("x.log");
    }

    [Fact]
    public void The_final_scan_does_not_trip_on_digests_timestamps_or_rules()
    {
        DiagnosticsLeakScan.FindLeak(new Dictionary<string, string>
        {
            ["a.json"] = "{\"image\": \"sha256:9f2bacacdbcdacbdbcaaddc2c1e4a5acdbdbcaabcdbcdacdbacdbacd09\", \"at\": \"2026-10-03T12:00:00Z\"}",
            ["b.txt"] = "--------------------\nDone: 3 jobs\n",
        }).ShouldBeNull();
    }

    [Fact]
    public void A_leak_the_redactor_cannot_see_refuses_the_zip_and_names_the_entry()
    {
        // The run history copies fields as they are, so a job id that is itself a DNA run, or an error code that is a
        // token, is the one place the redactor does not rewrite: the final scan must still stop it.
        foreach (var leaky in new[] { Run with { JobId = Sequence }, Run with { ErrorCode = Credentials[0] } })
        {
            var ex = Should.Throw<DiagnosticsLeakException>(() => DiagnosticsBundleBuilder.Build(new MemorySource([]), [leaky], Info));

            ex.EntryName.ShouldBe("run-history.json");
        }
    }
}
