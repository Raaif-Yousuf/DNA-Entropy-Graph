namespace DnaEntropyGraph.Presentation.Messaging;

/// <summary>
/// Sent after Reset all settings has emptied the settings file (issue #644). Every Settings group that holds values read from
/// that file re-reads them on this message, so the page never keeps showing (or saving over) values that no longer exist.
/// </summary>
public sealed record SettingsResetMessage;
