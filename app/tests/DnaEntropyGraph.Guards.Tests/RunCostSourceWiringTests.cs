using System.Reflection;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issues #100 and #557, wired-to-nothing: the Results header and the Runs rows take their cost source as an OPTIONAL constructor argument, so a registration that went
/// missing would compile, resolve and show no cost line anywhere. This resolves the real container and checks the source reaches both ViewModels and reads the ledger.
/// </summary>
public class RunCostSourceWiringTests
{
    [Fact]
    public async Task The_real_container_hands_the_ledger_backed_cost_source_to_the_Results_and_Runs_ViewModels()
    {
        var services = new ServiceCollection();
        var appData = Path.Combine(Path.GetTempPath(), "deg-runcost-guard-" + Guid.NewGuid().ToString("N"));
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

        var source = provider.GetRequiredService<IRunCostSource>();
        source.ShouldBeOfType<LedgerRunCostSource>();
        // Built without the repair a failed ledger write would show no figure on the Runs row until some other page happened to read the month.
        source.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(SpendLedger))
            .Select(f => f.GetValue(source))
            .ShouldContain(v => v != null, "the run cost source was built without the ledger repair");
        await provider.GetRequiredService<ICostLedgerRepository>().AppendAsync(new CostLedgerEntry(SpendLedger.VmRuntimeKind, DateTimeOffset.UtcNow, "wired-run", EndedUtc: DateTimeOffset.UtcNow, UsdEst: 1.25), TestContext.Current.CancellationToken);
        (await source.GetBookedAsync(TestContext.Current.CancellationToken))["wired-run"].ShouldBe(1.25, 1e-12);

        foreach (var viewModel in new object[] { provider.GetRequiredService<ResultsViewModel>(), provider.GetRequiredService<HistoryViewModel>() })
        {
            var field = viewModel.GetType().GetField("_costs", BindingFlags.Instance | BindingFlags.NonPublic);
            field.ShouldNotBeNull($"{viewModel.GetType().Name} has no _costs field");
            field.GetValue(viewModel).ShouldBeAssignableTo<IRunCostSource>($"{viewModel.GetType().Name} was built without a cost source: its cost line would never show");

            // A failed cost read is logged (error class only) so support can see why a cost line is missing; built without a log it would be silent again.
            var log = viewModel.GetType().GetField("_log", BindingFlags.Instance | BindingFlags.NonPublic);
            log.ShouldNotBeNull($"{viewModel.GetType().Name} has no _log field");
            log.GetValue(viewModel).ShouldBeAssignableTo<DnaEntropyGraph.Core.Abstractions.IDiagnosticsLog>($"{viewModel.GetType().Name} was built without a diagnostics log");
        }

        // The support zip reads the booked figure from the same source; built without one its cost fields would be null for every run.
        var exporter = provider.GetRequiredService<DnaEntropyGraph.Core.Diagnostics.IDiagnosticsExporter>();
        exporter.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(IRunCostSource))
            .Select(f => f.GetValue(exporter))
            .ShouldContain(v => v != null, "the diagnostics exporter was built without a cost source");
    }
}
