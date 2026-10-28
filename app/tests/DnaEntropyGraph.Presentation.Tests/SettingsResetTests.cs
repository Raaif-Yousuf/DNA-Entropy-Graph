using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Core.Viewers;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #644 cold review (2): Reset all settings empties the file, and every Settings group open on the page must show the
/// starting values at once and must not write them back. The reset is driven through the Advanced group's own command, the
/// way the button does it.
/// </summary>
public sealed class SettingsResetTests
{
    private const string Pinned = "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly MemorySettings _settings = new();
    private readonly IMessenger _messenger = new WeakReferenceMessenger();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();

    public SettingsResetTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private sealed record Page(
        AdvancedSettingsViewModel Advanced,
        NotificationsSettingsViewModel Notifications,
        RunDefaultsSettingsViewModel RunDefaults,
        ExternalViewersSettingsViewModel Viewers);

    private Page OpenSettingsPage()
    {
        var mode = Substitute.For<ICloudModeInfo>();
        var picker = Substitute.For<IFilePicker>();
        var probe = Substitute.For<IFileProbe>();
        probe.ExistsAsync(Arg.Any<string>()).Returns(true);
        var dispatcher = new InlineDispatcher();
        return new Page(
            new AdvancedSettingsViewModel(_settings, _settings, _dialogs, _strings, mode, _messenger),
            new NotificationsSettingsViewModel(new NotificationSettings(_settings), _strings, _messenger),
            new RunDefaultsSettingsViewModel(new OutputSelectionStore(_settings), _strings, dispatcher, _messenger),
            new ExternalViewersSettingsViewModel(_settings, picker, _strings, dispatcher, probe, _messenger));
    }

    private void SaveNonDefaults()
    {
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        _settings.Values[WorkerImageSettingKeys.WorkerImage] = Pinned;
        _settings.Values[NotificationSettings.NotifyKey] = "false";
        _settings.Values[NotificationSettings.OpenFolderKey] = "true";
        _settings.Values[NotificationSettings.OpenResultsKey] = "true";
        _settings.Values[ViewerSettingKeys.IgvPath] = @"C:\igv\igv.exe";
        _settings.Values[ViewerSettingKeys.IgvPort] = "61000";
        new OutputSelectionStore(_settings).Save(OutputInputKinds.GenBank, OutputFileKinds.Stats);
    }

    [Fact]
    public async Task After_Reset_all_every_open_group_shows_the_starting_values_and_nothing_is_written_back()
    {
        SaveNonDefaults();
        var page = OpenSettingsPage();
        page.Advanced.IsDeveloperMode.ShouldBeTrue();
        page.Advanced.WorkerImage.ShouldBe(Pinned);
        page.Notifications.NotifyWhenDone.ShouldBeFalse();
        page.RunDefaults.GenBankChoices.Count(c => c.IsChecked).ShouldBe(1);
        page.Viewers.IgvPortText.ShouldBe("61000");

        await page.Advanced.ResetAllSettingsCommand.ExecuteAsync(null);

        page.Advanced.IsDeveloperMode.ShouldBeFalse("the box must go back to disabled, or the next edit is saved and then refused");
        page.Advanced.WorkerImage.ShouldBeEmpty();
        page.Notifications.NotifyWhenDone.ShouldBeTrue();
        page.Notifications.OpenFolderWhenDone.ShouldBeFalse();
        page.Notifications.OpenResultsWhenDone.ShouldBeFalse();
        page.RunDefaults.GenBankChoices.Where(c => c.IsChecked).Select(c => c.WireName)
            .ShouldBe(OutputCatalog.ToWireNames(OutputCatalog.Defaults));
        page.Viewers.IgvPath.ShouldBeEmpty();
        page.Viewers.IgvPortText.ShouldBe(IgvBatchCommands.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _settings.Values.ShouldBeEmpty("showing the starting values must not save them again");
    }

    [Fact]
    public async Task After_Reset_all_the_theme_radio_goes_back_to_system_and_the_window_follows_without_a_restart_or_a_write()
    {
        // Issue #677: the Appearance radio is on the Settings page's own ViewModel, which Reset all did not tell.
        var themes = Substitute.For<IThemeApplier>();
        themes.CurrentTheme.Returns("Dark");
        var toasts = Substitute.For<IToastService>();
        var settingsViewModel = new SettingsViewModel(_settings, toasts, _strings, Substitute.For<IDiagnosticsExporter>(), Substitute.For<IFilePicker>(), Substitute.For<IFolderLauncher>(), TimeProvider.System, themes, _messenger);
        settingsViewModel.ThemeIndex.ShouldBe(1, "the window shows Dark");
        var page = OpenSettingsPage();

        await page.Advanced.ResetAllSettingsCommand.ExecuteAsync(null);

        settingsViewModel.ThemeIndex.ShouldBe(2);
        themes.Received(1).Apply("System");
        toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
        _settings.Values.ShouldBeEmpty("showing the starting theme must not save it again");
    }

    [Fact]
    public async Task A_group_opened_after_the_reset_agrees_with_the_groups_that_were_open_during_it()
    {
        SaveNonDefaults();
        var open = OpenSettingsPage();

        await open.Advanced.ResetAllSettingsCommand.ExecuteAsync(null);
        var fresh = OpenSettingsPage();

        fresh.Advanced.IsDeveloperMode.ShouldBe(open.Advanced.IsDeveloperMode);
        fresh.Notifications.NotifyWhenDone.ShouldBe(open.Notifications.NotifyWhenDone);
        fresh.Notifications.OpenResultsWhenDone.ShouldBe(open.Notifications.OpenResultsWhenDone);
        fresh.Viewers.IgvPortText.ShouldBe(open.Viewers.IgvPortText);
    }

    [Fact]
    public async Task A_declined_reset_leaves_every_group_as_it_was()
    {
        SaveNonDefaults();
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var page = OpenSettingsPage();

        await page.Advanced.ResetAllSettingsCommand.ExecuteAsync(null);

        page.Advanced.IsDeveloperMode.ShouldBeTrue();
        page.Notifications.NotifyWhenDone.ShouldBeFalse();
        page.Viewers.IgvPortText.ShouldBe("61000");
    }

    [Fact]
    public async Task A_group_edited_after_the_reset_saves_normally()
    {
        SaveNonDefaults();
        var page = OpenSettingsPage();
        await page.Advanced.ResetAllSettingsCommand.ExecuteAsync(null);

        page.Notifications.NotifyWhenDone = false;
        page.Viewers.IgvPortText = "62000";

        _settings.Values[NotificationSettings.NotifyKey].ShouldBe("false");
        _settings.Values[ViewerSettingKeys.IgvPort].ShouldBe("62000");
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private sealed class MemorySettings : ISettingsStore, IResettableSettings
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;

        public void ResetAll() => Values.Clear();
    }
}
