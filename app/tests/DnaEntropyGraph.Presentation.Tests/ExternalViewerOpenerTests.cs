using System.Net;
using System.Net.Sockets;
using System.Text;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Viewers;
using DnaEntropyGraph.Presentation.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #586: Open in IGV and Open in Geneious, without a UI thread. The decisive test puts a real loopback listener
/// where IGV would be and reads exactly what arrives, in order.
/// </summary>
public sealed class ExternalViewerOpenerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-viewer-opener", Guid.NewGuid().ToString("N"));
    private readonly IIgvBatchClient _client = Substitute.For<IIgvBatchClient>();
    private readonly IViewerProcessLauncher _launcher = Substitute.For<IViewerProcessLauncher>();
    private readonly IViewerLocator _locator = Substitute.For<IViewerLocator>();
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly MemorySettings _settings = new();

    public ExternalViewerOpenerTests()
    {
        Directory.CreateDirectory(_base);
        _client.SendAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(IgvBatchOutcome.NotListening);
        _launcher.Launch(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>()).Returns(true);
        _locator.Find(Arg.Any<ExternalViewer>()).Returns((string?)null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_listener_posing_as_igv_receives_new_genome_load_load_in_that_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            _settings.Values[ViewerSettingKeys.IgvPort] = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var received = new List<string>();
            var server = Task.Run(
                async () =>
                {
                    using var peer = await listener.AcceptTcpClientAsync(ct);
                    using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8);
                    await using var writer = new StreamWriter(peer.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        received.Add(line);
                        await writer.WriteLineAsync("OK".AsMemory(), ct);
                    }
                },
                ct);
            var opener = new ExternalViewerOpener(new TcpIgvBatchClient(), _launcher, _locator, _settings, _picker);

            var outcome = await opener.OpenInIgvAsync(Run(@"C:\my out\a\a.fasta", @"C:\my out\a\a.entropy.bedgraph", @"C:\my out\a\a.genes.gff3"), ct);

            outcome.ShouldBe(ExternalViewerOutcome.Opened);
            _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);

            // Closing the connection ends the fake's read loop.
            listener.Stop();
            await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(2), ct));
            received.ShouldBe(
            [
                "new",
                "genome \"C:\\my out\\a\\a.fasta\"",
                "load \"C:\\my out\\a\\a.entropy.bedgraph\"",
                "load \"C:\\my out\\a\\a.genes.gff3\"",
            ]);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task The_batch_port_is_the_default_unless_the_setting_holds_a_valid_number()
    {
        var opener = Opener();
        var files = Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph");

        await opener.OpenInIgvAsync(files, TestContext.Current.CancellationToken);
        _settings.Values[ViewerSettingKeys.IgvPort] = "61000";
        await opener.OpenInIgvAsync(files, TestContext.Current.CancellationToken);
        _settings.Values[ViewerSettingKeys.IgvPort] = "not a port";
        await opener.OpenInIgvAsync(files, TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _client.SendAsync(60151, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
            _client.SendAsync(61000, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
            _client.SendAsync(60151, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Igv_not_listening_launches_the_saved_program_with_the_files()
    {
        var program = FakeProgram("igv.exe");
        _settings.Values[ViewerSettingKeys.IgvPath] = program;

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.Opened);
        _launcher.Received(1).Launch(program, Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { @"C:\a\a.entropy.bedgraph", "-g", @"C:\a\a.fasta" })));
        _locator.DidNotReceiveWithAnyArgs().Find(default);
        await _picker.DidNotReceiveWithAnyArgs().PickProgramAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Without_a_saved_program_the_autodetected_one_is_used()
    {
        var found = FakeProgram("found-igv.exe");
        _locator.Find(ExternalViewer.Igv).Returns(found);

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.genes.gff3"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.Opened);
        _launcher.Received(1).Launch(found, Arg.Any<IReadOnlyList<string>>());
        await _picker.DidNotReceiveWithAnyArgs().PickProgramAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_saved_program_that_is_gone_is_ignored_and_the_autodetected_one_is_used()
    {
        _settings.Values[ViewerSettingKeys.IgvPath] = Path.Combine(_base, "deleted.exe");
        var found = FakeProgram("found-igv.exe");
        _locator.Find(ExternalViewer.Igv).Returns(found);

        await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        _launcher.Received(1).Launch(found, Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task With_no_program_known_the_user_is_asked_and_the_choice_is_remembered()
    {
        var chosen = FakeProgram("chosen.exe");
        _picker.PickProgramAsync(Arg.Any<CancellationToken>()).Returns(chosen);

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.Opened);
        _launcher.Received(1).Launch(chosen, Arg.Any<IReadOnlyList<string>>());
        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBe(chosen);
    }

    [Fact]
    public async Task A_cancelled_browse_is_not_found_and_nothing_is_launched_or_saved()
    {
        _picker.PickProgramAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.NotFound);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
        _settings.Values.ShouldNotContainKey(ViewerSettingKeys.IgvPath);
    }

    [Fact]
    public async Task A_launch_that_fails_is_reported_and_forgets_the_saved_program_so_the_next_press_asks_again()
    {
        var program = FakeProgram("igv.exe");
        _settings.Values[ViewerSettingKeys.IgvPath] = program;
        _launcher.Launch(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>()).Returns(false);

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.LaunchFailed);
        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBeEmpty();
    }

    [Theory]
    [InlineData(IgvBatchOutcome.Rejected, ExternalViewerOutcome.IgvRejected)]
    [InlineData(IgvBatchOutcome.NoReply, ExternalViewerOutcome.IgvNoReply)]
    public async Task When_igv_is_running_but_fails_the_app_does_not_start_a_second_igv(IgvBatchOutcome batch, ExternalViewerOutcome expected)
    {
        _client.SendAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(batch);

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(expected);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
    }

    [Fact]
    public async Task A_run_with_no_track_says_so_and_neither_connects_nor_launches()
    {
        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.summary.txt"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.NoFiles);
        await _client.DidNotReceiveWithAnyArgs().SendAsync(0, default!, TestContext.Current.CancellationToken);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
    }

    [Fact]
    public async Task A_locked_settings_file_does_not_stop_the_button_and_the_choice_is_just_not_remembered()
    {
        _settings.Unavailable = true;
        var chosen = FakeProgram("chosen.exe");
        _picker.PickProgramAsync(Arg.Any<CancellationToken>()).Returns(chosen);

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.Opened);
        _launcher.Received(1).Launch(chosen, Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task Geneious_is_launched_with_the_genbank_then_the_gff3_files_and_never_touches_the_socket()
    {
        var program = FakeProgram("geneious.exe");
        _settings.Values[ViewerSettingKeys.GeneiousPath] = program;

        var outcome = await Opener().OpenInGeneiousAsync(
            Run(@"C:\a\a.fasta", @"C:\a\a.gb", @"C:\a\a.entropy.bedgraph", @"C:\a\a.entropy.geneious.gff3"),
            TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.Opened);
        _launcher.Received(1).Launch(program, Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { @"C:\a\a.gb", @"C:\a\a.entropy.geneious.gff3" })));
        await _client.DidNotReceiveWithAnyArgs().SendAsync(0, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Geneious_with_no_genbank_or_gff3_says_so()
    {
        var outcome = await Opener().OpenInGeneiousAsync(Run(@"C:\a\a.fasta"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.NoFiles);
    }

    [Fact]
    public async Task Geneious_browse_is_remembered_under_its_own_key()
    {
        var chosen = FakeProgram("g.exe");
        _picker.PickProgramAsync(Arg.Any<CancellationToken>()).Returns(chosen);

        await Opener().OpenInGeneiousAsync(Run(@"C:\a\a.gb"), TestContext.Current.CancellationToken);

        _settings.Values[ViewerSettingKeys.GeneiousPath].ShouldBe(chosen);
        _settings.Values.ShouldNotContainKey(ViewerSettingKeys.IgvPath);
    }

    [Fact]
    public async Task A_connect_that_did_not_answer_does_not_start_a_second_igv()
    {
        _client.SendAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(IgvBatchOutcome.NoConnection);

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.fasta", @"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.IgvNoAnswer);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
    }

    [Fact]
    public async Task A_batch_file_with_an_unsafe_folder_is_refused_and_the_saved_program_is_kept()
    {
        var bat = FakeProgram("igv.bat");
        _settings.Values[ViewerSettingKeys.IgvPath] = bat;

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\Lab\R&D\a.fasta", @"C:\Lab\R&D\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.UnsafeProgramArguments);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBe(bat);
    }

    [Fact]
    public async Task An_exe_is_launched_with_the_same_ampersand_folder()
    {
        var exe = FakeProgram("igv.exe");
        _settings.Values[ViewerSettingKeys.IgvPath] = exe;

        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\Lab\R&D\a.fasta", @"C:\Lab\R&D\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.Opened);
        _launcher.Received(1).Launch(exe, Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task A_path_with_a_quote_is_its_own_outcome_not_a_failure_to_start()
    {
        var outcome = await Opener().OpenInIgvAsync(Run("C:\\a\"b\\a.fasta", "C:\\a\"b\\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.IgvPathUnsendable);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
    }

    [Fact]
    public async Task Tracks_with_no_genome_are_not_loaded_onto_whatever_genome_igv_has()
    {
        var outcome = await Opener().OpenInIgvAsync(Run(@"C:\a\a.entropy.bedgraph"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.NoGenome);
        await _client.DidNotReceiveWithAnyArgs().SendAsync(0, default!, TestContext.Current.CancellationToken);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
    }

    [Fact]
    public async Task With_several_inputs_only_the_first_is_sent_and_the_outcome_says_so()
    {
        _client.SendAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(IgvBatchOutcome.Done);

        var outcome = await Opener().OpenInIgvAsync(
            [
                new RunOutputFile("a/a.fasta", @"C:\o\a\a.fasta", 1),
                new RunOutputFile("a/a.entropy.bedgraph", @"C:\o\a\a.entropy.bedgraph", 1),
                new RunOutputFile("b/b.fasta", @"C:\o\b\b.fasta", 1),
                new RunOutputFile("b/b.entropy.bedgraph", @"C:\o\b\b.entropy.bedgraph", 1),
            ],
            TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.OpenedFirstInputOnly);
        await _client.Received(1).SendAsync(
            Arg.Any<int>(),
            Arg.Is<IReadOnlyList<string>>(c => c.Any(x => x.Contains(@"\a\a.fasta", StringComparison.Ordinal)) && !c.Any(x => x.Contains(@"\b\", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_geneious_launch_is_also_refused_for_a_batch_file_with_an_unsafe_folder()
    {
        var bat = FakeProgram("geneious.cmd");
        _settings.Values[ViewerSettingKeys.GeneiousPath] = bat;

        var outcome = await Opener().OpenInGeneiousAsync(Run(@"C:\Lab\R&D\a.gb"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ExternalViewerOutcome.UnsafeProgramArguments);
        _launcher.DidNotReceiveWithAnyArgs().Launch(default!, default!);
    }

    private static IReadOnlyList<RunOutputFile> Run(params string[] fullPaths)
        => [.. fullPaths.Select(p => new RunOutputFile("r/" + Path.GetFileName(p), p, 1))];

    private ExternalViewerOpener Opener() => new(_client, _launcher, _locator, _settings, _picker);

    private string FakeProgram(string name)
    {
        var path = Path.Combine(_base, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Unavailable { get; set; }

        public string? GetString(string key)
            => Unavailable ? throw new SettingsUnavailableException("locked") : Values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            if (Unavailable)
            {
                throw new SettingsUnavailableException("locked");
            }

            Values[key] = value;
        }
    }
}

