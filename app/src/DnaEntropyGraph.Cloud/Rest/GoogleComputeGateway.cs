using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.Compute.v1;
using ComputeData = Google.Apis.Compute.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IComputeGateway"/> over Compute Engine v1 REST (issue #56). Compute is operation-based: <c>insert</c>,
/// <c>stop</c> and <c>delete</c> return a zone operation, polled with <see cref="OperationPoller"/> (1 s doubling to 10 s,
/// one <see cref="GoogleCloudOptions.OperationDeadline"/> for the polling) until it reports <c>DONE</c>, and the real error
/// (stockout, quota, a missing permission) lives in <c>operation.error</c>, mapped to a <see cref="CloudOperationException"/>
/// through <see cref="CloudErrorClassifier"/> and nothing else: this class invents no classification rule. An HTTP error
/// (billing off and the Compute API off arrive here, as a 403) goes through <see cref="GoogleApiErrors"/>, which
/// delegates to the same classifier.
/// It does NOT call <see cref="CloudCallPipeline"/> itself: production wraps the whole gateway in
/// <see cref="ResilientComputeGateway"/>, the way every <see cref="IComputeGateway"/> is. That is safe for the mutating
/// <c>insert</c> because it carries a deterministic <c>requestId</c> (issue #257): Compute Engine treats a replay
/// with the same id as the same request, so a retry after a transient failure while polling cannot make a second VM.
/// The interface has no project parameter on the calls other than create (which has <see cref="VmSpec.ProjectId"/>), so the
/// project comes from <c>selectedProjectId</c>; none selected is an <see cref="InvalidOperationException"/>, never a guess.
/// </summary>
internal sealed class GoogleComputeGateway : IComputeGateway
{
    /// <summary>The only values a label can hold (the same rule <see cref="VmSpec"/> enforces), so a filter value built from one cannot carry filter syntax.</summary>
    private static readonly Regex LabelValuePattern = new("^[a-z0-9_-]{1,63}$", RegexOptions.Compiled);

    private readonly ComputeService _service;
    private readonly GoogleCloudOptions _options;
    private readonly Func<string?> _selectedProjectId;

    public GoogleComputeGateway(ComputeService service, GoogleCloudOptions options, Func<string?> selectedProjectId)
    {
        _service = service;
        _options = options;
        _selectedProjectId = selectedProjectId;
    }

    public async Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Hard Rule 10: a spec missing a label or a lifetime limit throws here, before any request exists.
        var labels = spec.ToLabels();
        var instance = ComputeVmShape.Build(spec, zone, labels);

        var insert = _service.Instances.Insert(instance, spec.ProjectId, zone);
        insert.RequestId = RequestIdFor(spec.ProjectId, zone, spec.VmName);
        var operation = await ExecuteAsync(() => insert.ExecuteAsync(cancellationToken)).ConfigureAwait(false);
        await AwaitOperationAsync(spec.ProjectId, zone, operation, goneIsDone: false, cancellationToken).ConfigureAwait(false);

