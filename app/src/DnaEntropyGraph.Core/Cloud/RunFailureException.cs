namespace DnaEntropyGraph.Core.Cloud;

/// <summary>A failure the run machinery already classified; <see cref="CloudJobRunner.RunAsync"/> records it as the run's terminal state with exactly this code.</summary>
internal sealed class RunFailureException(CloudErrorKind kind, string code, string message) : Exception(message)
{
    public CloudErrorKind Kind { get; } = kind;

    public string Code { get; } = code;

    /// <summary>
    /// Issue #91: the lost VM was a Spot VM that Google took back (a preemption), not a failure of the worker. Internal on purpose: it is not a run
    /// code, because the runner answers it with one automatic on-demand retry and a run is never recorded under it.
    /// </summary>
    public bool SpotPreempted { get; init; }
}

/// <summary>What the runner's guard needs to know about the VM, written by the steps as they learn it.</summary>
internal sealed class RunProgress
{
    public bool VmMayExist { get; set; }

    public string? Zone { get; set; }

    /// <summary>The run's results bucket, once the runner has ensured it: the zone ladder reads its location to prefer zones on its continent (#670).</summary>
    public string? Bucket { get; set; }

    /// <summary>The project the bucket lives in (set with <see cref="Bucket"/>).</summary>
    public string? ProjectId { get; set; }

    /// <summary>The job id (set with <see cref="Bucket"/>): the zone claim of a parallel ladder is written under its prefix (#670).</summary>
    public string? JobId { get; set; }

    /// <summary>
    /// A moment on the runner's own clock taken just before THIS run asked for the VM it is on to be created or started (null for a VM it
    /// adopted, whose age only the platform knows). The platform's limit clock starts at or just after it, so the result deadline measures
    /// from here and never subtracts the platform's timestamps from the app's wall clock, which a skewed laptop clock would get wrong.
    /// </summary>
    public long? VmStartMark { get; set; }

    /// <summary>The name of the VM the run is on when it is not the one computed from the job id: a reused VM keeps its old name (#94). Null until known.</summary>
    public string? VmName { get; set; }

    /// <summary><paramref name="request"/> with its spec naming the VM the run is really on, so every look at, and the end of, that VM use the right name.</summary>
    public CloudJobRequest Resolve(CloudJobRequest request)
        => VmName is null || string.Equals(VmName, request.Spec.VmName, StringComparison.Ordinal)
            ? request
            : request with { Spec = request.Spec with { ExistingVmName = VmName } };

    /// <summary>The service account the VM was asked to run as (issue #604), once the identity step has answered.</summary>
    public string? WorkerAccountEmail { get; set; }
}

/// <summary>
/// The "an attempt to end the VM was already made" mark carried on an exception's <see cref="Exception.Data"/>, and the
/// note that turns an unconfirmed end into <see cref="RunErrorCodes.VmEndUnconfirmed"/>. One definition for every collaborator.
/// </summary>
internal static class VmEndNotes
{
    private const string VmEndAttemptedKey = "deg.vmEndAttempted";

    /// <summary>The exception already went through an attempt to end the VM, so ending it again would only wait out the lifecycle deadline twice.</summary>
    public static bool AlreadyAttempted(Exception ex) => ex.Data.Contains(VmEndAttemptedKey);

    public static Exception MarkAttempted(Exception ex)
    {
        ex.Data[VmEndAttemptedKey] = true;
        return ex;
    }

    /// <summary>
    /// The exception to throw once an attempt to end the VM was made. When that end was not confirmed
    /// (<paramref name="note"/> is not null) the run is recorded under <see cref="RunErrorCodes.VmEndUnconfirmed"/>, whose copy
    /// sends the user to the Cloud page to delete the computer so it stops billing; the original code stays in the detail.
    /// </summary>
    public static Exception Apply(Exception ex, string? note)
    {
        if (note is null)
        {
            return MarkAttempted(ex);
        }

        var (kind, code) = ex switch
        {
            RunFailureException failure => (failure.Kind, failure.Code),
            CloudOperationException cloud => (cloud.Kind, RunErrorCodes.For(cloud.Error, cloud.Kind)),
            _ => (CloudErrorKind.Other, RunErrorCodes.Other),
        };
        return MarkAttempted(new RunFailureException(kind, RunErrorCodes.VmEndUnconfirmed, $"{ex.Message} Original code: {code}.{note}"));
    }
}

/// <summary>Small pure judgements about error classes and VM status, shared by the run collaborators.</summary>
internal static class VmFacts
{
    public static bool IsProjectWide(CloudErrorKind kind)
        => kind is CloudErrorKind.Billing or CloudErrorKind.ApiDisabled or CloudErrorKind.Permission or CloudErrorKind.OrgPolicy;

    /// <summary>PROVISIONING and STAGING: the VM exists and is still starting. THEORY (unverified) for a real instance; see <see cref="ResultWaiter.WaitForBootAsync"/>.</summary>
    public static bool IsBooting(VmDescriptor? vm) => vm?.Status is "PROVISIONING" or "STAGING";

    /// <summary>
    /// A stopped instance. THEORY (unverified): Compute Engine reports a stopped instance as <c>TERMINATED</c>
    /// (the API's name for it) and <c>STOPPED</c> is what this app's fake says; nothing here has measured which a
    /// real VM shows, so both count.
    /// </summary>
    public static bool IsStopped(VmDescriptor vm) => vm.Status is "STOPPED" or "TERMINATED";

    public static bool IsHealthyRunning(VmDescriptor? vm, out string? reason)
    {
        if (vm is null)
        {
            reason = "The VM could not be found.";
            return false;
        }

        if (vm.Status != "RUNNING")
        {
            reason = vm.StatusReason is { Length: > 0 }
                ? $"The VM is '{vm.Status}' ({vm.StatusReason})."
                : $"The VM is '{vm.Status}', not RUNNING.";
            return false;
        }

        reason = null;
        return true;
    }
}
