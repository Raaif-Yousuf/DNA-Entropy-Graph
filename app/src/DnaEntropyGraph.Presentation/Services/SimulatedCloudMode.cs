namespace DnaEntropyGraph.Presentation.Services;

/// <summary>Registered only when the app was started over the simulated cloud (issue #69). Its presence is what makes the shell show the permanent "simulated cloud" banner, so fake results can never pass for real ones.</summary>
public sealed class SimulatedCloudMode
{
}
