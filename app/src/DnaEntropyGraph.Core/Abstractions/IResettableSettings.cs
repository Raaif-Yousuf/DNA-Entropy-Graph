namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Forgets every saved preference (issue #644's Reset all settings). Never touches run history, outputs, inputs, sign-in or the installation id.</summary>
public interface IResettableSettings
{
    /// <exception cref="SettingsUnavailableException">The settings file is in use by another copy of the app.</exception>
    void ResetAll();
}

/// <summary>Which cloud the app talks to: the always-succeeding fake (a development build) or Google Cloud.</summary>
public interface ICloudModeInfo
{
    bool IsFakeCloud { get; }
}
