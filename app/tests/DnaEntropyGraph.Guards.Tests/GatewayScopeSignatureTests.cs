using System.Reflection;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #830, "no production class resolves an unscoped gateway": every method of a gateway that acts on a project or a run's resources takes a <see cref="CloudScope"/> as its first
/// parameter, so no call compiles, and no class can resolve a gateway and call it, without naming the project and account. The gateways below are exempt because their calls are about
/// the account or the whole PC, not a run's project (the wizard's catalogue and billing/budget steps, which are selection paths: see <c>SelectionReadGuardTests</c>).
/// </summary>
public class GatewayScopeSignatureTests
{
    private static readonly Dictionary<Type, string> Exempt = new()
    {
        [typeof(IBillingGateway)] = "billing accounts and links of the account in the wizard, not a run's resources",
        [typeof(IBudgetGateway)] = "the budget of the selected project (selection path)",
        [typeof(IPricingCatalogGateway)] = "a public price list, no project or account",
        [typeof(IProjectCatalogGateway)] = "the wizard's list of the account's projects",
        [typeof(IServiceEnablementGateway)] = "the wizard's API-enable step on the project being set up",
    };

    private static IEnumerable<Type> Gateways()
        => typeof(IComputeGateway).Assembly.GetTypes().Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Name.EndsWith("Gateway", StringComparison.Ordinal));

    private static IEnumerable<string> Unscoped(IEnumerable<Type> interfaces)
        => interfaces.SelectMany(i => i.GetMethods().Where(m => m.GetParameters().FirstOrDefault()?.ParameterType != typeof(CloudScope)).Select(m => $"{i.Name}.{m.Name}"));

    [Fact]
    public void Every_method_of_a_project_gateway_takes_a_CloudScope_first()
    {
        var scoped = Gateways().Where(g => !Exempt.ContainsKey(g)).ToList();

        // Vacuity: the five project gateways are found, with methods.
        scoped.Select(g => g.Name).Order().ToList().ShouldBe(["IComputeGateway", "IProjectSetupGateway", "IQuotaGateway", "IStorageGateway", "IWorkerIdentityGateway"]);
        scoped.Sum(g => g.GetMethods().Length).ShouldBeGreaterThan(25);

        Unscoped(scoped).ShouldBeEmpty("add a CloudScope first parameter: the project and account must be named by the caller, never read from the selection inside the gateway");
        Exempt.Keys.ShouldAllBe(t => Gateways().Contains(t), "a stale exemption: delete it");
    }

    [Fact]
    public void The_scan_catches_a_method_with_no_scope()
    {
        Unscoped([typeof(IPlantedUnscopedGateway)]).ShouldBe(["IPlantedUnscopedGateway.ListSomethingAsync"]);
    }

    private interface IPlantedUnscopedGateway
    {
        Task ListSomethingAsync(string projectId, CancellationToken cancellationToken);

        Task FineAsync(CloudScope scope, CancellationToken cancellationToken);
    }
}
