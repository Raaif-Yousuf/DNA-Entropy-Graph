using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #460: the manifest the app writes to the bucket (docs/job_contract.md
/// section 3). The worker refuses a manifest whose stride is not
/// window - contextLength and reads <c>outputs</c> literally, so these tests
/// assert the values the worker will see, not merely that a field exists.
/// </summary>
public class WorkerManifestBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<StagedInput> OneInput = [new StagedInput(@"C:\runs\j1\input\SetTnpB.gb", "SetTnpB.gb")];

    private static RunOptions Opts(Func<RunOptions, RunOptions>? tweak = null)
    {
        var o = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" };
        return tweak is null ? o : tweak(o);
    }

    private static JsonElement Build(RunOptions? options = null, IReadOnlyList<StagedInput>? inputs = null, string? image = "reg/x@sha256:a")
        => JsonDocument.Parse(WorkerManifestBuilder.Build(
            options ?? Opts(),
            "20261002-120000-abcdef",
            "install-1",
            "0.1.0",
            image,
            "deg-123-abc",
            inputs ?? OneInput,
            Now)).RootElement;

    private static List<string?> Outputs(JsonElement m) => m.GetProperty("outputs").EnumerateArray().Select(e => e.GetString()).ToList();

    [Fact]
    public void Carries_every_field_the_worker_schema_requires()
    {
        var m = Build();

        foreach (var required in new[] { "schema", "jobId", "inputs", "predictor", "analysis", "outputs", "limits", "lifecycle", "store" })
        {
            m.TryGetProperty(required, out _).ShouldBeTrue($"manifest is missing required field '{required}'");
        }

        m.GetProperty("schema").GetInt32().ShouldBe(1);
        m.GetProperty("jobId").GetString().ShouldBe("20261002-120000-abcdef");
        m.GetProperty("createdAt").GetString().ShouldBe("2026-10-02T12:00:00Z");
        m.GetProperty("createdBy").GetProperty("installationId").GetString().ShouldBe("install-1");
        m.GetProperty("createdBy").GetProperty("appVersion").GetString().ShouldBe("0.1.0");
        m.GetProperty("createdBy").TryGetProperty("accountSub", out _).ShouldBeFalse("the account id is never written to the bucket");
    }

    [Fact]
    public void Each_staged_input_becomes_an_input_entry_under_input_slash()
    {
        var m = Build(inputs: [new StagedInput("x", "a.gb"), new StagedInput("y", "b.fasta")]);

        var inputs = m.GetProperty("inputs").EnumerateArray().ToList();
        inputs.Count.ShouldBe(2);
        inputs[0].GetProperty("id").GetString().ShouldBe("in1");
        inputs[0].GetProperty("path").GetString().ShouldBe("input/a.gb");
        inputs[0].GetProperty("name").GetString().ShouldBe("a");
        inputs[1].GetProperty("id").GetString().ShouldBe("in2");
        inputs[1].GetProperty("path").GetString().ShouldBe("input/b.fasta");
        inputs[1].GetProperty("name").GetString().ShouldBe("b");
    }

    [Fact]
    public void A_staged_annotation_becomes_the_annotation_of_the_input_before_it_not_an_input_of_its_own()
    {
        // Issue #686: worker manifest.py (#704) reads inputs[].annotation, the object name of a GFF3 staged beside the FASTA.
        var m = Build(inputs: [new StagedInput("x", "a.fasta"), new StagedInput("y", "genes.gff3", IsAnnotation: true)]);

        var inputs = m.GetProperty("inputs").EnumerateArray().ToList();
        inputs.Count.ShouldBe(1);
        inputs[0].GetProperty("path").GetString().ShouldBe("input/a.fasta");
        inputs[0].GetProperty("annotation").GetString().ShouldBe("input/genes.gff3");
    }

    [Fact]
    public void An_input_without_an_annotation_has_no_annotation_field()
        => Build().GetProperty("inputs")[0].TryGetProperty("annotation", out _).ShouldBeFalse();

    [Fact]
    public void An_annotation_with_no_input_before_it_is_refused()
        => Should.Throw<ArgumentException>(() => Build(inputs: [new StagedInput("y", "genes.gff3", IsAnnotation: true), new StagedInput("x", "a.fasta")]));

    [Fact]
    public void An_annotation_name_that_could_leave_the_job_prefix_is_refused()
        => Should.Throw<ArgumentException>(() => Build(inputs: [new StagedInput("x", "a.fasta"), new StagedInput("y", "..", IsAnnotation: true)]));

    [Fact]
    public void Two_annotations_for_one_input_are_refused()
        => Should.Throw<ArgumentException>(() => Build(inputs: [new StagedInput("x", "a.fasta"), new StagedInput("y", "g1.gff3", IsAnnotation: true), new StagedInput("z", "g2.gff3", IsAnnotation: true)]));

    [Fact]
    public void Input_options_use_the_wire_vocabulary()
    {
        var options = Opts(o => o with
        {
            Format = InputFormat.Plain,
            TreatAsRna = true,
            StartCoordinate = 5,
            FindGenes = false,
            AmbiguityPolicy = AmbiguityPolicy.Mask,
            FastaRecords = FastaRecordsSelection.FirstOnly,
        });

        var input = Build(options).GetProperty("inputs")[0];

        input.GetProperty("informat").GetString().ShouldBe("paste");
        input.GetProperty("rna").GetBoolean().ShouldBeTrue();
        input.GetProperty("start").GetInt32().ShouldBe(5);
        input.GetProperty("genes").GetBoolean().ShouldBeFalse();
        input.GetProperty("ambiguityPolicy").GetString().ShouldBe("mask");
        input.GetProperty("fastaRecords").GetString().ShouldBe("first");
    }

    [Fact]
    public void The_manifests_batch_budget_equals_the_validators_so_the_two_cannot_drift()
    {
        var limits = Build().GetProperty("limits");

        limits.GetProperty("maxTotalNt").GetInt64().ShouldBe(new InputLimits().MaxBatchNt);
    }

    [Fact]
    public void Not_finding_genes_never_requests_the_genes_gff3_output()
    {
        // The worker runs gene finding when "genes_gff3" is in outputs, whatever input.genes says.
        var outputs = Build(Opts(o => o with { FindGenes = false, OutputFiles = OutputFileKinds.All })).GetProperty("outputs")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        outputs.ShouldNotContain("genes_gff3");
        outputs.ShouldContain("bedgraph");
        outputs.ShouldContain("stats");
    }

    [Fact]
    public void Finding_genes_with_every_output_requests_the_genes_gff3_output()
    {
        var root = Build(Opts(o => o with { FindGenes = true, OutputFiles = OutputFileKinds.All }));

        root.GetProperty("outputs").EnumerateArray().Select(e => e.GetString()).ShouldContain("genes_gff3");
        root.GetProperty("inputs")[0].GetProperty("genes").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Only_a_genes_file_with_gene_finding_off_is_refused_not_silently_turned_on()
        => Should.Throw<ArgumentException>(() => Build(Opts(o => o with { FindGenes = false, OutputFiles = OutputFileKinds.GenesGff3 })));

    [Theory]
    [InlineData(InputFormat.Auto, "auto")]
    [InlineData(InputFormat.GenBank, "genbank")]
    [InlineData(InputFormat.Fasta, "fasta")]
    [InlineData(InputFormat.Plain, "paste")]
    [InlineData(InputFormat.Embl, "embl")]
    [InlineData(InputFormat.SnapGene, "snapgene")]
    public void Informat_maps_every_value(InputFormat format, string wire)
        => Build(Opts(o => o with { Format = format })).GetProperty("inputs")[0].GetProperty("informat").GetString().ShouldBe(wire);

    [Theory]
    [InlineData(Direction.BothCombined, "both-combined")]
    [InlineData(Direction.BothAveraged, "both-averaged")]
    [InlineData(Direction.BothSeparate, "both-separate")]
    [InlineData(Direction.ForwardOnly, "forward-only")]
    [InlineData(Direction.ReverseOnly, "reverse-only")]
    public void Direction_maps_every_value(Direction direction, string wire)
        => Build(Opts(o => o with { Direction = direction })).GetProperty("analysis").GetProperty("direction").GetString().ShouldBe(wire);

    [Fact]
    public void Stride_is_window_minus_context_so_the_worker_does_not_refuse_the_manifest()
    {
        var a = Build().GetProperty("analysis");
        a.GetProperty("contextLength").GetInt32().ShouldBe(4096);
        a.GetProperty("window").GetInt32().ShouldBe(8192);
        a.GetProperty("stride").GetInt32().ShouldBe(4096);

        // A user-changed K with the stale default window/stride: window = 2K, stride = K.
        var k = Build(Opts(o => o with { ContextLength = 8192 })).GetProperty("analysis");
        k.GetProperty("window").GetInt32().ShouldBe(16384);
        k.GetProperty("stride").GetInt32().ShouldBe(8192);

        // A window that is not larger than K can never give a positive stride.
        var small = Build(Opts(o => o with { ContextLength = 2048, Window = 2048, Stride = 7 })).GetProperty("analysis");
        small.GetProperty("stride").GetInt32().ShouldBe(small.GetProperty("window").GetInt32() - small.GetProperty("contextLength").GetInt32());
        small.GetProperty("stride").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Outputs_list_exactly_the_chosen_kinds_with_one_track_format()
    {
        var all = Outputs(Build());
        all.ShouldContain("genbank");
        all.ShouldContain("bedgraph");
        all.ShouldNotContain("wig", "both track formats would let the worker pick the wrong one; TrackFormat decides");
        all.Count.ShouldBe(10);

        var wig = Build(Opts(o => o with { TrackFormat = TrackFormat.Wig, OutputFiles = OutputCatalog.Defaults | OutputFileKinds.Wig }));
        Outputs(wig).ShouldContain("wig");
        Outputs(wig).ShouldNotContain("bedgraph");
        wig.GetProperty("analysis").GetProperty("format").GetString().ShouldBe("wig");

        var some = Build(Opts(o => o with { OutputFiles = OutputFileKinds.Stats | OutputFileKinds.EntropyTsv | OutputFileKinds.BedGraph }));
        Outputs(some).ShouldBe(["bedgraph", "stats", "tsv"], ignoreOrder: true);
    }

    [Fact]
    public void An_empty_output_selection_is_refused_because_the_worker_reads_empty_as_everything()
        => Should.Throw<ArgumentException>(() => Build(Opts(o => o with { OutputFiles = OutputFileKinds.None })));

    [Fact]
    public void Unchecking_wig_leaves_it_out_of_outputs_and_every_catalog_file_reaches_the_wire_when_chosen()
    {
        // Observable for #103: a box the user clears is absent from outputs (the worker reads a non-empty list literally).
        var noWig = Opts(o => o with { OutputFiles = OutputCatalog.Defaults & ~OutputFileKinds.Wig });
        Outputs(Build(noWig)).ShouldNotContain("wig");

        // Every catalog entry, chosen alone (with a track so gene-less runs are legal), is named on the wire: none is "wired to nothing".
        foreach (var entry in OutputCatalog.Entries.Where(e => !e.NeedsGenes))
        {
            Outputs(Build(Opts(o => o with { OutputFiles = entry.Kind }))).ShouldBe([entry.WireName]);
        }

        foreach (var entry in OutputCatalog.Entries.Where(e => e.NeedsGenes))
        {
            Outputs(Build(Opts(o => o with { FindGenes = true, OutputFiles = entry.Kind }))).ShouldBe([entry.WireName]);
        }
    }

    [Fact]
    public void The_files_the_default_selection_adds_since_the_first_eight_are_all_requested()
    {
        var all = Outputs(Build());

        all.ShouldContain("smoothed");
        all.ShouldContain("regions");
        all.ShouldContain("gene_summary");
        all.ShouldNotContain("probs", "the 16-bytes-per-base matrix is opt-in");
        all.ShouldNotContain("probs_npy");
    }

    [Fact]
    public void The_probability_matrix_is_requested_only_when_chosen()
    {
        var chosen = Build(Opts(o => o with { OutputFiles = OutputCatalog.Defaults | OutputFileKinds.Probs | OutputFileKinds.ProbsNpy }));

        Outputs(chosen).ShouldContain("probs");
        Outputs(chosen).ShouldContain("probs_npy");
    }

    [Fact]
    public void An_accession_input_names_the_accession_and_no_path_and_uploads_nothing()
    {
        var m = Build(inputs: [StagedInput.ForAccession("NC_045512.2", "100-500")]);

        var input = m.GetProperty("inputs")[0];
        input.GetProperty("accession").GetString().ShouldBe("NC_045512.2");
        input.GetProperty("accessionRange").GetString().ShouldBe("100-500");
        input.TryGetProperty("path", out _).ShouldBeFalse("the worker refuses an input with both a path and an accession");
        input.GetProperty("name").GetString().ShouldBe("NC_045512_2", "the name keeps the version, and a dot would read as a file extension");
        input.GetProperty("informat").GetString().ShouldBe("auto");
    }

    [Fact]
    public void An_accession_without_a_range_sends_no_range()
    {
        var input = Build(inputs: [StagedInput.ForAccession("MN908947.3", null)]).GetProperty("inputs")[0];

        input.TryGetProperty("accessionRange", out _).ShouldBeFalse();
        StagedInput.ForAccession("MN908947.3", null).IsAccession.ShouldBeTrue();
        new StagedInput("x.fa", "x.fa").IsAccession.ShouldBeFalse();
    }

    [Fact]
    public void An_accession_cannot_carry_a_gff3()
        => Should.Throw<ArgumentException>(() => Build(inputs: [StagedInput.ForAccession("NC_045512.2", null), new StagedInput("a.gff3", "a.gff3", IsAnnotation: true)]));

    [Fact]
    public void Gc_and_codons_are_requested_only_when_chosen_and_gc_carries_its_window()
    {
        var plain = Build();
        Outputs(plain).ShouldNotContain("gc");
        Outputs(plain).ShouldNotContain("codons");
        plain.GetProperty("analysis").TryGetProperty("gcWindow", out _).ShouldBeFalse("the window only matters when gc is named");

        var chosen = Build(Opts(o => o with { OutputFiles = OutputCatalog.Defaults | OutputFileKinds.Gc | OutputFileKinds.Codons, GcWindow = 101 }));
        Outputs(chosen).ShouldContain("gc");
        Outputs(chosen).ShouldContain("codons");
        chosen.GetProperty("analysis").GetProperty("gcWindow").GetInt32().ShouldBe(101);

        var codonsOnly = Build(Opts(o => o with { OutputFiles = OutputCatalog.Defaults | OutputFileKinds.Codons }));
        codonsOnly.GetProperty("analysis").TryGetProperty("gcWindow", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_default_gc_window_is_the_workers_default()
        => new RunOptions { ModelId = "m", RunTarget = "Cloud" }.GcWindow.ShouldBe(51);

    [Fact]
    public void Not_finding_genes_never_requests_the_gene_summary_either()
    {
        var outputs = Outputs(Build(Opts(o => o with { FindGenes = false })));

        outputs.ShouldNotContain("gene_summary");
        outputs.ShouldNotContain("genes_gff3");
        outputs.ShouldContain("regions");
    }

    [Fact]
    public void The_legacy_all_selection_still_sends_the_same_seven_names_it_always_did()
        => Outputs(Build(Opts(o => o with { OutputFiles = OutputFileKinds.All })))
            .ShouldBe(["genbank", "fasta", "bedgraph", "geneious_gff3", "genes_gff3", "stats", "tsv"]);

    [Fact]
    public void Predictor_uses_the_wire_kind_precision_and_device()
    {
        var p = Build().GetProperty("predictor");
        p.GetProperty("kind").GetString().ShouldBe("evo");
        p.GetProperty("model").GetString().ShouldBe("evo2_7b");
        p.GetProperty("precision").GetString().ShouldBe("bf16");
        p.GetProperty("device").GetString().ShouldBe("cuda");

        var hopper = Build(Opts(o => o with { ModelId = "evo2_20b" })).GetProperty("predictor");
        hopper.GetProperty("precision").GetString().ShouldBe("fp8", "the worker refuses a precision that disagrees with the model");

        var mock = Build(Opts(o => o with { Predictor = PredictorSelection.MockTestPredictor, Seed = 3 })).GetProperty("predictor");
        mock.GetProperty("kind").GetString().ShouldBe("mock");
        mock.GetProperty("seed").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void Lifecycle_limits_store_and_worker_reflect_the_request()
    {
        var options = Opts(o => o with
        {
            AfterTask = AfterTaskAction.KeepAlive,
            KeepAliveMinutes = 45,
            AfterKeepAlive = AfterKeepAliveAction.Delete,
            MaxRunDurationMinutes = 60,
        });

        var m = Build(options);

        m.GetProperty("lifecycle").GetProperty("afterTask").GetString().ShouldBe("keep");
        m.GetProperty("lifecycle").GetProperty("keepAliveMinutes").GetInt32().ShouldBe(45);
        m.GetProperty("lifecycle").GetProperty("afterKeepAlive").GetString().ShouldBe("delete");
        m.GetProperty("limits").GetProperty("maxRunSeconds").GetInt32().ShouldBe(3600);
        m.GetProperty("store").GetProperty("kind").GetString().ShouldBe("gcs");
        m.GetProperty("store").GetProperty("bucket").GetString().ShouldBe("deg-123-abc");
        m.GetProperty("store").GetProperty("prefix").GetString().ShouldBe("jobs/20261002-120000-abcdef/");
        m.GetProperty("worker").GetProperty("image").GetString().ShouldBe("reg/x@sha256:a");
        m.GetProperty("worker").GetProperty("version").GetString().ShouldBe("0.1.0");
    }

    [Fact]
    public void A_manifest_with_no_worker_image_omits_it_rather_than_writing_null()
        => Build(image: null).GetProperty("worker").TryGetProperty("image", out _).ShouldBeFalse();

    [Fact]
    public void An_input_file_name_that_could_escape_the_job_prefix_is_refused()
    {
        foreach (var bad in new[] { "..", "a/b.gb", @"a\b.gb", "" })
        {
            Should.Throw<ArgumentException>(() => Build(inputs: [new StagedInput("x", bad)]), bad);
        }
    }

    [Fact]
    public void No_inputs_is_refused()
        => Should.Throw<ArgumentException>(() => Build(inputs: []));
}
