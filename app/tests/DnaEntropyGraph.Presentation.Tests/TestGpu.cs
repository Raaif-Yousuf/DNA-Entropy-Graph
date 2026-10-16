using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>NewRunViewModel takes its GPU pickers from DI with no fallback (issue #87): a test that does not care about them passes these.</summary>
internal static class TestGpu
{
    private sealed class KeyStrings : IStringResourceProvider
    {
        public string GetString(string key) => key;
    }

    public static GpuPlanViewModel Plan(GpuCatalog? catalog = null, IStringResourceProvider? strings = null)
        => new(catalog ?? GpuCatalog.Shipped, strings ?? new KeyStrings());
}
