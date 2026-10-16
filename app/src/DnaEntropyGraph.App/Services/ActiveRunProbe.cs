using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App.Services;

/// <summary>Issue #179: "is a run being driven" for the updater, answered by the one registry of run drivers (<see cref="ActiveRuns"/>), which the engine and the reattach both register in.</summary>
public sealed class ActiveRunProbe(ActiveRuns runs) : IActiveRunProbe
{
    public bool AnyActive => runs.Any;
}
