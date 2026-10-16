using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
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

    private static Dictionary<string, string> BuildFor(RunRecord run, string log, bool include)
    {
        var files = new Dictionary<string, string> { [$"runs/{run.JobId}/logs/app.log"] = log };
        var zip = DiagnosticsBundleBuilder.Build(new MemorySource(files), [run], Info, includeNotesAndTags: include);
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

    [Fact]
    public void A_tag_is_scrubbed_from_logs_as_a_whole_word_only_never_inside_a_longer_word()
    {
        var run = new RunRecord(JobId, JobPhase.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), TagsJson: "[\"info\"]");

        var log = BuildFor(run, "information was read; info here\n", include: false).Single(e => e.Key.EndsWith(".log")).Value;

        log.ShouldContain("information was read");
        log.ShouldNotContain("info here");
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
}
