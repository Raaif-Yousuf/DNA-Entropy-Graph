namespace DnaEntropyGraph.Core.Net;

/// <summary>How the app reaches the internet (issue #239): follow Windows, use one typed proxy, or go direct.</summary>
public enum ProxyMode
{
    /// <summary>What Windows and the network say (Internet Options, a PAC script, automatic detection). The default.</summary>
    System,

    /// <summary>The one proxy the user typed, as host:port.</summary>
    Manual,

    /// <summary>No proxy at all, even when Windows names one.</summary>
    None,
}

/// <summary>The saved proxy choice. <paramref name="ManualAddress"/> only matters in <see cref="ProxyMode.Manual"/>. Never holds a sign-in: that lives in <see cref="IProxyCredentialStore"/> (Hard Rule 12).</summary>
public sealed record ProxySettings(ProxyMode Mode, string ManualAddress)
{
    public static ProxySettings Default { get; } = new(ProxyMode.System, string.Empty);
}
