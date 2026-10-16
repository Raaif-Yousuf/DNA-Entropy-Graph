using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The two update settings (issues #179, #145): the channel (default Stable) and whether to check when the app starts (default On).
/// One class, so the page and the launch check cannot disagree about a key or a default. A read of a locked settings file is the
/// default; a write of one throws <see cref="SettingsUnavailableException"/> for the caller to report.
/// </summary>
public sealed class UpdateSettings(ISettingsStore store)
{
    public const string ChannelKey = "update_channel";

    public const string CheckOnLaunchKey = "update_check_on_launch";

    public UpdateChannel Channel
    {
        get => Read(ChannelKey) switch
        {
            "beta" => UpdateChannel.Beta,
            _ => UpdateChannel.Stable,
        };
        set => store.SetString(ChannelKey, value == UpdateChannel.Beta ? "beta" : "stable");
    }

    public bool CheckOnLaunch
    {
        get => !bool.TryParse(Read(CheckOnLaunchKey), out var value) || value;
        set => store.SetString(CheckOnLaunchKey, value ? "true" : "false");
    }

    private string? Read(string key)
    {
        try
        {
            return store.GetString(key);
        }
        catch (SettingsUnavailableException)
        {
            return null;
        }
    }
}
