using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Diagnostics;

/// <summary>Issue #141: a run's notes and tags are user text. They stay out of the support bundle unless the user ticks the box.</summary>
public sealed class DiagnosticsNotesTagsTests
{
    private const string JobId = "20261001-080000-abc234";
    private const string Tag = "clone7-secret";
    private const string NoteWord = "zebra-batch-42";

    private static readonly DiagnosticsInfo Info = new(
        "0.1.0", "Windows 11", ".NET 10", "130.0.1", Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "jdoe"), [], new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), "readme");

    private static readonly RunRecord Run = new(JobId, JobPhase.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        Notes: $"this was the {NoteWord} that failed", TagsJson: $"[\"{Tag}\",\"redo\"]");

    private sealed class MemorySource(Dictionary<string, string> files) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => files.Keys.ToList();

        public DiagnosticsFile? TryRead(string relativePath, long maxBytes)
            => files.TryGetValue(relativePath, out var text) ? new DiagnosticsFile(Encoding.UTF8.GetBytes(text), false) : null;
    }

    private static Dictionary<string, string> Build(bool include)
    {
        // A log that happens to repeat the user's own words: the default must scrub them there too.
        var files = new Dictionary<string, string> { [$"runs/{JobId}/logs/app.log"] = $"opened results for {Tag} and {Run.Notes}\n" };
        var zip = include
            ? DiagnosticsBundleBuilder.Build(new MemorySource(files), [Run], Info, includeNotesAndTags: true)
            : DiagnosticsBundleBuilder.Build(new MemorySource(files), [Run], Info);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
    }

    [Fact]
    public void By_default_no_tag_and_no_whole_note_appears_anywhere_in_the_bundle()
    {
        var entries = Build(include: false);

        entries.ShouldNotBeEmpty();
        foreach (var (name, text) in entries)
        {
            text.ShouldNotContain(Tag, Case.Insensitive, $"in {name}");
            text.ShouldNotContain(Run.Notes!, Case.Insensitive, $"in {name}");
        }
    }

    [Fact]
    public void When_the_box_is_ticked_the_history_carries_the_note_and_tags()
    {
        var entries = Build(include: true);

        var row = JsonNode.Parse(entries["run-history.json"])!.AsArray().Single()!;
        row["notes"]!.GetValue<string>().ShouldContain(NoteWord);
        row["tags"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe([Tag, "redo"]);
    }

    [Fact]
    public void The_default_history_row_has_no_notes_or_tags_fields_at_all()
    {
        var row = JsonNode.Parse(Build(include: false)["run-history.json"])!.AsArray().Single()!.AsObject();

        row.ContainsKey("notes").ShouldBeFalse();
        row.ContainsKey("tags").ShouldBeFalse();
    }

    private static Dictionary<string, string> BuildFor(RunRecord run, string log, bool include, DiagnosticsInfo? info = null)
    {
        var files = new Dictionary<string, string> { [$"runs/{run.JobId}/logs/app.log"] = log };
        var zip = DiagnosticsBundleBuilder.Build(new MemorySource(files), [run], info ?? Info, includeNotesAndTags: include);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
    }

    // MEASURED 2026-10-16 (cold review #782): a tag equal to a word the bundle itself writes made the leak scan refuse every bundle.
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("phase")]
    [InlineData("target")]
    [InlineData("standard")]
    [InlineData("error")]
    [InlineData("info")]
    public void A_tag_that_is_an_ordinary_word_never_stops_the_bundle_being_made(string tag)
    {
        var run = new RunRecord(JobId, JobPhase.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), TagsJson: $"[\"{tag}\"]", Notes: tag);

        foreach (var include in new[] { false, true })
        {
            var entries = BuildFor(run, "info: phase completed with target standard, error none\n", include);
            entries.ShouldContainKey("run-history.json");
        }
    }

    // DECISION (agent-made, reversible): a tag or note word that is log vocabulary (a level, a status or phase word, a JSON key the
    // bundle keeps, a marker word) is not scrubbed from logs. The app never writes a note or tag into a log (Hard Rule on logging), so
    // such a word in a log is the log's own vocabulary; scrubbing it blanked every "info"/"error" and tripped the whole-file backstop.
    [Theory]
    [InlineData("info")]
    [InlineData("error")]
    [InlineData("failed")]
    [InlineData("Completed")]
    public void A_tag_that_is_log_vocabulary_does_not_blank_ordinary_log_words(string tag)
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, TagsJson: $"[\"{tag}\"]");

        var log = BuildFor(run, "info: job failed with error; completed\n", include: false).Single(e => e.Key.EndsWith(".log")).Value;

        log.ShouldContain("info: job failed with error; completed");
        log.ShouldNotContain("<redacted>");
    }

    [Fact]
    public void A_distinctive_tag_is_scrubbed_from_logs_as_a_whole_word_only_never_inside_a_longer_word()
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, TagsJson: "[\"clone7\"]");

        var log = BuildFor(run, "clone77 was read; clone7 here\n", include: false).Single(e => e.Key.EndsWith(".log")).Value;

        log.ShouldContain("clone77 was read");
        log.ShouldNotContain("clone7 here");
    }

    // MEASURED 2026-10-28 (cold review of #141): TagsJson=["level"] plus a progress.jsonl line with "level":"info" turned the file into
    // "<omitted: file withheld>", because the backstop re-matched the tag against keys and markers the redactor deliberately keeps.
    [Theory]
    [InlineData("level")]
    [InlineData("phase")]
    [InlineData("stage")]
    [InlineData("code")]
    [InlineData("status")]
    [InlineData("percent")]
    [InlineData("user")]
    [InlineData("path")]
    [InlineData("email")]
    [InlineData("file")]
    [InlineData("redacted")]
    [InlineData("token")]
    [InlineData("sequence")]
    [InlineData("omitted")]
    public void A_tag_equal_to_a_kept_key_or_a_marker_word_never_withholds_the_run_files(string tag)
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, TagsJson: $"[\"{tag}\"]");
        var files = new Dictionary<string, string>
        {
            [$"runs/{JobId}/progress.jsonl"] = "{\"seq\":1,\"stage\":\"predict\",\"level\":\"info\",\"percent\":5}\n",
            [$"runs/{JobId}/status.json"] = "{\"stage\":\"predict\",\"percent\":5}",
            [$"runs/{JobId}/result.json"] = "{\"status\":\"ok\"}",
            [$"runs/{JobId}/logs/app.log"] = $"read C:\\Lab\\x.fasta for jane@example.org and {tag}\n",
        };

        var zip = DiagnosticsBundleBuilder.Build(new MemorySource(files), [run], Info);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = archive.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());

        var copied = entries.Where(e => e.Key.StartsWith("files/", StringComparison.Ordinal)).ToList();
        copied.Count.ShouldBe(4);
        copied.ShouldNotContain(e => e.Value.Contains("<omitted: file withheld>", StringComparison.Ordinal), tag);
        entries.Single(e => e.Key.EndsWith("progress.jsonl")).Value.ShouldContain("\"level\":\"info\"");
        entries.Single(e => e.Key.EndsWith("status.json")).Value.ShouldContain("predict");
        entries.Single(e => e.Key.EndsWith("result.json")).Value.ShouldContain("\"status\"");
    }

    // The one real path past the primary scrub: a withheld all-digit job id in a number leaf is rewritten to the string "withheldid<n>" AFTER
    // the scrub (JobIdWithholder), so a tag spelled "withheldid1" survives into the copied file and only the whole-file backstop can see it.
    [Fact]
    public void A_distinctive_tag_that_survives_redaction_in_a_copied_file_withholds_that_file_whole()
    {
        var run = new RunRecord("12", JobPhase.Completed, Created, Name: "n", ManifestJson: "{}", OptionsJson: "{}", TagsJson: "[\"withheldid1\"]");
        var files = new Dictionary<string, string>
        {
            ["runs/12/result.json"] = """{"jobId":"12","status":"failed","inputs":[{"id":12,"status":"ok"}]}""",
            ["runs/12/status.json"] = """{"stage":"running","percent":7}""",
        };

        var entries = BuildEntries(files, run);

        entries.Single(e => e.Key.EndsWith("result.json")).Value.ShouldBe("<omitted: file withheld>");
        entries.Single(e => e.Key.EndsWith("status.json")).Value.ShouldContain("running");
    }

    // Cold review round 4: a tag spelled like a number ("1.0", "42.5") is a number leaf the same text can legitimately appear in, so it must
    // not make the backstop withhold progress/status/result whole. The primary scrub still removes it from log text.
    [Theory]
    [InlineData("1.0")]
    [InlineData("42.5")]
    [InlineData("-3")]
    [InlineData("1e3")]
    public void A_number_shaped_tag_does_not_withhold_a_file_that_holds_the_same_number_in_a_number_leaf(string tag)
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, TagsJson: $"[\"{tag}\"]");
        var files = new Dictionary<string, string>
        {
            [$"runs/{JobId}/progress.jsonl"] = "{\"seq\":1,\"stage\":\"running\",\"percent\":" + tag + "}\n",
            [$"runs/{JobId}/logs/app.log"] = $"INFO reading value {tag} now\n",
        };

        var entries = BuildEntries(files, run);

        var progress = entries.Single(e => e.Key.EndsWith("progress.jsonl")).Value;
        progress.ShouldContain("\"stage\":\"running\"");
        progress.ShouldNotBe("<omitted: file withheld>");
        entries.Single(e => e.Key.Contains("log-")).Value.ShouldNotContain(tag);
    }

    [Fact]
    public void ContainsUserText_ignores_a_number_shaped_word_but_the_primary_scrub_still_removes_it()
    {
        var redactor = new DiagnosticsRedactor(Info.UserProfilePath, [], ["42.5", "clone7"]);

        redactor.ContainsUserText("\"percent\": 42.5").ShouldBeFalse();
        redactor.ContainsUserText("saw clone7 here").ShouldBeTrue();
        redactor.RedactText("value 42.5 here").ShouldNotContain("42.5");
    }

    [Fact]
    public void ContainsUserText_sees_a_distinctive_word_but_not_a_marker_or_a_vocabulary_word()
    {
        var redactor = new DiagnosticsRedactor(Info.UserProfilePath, [], ["clone7", "level", "user"]);

        redactor.ContainsUserText("saw clone7 here").ShouldBeTrue();
        redactor.ContainsUserText("\"level\":\"info\" <user> <path>").ShouldBeFalse();
    }

    private static Dictionary<string, string> BuildEntries(IReadOnlyDictionary<string, string> files, RunRecord run, HashSet<string>? truncated = null)
    {
        var zip = DiagnosticsBundleBuilder.Build(new TruncatingSource(files, truncated ?? []), [run], Info);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
    }

    private sealed class TruncatingSource(IReadOnlyDictionary<string, string> files, HashSet<string> truncated) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => files.Keys.ToList();

        public DiagnosticsFile? TryRead(string relativePath, long maxBytes)
            => files.TryGetValue(relativePath, out var text) ? new DiagnosticsFile(Encoding.UTF8.GetBytes(text), truncated.Contains(relativePath)) : null;
    }

    // The vocabulary is derived from the code's own lists, so a tag equal to any value the bundle keeps leaves that value readable.
    public static TheoryData<string> KeptValues()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in WorkerStages.All)
        {
            values.Add(stage);
        }

        foreach (var code in RunErrorCodes.All.Concat(RunErrorCodes.WorkerErrorCodes))
        {
            values.Add(code);
        }

        foreach (var kind in Enum.GetValues<CloudErrorKind>())
        {
            values.Add(kind.ToString());
            values.Add(string.Concat(kind.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
        }

        foreach (var gpu in GpuCatalog.Shipped.Gpus)
        {
            values.Add(gpu.MachineType);
            values.Add(gpu.Tier.ToString());
            values.UnionWith(gpu.Accelerator.Split('-'));
        }

        values.UnionWith(["L4", "A100", "H100", "quota", "stockout", "billing", "permission", "network"]);
        values.RemoveWhere(v => v.Length < 2 || v.All(char.IsDigit) || v.Length > 40);
        var data = new TheoryData<string>();
        foreach (var value in values.OrderBy(v => v, StringComparer.Ordinal))
        {
            data.Add(value);
        }

        return data;
    }

    [Fact]
    public void The_kept_value_roster_is_not_vacuous()
    {
        KeptValues().Count().ShouldBeGreaterThan(80);
    }

    // MEASURED 2026-10-28 (cold review round 3): the first vocabulary missed worker stages, error codes, cloud buckets and GPU names, so a tag
    // "quota" or "L4" blanked those words in every kept leaf.
    [Theory]
    [MemberData(nameof(KeptValues))]
    public void A_tag_equal_to_a_value_the_bundle_keeps_leaves_that_value_readable(string value)
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, TagsJson: JsonSerializer.Serialize(new[] { value }));
        var files = new Dictionary<string, string>
        {
            [$"runs/{JobId}/progress.jsonl"] = JsonSerializer.Serialize(new { seq = 1, stage = value, level = "info", percent = 5 }) + "\n",
        };

        var progress = BuildEntries(files, run).Single(e => e.Key.EndsWith("progress.jsonl")).Value;

        progress.ShouldContain($"\"stage\":\"{value}\"");
    }

    // The fixed words of the bundle's own omission markers must never turn a marker into a withheld file.
    [Theory]
    [InlineData("omitted")]
    [InlineData("withheld")]
    [InlineData("too")]
    [InlineData("large")]
    [InlineData("valid")]
    [InlineData("json")]
    public void A_tag_that_is_a_word_of_an_omission_marker_leaves_the_marker_as_it_is(string tag)
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, TagsJson: $"[\"{tag}\"]");
        var files = new Dictionary<string, string>
        {
            [$"runs/{JobId}/status.json"] = "{\"stage\":\"running\"}",
            [$"runs/{JobId}/result.json"] = "not json at all",
        };

        var entries = BuildEntries(files, run, [$"runs/{JobId}/status.json"]);

        entries.Single(e => e.Key.EndsWith("status.json")).Value.ShouldBe("<omitted: file too large>");
        entries.Single(e => e.Key.EndsWith("result.json")).Value.ShouldBe("<omitted: file is not valid json>");
    }

    // The marker roster has one source (DiagnosticsMarkers); the redactor's backstop and the leak scan read it, and so does the vocabulary.
    [Fact]
    public void Every_marker_the_builder_writes_matches_the_one_marker_pattern_and_its_words_are_vocabulary()
    {
        List<string> markers =
        [
            DiagnosticsMarkers.OmittedFile, DiagnosticsMarkers.OmittedNote, DiagnosticsMarkers.OmittedUnreadable, DiagnosticsMarkers.OmittedTooLarge,
            DiagnosticsMarkers.Unparseable, .. DiagnosticsMarkers.Words.Select(w => $"<{w}>"),
        ];

        var pattern = new System.Text.RegularExpressions.Regex($"^(?:{DiagnosticsMarkers.Pattern})$");
        foreach (var marker in markers)
        {
            pattern.IsMatch(marker).ShouldBeTrue(marker);
        }

        DiagnosticsMarkers.Words.Count.ShouldBeGreaterThanOrEqualTo(7);
        foreach (var word in DiagnosticsMarkers.Words.Concat(["omitted", "withheld"]))
        {
            DiagnosticsRedactor.IsLogVocabulary(word).ShouldBeTrue(word);
        }
    }

    [Fact]
    public void The_leak_scan_ignores_every_marker_even_when_a_sensitive_value_is_one_of_its_words()
    {
        var entries = new Dictionary<string, string>
        {
            ["a"] = string.Join(' ', DiagnosticsMarkers.Words.Select(w => $"<{w}>")) + " " + DiagnosticsMarkers.OmittedFile + " " + DiagnosticsMarkers.OmittedNote,
        };

        DiagnosticsLeakScan.FindLeak(entries, [.. DiagnosticsMarkers.Words, "omitted", "withheld"]).ShouldBeNull();
    }

    [Fact]
    public void A_note_the_redactor_cannot_clean_is_withheld_whole_instead_of_failing_the_bundle()
    {
        // A two-part token shape: the scan's credential rule matches it and the redactor's three-part rule does not.
        var note = "pasted eyJabcdefghijk.abcdefghijkl by mistake";
        var run = new RunRecord(JobId, JobPhase.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), Notes: note);

        var history = BuildFor(run, "ok\n", include: true)["run-history.json"];

        JsonNode.Parse(history)!.AsArray().Single()!["notes"]!.GetValue<string>().ShouldBe("<omitted: note withheld>");
    }

    [Fact]
    public void With_the_box_ticked_a_note_holding_a_path_an_email_and_a_sequence_is_redacted_and_the_bundle_is_still_made()
    {
        var note = @"see C:\Lab\Patient42\plate.fasta, mail jane.roe@example.org, read ACGTACGTACGTACGTACGTACGTACGT end";
        var run = new RunRecord(JobId, JobPhase.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), Notes: note, TagsJson: "[\"jane.roe@example.org\",\"D:\\\\x\\\\y.txt\"]");

        var history = BuildFor(run, "ok\n", include: true)["run-history.json"];

        history.ShouldNotContain("Patient42");
        history.ShouldNotContain("jane.roe@example.org");
        history.ShouldNotContain("ACGTACGTACGTACGTACGT");
        var row = JsonNode.Parse(history)!.AsArray().Single()!;
        row["notes"]!.GetValue<string>().ShouldContain("end");
    }

    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_multi_line_note_is_scrubbed_from_a_log_line_by_line()
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, Notes: "a\nb secret");

        var entries = BuildFor(run, "opened b secret here\n", include: false);

        foreach (var (name, text) in entries)
        {
            text.ShouldNotContain("b secret", Case.Insensitive, $"in {name}");
        }
    }

    [Fact]
    public void A_tag_with_quotes_is_scrubbed_in_its_json_escaped_spelling_too()
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, TagsJson: "[\"say \\\"hi there\\\"\"]");

        var log = BuildFor(run, "msg=say \\\"hi there\\\" and say \"hi there\"\n", include: false).Single(e => e.Key.EndsWith(".log")).Value;

        log.ShouldNotContain("hi there");
    }

    [Fact]
    public void A_ticked_note_that_names_the_profile_folder_is_withheld_not_a_failed_bundle()
    {
        var run = new RunRecord(JobId, JobPhase.Completed, Created, Notes: "ask jdoe");

        var history = BuildFor(run, "ok\n", include: true)["run-history.json"];

        history.ShouldNotContain("jdoe");
    }

    [Fact]
    public void A_user_named_User_with_a_ticked_path_note_still_makes_the_bundle_and_keeps_the_marker_readable()
    {
        var profile = Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "User");
        var info = Info with { UserProfilePath = profile, SensitiveValues = ["User"] };
        var run = new RunRecord(JobId, JobPhase.Completed, Created, Notes: profile + Path.DirectorySeparatorChar + "x.txt");

        var history = BuildFor(run, "ok\n", include: true, info)["run-history.json"];

        history.ShouldNotContain("x.txt");
        history.ShouldNotContain("\\u003C");
    }
}
