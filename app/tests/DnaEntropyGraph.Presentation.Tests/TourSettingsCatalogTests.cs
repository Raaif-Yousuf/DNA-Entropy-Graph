using DnaEntropyGraph.Core.SettingsPortability;
using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public sealed class TourSettingsCatalogTests
{
    // SettingsCatalog (Core) cannot reference TourSettings (Presentation), so it spells the key as a literal; this pins the two together.
    [Fact]
    public void The_tour_completed_key_is_excluded_from_the_settings_export()
    {
        SettingsCatalog.Excluded.Keys.ShouldContain(TourSettings.CompletedKey);
    }
}
