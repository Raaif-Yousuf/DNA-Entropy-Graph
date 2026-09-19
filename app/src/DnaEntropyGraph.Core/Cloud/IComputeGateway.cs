namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Hard Rule 7: every Google Cloud call goes through an interface defined
/// HERE, in Core, never called directly from Presentation or App. Only
/// <c>DnaEntropyGraph.Cloud</c> may implement this against real
/// <c>Google.*</c> types; <c>FakeGcp</c> (also in Cloud) implements it
/// in-memory for every other test project. This project must never
/// reference <c>Google.*</c> itself.
/// </summary>
public interface IComputeGateway
{
    Task<VmDescriptor> CreateVmAsync(VmSpec spec, CancellationToken cancellationToken);

    Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken);
}

/// <summary>
/// The request to create one VM. Hard Rule 10: every VM carries the
/// standard labels (<c>app</c>, <c>installation-id</c>, <c>job-id</c>,
/// <c>model</c>, <c>app-version</c>, <c>lifecycle</c>) and a
/// <c>maxRunDuration</c> plus <c>instanceTerminationAction</c>.
/// <see cref="EnsurePreconditions"/> is the guard from issue #68: called by
/// every real gateway before a Compute Insert request is ever built, so an
/// unlabelled resource - invisible to the Cloud page, therefore a leak
/// (Hard Rule 9/10) - can never reach the API in the first place.
/// </summary>
public sealed record VmSpec(
    string InstallationId,
    string JobId,
    string Model,
    string AppVersion,
    string Lifecycle,
    string MachineType,
    TimeSpan MaxRunDuration,
    string TerminationAction)
{
    /// <summary>The <c>app</c> label's fixed value - every DNA Entropy Graph resource carries the same one.</summary>
    public const string AppLabelValue = "dna-entropy-graph";

    /// <summary>
    /// Throws naming the first missing precondition. Never silently
    /// defaults a missing field - an optional label with a sensible
    /// default is exactly the "hides a missing caller" shape wired-to-nothing
    /// warns about.
    /// </summary>
    public void EnsurePreconditions()
    {
        Require(InstallationId, "installation-id");
        Require(JobId, "job-id");
        Require(Model, "model");
        Require(AppVersion, "app-version");
        Require(Lifecycle, "lifecycle");
        Require(TerminationAction, "instanceTerminationAction");

        if (MaxRunDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("VmSpec is missing a positive maxRunDuration (Hard Rule 10).");
        }
    }

    /// <summary>The six standard labels (Hard Rule 10), keyed exactly as the Cloud page discovers them by.</summary>
    public IReadOnlyDictionary<string, string> ToLabels()
    {
        EnsurePreconditions();
        return new Dictionary<string, string>
        {
            ["app"] = AppLabelValue,
            ["installation-id"] = InstallationId,
            ["job-id"] = JobId,
            ["model"] = Model,
            ["app-version"] = AppVersion,
            ["lifecycle"] = Lifecycle,
        };
    }

    private static void Require(string value, string labelName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"VmSpec is missing required label '{labelName}' (Hard Rule 10).");
        }
    }
}

public sealed record VmDescriptor(string Name, string Zone, string Status);
