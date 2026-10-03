using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #98, wired-to-nothing: the price list must be in the BUILD OUTPUT (not just in the source tree), be the file the
/// real app's service reads, parse, and price the machine the New run page actually starts.
/// </summary>
public class CostEstimateWiringTests
{
    private static readonly string OutputPricing = Path.Combine(AppContext.BaseDirectory, "Assets", "pricing.json");

    [Fact]
    public void The_price_list_is_copied_into_the_build_output_next_to_the_app()
        => File.Exists(OutputPricing).ShouldBeTrue(
            "Assets\\pricing.json is not in the output folder: the csproj <Content Include=\"Assets\\pricing.json\" CopyToOutputDirectory> item is missing or wrong.");

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
        table.Find(CloudJobRequestFactory.MachineTypeFor(GpuTier.A100_40)).ShouldNotBeNull("the A100 fallback tier is priced");

        var estimate = await provider.GetRequiredService<ICostEstimateService>()
            .EstimateAsync(machine, spot: false, bases: 5_000, TestContext.Current.CancellationToken);
        estimate.Estimate.ShouldNotBeNull();
        estimate.Estimate.MinUsd.ShouldBeGreaterThan(0);
    }
}
