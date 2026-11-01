using System.Globalization;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Net;
using DnaEntropyGraph.Core.Palettes;
using DnaEntropyGraph.Core.Presets;
using DnaEntropyGraph.Core.Viewers;

namespace DnaEntropyGraph.Core.SettingsPortability;

/// <summary>One setting the export carries: its key, the Resources.resw key of its plain name, and the check an imported value must pass.</summary>
public sealed record ExportedSetting(string Key, string LabelKey, Func<string, bool> IsValid);

/// <summary>
/// The allowlist behind Export settings (issue #242). A settings key is either here in <see cref="Exported"/> (with a validator, so an
/// imported value is one the app itself would have written) or in <see cref="Excluded"/> (with the reason it never leaves the PC).
/// <c>Guards.Tests/SettingsKeyCatalogGuardTests</c> fails when the source uses a key that is in neither, so a setting added later
/// cannot be silently dropped from the export. Secrets (tokens, DPAPI files, credentials) are not in settings.json at all (Hard Rule 12);
/// the exclusions below are the machine-specific and identity keys that are.
/// </summary>
public static class SettingsCatalog
{
    private static bool Bool(string v) => v is "true" or "false";

    private static bool WholeNumber(string v, long min, long max)
        => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max;

    private static bool OutputList(string v)
    {
        var names = v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var known = Enum.GetValues<OutputFileKinds>().Where(k => k != OutputFileKinds.None).SelectMany(k => OutputCatalog.ToWireNames(k)).ToHashSet(StringComparer.Ordinal);
        return names.Length > 0 && names.All(known.Contains);
    }

    // Literal resw keys: scripts/check_app_wiring.py's ORPHAN-RESOURCE scan sees only literal names.
    public static IReadOnlyList<ExportedSetting> Exported { get; } =
    [
        new("Theme", "SettingsKeyLabel_Theme", v => v is "Light" or "Dark" or "System"),
        new(TrackPalettes.SettingKey, "SettingsKeyLabel_TrackPalette", v => TrackPalettes.All.Any(p => p.Id == v)),
        new("notify_when_done", "SettingsKeyLabel_NotifyWhenDone", Bool),
        new("open_folder_when_done", "SettingsKeyLabel_OpenFolderWhenDone", Bool),
        new("open_results_when_done", "SettingsKeyLabel_OpenResultsWhenDone", Bool),
        new("update_channel", "SettingsKeyLabel_UpdateChannel", v => v is "stable" or "beta"),
        new("update_check_on_launch", "SettingsKeyLabel_UpdateCheckOnLaunch", Bool),
        new(CloudHousekeepingSettings.IdleStoppedVmHoursKey, "SettingsKeyLabel_IdleStoppedVmHours", v => WholeNumber(v, CloudHousekeepingSettings.MinIdleStoppedVmHours, CloudHousekeepingSettings.MaxIdleStoppedVmHours)),
        new(ResultsRetentionSetting.Key, "SettingsKeyLabel_ResultsRetentionDays", v => WholeNumber(v, 1, ResultsBucket.MaxRetentionDays)),
        new(OutputSelectionStore.KeyFor(OutputInputKinds.GenBank), "SettingsKeyLabel_OutputFilesGenBank", OutputList),
        new(OutputSelectionStore.KeyFor(OutputInputKinds.Sequence), "SettingsKeyLabel_OutputFilesSequence", OutputList),
        new("run.target", "SettingsKeyLabel_RunTarget", v => v == RunTargets.Cloud || v == RunTargets.ThisPc || v == RunTargets.Auto),
        new(ViewerSettingKeys.IgvPort, "SettingsKeyLabel_IgvPort", v => WholeNumber(v, 1, 65535)),
    ];

    /// <summary>Keys that are never exported and never imported, each with why. An imported file that names one of them has it ignored.</summary>
    public static IReadOnlyDictionary<string, string> Excluded { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [InstallationId.SettingsKey] = "Identifies this PC's cloud resources (Hard Rules 9 and 10); sharing it would let two PCs claim each other's VMs.",
        ["MainWindowPlacement"] = "Where the window sits on this PC's screens.",
        ["dismissed_imported_runs"] = "Imported-run rows this user dismissed on this PC (ImportedRunDismissals.SettingsKey, #111); they name job ids from the bucket (and legacy- ids hashed from a folder path, #915) and are re-derived from those, so they are not carried over.",
        // Literal on purpose: TourSettings.CompletedKey lives in Presentation, which references Core, so Core cannot name it. A Presentation.Tests test pins the two together.
        ["onboarding_tour_done"] = "Whether the first-run tour was shown on this PC (TourSettings.CompletedKey, #143); a new PC should show it once, so it is not carried over.",
        [ProxySettingKeys.ModeKey] = "The network proxy choice is specific to this PC and its network; an imported file must not be able to route traffic through a proxy (#239).",
        [ProxySettingKeys.AddressKey] = "A proxy address can name the user's institution and only means something on that network (#239); never exported or imported.",
        [WorkerImageSettingKeys.DeveloperMode] = "Developer switch; an imported file must not be able to turn it on.",
        [WorkerImageSettingKeys.WorkerImage] = "An image the app would run on a rented computer; an imported file must not be able to choose one.",
        [ViewerSettingKeys.IgvPath] = "A program path on this PC.",
        [ViewerSettingKeys.GeneiousPath] = "A program path on this PC.",
        ["session.page"] = "Where the user was when they closed the app (issue #244); about this PC's last session, not a preference.",
        ["session.run"] = "The run the user was looking at when they closed the app (issue #244).",
        [Cloud.RunAccountBackfill.MarkerKey] = "Whether this PC already gave its old run rows an account (issue #830); about this PC's run history, not a preference.",
        [SpendCapSettings.CapUsdKey] = "A monthly cap checked against this PC's own spend ledger (issue #109); a second PC has its own month-to-date, so the number is not carried over.",
        [SpendCapSettings.HardStopKey] = "Goes with the spending cap (issue #109): it only means something against this PC's own ledger, so it is not carried over.",
        ["prototype_import_done"] = "Whether this PC already looked for the earlier version's runs (issue #243); about this PC, not a preference.",
        // Literal on purpose, like the tour key: OutputFolderViewModel.SettingKey lives in Presentation. A Presentation.Tests test pins the two together.
        ["output_folder"] = "A folder on this PC (OutputFolderViewModel.SettingKey, #1037); a second PC has its own folders, and an imported file must not be able to point results at one.",
        [PresetStore.SettingsKey] = "Presets travel in their own versioned block of the file, not as a raw setting.",
    };

    public static ExportedSetting? Find(string key) => Exported.FirstOrDefault(e => e.Key == key);
}
