using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Presets;
using DnaEntropyGraph.Core.SettingsPortability;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #242: the Settings "Share settings" group. Export writes a file, import previews before it writes, and an applied import reloads every open group.</summary>
public sealed class SettingsTransferViewModelTests : IDisposable
{
    private readonly MemorySettings _settings = new();
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IThemeApplier _theme = Substitute.For<IThemeApplier>();
    private readonly IMessenger _messenger = new WeakReferenceMessenger();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-settings-transfer-" + Guid.NewGuid().ToString("N"));
    private int _resets;

    public SettingsTransferViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _messenger.Register<SettingsTransferViewModelTests, SettingsResetMessage>(this, static (t, _) => t._resets++);
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        Directory.Delete(_dir, recursive: true);
    }

    private SettingsTransferViewModel Make(MemorySettings? store = null)
    {
        store ??= _settings;
        return new SettingsTransferViewModel(new SettingsTransfer(store, new PresetStore(store)), _picker, _strings, _theme, _messenger);
    }

    private string Write(string text)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Export_writes_the_chosen_file_and_says_where()
    {
        _settings.Values["Theme"] = "Dark";
        var target = Path.Combine(_dir, "mine.json");
        _picker.PickSaveSettingsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(target);
        var vm = Make();

        await vm.ExportCommand.ExecuteAsync(null);

        File.ReadAllText(target).ShouldContain("\"Theme\": \"Dark\"");
        vm.Status.ShouldBe("SettingsTransferExportDone");
    }

    [Fact]
    public async Task A_cancelled_export_writes_and_says_nothing()
    {
        _picker.PickSaveSettingsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var vm = Make();

        await vm.ExportCommand.ExecuteAsync(null);

        vm.Status.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_export_that_cannot_be_written_names_what_to_do()
    {
        _picker.PickSaveSettingsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Path.Combine(_dir, "no-such-folder", "x.json"));
        var vm = Make();

        await vm.ExportCommand.ExecuteAsync(null);

        vm.Status.ShouldBe("SettingsTransferExportFailed");
    }

    [Fact]
    public async Task Choosing_a_file_shows_the_diff_and_writes_nothing()
    {
        _settings.Values["Theme"] = "Light";
        var file = Write("""{"format":"dna-entropy-graph-settings","schema":1,"settings":{"Theme":"Dark","notify_when_done":"false","installation_id":"x"}}""");
        _picker.PickSettingsFileAsync(Arg.Any<CancellationToken>()).Returns(file);
        var vm = Make();

        await vm.ChooseImportFileCommand.ExecuteAsync(null);

        vm.HasPreview.ShouldBeTrue();
        vm.Changes.Select(c => c.Label).ShouldBe(["SettingsKeyLabel_Theme", "SettingsKeyLabel_NotifyWhenDone"], ignoreOrder: true);
        var theme = vm.Changes.Single(c => c.Label == "SettingsKeyLabel_Theme");
        (theme.Current, theme.Incoming).ShouldBe(("Light", "Dark"));
        _settings.Values["Theme"].ShouldBe("Light");
        _settings.Values.ShouldNotContainKey("notify_when_done");
        _resets.ShouldBe(0);
    }

    [Fact]
    public async Task Applying_writes_the_settings_applies_the_theme_and_reloads_every_open_group()
    {
        var file = Write("""{"format":"dna-entropy-graph-settings","schema":1,"settings":{"Theme":"Dark","output_files_genbank":"bedgraph"}}""");
        _picker.PickSettingsFileAsync(Arg.Any<CancellationToken>()).Returns(file);
        var vm = Make();
        await vm.ChooseImportFileCommand.ExecuteAsync(null);

        vm.ApplyImportCommand.Execute(null);

        _settings.Values["Theme"].ShouldBe("Dark");
        _settings.Values["output_files_genbank"].ShouldBe("bedgraph");
        _theme.Received(1).Apply("Dark");
        _resets.ShouldBe(1);
        vm.HasPreview.ShouldBeFalse();
        vm.Status.ShouldBe("SettingsTransferImportDone");
    }

    [Fact]
    public async Task Cancelling_the_preview_leaves_everything_as_it_was()
    {
        var file = Write("""{"format":"dna-entropy-graph-settings","schema":1,"settings":{"Theme":"Dark"}}""");
        _picker.PickSettingsFileAsync(Arg.Any<CancellationToken>()).Returns(file);
        var vm = Make();
        await vm.ChooseImportFileCommand.ExecuteAsync(null);

        vm.CancelImportCommand.Execute(null);

        vm.HasPreview.ShouldBeFalse();
        vm.Changes.ShouldBeEmpty();
        vm.ApplyImportCommand.CanExecute(null).ShouldBeFalse();
        _settings.Values.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not json", "SettingsTransferImportNotSettings")]
    [InlineData("""{"schema":1}""", "SettingsTransferImportNotSettings")]
    [InlineData("""{"format":"dna-entropy-graph-settings","schema":99,"settings":{}}""", "SettingsTransferImportNewer")]
    [InlineData("""{"format":"dna-entropy-graph-settings","schema":1,"settings":{"Theme":"Purple"}}""", "SettingsTransferImportNothing")]
    public async Task A_file_that_cannot_be_used_says_why_and_shows_no_preview(string text, string expectedKey)
    {
        _picker.PickSettingsFileAsync(Arg.Any<CancellationToken>()).Returns(Write(text));
        var vm = Make();

        await vm.ChooseImportFileCommand.ExecuteAsync(null);

        vm.HasPreview.ShouldBeFalse();
        vm.Status.ShouldBe(expectedKey);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_names_what_to_do()
    {
        _picker.PickSettingsFileAsync(Arg.Any<CancellationToken>()).Returns(Path.Combine(_dir, "missing.json"));
        var vm = Make();

        await vm.ChooseImportFileCommand.ExecuteAsync(null);

        vm.Status.ShouldBe("SettingsTransferImportReadFailed");
    }

    [Fact]
    public async Task A_settings_file_in_use_by_another_copy_keeps_the_preview_and_says_so()
    {
        var store = new LockedOnSecondWriteSettings();
        var file = Write("""{"format":"dna-entropy-graph-settings","schema":1,"settings":{"notify_when_done":"false"}}""");
        _picker.PickSettingsFileAsync(Arg.Any<CancellationToken>()).Returns(file);
        var vm = Make(store);
        await vm.ChooseImportFileCommand.ExecuteAsync(null);
        store.Locked = true;

        vm.ApplyImportCommand.Execute(null);

        vm.Status.ShouldBe("SettingsTransferImportLocked");
        _resets.ShouldBe(0);
    }

    private class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public virtual void SetString(string key, string value) => Values[key] = value;
    }

    private sealed class LockedOnSecondWriteSettings : MemorySettings
    {
        public bool Locked { get; set; }

        public override void SetString(string key, string value)
        {
            if (Locked)
            {
                throw new SettingsUnavailableException("locked");
            }

            base.SetString(key, value);
        }
    }
}
