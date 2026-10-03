using System.Text.Json;
using DnaEntropyGraph.Presentation.Viewer;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public sealed class IgvLoadPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-igv-" + Guid.NewGuid().ToString("N"));

    public IgvLoadPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private void Touch(params string[] names)
    {
        foreach (var n in names)
        {
            File.WriteAllText(Path.Combine(_dir, n), "x");
        }
    }

    private static JsonElement Parse(IgvLoadPlan plan) => JsonDocument.Parse(plan.LoadMessageJson!).RootElement;

    private static List<JsonElement> Tracks(IgvLoadPlan plan) =>
        Parse(plan).GetProperty("config").GetProperty("tracks").EnumerateArray().ToList();

    [Fact]
    public void Full_run_folder_yields_reference_entropy_track_and_gene_track()
    {
        Touch("sample.fasta", "sample.entropy.bedgraph", "sample.genes.gff3", "sample.entropy.tsv");

        var plan = IgvLoadPlanner.Plan(_dir);

        plan.Failure.ShouldBe(IgvPlanFailure.None);
        var root = Parse(plan);
        root.GetProperty("cmd").GetString().ShouldBe("load");
        var cfg = root.GetProperty("config");
        var reference = cfg.GetProperty("reference");
        reference.GetProperty("fastaURL").GetString().ShouldBe("https://run.deg/sample.fasta");
        reference.GetProperty("indexed").GetBoolean().ShouldBeFalse();
        var tracks = Tracks(plan);
        tracks.Count.ShouldBe(2);
        var entropy = tracks[0];
        entropy.GetProperty("type").GetString().ShouldBe("wig");
        entropy.GetProperty("format").GetString().ShouldBe("bedgraph");
        entropy.GetProperty("url").GetString().ShouldBe("https://run.deg/sample.entropy.bedgraph");
        entropy.GetProperty("min").GetDouble().ShouldBe(0.0);
        entropy.GetProperty("max").GetDouble().ShouldBe(2.0);
        entropy.GetProperty("autoscale").GetBoolean().ShouldBeFalse();
        var genes = tracks[1];
        genes.GetProperty("type").GetString().ShouldBe("annotation");
        genes.GetProperty("format").GetString().ShouldBe("gff3");
        genes.GetProperty("url").GetString().ShouldBe("https://run.deg/sample.genes.gff3");
    }

    [Fact]
    public void Missing_genes_file_means_no_gene_track_but_the_entropy_track_is_still_there()
    {
        Touch("s.fasta", "s.entropy.bedgraph");
        var tracks = Tracks(IgvLoadPlanner.Plan(_dir));
        tracks.Count.ShouldBe(1);
        tracks[0].GetProperty("url").GetString().ShouldBe("https://run.deg/s.entropy.bedgraph");
    }

    [Fact]
    public void Wig_is_used_when_no_bedgraph_exists()
    {
        Touch("s.fasta", "s.entropy.wig");
        var t = Tracks(IgvLoadPlanner.Plan(_dir))[0];
        t.GetProperty("format").GetString().ShouldBe("wig");
        t.GetProperty("url").GetString().ShouldBe("https://run.deg/s.entropy.wig");
    }

    [Fact]
    public void Bedgraph_wins_over_wig_for_the_same_track()
    {
        Touch("s.fasta", "s.entropy.bedgraph", "s.entropy.wig");
        var tracks = Tracks(IgvLoadPlanner.Plan(_dir));
        tracks.Count.ShouldBe(1);
        tracks[0].GetProperty("format").GetString().ShouldBe("bedgraph");
    }

    [Fact]
    public void Both_separate_tracks_mode_has_combined_forward_and_reverse_all_three_with_distinct_names()
    {
        // docs/science_and_formats.md: ".entropy.fwd.* and .entropy.rev.* alongside the combined track".
        Touch("s.fasta", "s.entropy.bedgraph", "s.entropy.fwd.bedgraph", "s.entropy.rev.bedgraph");
        var tracks = Tracks(IgvLoadPlanner.Plan(_dir));
        tracks.Select(t => t.GetProperty("url").GetString()).ShouldBe(
        [
            "https://run.deg/s.entropy.bedgraph",
            "https://run.deg/s.entropy.fwd.bedgraph",
            "https://run.deg/s.entropy.rev.bedgraph",
        ]);
        tracks.Select(t => t.GetProperty("name").GetString()).Distinct().Count().ShouldBe(3);
        tracks.ForEach(t => t.GetProperty("max").GetDouble().ShouldBe(2.0));
    }

    [Fact]
    public void Only_forward_and_reverse_files_give_just_those_two_tracks()
    {
        Touch("s.fasta", "s.entropy.fwd.bedgraph", "s.entropy.rev.bedgraph");
        var tracks = Tracks(IgvLoadPlanner.Plan(_dir));
        tracks.Select(t => t.GetProperty("url").GetString()).ShouldBe(
        [
            "https://run.deg/s.entropy.fwd.bedgraph",
            "https://run.deg/s.entropy.rev.bedgraph",
        ]);
    }

    [Fact]
    public void Surprisal_only_is_not_an_entropy_track_and_says_so()
    {
        Touch("s.fasta", "s.surprisal.bedgraph");
        var plan = IgvLoadPlanner.Plan(_dir);
        plan.Failure.ShouldBe(IgvPlanFailure.NoEntropyTrack);
        plan.LoadMessageJson.ShouldBeNull();
    }

    [Fact]
    public void Surprisal_next_to_entropy_is_ignored()
    {
        Touch("s.fasta", "s.entropy.bedgraph", "s.surprisal.bedgraph");
        Tracks(IgvLoadPlanner.Plan(_dir)).Count.ShouldBe(1);
    }

    [Fact]
    public void No_fasta_fails_with_NoSequenceFile()
    {
        Touch("s.entropy.bedgraph");
        var plan = IgvLoadPlanner.Plan(_dir);
        plan.Failure.ShouldBe(IgvPlanFailure.NoSequenceFile);
        plan.LoadMessageJson.ShouldBeNull();
    }

    [Fact]
    public void Missing_folder_fails_with_FolderMissing()
    {
        IgvLoadPlanner.Plan(Path.Combine(_dir, "nope")).Failure.ShouldBe(IgvPlanFailure.FolderMissing);
    }

    [Fact]
    public void Empty_folder_argument_fails_with_FolderMissing()
    {
        IgvLoadPlanner.Plan("").Failure.ShouldBe(IgvPlanFailure.FolderMissing);
    }

    [Theory]
    [InlineData("my seq#1", "my%20seq%231")]
    [InlineData("s\u00e9q", "s%C3%A9q")]
    [InlineData("100%", "100%25")]
    [InlineData("a&b=c", "a%26b%3Dc")]
    public void Every_url_is_escaped_for_reference_entropy_and_gene_files(string stem, string escaped)
    {
        Touch(stem + ".fasta", stem + ".entropy.bedgraph", stem + ".genes.gff3");
        var cfg = Parse(IgvLoadPlanner.Plan(_dir)).GetProperty("config");
        cfg.GetProperty("reference").GetProperty("fastaURL").GetString().ShouldBe($"https://run.deg/{escaped}.fasta");
        var tracks = cfg.GetProperty("tracks").EnumerateArray().ToList();
        tracks[0].GetProperty("url").GetString().ShouldBe($"https://run.deg/{escaped}.entropy.bedgraph");
        tracks[1].GetProperty("url").GetString().ShouldBe($"https://run.deg/{escaped}.genes.gff3");
    }

    [Fact]
    public void Entropy_for_a_different_stem_than_the_only_fasta_is_NoEntropyTrack()
    {
        Touch("a.fasta", "other.entropy.bedgraph");
        IgvLoadPlanner.Plan(_dir).Failure.ShouldBe(IgvPlanFailure.NoEntropyTrack);
    }

    [Fact]
    public void Several_fastas_prefer_the_one_with_an_entropy_track()
    {
        Touch("a.fasta", "b.fasta", "b.entropy.bedgraph", "b.genes.gff3", "a.genes.gff3");
        var plan = IgvLoadPlanner.Plan(_dir);
        plan.Failure.ShouldBe(IgvPlanFailure.None);
        Parse(plan).GetProperty("config").GetProperty("reference").GetProperty("fastaURL").GetString().ShouldBe("https://run.deg/b.fasta");
        Tracks(plan).Select(t => t.GetProperty("url").GetString()).ShouldBe(["https://run.deg/b.entropy.bedgraph", "https://run.deg/b.genes.gff3"]);
    }

    [Fact]
    public void Several_fastas_each_with_entropy_is_ambiguous_not_silently_the_first()
    {
        Touch("a.fasta", "b.fasta", "a.entropy.bedgraph", "b.entropy.bedgraph");
        var plan = IgvLoadPlanner.Plan(_dir);
        plan.Failure.ShouldBe(IgvPlanFailure.AmbiguousSequenceFile);
        plan.LoadMessageJson.ShouldBeNull();
    }

    [Fact]
    public void Several_fastas_none_with_entropy_is_NoEntropyTrack()
    {
        Touch("a.fasta", "b.fasta");
        IgvLoadPlanner.Plan(_dir).Failure.ShouldBe(IgvPlanFailure.NoEntropyTrack);
    }

    [Fact]
    public void A_fasta_over_the_size_limit_fails_with_SequenceTooLarge()
    {
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), "0123456789");
        Touch("s.entropy.bedgraph");
        IgvLoadPlanner.Plan(_dir, maxFastaBytes: 9).Failure.ShouldBe(IgvPlanFailure.SequenceTooLarge);
    }

    [Fact]
    public void A_fasta_exactly_at_the_size_limit_loads()
    {
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), "0123456789");
        Touch("s.entropy.bedgraph");
        IgvLoadPlanner.Plan(_dir, maxFastaBytes: 10).Failure.ShouldBe(IgvPlanFailure.None);
    }

    [Fact]
    public void The_default_size_limit_is_the_spec_50_MB()
    {
        ViewerLimits.MaxFastaBytes.ShouldBe(50L * 1024 * 1024);
    }

    [Fact]
    public void Plan_exposes_the_run_folder_for_host_mapping()
    {
        Touch("s.fasta", "s.entropy.bedgraph");
        IgvLoadPlanner.Plan(_dir).RunFolder.ShouldBe(_dir);
    }
}

