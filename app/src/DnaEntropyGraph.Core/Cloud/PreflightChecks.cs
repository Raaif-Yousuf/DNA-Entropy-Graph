namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// docs/cloud_design.md section 2's preflight order, steps 1-4 (step 5, bucket-exists, is the runner's own
/// <see cref="IStorageGateway.EnsureBucketAsync"/> call). Returns null on success; a project-wide failure here means the
/// run never reaches Uploading at all, per the same rule <see cref="VmProvisioner"/> enforces for a project-wide error
/// discovered later, at create time.
/// </summary>
internal sealed class PreflightChecks(IProjectSetupGateway projectSetup, IQuotaGateway quota, GatewayCalls calls)
{
    public async Task<(CloudErrorKind Kind, string Message)?> RunAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var projectId = request.Spec.ProjectId;

        var state = await calls.CallAsync(token => projectSetup.GetProjectStateAsync(projectId, token), cancellationToken).ConfigureAwait(false);
        if (state != ProjectLifecycleState.Active)
        {
            return (CloudErrorKind.Permission, $"Project '{projectId}' is not ACTIVE (state: {state}).");
        }

        if (!await calls.CallAsync(token => projectSetup.IsBillingEnabledAsync(projectId, token), cancellationToken).ConfigureAwait(false))
        {
            return (CloudErrorKind.Billing, $"Billing is not enabled on project '{projectId}'.");
        }

        if (!await calls.CallAsync(token => projectSetup.IsComputeApiEnabledAsync(projectId, token), cancellationToken).ConfigureAwait(false))
        {
            return (CloudErrorKind.ApiDisabled, $"The Compute Engine API is not enabled on project '{projectId}'.");
        }

        // THEORY (unverified) / known gap: VmSpec has no accelerator-type
        // field yet (only MachineType, e.g. "g2-standard-8"), so this passes
        // a placeholder rather than the real GPU type the zone ladder (#86)
        // will eventually resolve from RunOptions.GpuTier. FakeGcp's default
        // quota (1) makes this preflight step pass unless a test explicitly
        // scripts a quota for this exact placeholder string.
        const string PlaceholderAcceleratorType = "gpu";
        var gpuQuota = await calls.CallAsync(token => quota.GetGpuQuotaAsync(projectId, RegionOf(request.Zones[0]), PlaceholderAcceleratorType, token), cancellationToken).ConfigureAwait(false);
        if (gpuQuota <= 0)
        {
            return (CloudErrorKind.Quota, $"No GPU quota available in region '{RegionOf(request.Zones[0])}' for project '{projectId}'.");
        }

        return null;
    }

    /// <summary>
    /// A zone id's region is everything before its last <c>-&lt;letter&gt;</c>
    /// suffix (<c>us-central1-a</c> -&gt; <c>us-central1</c>). THEORY
    /// (unverified): this is the general Compute Engine zone-naming shape,
    /// not verified against a real zone list this session.
    /// </summary>
    internal static string RegionOf(string zone)
    {
        var lastDash = zone.LastIndexOf('-');
        return lastDash > 0 ? zone[..lastDash] : zone;
    }
}
