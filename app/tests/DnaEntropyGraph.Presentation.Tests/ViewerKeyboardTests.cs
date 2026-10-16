using System.Text.Json;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #247: keyboard pan, zoom and presets in the viewer. The decisions are C#; bridge.js only forwards keys.</summary>
public sealed class ViewerKeyboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-vkb-" + Guid.NewGuid().ToString("N"));
    private readonly IWebViewRuntimeProbe _probe = Substitute.For<IWebViewRuntimeProbe>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IEntropyOverviewSource _overview = Substitute.For<IEntropyOverviewSource>();
    private readonly List<string> _sent = [];

    public ViewerKeyboardTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), ">a\nACGT\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.bedgraph"), "a 0 1 2\n");
        _probe.GetVersion().Returns("130.0.1");
        _strings.GetString(Arg.Any<string>()).Returns(ci => "[" + ci.Arg<string>() + "]");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static ViewerLocus L(long s, long e) => new("a", s, e);

    [Theory]
    [InlineData("a:1-100", "a", 1, 100)]
    [InlineData("chr:with:colon:5-9", "chr:with:colon", 5, 9)]
    public void Locus_parses_the_text_igv_reports(string text, string chr, long start, long end)
    {
        ViewerLocus.TryParse(text, out var l).ShouldBeTrue();
        l.ShouldBe(new ViewerLocus(chr, start, end));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a:x-5")]
    [InlineData("a:9-5")]
    [InlineData("a:0-5")]
    public void Locus_rejects_text_it_cannot_read(string text) => ViewerLocus.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Right_arrow_pans_a_quarter_of_the_window()
    {
        var r = ViewerKeyboard.Resolve("ArrowRight", false, L(1001, 2000), 10_000);
        r.Kind.ShouldBe(ViewerKeyKind.Goto);
        r.Locus.ShouldBe("a:1251-2250");
    }

    [Fact]
    public void Left_arrow_pans_back_and_stops_at_the_start()
    {
        ViewerKeyboard.Resolve("ArrowLeft", false, L(1001, 2000), 10_000).Locus.ShouldBe("a:751-1750");
        ViewerKeyboard.Resolve("ArrowLeft", false, L(101, 1100), 10_000).Locus.ShouldBe("a:1-1000");
    }

    [Fact]
    public void Right_arrow_stops_at_the_end_of_the_contig()
    {
        ViewerKeyboard.Resolve("ArrowRight", false, L(9101, 10_000), 10_000).Locus.ShouldBe("a:9101-10000");
    }

    [Theory]
    [InlineData("+")]
    [InlineData("=")]
    public void Plus_halves_the_window_about_its_centre(string key)
    {
        ViewerKeyboard.Resolve(key, false, L(1001, 2000), 10_000).Locus.ShouldBe("a:1251-1750");
    }

    [Theory]
    [InlineData("-")]
    [InlineData("_")]
    public void Minus_doubles_the_window_about_its_centre_and_clamps(string key)
    {
        ViewerKeyboard.Resolve(key, false, L(1001, 2000), 10_000).Locus.ShouldBe("a:501-2500");
        ViewerKeyboard.Resolve(key, false, L(1, 9000), 10_000).Locus.ShouldBe("a:1-10000");
    }

    [Fact]
    public void Plus_never_zooms_below_forty_bases()
    {
        ViewerKeyboard.Resolve("+", false, L(101, 140), 10_000).Locus.ShouldBe("a:101-140");
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("1")]
    public void Home_and_one_show_the_whole_contig(string key)
    {
        ViewerKeyboard.Resolve(key, false, L(500, 600), 10_000).Locus.ShouldBe("a:1-10000");
    }

    [Fact]
    public void Presets_two_and_three_centre_on_the_window()
    {
        ViewerKeyboard.Resolve("2", false, L(5001, 5100), 100_000).Locus.ShouldBe("a:51-10050");
        ViewerKeyboard.Resolve("3", false, L(5001, 5100), 100_000).Locus.ShouldBe("a:4551-5550");
    }

    [Fact]
    public void A_preset_larger_than_the_contig_shows_the_whole_contig()
    {
        ViewerKeyboard.Resolve("2", false, L(1, 500), 800).Locus.ShouldBe("a:1-800");
    }

    [Fact]
    public void Ctrl_L_focuses_the_locus_box_without_needing_a_locus()
    {
        ViewerKeyboard.Resolve("l", true, null, null).Kind.ShouldBe(ViewerKeyKind.FocusLocus);
        ViewerKeyboard.Resolve("L", true, null, null).Kind.ShouldBe(ViewerKeyKind.FocusLocus);
    }

    [Theory]
    [InlineData("ArrowRight")]
    [InlineData("+")]
    [InlineData("Home")]
    public void Navigation_with_no_known_locus_does_nothing(string key)
    {
        ViewerKeyboard.Resolve(key, false, null, null).Kind.ShouldBe(ViewerKeyKind.None);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Enter")]
    [InlineData("Escape")]
    public void Other_keys_do_nothing(string key) => ViewerKeyboard.Resolve(key, false, L(1, 100), 1000).Kind.ShouldBe(ViewerKeyKind.None);

    [Fact]
    public void Ctrl_with_a_navigation_key_does_nothing()
    {
        ViewerKeyboard.Resolve("Home", true, L(1, 100), 1000).Kind.ShouldBe(ViewerKeyKind.None);
    }

    private async Task<ViewerViewModel> ReadyViewer(int length, string? loadedLocus = null)
    {
        _overview.Load(Arg.Any<string>()).Returns(new EntropyOverview([new EntropyContig("a", length, 1, [1f])]));
        var vm = new ViewerViewModel(_probe, _strings, _overview);
        vm.MessageToViewer += _sent.Add;
        vm.OpenRun(_dir);
        await vm.ContigsReady;
        vm.OnViewerMessage("{\"evt\":\"ready\"}");
        vm.OnViewerMessage(loadedLocus is null ? "{\"evt\":\"loaded\"}" : JsonSerializer.Serialize(new { evt = "loaded", locus = loadedLocus }));
        _sent.Clear();
        return vm;
    }

    private static string Key(string key, bool ctrl = false) => JsonSerializer.Serialize(new { evt = "key", key, ctrl });

    private List<JsonElement> Sent(string cmd) =>
        _sent.Select(s => JsonDocument.Parse(s).RootElement.Clone()).Where(r => r.GetProperty("cmd").GetString() == cmd).ToList();

    [Fact]
    public async Task Home_after_zooming_sends_goto_for_the_whole_contig()
    {
        var vm = await ReadyViewer(10_000);
        vm.OnViewerMessage("{\"evt\":\"locusChanged\",\"locus\":\"a:500-600\"}");

        vm.OnViewerMessage(Key("Home"));

        Sent("goto").ShouldHaveSingleItem().GetProperty("locus").GetString().ShouldBe("a:1-10000");
    }

    [Fact]
    public async Task Right_arrow_uses_the_last_reported_locus()
    {
        var vm = await ReadyViewer(10_000);
        vm.OnViewerMessage("{\"evt\":\"locusChanged\",\"locus\":\"a:1001-2000\"}");

        vm.OnViewerMessage(Key("ArrowRight"));

        Sent("goto").ShouldHaveSingleItem().GetProperty("locus").GetString().ShouldBe("a:1251-2250");
    }

    [Fact]
    public async Task The_locus_reported_with_loaded_seeds_the_keys_before_any_mouse_pan()
    {
        var vm = await ReadyViewer(10_000, loadedLocus: "a:1-1000");

        vm.OnViewerMessage(Key("ArrowRight"));

        Sent("goto").ShouldHaveSingleItem().GetProperty("locus").GetString().ShouldBe("a:251-1250");
    }

    [Fact]
    public async Task Keys_are_enabled_in_the_page_only_while_the_run_is_drawn()
    {
        _overview.Load(Arg.Any<string>()).Returns(new EntropyOverview([new EntropyContig("a", 100, 1, [1f])]));
        var vm = new ViewerViewModel(_probe, _strings, _overview);
        vm.MessageToViewer += _sent.Add;
        vm.OpenRun(_dir);
        await vm.ContigsReady;
        vm.OnViewerMessage("{\"evt\":\"ready\"}");
        Sent("keys").ShouldBeEmpty();

        vm.OnViewerMessage("{\"evt\":\"loaded\"}");
        Sent("keys").ShouldHaveSingleItem().GetProperty("enabled").GetBoolean().ShouldBeTrue();

        vm.OnViewerMessage("{\"evt\":\"error\"}");
        Sent("keys").Select(k => k.GetProperty("enabled").GetBoolean()).ToArray().ShouldBe([true, false]);
    }

    [Fact]
    public async Task Ctrl_L_posts_focusLocus()
    {
        var vm = await ReadyViewer(10_000);

        vm.OnViewerMessage(Key("l", ctrl: true));

        Sent("focusLocus").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_key_before_the_page_has_drawn_sends_nothing()
    {
        _overview.Load(Arg.Any<string>()).Returns(new EntropyOverview([new EntropyContig("a", 100, 1, [1f])]));
        var vm = new ViewerViewModel(_probe, _strings, _overview);
        vm.MessageToViewer += _sent.Add;
        vm.OpenRun(_dir);
        await vm.ContigsReady;

        vm.OnViewerMessage(Key("l", ctrl: true));

        _sent.Where(s => s.Contains("focusLocus")).ShouldBeEmpty();
    }
}
