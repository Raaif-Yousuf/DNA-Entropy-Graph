using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

/// <summary>
/// The in-memory double every other test project depends on instead of a
/// real Google Cloud project (Hard Rule 7, CLAUDE.md). Scripted failures
/// (billing off, API off, quota, stockout, 403, preempt) are the scope of
/// the issue that builds the real provisioning flow; this skeleton's
/// FakeGcp is the always-succeeds baseline the DI graph resolves today.
/// </summary>
public sealed class FakeGcp : IComputeGateway, IStorageGateway, IProjectSetupGateway, IQuotaGateway, IGcpAccount
{
    private readonly ConcurrentDictionary<string, VmDescriptor> _vms = new();

    public bool IsSignedIn => true;

    public string? SelectedProjectId => "fake-project";

    public Task SignInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<VmDescriptor> CreateVmAsync(VmSpec spec, CancellationToken cancellationToken)
    {
        // Hard Rule 10, enforced here exactly as a real gateway must: a spec
        // missing a label or a maxRunDuration never reaches "creation", real
        // or fake.
        spec.EnsurePreconditions();

        var vm = new VmDescriptor($"deg-{spec.JobId}", "us-central1-a", "RUNNING");
        _vms[vm.Name] = vm;
        return Task.FromResult(vm);
    }

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => Task.FromResult(_vms.TryGetValue(vmName, out var vm) ? vm : null);

    public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        if (_vms.TryGetValue(vmName, out var vm))
        {
            _vms[vmName] = vm with { Status = "STOPPED" };
        }

        return Task.CompletedTask;
    }

    public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        _vms.TryRemove(vmName, out _);
        return Task.CompletedTask;
    }

    public Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult($"deg-{projectId}-fake");

    public Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => Task.FromResult<Stream>(new MemoryStream());

    public Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken)
        => Task.FromResult(1);
}
