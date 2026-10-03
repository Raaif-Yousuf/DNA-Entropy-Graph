namespace DnaEntropyGraph.Core.Cloud;

/// <summary>A failure the run machinery already classified; <see cref="CloudJobRunner.RunAsync"/> records it as the run's terminal state with exactly this code.</summary>
internal sealed class RunFailureException(CloudErrorKind kind, string code, string message) : Exception(message)
{
    public CloudErrorKind Kind { get; } = kind;

    public string Code { get; } = code;
}

/// <summary>What the runner's guard needs to know about the VM, written by the steps as they learn it.</summary>
internal sealed class RunProgress
{
    public bool VmMayExist { get; set; }

    public string? Zone { get; set; }
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
            CloudOperationException cloud => (cloud.Kind, RunErrorCodes.For(cloud.Kind)),
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
