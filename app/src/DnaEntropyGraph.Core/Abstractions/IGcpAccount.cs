namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// The signed-in Google account and its selected project, as Presentation
/// is allowed to see it. Implemented in DnaEntropyGraph.Cloud (which is
/// allowed to reference Google.*); Presentation never references Cloud
/// directly (docs/architecture.md section 2's dependency-arrow diagram).
/// </summary>
public interface IGcpAccount
{
    bool IsSignedIn { get; }

    string? SelectedProjectId { get; }

    Task SignInAsync(CancellationToken cancellationToken);
}
