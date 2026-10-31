// Issue #830: what a mocked IGcpAccount answers for SelectedScope() next to the SelectedProjectId it already answers (no account sub: tests that care model one).
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Presentation.Tests;

internal static class TestScopes
{
    internal static CloudScope? Of(string? projectId) => string.IsNullOrEmpty(projectId) ? null : new CloudScope(projectId);
}