        return await GetAsync(spec.ProjectId, spec.VmName, zone, cancellationToken).ConfigureAwait(false)
            ?? throw new CloudOperationException(
                new CloudError("NOT_FOUND", 404, $"Compute Engine reported the create of '{spec.VmName}' done, but the VM cannot be found."),
                CloudErrorKind.Other);
    }

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => GetAsync(RequireProject(), vmName, zone, cancellationToken);

    public async Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        ComputeData.Operation operation;
        try
        {
            operation = await ExecuteAsync(() => _service.Instances.Stop(project, zone, vmName).ExecuteAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            return; // Already gone: nothing left to stop.
        }

        await AwaitOperationAsync(project, zone, operation, goneIsDone: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        ComputeData.Operation operation;
        try
        {
            operation = await ExecuteAsync(() => _service.Instances.Delete(project, zone, vmName).ExecuteAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            return; // Already gone is what a delete wants.
        }

        await AwaitOperationAsync(project, zone, operation, goneIsDone: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
        => ListByLabelAsync("job-id", jobId, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken)
        => ListByLabelAsync("installation-id", installationId, cancellationToken);

    private async Task<IReadOnlyList<VmDescriptor>> ListByLabelAsync(string labelKey, string labelValue, CancellationToken cancellationToken)
    {
        var project = RequireProject();

        // A value no label can hold matches no VM, and it must never reach a filter expression.
        if (!LabelValuePattern.IsMatch(labelValue ?? string.Empty))
        {
            return [];
        }

        var filter = $"(labels.app = \"{VmSpec.AppLabelValue}\") AND (labels.{labelKey} = \"{labelValue}\")";
        var found = new List<VmDescriptor>();
        string? pageToken = null;
        do
        {
            var request = _service.Instances.AggregatedList(project);
            request.Filter = filter;
            request.PageToken = pageToken;
            var page = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken)).ConfigureAwait(false);

            foreach (var (scope, scoped) in page.Items ?? new Dictionary<string, ComputeData.InstancesScopedList>())
            {
                foreach (var instance in scoped?.Instances ?? [])
                {
                    // The server filter is the query; this is the guard that a VM of another job, installation or app can never be returned.
                    if (instance.Labels is { } labels
                        && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
                        && labels.TryGetValue(labelKey, out var value) && value == labelValue)
                    {
                        found.Add(ToDescriptor(instance, ZoneName(scope) ?? string.Empty));
                    }
                }
            }

            var next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            pageToken = next == pageToken ? null : next;
        }
        while (pageToken is not null);

        return found;
    }

    private async Task<VmDescriptor?> GetAsync(string project, string vmName, string zone, CancellationToken cancellationToken)
    {
        try
        {
            var instance = await ExecuteAsync(() => _service.Instances.Get(project, zone, vmName).ExecuteAsync(cancellationToken)).ConfigureAwait(false);
            return ToDescriptor(instance, zone);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            return null;
        }
    }

    /// <summary>Polls the zone operation until <c>DONE</c> and throws its error. <paramref name="goneIsDone"/>: a stop or delete whose operation says the VM is not found has nothing left to do.</summary>
    private async Task AwaitOperationAsync(string project, string zone, ComputeData.Operation operation, bool goneIsDone, CancellationToken cancellationToken)
    {
        if (!IsDone(operation))
        {
            var name = operation.Name;
            var latest = operation;
            var outcome = await OperationPoller.PollAsync(
                async token =>
                {
                    latest = await ExecuteAsync(() => _service.ZoneOperations.Get(project, zone, name).ExecuteAsync(token)).ConfigureAwait(false);
                    return new OperationPoll<bool>(IsDone(latest), true, null);
                },
                _options.OperationDeadline,
                cancellationToken,
                _options.Delay,
                _options.TimeProvider).ConfigureAwait(false);
            if (!outcome.Success)
            {
                throw new CloudOperationException(outcome.Error!, CloudErrorClassifier.Classify(outcome.Error!));
            }

            operation = latest;
        }

        if (operation.Error?.Errors is { Count: > 0 } errors)
        {
            var first = errors[0];
            if (goneIsDone && (operation.HttpErrorStatusCode == 404 || first.Code == "RESOURCE_NOT_FOUND"))
            {
                return;
            }

            var error = new CloudError(first.Code, operation.HttpErrorStatusCode, first.Message ?? operation.HttpErrorMessage ?? string.Empty);
            throw new CloudOperationException(error, CloudErrorClassifier.Classify(error));
        }
    }

    private static bool IsDone(ComputeData.Operation operation) => string.Equals(operation.Status, "DONE", StringComparison.Ordinal);

    /// <summary>Runs one request, turning a Google HTTP error into the one exception the app understands.</summary>
    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> request)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
        }
    }

    private string RequireProject()
        => _selectedProjectId() is { Length: > 0 } project
            ? project
            : throw new InvalidOperationException("No Google Cloud project is selected, so there is no project to ask Compute Engine about.");

    /// <summary>
    /// A UUID derived from what the request creates, so every replay of one create (a retry, or a resume after a crash) carries the
    /// same <c>requestId</c> and Compute Engine answers it with the original operation instead of making a second VM.
    /// </summary>
    private static string RequestIdFor(string project, string zone, string vmName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"deg-create/{project}/{zone}/{vmName}"));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    private static VmDescriptor ToDescriptor(ComputeData.Instance instance, string zone)
        => new(
            instance.Name,
            ZoneName(instance.Zone) ?? zone,
            instance.Status ?? string.Empty,
            instance.StatusMessage,
            ParseTime(instance.CreationTimestamp),
            instance.Labels is null ? null : new Dictionary<string, string>(instance.Labels),
            ParseTime(instance.LastStopTimestamp));

    /// <summary>The zone name from a zone URL (<c>.../zones/us-central1-a</c>) or an aggregated-list key (<c>zones/us-central1-a</c>).</summary>
    private static string? ZoneName(string? url)
        => string.IsNullOrEmpty(url) ? null : url[(url.LastIndexOf('/') + 1)..];

    private static DateTimeOffset? ParseTime(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
}

