using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #110: the window-level keyboard shortcuts bind to parameterless methods; these are the behaviours behind them.</summary>
public sealed class KeyboardShortcutTests
{
    private static IStringResourceProvider Strings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return strings;
    }

    [Fact]
    public void Go_back_asks_the_navigator_to_go_back_when_it_can()
    {
        var navigator = Substitute.For<INavigator>();
        navigator.CanGoBack.Returns(true);
        var shell = new ShellViewModel(navigator, Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), Strings(), TestMessages.Center(), TestMessages.Completion());

        shell.GoBack();

        navigator.Received(1).GoBack();
    }

    [Fact]
    public void Go_back_does_nothing_when_there_is_no_page_to_go_back_to()
    {
        var navigator = Substitute.For<INavigator>();
        navigator.CanGoBack.Returns(false);
        var shell = new ShellViewModel(navigator, Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), Strings(), TestMessages.Center(), TestMessages.Completion());

        shell.GoBack();

        navigator.DidNotReceive().GoBack();
    }

    private static NewRunViewModel NewRun(IClipboardTextReader? clipboard)
    {
        var estimates = Substitute.For<ICostEstimateService>();
        estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        return new NewRunViewModel(
            Substitute.For<IFilePicker>(),
            Substitute.For<IJobEngine>(),
            Substitute.For<INavigator>(),
            Strings(),
            Substitute.For<IPastedInputStore>(),
            estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()),
            Substitute.For<IDiagnosticsLog>(),
            TestGpu.Plan(),
            clipboard: clipboard);
    }

    [Fact]
    public async Task Paste_from_clipboard_fills_the_paste_box_and_opens_it()
    {
        var clipboard = Substitute.For<IClipboardTextReader>();
        clipboard.ReadTextAsync().Returns(Task.FromResult<string?>("ACGTACGT"));
        var viewModel = NewRun(clipboard);

        await viewModel.PasteFromClipboardAsync();

        viewModel.PasteText.ShouldBe("ACGTACGT");
        viewModel.IsPasteOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Paste_from_an_empty_clipboard_leaves_the_box_alone_and_says_so()
    {
        var clipboard = Substitute.For<IClipboardTextReader>();
        clipboard.ReadTextAsync().Returns(Task.FromResult<string?>(null));
        var viewModel = NewRun(clipboard);
        viewModel.PasteText = "keep me";

        await viewModel.PasteFromClipboardAsync();

        viewModel.PasteText.ShouldBe("keep me");
        viewModel.StatusMessage.ShouldBe("NewRunStatusClipboardEmpty");
    }
}