public sealed class ViewerUrlsTests
{
    [Theory]
    [InlineData("https://viewer.deg/index.html", true)]
    [InlineData("https://viewer.deg/", true)]
    [InlineData("https://viewer.deg", true)]
    [InlineData("https://VIEWER.deg/index.html", true)]
    [InlineData("https://run.deg/x.fasta", false)]
    [InlineData("https://viewer.deg.evil.com/index.html", false)]
    [InlineData("https://user@viewer.deg/index.html", false)]
    [InlineData("https://user:pw@viewer.deg/index.html", false)]
    [InlineData("https://viewer.deg:8443/index.html", false)]
    [InlineData("http://viewer.deg/index.html", false)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("not a uri", false)]
    [InlineData("", false)]
    public void Navigation_is_allowed_only_to_https_viewer_deg(string uri, bool allowed)
    {
        ViewerUrls.IsAllowedNavigation(uri).ShouldBe(allowed);
    }

    [Fact]
    public void Null_is_not_allowed()
    {
        ViewerUrls.IsAllowedNavigation(null).ShouldBeFalse();
    }

    [Fact]
    public void Start_url_itself_is_allowed()
    {
        ViewerUrls.IsAllowedNavigation(ViewerUrls.StartUrl).ShouldBeTrue();
    }
}

public sealed class WebViewRuntimeDecisionTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("130.0.2849.39", true)]
    public void Version_string_decides_availability(string? version, bool available)
    {
        WebViewRuntimeDecision.IsAvailable(() => version).ShouldBe(available);
    }

    [Fact]
    public void Exception_means_unavailable()
    {
        WebViewRuntimeDecision.IsAvailable(() => throw new InvalidOperationException()).ShouldBeFalse();
    }
}

public sealed class ViewerAttachGateTests
{
    [Fact]
    public void First_begin_succeeds_and_a_second_is_refused()
    {
        var gate = new ViewerAttachGate();
        gate.TryBegin(out var generation).ShouldBeTrue();
        gate.IsCurrent(generation).ShouldBeTrue();
        gate.TryBegin(out _).ShouldBeFalse();
    }

    [Fact]
    public void Close_during_an_attach_makes_it_stale()
    {
        var gate = new ViewerAttachGate();
        gate.TryBegin(out var generation).ShouldBeTrue();
        gate.Close();
        gate.IsCurrent(generation).ShouldBeFalse();
    }

    [Fact]
    public void After_close_no_new_attach_is_allowed_re_entry_on_a_closed_control()
    {
        var gate = new ViewerAttachGate();
        gate.TryBegin(out _).ShouldBeTrue();
        gate.Close();
        gate.TryBegin(out _).ShouldBeFalse();
    }

    [Fact]
    public void Close_before_any_attach_also_refuses_attach()
    {
        var gate = new ViewerAttachGate();
        gate.Close();
        gate.TryBegin(out _).ShouldBeFalse();
    }
}
