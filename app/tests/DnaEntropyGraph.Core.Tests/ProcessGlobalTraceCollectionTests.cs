using System.Reflection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #994: DiagnosticsTraceListenerTests counts lines of a log fed by the process-global Trace listener, so it must never run beside another test class.
/// Removing its [Collection] attribute (or the DisableParallelization flag) must go red here, not flake later.
/// </summary>
public sealed class ProcessGlobalTraceCollectionTests
{
    [Fact]
    public void DiagnosticsTraceListenerTests_is_in_a_collection_whose_definition_disables_parallelization()
    {
        var attr = typeof(DiagnosticsTraceListenerTests).GetCustomAttribute<CollectionAttribute>();
        attr.ShouldNotBeNull("DiagnosticsTraceListenerTests must carry [Collection] (#994)");

        var definitions = typeof(ProcessGlobalTraceCollectionTests).Assembly.GetTypes()
            .Select(t => (Type: t, Def: t.GetCustomAttribute<CollectionDefinitionAttribute>()))
            .Where(x => x.Def is not null && x.Def.Name == attr.Name)
            .ToList();

        definitions.Count.ShouldBe(1, $"exactly one [CollectionDefinition] must name '{attr.Name}'");
        definitions[0].Def!.DisableParallelization.ShouldBeTrue("the collection must run with no other test class in parallel");
    }
}
