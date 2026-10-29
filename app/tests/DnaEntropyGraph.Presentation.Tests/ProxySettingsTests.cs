using System.Net;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Net;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #239: the Settings Advanced proxy section. Choosing a mode, typing a proxy, and what is saved where.</summary>
public sealed class ProxySettingsTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Unavailable { get; set; }

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            if (Unavailable)
            {
                throw new SettingsUnavailableException("locked");
            }

            Values[key] = value;
        }
    }

    private sealed class MemoryCredentials : IProxyCredentialStore
    {
        public NetworkCredential? Saved { get; set; }

        public NetworkCredential? Load() => Saved;

        public void Save(NetworkCredential credential) => Saved = credential;

        public void Clear() => Saved = null;
    }

    private readonly MemorySettings _settings = new();
    private readonly MemoryCredentials _credentials = new();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public ProxySettingsTests() => _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());

    private (ProxySettingsViewModel Vm, ProxyPolicy Policy) Create(IMessenger? messenger = null)
    {
        var policy = new ProxyPolicy(_settings, _credentials);
        return (new ProxySettingsViewModel(policy, _strings, messenger), policy);
    }

    [Fact]
    public void A_first_launch_follows_windows_and_the_manual_boxes_are_off()
    {
        var (vm, _) = Create();

        vm.SelectedModeIndex.ShouldBe(0);
        vm.IsManual.ShouldBeFalse();
        vm.HasSavedSignIn.ShouldBeFalse();
    }

    [Fact]
    public void Applying_a_manual_proxy_changes_the_live_policy_and_saves_it()
    {
        var (vm, policy) = Create();
        vm.SelectedModeIndex = 1;
        vm.ManualAddress = "proxy.uni.edu:8080";

        vm.ApplyCommand.Execute(null);

        policy.Settings.ShouldBe(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"));
        _settings.Values[ProxySettingKeys.ModeKey].ShouldBe("manual");
        vm.Status.ShouldBe("ProxySaved");
    }

    [Fact]
    public void An_address_that_is_not_host_and_port_is_refused_and_nothing_is_changed()
    {
        var (vm, policy) = Create();
        vm.SelectedModeIndex = 1;
        vm.ManualAddress = "http://alice:pw@proxy.uni.edu:8080";

        vm.ApplyCommand.Execute(null);

        vm.Status.ShouldBe("ProxyAddressInvalid");
        policy.Settings.Mode.ShouldBe(ProxyMode.System);
        _settings.Values.ShouldBeEmpty();
    }

    [Fact]
    public void A_user_name_and_password_go_to_the_encrypted_store_and_never_to_settings_json()
    {
        var (vm, policy) = Create();
        vm.SelectedModeIndex = 1;
        vm.ManualAddress = "proxy.uni.edu:8080";
        vm.ProxyUserName = "alice";
        vm.ProxyPassword = "hunter2";

        vm.ApplyCommand.Execute(null);

        _credentials.Saved!.UserName.ShouldBe("alice");
        _credentials.Saved.Password.ShouldBe("hunter2");
        policy.ManualCredential!.UserName.ShouldBe("alice");
        _settings.Values.Values.ShouldNotContain(v => v.Contains("hunter2", StringComparison.Ordinal) || v.Contains("alice", StringComparison.Ordinal));
        vm.ProxyPassword.ShouldBeEmpty();
        vm.HasSavedSignIn.ShouldBeTrue();
    }

    [Fact]
    public void A_user_name_with_no_password_and_nothing_saved_asks_for_the_password()
    {
        var (vm, policy) = Create();
        vm.SelectedModeIndex = 1;
        vm.ManualAddress = "proxy.uni.edu:8080";
        vm.ProxyUserName = "alice";

        vm.ApplyCommand.Execute(null);

        vm.Status.ShouldBe("ProxyPasswordNeeded");
        policy.Settings.Mode.ShouldBe(ProxyMode.System);
    }

    [Fact]
    public void Applying_again_with_the_password_box_empty_keeps_the_saved_password_for_the_same_user()
    {
        _credentials.Saved = new NetworkCredential("alice", "hunter2");
        _settings.Values[ProxySettingKeys.ModeKey] = "manual";
        _settings.Values[ProxySettingKeys.AddressKey] = "proxy.uni.edu:8080";
        var (vm, policy) = Create();
        vm.ProxyUserName.ShouldBe("alice");
        vm.ManualAddress = "proxy2.uni.edu:8080";

        vm.ApplyCommand.Execute(null);

        policy.ManualCredential!.Password.ShouldBe("hunter2");
        policy.Settings.ManualAddress.ShouldBe("proxy2.uni.edu:8080");
    }

    [Fact]
    public void Clearing_the_user_name_forgets_the_saved_sign_in()
    {
        _credentials.Saved = new NetworkCredential("alice", "hunter2");
        _settings.Values[ProxySettingKeys.ModeKey] = "manual";
        _settings.Values[ProxySettingKeys.AddressKey] = "proxy.uni.edu:8080";
        var (vm, policy) = Create();
        vm.ProxyUserName = string.Empty;

        vm.ApplyCommand.Execute(null);

        _credentials.Saved.ShouldBeNull();
        policy.ManualCredential.ShouldBeNull();
        vm.HasSavedSignIn.ShouldBeFalse();
    }

    [Fact]
    public void Choosing_none_goes_direct_and_drops_the_manual_sign_in()
    {
        _credentials.Saved = new NetworkCredential("alice", "hunter2");
        var (vm, policy) = Create();
        vm.SelectedModeIndex = 2;

        vm.ApplyCommand.Execute(null);

        policy.Settings.Mode.ShouldBe(ProxyMode.None);
        _credentials.Saved.ShouldBeNull();
    }

    [Fact]
    public void A_locked_settings_file_says_not_saved_and_leaves_the_live_policy_as_it_was()
    {
        var (vm, policy) = Create();
        _settings.Unavailable = true;
        vm.SelectedModeIndex = 2;

        vm.ApplyCommand.Execute(null);

        vm.Status.ShouldBe("AdvancedNotSaved");
        policy.Settings.Mode.ShouldBe(ProxyMode.System);
    }

    [Fact]
    public void Reset_all_settings_returns_to_windows_and_forgets_the_sign_in()
    {
        var messenger = new WeakReferenceMessenger();
        _credentials.Saved = new NetworkCredential("alice", "hunter2");
        _settings.Values[ProxySettingKeys.ModeKey] = "manual";
        _settings.Values[ProxySettingKeys.AddressKey] = "proxy.uni.edu:8080";
        var (vm, policy) = Create(messenger);

        _settings.Values.Clear();
        messenger.Send(new SettingsResetMessage());

        policy.Settings.Mode.ShouldBe(ProxyMode.System);
        policy.ManualCredential.ShouldBeNull();
        _credentials.Saved.ShouldBeNull();
        vm.SelectedModeIndex.ShouldBe(0);
        vm.ProxyUserName.ShouldBeEmpty();
    }
}
