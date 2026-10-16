using System.Net;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Net;

/// <summary>What every network client asks, per request, for its proxy answer.</summary>
public interface IProxyPolicy
{
    ProxySettings Settings { get; }

    /// <summary>The saved manual sign-in, or null (then the Windows sign-in is offered to a proxy that asks).</summary>
    NetworkCredential? ManualCredential { get; }
}

/// <summary>
/// The one live proxy policy (issue #239). Loaded once at launch; <see cref="Apply"/> saves a change and the very next request
/// sees it, because every client holds a <see cref="PolicyWebProxy"/> over this object instead of a copy of the answer.
/// </summary>
public sealed class ProxyPolicy : IProxyPolicy
{
    /// <summary>What a client built with no policy uses: follow Windows, with the Windows sign-in. Never changes.</summary>
    public static IProxyPolicy SystemDefault { get; } = new FixedPolicy();

    private readonly ProxySettingsStore _store;
    private readonly IProxyCredentialStore _credentials;
    private volatile Snapshot _current;

    private sealed class FixedPolicy : IProxyPolicy
    {
        public ProxySettings Settings => ProxySettings.Default;

        public NetworkCredential? ManualCredential => null;
    }

    private sealed record Snapshot(ProxySettings Settings, NetworkCredential? Credential);

    public ProxyPolicy(ISettingsStore settings, IProxyCredentialStore credentials)
    {
        _store = new ProxySettingsStore(settings);
        _credentials = credentials;
        _current = Read();
    }

    public ProxySettings Settings => _current.Settings;

    public NetworkCredential? ManualCredential => _current.Credential;

    /// <summary>Re-reads settings.json and the credential file (after Reset all settings).</summary>
    public void Reload() => _current = Read();

    /// <summary>After Reset all settings emptied settings.json: forgets the saved sign-in too and follows Windows again. Never throws.</summary>
    public void ResetToDefault()
    {
        try
        {
            _credentials.Clear();
        }
        catch (ProxyCredentialUnreadableException)
        {
            // A file that cannot be removed is also one that is never read back as a sign-in for a mode that does not use it.
        }

        _current = new Snapshot(_store.Load(), null);
    }

    /// <summary>
    /// Makes <paramref name="settings"/> (and the manual sign-in) the policy now and saves both. Throws <see cref="SettingsUnavailableException"/> or
    /// <see cref="ProxyCredentialUnreadableException"/> when a save fails; the policy in use is then left as it was.
    /// </summary>
    public void Apply(ProxySettings settings, NetworkCredential? manualCredential)
    {
        var credential = settings.Mode == ProxyMode.Manual ? manualCredential : null;
        _store.Save(settings);
        if (credential is null)
        {
            _credentials.Clear();
        }
        else
        {
            _credentials.Save(credential);
        }

        _current = new Snapshot(settings, credential);
    }

    private Snapshot Read()
    {
        NetworkCredential? credential = null;
        try
        {
            credential = _credentials.Load();
        }
        catch (ProxyCredentialUnreadableException)
        {
            // An unreadable file is no sign-in: the Windows sign-in is still offered, and the box in Settings shows it empty.
        }

        return new Snapshot(_store.Load(), credential);
    }
}

/// <summary>
/// The <see cref="IWebProxy"/> every client uses (issue #239): the single place the policy meets a request.
/// System asks the OS (<see cref="System.Net.Http.HttpClient.DefaultProxy"/>: WinHTTP, PAC and automatic detection included) and offers the
/// Windows sign-in to a proxy that wants one (NTLM, Kerberos); Manual uses the typed proxy; None goes direct. Loopback never uses a proxy.
/// </summary>
public sealed class PolicyWebProxy(IProxyPolicy policy, IWebProxy? system = null) : IWebProxy
{
    private readonly IWebProxy _system = system ?? System.Net.Http.HttpClient.DefaultProxy;

    /// <summary>A setter is accepted and ignored: the policy is the only source of credentials.</summary>
    public ICredentials? Credentials
    {
        get
        {
            var settings = policy.Settings;
            if (settings.Mode == ProxyMode.None)
            {
                return null;
            }

            return settings.Mode == ProxyMode.Manual && TryManual(out _) && policy.ManualCredential is { } saved
                ? saved
                : CredentialCache.DefaultNetworkCredentials;
        }
        set { }
    }

    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.IsLoopback)
        {
            return null;
        }

        var settings = policy.Settings;
        return settings.Mode switch
        {
            ProxyMode.None => null,
            ProxyMode.Manual when TryManual(out var manual) => manual,
            _ => _system.GetProxy(destination),
        };
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;

    private bool TryManual(out Uri uri) => ProxyAddress.TryParse(policy.Settings.ManualAddress, out uri);
}
