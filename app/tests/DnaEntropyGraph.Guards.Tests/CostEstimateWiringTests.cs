using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #98, wired-to-nothing: the price list must be in the BUILD OUTPUT (not just in the source tree) and asserted in the
/// PUBLISH output by CI (the installer is built from publish, and a build-output check cannot see a publish that drops it), be
/// the file the real app's service reads, parse, and price every GPU tier the app can start (or name why one is excluded).
/// </summary>
public class CostEstimateWiringTests
{
    private static readonly string OutputPricing = Path.Combine(AppContext.BaseDirectory, "Assets", "pricing.json");

    /// <summary>
    /// Tiers the app can start that have no price row on purpose. An entry that gains a price fails the guard (the exclusion
    /// expires itself), so this list can only shrink. The estimate line for such a tier says it is not available yet.
    /// </summary>
    private static readonly Dictionary<GpuTier, string> UnpricedOnPurpose = new()
    {
        [GpuTier.H100] = "a3-highgpu-1g has no figure in docs/research/2026-09-19-gpu-pricing-and-instances.md and a price copied from memory would be invented; Evo 2 7B runs on L4 and A100 only (docs/cloud_design.md section 10, issue #98 round 2).",
    };

    [Fact]
    public void The_price_list_is_copied_into_the_build_output_next_to_the_app()
        => File.Exists(OutputPricing).ShouldBeTrue(
            "Assets\\pricing.json is not in the output folder: the csproj <Content Include=\"Assets\\pricing.json\" CopyToOutputDirectory> item is missing or wrong.");

    [Fact]
    public void CI_asserts_the_price_list_is_in_the_publish_output_the_installer_is_built_from()
    {
        var workflow = Path.Combine(RepoPaths.AppRoot, "..", ".github", "workflows", "ci-app.yml");
        File.Exists(workflow).ShouldBeTrue("the guard must find the real workflow, or it checks nothing.");

        File.ReadAllText(workflow).ShouldContain(
            "Test-Path publish/Assets/pricing.json",
            customMessage: "ci-app.yml must fail a publish that lacks Assets\\pricing.json: the build-output guard above cannot see a publish that drops it (#98).");
    }

    [Fact]
    public void Every_gpu_tier_the_app_can_start_has_an_on_demand_price_or_a_documented_exclusion()
    {
        var table = RealTable();

        foreach (var tier in Enum.GetValues<GpuTier>())
        {
            var machine = CloudJobRequestFactory.MachineTypeFor(tier);
            if (UnpricedOnPurpose.TryGetValue(tier, out var reason))
            {
                reason.ShouldNotBeNullOrWhiteSpace();
                table.Find(machine).ShouldBeNull($"{tier} ({machine}) now has a price: remove it from UnpricedOnPurpose and from docs/cloud_design.md section 10.");
            }
            else
            {
                table.Find(machine).ShouldNotBeNull($"{tier} maps to {machine}, which the shipped price list does not price, and it is not in UnpricedOnPurpose.");
            }
        }
    }

    private static PricingTable RealTable()
    {
        var load = new FilePricingSource(OutputPricing).Load();
        load.Problem.ShouldBeNull(load.Detail);
        return load.Table.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_real_app_services_read_that_shipped_file_and_price_the_machine_a_run_starts()
    {
        var services = new ServiceCollection();
        var appData = Path.Combine(Path.GetTempPath(), "deg-cost-guard-" + Guid.NewGuid().ToString("N"));
        services.AddDnaEntropyGraph(appDataRoot: appData);
        using var provider = services.BuildServiceProvider();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(appData, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        };

        var load = provider.GetRequiredService<IPricingSource>().Load();

        load.Problem.ShouldBeNull(load.Detail);
        var table = load.Table.ShouldNotBeNull();
        var machine = CloudJobRequestFactory.MachineTypeFor(GpuTier.CheapestAvailable);
        table.Find(machine).ShouldNotBeNull($"the shipped price list has no price for {machine}, the machine the New run page starts");

        var estimate = await provider.GetRequiredService<ICostEstimateService>()
            .EstimateAsync(machine, spot: false, bases: 5_000, bootDiskGb: 150, TestContext.Current.CancellationToken);
        estimate.Estimate.ShouldNotBeNull();
        estimate.Estimate.MinUsd.ShouldBeGreaterThan(0);
    }
}
