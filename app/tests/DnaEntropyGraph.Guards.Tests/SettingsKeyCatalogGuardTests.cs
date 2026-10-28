using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.SettingsPortability;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #242: a settings key added later must not be silently dropped from the settings export. Every key the source reads or writes
/// through the settings store has to be either exported or excluded (with a reason) in <see cref="SettingsCatalog"/>. Add a carve-out
/// to <see cref="SettingsCatalog"/> and the docs/hard_rules.md-style note in docs/ui_conventions.md, or to neither.
/// </summary>
public sealed partial class SettingsKeyCatalogGuardTests
{
    // A literal passed straight to GetString/SetString, e.g. settingsStore.GetString("Theme").
    // The receiver must look like a settings store (not strings.GetString("ResourceKey")).
    [GeneratedRegex("""\b\w*(?:[sS]ettings|[sS]tore)\w*\??\.(?:Get|Set)String\(\s*"(?<key>[^"]+)"\s*[,)]""")]
    private static partial Regex LiteralCall();

    // A string constant in a file that talks to the settings store or is named as a settings-key holder.
    [GeneratedRegex("""const\s+string\s+(?<name>\w+)\s*=\s*"(?<key>[A-Za-z][A-Za-z0-9_.]*)"\s*;""")]
    private static partial Regex Constant();

    // Constants that live in settings-touching files but are not settings keys (page names, deep-link words, resource keys, metadata keys).
    private static readonly HashSet<string> NotSettingsKeys = new(StringComparer.Ordinal)
    {
        "run", "Results", "Viewer", "startup-script", "deg.vmEndAttempted", "MainWindowPlacementPlaceholder",
        "UninstallRemoveData_Title", "UninstallRemoveData_Body",
    };

    // ProxySettingKeys.Mode / .Address (issue #239) are named for what they hold, not "...Key": recognised by their holder class so a bare "Mode" elsewhere is not read as a settings key.
    private static bool IsProxyKeyConstant(string path, string constantName)
        => Path.GetFileName(path).Contains("ProxySetting", StringComparison.Ordinal) && constantName is "Mode" or "Address";

    internal static IReadOnlySet<string> DiscoverKeys(IEnumerable<(string Path, string Text)> files)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            foreach (Match m in LiteralCall().Matches(text))
            {
                found.Add(m.Groups["key"].Value);
            }

            var touchesSettings = text.Contains("ISettingsStore", StringComparison.Ordinal)
                || Path.GetFileName(path).Contains("Setting", StringComparison.Ordinal);
            if (!touchesSettings)
            {
                continue;
            }

            foreach (Match m in Constant().Matches(text))
            {
                var name = m.Groups["name"].Value;
                if (name.Contains("Key", StringComparison.Ordinal) || name is "Theme" or "DeveloperMode" or "WorkerImage" or "IgvPath" or "IgvPort" or "GeneiousPath" || IsProxyKeyConstant(path, name))
                {
                    found.Add(m.Groups["key"].Value);
                }
            }
        }

        found.ExceptWith(NotSettingsKeys);
        return found;
    }

    private static IEnumerable<(string, string)> RealSources()
        => RepoPaths.AllCSharpFiles.Select(p => (p, File.ReadAllText(p)));

    [Fact]
    public void Every_settings_key_in_the_source_is_exported_or_excluded()
    {
        var known = SettingsCatalog.Exported.Select(e => e.Key).Concat(SettingsCatalog.Excluded.Keys).ToHashSet(StringComparer.Ordinal);

        var unknown = DiscoverKeys(RealSources()).Where(k => !known.Contains(k)).Order().ToList();

        unknown.ShouldBeEmpty("These settings keys are neither exported nor excluded in SettingsCatalog (issue #242). Add each to Exported (with a validator and a label) or to Excluded (with a reason).");
    }

    [Fact]
    public void The_scanner_is_not_vacuous()
    {
        var discovered = DiscoverKeys(RealSources());

        discovered.ShouldContain("Theme");
        discovered.ShouldContain("installation_id");
        discovered.ShouldContain("notify_when_done");
        discovered.ShouldContain("Viewer.IgvPath");
        discovered.ShouldContain("MainWindowPlacement");
    }

    [Fact]
    public void A_planted_new_key_is_caught()
    {
        var planted = DiscoverKeys([("Planted.cs", "settings.SetString(\"brand_new_budget_cap\", \"5\");"), ("PlantedSettings.cs", "public const string CapKey = \"another_new_key\";")]);

        planted.ShouldBe(["brand_new_budget_cap", "another_new_key"], ignoreOrder: true);
        SettingsCatalog.Exported.Select(e => e.Key).Concat(SettingsCatalog.Excluded.Keys).ShouldNotContain("brand_new_budget_cap");
    }

    [Fact]
    public void Every_catalog_key_is_still_used_somewhere()
    {
        var discovered = DiscoverKeys(RealSources());
        var stale = SettingsCatalog.Exported.Select(e => e.Key).Concat(SettingsCatalog.Excluded.Keys)
            .Where(k => !discovered.Contains(k) && !k.StartsWith("output_files_", StringComparison.Ordinal) && k != "user_presets")
            .ToList();

        stale.ShouldBeEmpty("These catalog keys no longer appear in the source; remove them from SettingsCatalog.");
    }
}