/// <summary>
/// The shape of the VM a <see cref="VmSpec"/> becomes: the parts <see cref="VmSpec"/> does not carry (boot image, disk, service
/// account, network) are fixed here, in one place. THEORY (unverified, no live project): that
/// <c>scheduling.maxRunDuration</c> is accepted on a standard-provisioning VM with <c>onHostMaintenance=TERMINATE</c> and
/// <c>automaticRestart=false</c>; that the worker service account is named <c>deg-worker@&lt;project&gt;.iam.gserviceaccount.com</c>
/// (docs/cloud_design.md section 7; nothing creates it yet); and that the CPU image family is right for
/// <c>worker/vm/startup.sh</c>. docs/ToTest.md carries the row that proves them on a real VM.
/// </summary>
internal static class ComputeVmShape
{
    public const string GpuImage = "projects/deeplearning-platform-release/global/images/family/pytorch-2-9-cu129-ubuntu-2404-nvidia-580";

    public const string CpuImage = "projects/cos-cloud/global/images/family/cos-stable";

    public const int BootDiskGb = 150;

    private static readonly string[] GpuMachineFamilies = ["g2-", "g4-", "a2-", "a3-", "a4-"];

    public static ComputeData.Instance Build(VmSpec spec, string zone, IReadOnlyDictionary<string, string> labels)
    {
        var items = new List<ComputeData.Metadata.ItemsData>();
        foreach (var (key, value) in spec.Metadata ?? new Dictionary<string, string>())
        {
            items.Add(new ComputeData.Metadata.ItemsData { Key = key, Value = value });
        }

        // docs/cloud_design.md section 9: the bucket is the transport, nobody logs in, so no project-wide SSH key may reach the VM.
        if (items.All(i => i.Key != "block-project-ssh-keys"))
        {
            items.Add(new ComputeData.Metadata.ItemsData { Key = "block-project-ssh-keys", Value = "true" });
        }

        return new ComputeData.Instance
        {
            Name = spec.VmName,
            MachineType = $"zones/{zone}/machineTypes/{spec.MachineType}",
            Labels = labels.ToDictionary(p => p.Key, p => p.Value),
            Metadata = new ComputeData.Metadata { Items = items },
            Scheduling = new ComputeData.Scheduling
            {
                MaxRunDuration = new ComputeData.Duration { Seconds = (long)Math.Ceiling(spec.MaxRunDuration.TotalSeconds) },
                InstanceTerminationAction = spec.TerminationAction,
                OnHostMaintenance = "TERMINATE",
                AutomaticRestart = false,
            },
            Disks =
            [
                new ComputeData.AttachedDisk
                {
                    Boot = true,
                    AutoDelete = true,
                    InitializeParams = new ComputeData.AttachedDiskInitializeParams
                    {
                        SourceImage = IsGpu(spec.MachineType) ? GpuImage : CpuImage,
                        DiskSizeGb = BootDiskGb,
                        DiskType = $"zones/{zone}/diskTypes/pd-balanced",
                    },
                },
            ],
            NetworkInterfaces =
            [
                new ComputeData.NetworkInterface
                {
                    Network = "global/networks/default",
                    AccessConfigs = [new ComputeData.AccessConfig { Name = "External NAT", Type = "ONE_TO_ONE_NAT" }],
                },
            ],
            ServiceAccounts =
            [
                new ComputeData.ServiceAccount
                {
                    Email = $"deg-worker@{spec.ProjectId}.iam.gserviceaccount.com",
                    Scopes = ["https://www.googleapis.com/auth/cloud-platform"],
                },
            ],
        };
    }

    private static bool IsGpu(string machineType) => GpuMachineFamilies.Any(f => machineType.StartsWith(f, StringComparison.Ordinal));
}
