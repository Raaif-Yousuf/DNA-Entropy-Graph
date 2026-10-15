using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #644: the Settings Advanced group (worker image override, fake-cloud indicator, reset all settings).</summary>
public sealed class AdvancedSettingsTests
{
    private const string Pinned = "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly MemorySettings _settings = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ICloudModeInfo _mode = Substitute.For<ICloudModeInfo>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public AdvancedSettingsTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private AdvancedSettingsViewModel Create() => new(_settings, _settings, _dialogs, _strings, _mode);

    [Fact]
    public void Without_developer_mode_the_override_box_is_off_and_empty()
    {
        var vm = Create();

        vm.IsDeveloperMode.ShouldBeFalse();
        vm.WorkerImage.ShouldBeEmpty();
    }

    [Fact]
    public void A_digest_pinned_image_is_saved_under_the_key_the_run_start_reads_when_developer_mode_is_on()
    {
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        var vm = Create();

        vm.WorkerImage = "  " + Pinned + " ";

        vm.WorkerImage.ShouldBe(Pinned);
        _settings.Values[WorkerImageSettingKeys.WorkerImage].ShouldBe(Pinned);
        vm.WorkerImageStatus.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ghcr.io/raaif-yousuf/dna-entropy-worker:latest")]
    [InlineData("not an image")]
    public void An_image_that_is_not_pinned_by_digest_is_refused_and_not_saved(string text)
    {
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        var vm = Create();

        vm.WorkerImage = text;

        vm.WorkerImageStatus.ShouldBe("WorkerImageNotPinned");
        _settings.Values.ContainsKey(WorkerImageSettingKeys.WorkerImage).ShouldBeFalse();
    }

    [Fact]
    public void A_refused_image_also_clears_the_override_saved_before_so_the_box_never_disagrees_with_what_runs()
    {
        // #644 cold review (3): the box showed the refused text while the earlier override stayed saved and active.
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        _settings.Values[WorkerImageSettingKeys.WorkerImage] = Pinned;
        var vm = Create();

        vm.WorkerImage = "ghcr.io/raaif-yousuf/dna-entropy-worker:latest";

        _settings.Values[WorkerImageSettingKeys.WorkerImage].ShouldBeEmpty();
        vm.WorkerImageStatus.ShouldBe("WorkerImageNotPinned");
    }

    [Fact]
    public void A_refused_image_that_cannot_clear_the_saved_one_says_the_file_is_locked()
    {
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        _settings.Values[WorkerImageSettingKeys.WorkerImage] = Pinned;
        var vm = Create();
        _settings.Unavailable = true;

        vm.WorkerImage = "not an image";

        vm.WorkerImageStatus.ShouldBe("AdvancedNotSaved");
    }

    [Fact]
    public void Clearing_the_box_removes_the_override_so_the_shipped_image_is_used()
    {
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        _settings.Values[WorkerImageSettingKeys.WorkerImage] = Pinned;
        var vm = Create();
        vm.WorkerImage.ShouldBe(Pinned);

        vm.WorkerImage = string.Empty;

        _settings.Values[WorkerImageSettingKeys.WorkerImage].ShouldBeEmpty();
        vm.WorkerImageStatus.ShouldBeEmpty();
    }

    [Fact]
    public void Opening_the_group_saves_nothing()
    {
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        _settings.Values[WorkerImageSettingKeys.WorkerImage] = Pinned;
        var before = _settings.Values.ToDictionary(p => p.Key, p => p.Value);

        _ = Create();

        _settings.Values.ShouldBe(before);
    }

    [Fact]
    public void A_locked_settings_file_is_reported_with_the_action()
    {
        _settings.Values[WorkerImageSettingKeys.DeveloperMode] = "true";
        var vm = Create();
        _settings.Unavailable = true;

        vm.WorkerImage = Pinned;

        vm.WorkerImageStatus.ShouldBe("AdvancedNotSaved");
    }

    [Theory]
    [InlineData(true, "FakeCloudOn")]
    [InlineData(false, "FakeCloudOff")]
    public void The_fake_cloud_indicator_says_which_cloud_the_app_talks_to(bool fake, string expected)
    {
        _mode.IsFakeCloud.Returns(fake);

        Create().FakeCloudText.ShouldBe(expected);
    }

    [Fact]
    public async Task Reset_asks_first_and_a_no_changes_nothing()
    {
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _settings.Values["Theme"] = "Dark";
        var vm = Create();

        await vm.ResetAllSettingsCommand.ExecuteAsync(null);

        _settings.Values["Theme"].ShouldBe("Dark");
        _settings.ResetCalls.ShouldBe(0);
        vm.ResetStatus.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reset_after_a_yes_clears_the_settings_and_says_to_restart()
    {
        _settings.Values["Theme"] = "Dark";
        var vm = Create();

        await vm.ResetAllSettingsCommand.ExecuteAsync(null);

        _settings.ResetCalls.ShouldBe(1);
        _settings.Values.ShouldBeEmpty();
        vm.ResetStatus.ShouldBe("AdvancedResetDone");
    }

    [Fact]
    public async Task A_locked_settings_file_during_reset_is_reported_and_nothing_else_is_touched()
    {
        var vm = Create();
        _settings.Unavailable = true;

        await vm.ResetAllSettingsCommand.ExecuteAsync(null);

        vm.ResetStatus.ShouldBe("AdvancedNotSaved");
    }

    private sealed class MemorySettings : ISettingsStore, IResettableSettings
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Unavailable { get; set; }

        public int ResetCalls { get; private set; }

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

        public void ResetAll()
        {
            if (Unavailable)
            {
                throw new SettingsUnavailableException("locked");
            }

            ResetCalls++;
            Values.Clear();
        }
    }
}
