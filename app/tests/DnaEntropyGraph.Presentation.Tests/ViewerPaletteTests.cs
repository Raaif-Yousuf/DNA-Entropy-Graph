using System.Text.Json;
using DnaEntropyGraph.Core.Palettes;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #195: the viewer's track and seam colours come from the chosen palette, read when the run is drawn and again on a theme change.</summary>
public sealed class ViewerPaletteTests : IDisposable
{
    private const string ReadyMsg = "{\"evt\":\"ready\"}";
    private const string LoadedMsg = "{\"evt\":\"loaded\"}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-vpt-" + Guid.NewGuid().ToString("N"));
    private readonly IWebViewRuntimeProbe _probe = Substitute.For<IWebViewRuntimeProbe>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IEntropyOverviewSource _overview = Substitute.For<IEntropyOverviewSource>();
    private readonly MutablePalette _palette = new();
    private readonly List<string> _sent = [];

    public ViewerPaletteTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), ">a\nACGT\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.bedgraph"), "a 0 1 2\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.fwd.bedgraph"), "a 0 1 2\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.rev.bedgraph"), "a 0 1 2\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.smooth51.bedgraph"), "a 0 1 2\n");
        _probe.GetVersion().Returns("130.0.1");
        _strings.GetString(Arg.Any<string>()).Returns(ci => "[" + ci.Arg<string>() + "]");
        _overview.Load(Arg.Any<string>()).Returns(new EntropyOverview([new EntropyContig("a", 100, 1, [1f], 40)]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private sealed class MutablePalette : ITrackPaletteProvider
    {
        public TrackPalette Current { get; set; } = TrackPalettes.Default;
    }

    private ViewerViewModel Make()
    {
        var vm = new ViewerViewModel(_probe, _strings, _overview, palettes: _palette);
        vm.MessageToViewer += _sent.Add;
        return vm;
    }

    private async Task<ViewerViewModel> Ready(bool dark = false)
    {
        var vm = Make();
        vm.SetDarkMode(dark);
        vm.OpenRun(_dir);
        await vm.ContigsReady;
        vm.OnViewerMessage(ReadyMsg);
        vm.OnViewerMessage(LoadedMsg);
        return vm;
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private List<JsonElement> Sent(string cmd) => _sent.Select(Root).Where(r => r.GetProperty("cmd").GetString() == cmd).ToList();

    private Dictionary<string, string> LoadedTrackColours()
        => Sent("load").Last().GetProperty("config").GetProperty("tracks").EnumerateArray()
            .Where(t => t.TryGetProperty("color", out _))
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("color").GetString()!);

    [Fact]
    public async Task The_three_entropy_tracks_load_in_three_different_palette_colours()
    {
        await Ready();

        var colours = LoadedTrackColours();
        var light = TrackPalettes.Default.Light;
        colours[IgvLoadPlanner.EntropyTrackName].ShouldBe(light.Entropy);
        colours[IgvLoadPlanner.ForwardTrackName].ShouldBe(light.Forward);
        colours[IgvLoadPlanner.ReverseTrackName].ShouldBe(light.Reverse);
    }

    [Fact]
    public async Task A_dark_app_theme_still_loads_the_light_colours_because_the_page_filter_inverts_them()
    {
        await Ready(dark: true);

        LoadedTrackColours()[IgvLoadPlanner.EntropyTrackName].ShouldBe(TrackPalettes.Default.ForViewer().Entropy);
    }

    [Fact]
    public async Task The_chosen_palette_is_read_each_time_a_run_is_opened()
    {
        var vm = await Ready();
        _palette.Current = TrackPalettes.HighContrast;
        _sent.Clear();

        vm.OpenRun(_dir);

        LoadedTrackColours()[IgvLoadPlanner.EntropyTrackName].ShouldBe(TrackPalettes.HighContrast.Light.Entropy);
    }

    [Fact]
    public async Task A_smoothed_track_is_added_in_the_smoothed_colour()
    {
        var vm = await Ready();
        _sent.Clear();

        vm.SmoothedTracks.Single().IsOn = true;

        var config = Sent("track").Single().GetProperty("config");
        config.GetProperty("color").GetString().ShouldBe(TrackPalettes.Default.Light.Smoothed);
    }

    [Fact]
    public async Task The_seam_marker_colour_is_sent_to_the_page_not_spelled_in_javascript()
    {
        await Ready();

        Sent("seams").Single().GetProperty("color").GetString().ShouldBe(TrackPalettes.Default.Light.MarkerRgba(0.6));
    }

    [Fact]
    public async Task A_theme_change_after_the_run_is_drawn_sends_only_the_theme()
    {
        var vm = await Ready();
        _sent.Clear();

        vm.SetDarkMode(true);

        _sent.Select(s => Root(s).GetProperty("cmd").GetString()).ShouldBe(["theme"]);
    }

    [Fact]
    public async Task Planning_with_no_palette_leaves_igv_to_its_own_colours()
    {
        await Ready();
        var plan = IgvLoadPlanner.Plan(_dir);

        JsonDocument.Parse(plan.LoadMessageJson!).RootElement.GetProperty("config").GetProperty("tracks").EnumerateArray()
            .Count(t => t.TryGetProperty("color", out var _c) && _c.ValueKind != JsonValueKind.Undefined).ShouldBe(0);
    }
}
