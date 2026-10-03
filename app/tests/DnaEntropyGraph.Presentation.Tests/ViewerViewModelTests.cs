using System.Text.Json;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using DnaEntropyGraph.Presentation.Viewer;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public sealed class ViewerViewModelTests : IDisposable
{
    private const string ReadyMsg = "{\"evt\":\"ready\"}";
    private const string LoadedMsg = "{\"evt\":\"loaded\"}";
    private const string FatalMsg = "{\"evt\":\"error\",\"message\":\"bad fasta\"}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-vvm-" + Guid.NewGuid().ToString("N"));
    private readonly IWebViewRuntimeProbe _probe = Substitute.For<IWebViewRuntimeProbe>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly List<string> _sent = [];

    public ViewerViewModelTests()
    {
        Directory.CreateDirectory(_dir);
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

    private ViewerViewModel Make()
    {
        var vm = new ViewerViewModel(_probe, _strings);
        vm.MessageToViewer += _sent.Add;
        return vm;
    }

    private void GoodRun()
    {
        File.WriteAllText(Path.Combine(_dir, "s.fasta"), ">s\nACGT\n");
        File.WriteAllText(Path.Combine(_dir, "s.entropy.bedgraph"), "s 0 1 2\n");
    }

    private static string Cmd(string json) => JsonDocument.Parse(json).RootElement.GetProperty("cmd").GetString()!;

    [Fact]
    public void Null_probe_version_means_RuntimeMissing_and_nothing_is_mapped()
    {
        _probe.GetVersion().Returns((string?)null);
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.Status.ShouldBe(ViewerStatus.RuntimeMissing);
        vm.IsViewerVisible.ShouldBeFalse();
        vm.ErrorTitle.ShouldBe("[ViewerRuntimeMissing_Title]");
        vm.ErrorBody.ShouldBe("[ViewerRuntimeMissing_Body]");
        vm.ActionLabel.ShouldBe("[ViewerRuntimeMissing_Action]");
        vm.ActionUrl.ShouldBe(ViewerUrls.RuntimeDownloadUrl);
        vm.HasLinkAction.ShouldBeTrue();
        vm.HasRetryAction.ShouldBeFalse();
        vm.MapRunFolder.ShouldBeNull();
    }

    [Fact]
    public void Probe_throwing_is_treated_as_runtime_missing()
    {
        _probe.GetVersion().Returns(_ => throw new InvalidOperationException("no runtime"));
        var vm = Make();
        vm.OpenRun(_dir);
        vm.Status.ShouldBe(ViewerStatus.RuntimeMissing);
    }

    [Fact]
    public void Good_run_is_Loading_with_run_folder_to_map_and_load_is_held_until_ready()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.Status.ShouldBe(ViewerStatus.Loading);
        vm.IsViewerVisible.ShouldBeTrue();
        vm.IsLoading.ShouldBeTrue();
        vm.MapRunFolder.ShouldBe(_dir);
        _sent.ShouldBeEmpty();

        vm.OnViewerMessage(ReadyMsg);

        _sent.Count.ShouldBe(1);
        Cmd(_sent[0]).ShouldBe("load");
        vm.Status.ShouldBe(ViewerStatus.Loading);
        vm.IsLoading.ShouldBeTrue();

        vm.OnViewerMessage(LoadedMsg);

        vm.Status.ShouldBe(ViewerStatus.Ready);
        vm.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public void Ready_before_OpenRun_is_remembered_and_load_sent_on_open()
    {
        GoodRun();
        var vm = Make();
        vm.OnViewerMessage(ReadyMsg);
        _sent.ShouldBeEmpty();
        vm.OpenRun(_dir);
        _sent.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(IgvPlanFailure.NoSequenceFile, ViewerStatus.NoSequenceFile, "ViewerNoSequenceFile")]
    [InlineData(IgvPlanFailure.NoEntropyTrack, ViewerStatus.NoEntropyTrack, "ViewerNoEntropyTrack")]
    [InlineData(IgvPlanFailure.AmbiguousSequenceFile, ViewerStatus.AmbiguousSequenceFile, "ViewerAmbiguousSequence")]
    [InlineData(IgvPlanFailure.SequenceTooLarge, ViewerStatus.SequenceTooLarge, "ViewerSequenceTooLarge")]
    public void Each_planner_failure_has_its_own_status_and_copy(IgvPlanFailure failure, ViewerStatus status, string keyPrefix)
    {
        switch (failure)
        {
            case IgvPlanFailure.NoSequenceFile:
                break;
            case IgvPlanFailure.NoEntropyTrack:
                File.WriteAllText(Path.Combine(_dir, "s.fasta"), ">s\nA\n");
                break;
            case IgvPlanFailure.AmbiguousSequenceFile:
                foreach (var n in new[] { "a.fasta", "b.fasta", "a.entropy.bedgraph", "b.entropy.bedgraph" })
                {
                    File.WriteAllText(Path.Combine(_dir, n), "x");
                }

                break;
            case IgvPlanFailure.SequenceTooLarge:
                GoodRun();
                break;
        }

        var vm = new ViewerViewModel(_probe, _strings, failure == IgvPlanFailure.SequenceTooLarge ? 3 : ViewerLimits.MaxFastaBytes);
        vm.OpenRun(_dir);

        vm.Status.ShouldBe(status);
        vm.ErrorTitle.ShouldBe($"[{keyPrefix}_Title]");
        vm.ErrorBody.ShouldBe($"[{keyPrefix}_Body]");
        vm.IsViewerVisible.ShouldBeFalse();
        vm.MapRunFolder.ShouldBeNull();
    }

    [Fact]
    public void Missing_folder_shows_FolderMissing_copy_and_clears_a_previous_mapping()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.MapRunFolder.ShouldBe(_dir);
        vm.OpenRun(Path.Combine(_dir, "gone"));
        vm.Status.ShouldBe(ViewerStatus.FolderMissing);
        vm.ErrorBody.ShouldBe("[ViewerFolderMissing_Body]");
        vm.MapRunFolder.ShouldBeNull();
    }

    [Fact]
    public void Null_run_folder_shows_NoRun_copy()
    {
        var vm = Make();
        vm.OpenRun(null);
        vm.Status.ShouldBe(ViewerStatus.NoRun);
        vm.ErrorBody.ShouldBe("[ViewerNoRun_Body]");
    }

    [Fact]
    public void A_warning_event_is_noise_and_changes_nothing()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        vm.OnViewerMessage(LoadedMsg);
        vm.OnViewerMessage("{\"evt\":\"warning\",\"message\":\"ResizeObserver loop\"}");
        vm.Status.ShouldBe(ViewerStatus.Ready);
        vm.IsViewerVisible.ShouldBeTrue();
        vm.MapRunFolder.ShouldBe(_dir);
    }

    [Fact]
    public void A_fatal_error_shows_ViewerError_with_a_retry_but_keeps_the_run_so_it_can_recover()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        vm.OnViewerMessage(FatalMsg);

        vm.Status.ShouldBe(ViewerStatus.ViewerError);
        vm.ErrorBody.ShouldBe("[ViewerError_Body]");
        vm.IsViewerVisible.ShouldBeFalse();
        vm.IsLoading.ShouldBeFalse();
        vm.HasRetryAction.ShouldBeTrue();
        vm.HasLinkAction.ShouldBeFalse();
        vm.ActionLabel.ShouldBe("[ViewerError_Action]");
        vm.MapRunFolder.ShouldBe(_dir);

        _sent.Clear();
        vm.RetryCommand.CanExecute(null).ShouldBeTrue();
        vm.RetryCommand.Execute(null);

        Cmd(_sent.Single()).ShouldBe("load");
        vm.Status.ShouldBe(ViewerStatus.Loading);
        vm.IsViewerVisible.ShouldBeTrue();
        vm.IsErrorVisible.ShouldBeFalse();
    }

    [Fact]
    public void Retry_is_not_available_when_there_is_no_error()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.RetryCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void A_ready_event_after_a_fatal_error_a_page_reload_recovers()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        vm.OnViewerMessage(FatalMsg);
        _sent.Clear();

        vm.OnViewerMessage(ReadyMsg);

        Cmd(_sent.Single()).ShouldBe("load");
        vm.IsViewerVisible.ShouldBeTrue();
        vm.IsErrorVisible.ShouldBeFalse();
    }

    [Fact]
    public void A_page_reload_ready_event_resends_the_current_load_command()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        _sent.Clear();
        vm.OnViewerMessage(ReadyMsg);
        Cmd(_sent.Single()).ShouldBe("load");
    }

    [Fact]
    public void After_a_failed_open_a_ready_event_sends_nothing()
    {
        var vm = Make();
        vm.OpenRun(Path.Combine(_dir, "gone"));
        vm.OnViewerMessage(ReadyMsg);
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public void Process_failure_shows_an_error_and_resets_the_ready_flag_until_the_page_is_back()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        vm.OnViewerMessage(LoadedMsg);
        _sent.Clear();

        vm.OnViewerProcessFailed();

        vm.Status.ShouldBe(ViewerStatus.ViewerError);
        vm.ErrorBody.ShouldBe("[ViewerProcessFailed_Body]");
        vm.IsViewerVisible.ShouldBeFalse();

        // Not ready: a theme change and a retry post nothing into a dead page.
        vm.SetDarkMode(true);
        vm.RetryCommand.CanExecute(null).ShouldBeFalse();
        _sent.ShouldBeEmpty();

        // The page comes back (host reload): the theme and the load are re-sent and the viewer returns.
        vm.OnViewerMessage(ReadyMsg);
        _sent.Select(Cmd).ShouldBe(["theme", "load"]);
        vm.IsViewerVisible.ShouldBeTrue();
        vm.IsErrorVisible.ShouldBeFalse();
    }

    [Fact]
    public void Init_failure_shows_a_named_action_error()
    {
        var vm = Make();
        vm.OnViewerInitFailed();
        vm.Status.ShouldBe(ViewerStatus.ViewerError);
        vm.ErrorTitle.ShouldBe("[ViewerInitFailed_Title]");
        vm.ErrorBody.ShouldBe("[ViewerInitFailed_Body]");
        vm.IsViewerVisible.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"evt\":\"unknown\"}")]
    [InlineData("{\"nothing\":1}")]
    [InlineData("[1,2]")]
    public void Garbage_messages_are_ignored(string msg)
    {
        var vm = Make();
        Should.NotThrow(() => vm.OnViewerMessage(msg));
        vm.Status.ShouldBe(ViewerStatus.Idle);
    }

    [Fact]
    public void SetDarkMode_is_resent_on_ready_and_live_afterwards()
    {
        GoodRun();
        var vm = Make();
        vm.SetDarkMode(true);
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        var cmds = _sent.Select(s => JsonDocument.Parse(s).RootElement).ToList();
        cmds.ShouldContain(c => c.GetProperty("cmd").GetString() == "theme" && c.GetProperty("dark").GetBoolean());
        vm.SetDarkMode(false);
        var last = JsonDocument.Parse(_sent.Last()).RootElement;
        last.GetProperty("cmd").GetString().ShouldBe("theme");
        last.GetProperty("dark").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Opening_a_second_run_on_an_already_ready_page_resends_load_and_remaps()
    {
        GoodRun();
        var vm = Make();
        vm.OpenRun(_dir);
        vm.OnViewerMessage(ReadyMsg);
        _sent.Clear();
        vm.OpenRun(_dir);
        _sent.Count.ShouldBe(1);
        vm.MapRunFolder.ShouldBe(_dir);
    }
}
