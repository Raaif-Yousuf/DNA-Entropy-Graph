namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The first-run wizard's preflight chain (docs/architecture.md Critical
/// Pitfalls: billing off and API off look exactly like a stockout).
/// See IComputeGateway for the Hard Rule 7 boundary this interface exists to enforce.
/// </summary>
public interface IProjectSetupGateway
{
    /// <summary>
    /// Preflight step 1 (docs/cloud_design.md section 2's order: "project
    /// ACTIVE -&gt; billing -&gt; Compute API -&gt; GPU quota -&gt; bucket").
    /// Closes issue #388: until this method existed, no interface anywhere
    /// in Core/Cloud could express this step at all, and a caller
    /// implementing the documented preflight chain had no call for its own
    /// first check.
    /// </summary>
    Task<ProjectLifecycleState> GetProjectStateAsync(string projectId, CancellationToken cancellationToken);

    Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken);

    Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken);

    Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// A GCP project's lifecycle state, reduced to what preflight step 1 needs
/// to decide (docs/cloud_design.md section 2). Real values come from
/// <c>projects.get</c>'s <c>lifecycleState</c> field (<c>ACTIVE</c>,
/// <c>DELETE_REQUESTED</c>, <c>DELETE_IN_PROGRESS</c>) plus the case where
/// the project cannot be described at all (wrong id, no access, deleted
/// past recovery) - <see cref="NotFound"/> covers all of those, since none
/// of them is actionable differently from the caller's point of view: pick
/// a different project.
/// </summary>
public enum ProjectLifecycleState
{
    Active,
    NotFound,
    Other,
}
