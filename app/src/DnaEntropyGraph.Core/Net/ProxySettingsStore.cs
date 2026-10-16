using System.Net;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Net;

/// <summary>The settings.json keys for the proxy choice (issue #239). The sign-in for a proxy is NOT here: it is DPAPI-encrypted (<see cref="IProxyCredentialStore"/>).</summary>
public static class ProxySettingKeys
{
    public const string Mode = "proxy_mode";

    public const string Address = "proxy_address";
}

/// <summary>Reads and writes the proxy choice in settings.json.</summary>
public sealed class ProxySettingsStore(ISettingsStore settings)
{
    public ProxySettings Load()
    {
        string? mode;
        string? address;
        try
        {
            mode = settings.GetString(ProxySettingKeys.Mode);
            address = settings.GetString(ProxySettingKeys.Address);
        }
        catch (SettingsUnavailableException)
        {
            return ProxySettings.Default;
        }

        var parsed = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "manual" => ProxyMode.Manual,
            "none" => ProxyMode.None,
            _ => ProxyMode.System,
        };
        return new ProxySettings(parsed, (address ?? string.Empty).Trim());
    }

    /// <summary>What settings.json holds right now (null when a key is absent), or null when it cannot be read.</summary>
    public (string? Mode, string? Address)? TryReadRaw()
    {
        try
        {
            return (settings.GetString(ProxySettingKeys.Mode), settings.GetString(ProxySettingKeys.Address));
        }
        catch (SettingsUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Writes back values read by <see cref="TryReadRaw"/>; an absent key is written empty, which reads back as the same default.</summary>
    public void WriteRaw(string? mode, string? address)
    {
        settings.SetString(ProxySettingKeys.Mode, mode ?? string.Empty);
        settings.SetString(ProxySettingKeys.Address, address ?? string.Empty);
    }

    public void Save(ProxySettings value)
    {
        settings.SetString(ProxySettingKeys.Mode, value.Mode switch { ProxyMode.Manual => "manual", ProxyMode.None => "none", _ => "system" });
        settings.SetString(ProxySettingKeys.Address, value.Mode == ProxyMode.Manual ? value.ManualAddress.Trim() : string.Empty);
    }
}

/// <summary>Where the manual proxy's user name and password live: encrypted for this Windows user, never in settings.json or SQLite (Hard Rule 12).</summary>
public interface IProxyCredentialStore
{
    /// <summary>The saved sign-in, or null when none is saved. Throws <see cref="ProxyCredentialUnreadableException"/> for a file this user cannot decrypt.</summary>
    NetworkCredential? Load();

    void Save(NetworkCredential credential);

    void Clear();
}

/// <summary>The saved proxy sign-in could not be read or written (another Windows user's file, corruption, a locked or read-only folder).</summary>
public sealed class ProxyCredentialUnreadableException(string message, Exception? inner) : Exception(message, inner);
