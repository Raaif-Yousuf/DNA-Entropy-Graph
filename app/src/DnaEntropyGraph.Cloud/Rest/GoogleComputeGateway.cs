using System.Globalization;
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
/// Every HTTP request goes through <see cref="CloudCallPipeline"/> on its own (as the project and service gateways do), so a
/// transient error on one poll retries THAT poll and never replays the insert. Production therefore must NOT wrap this gateway in
/// <see cref="ResilientComputeGateway"/> (a second layer would replay the whole create while the original operation may still be
/// pending). Each <c>CreateVmAsync</c> call fixes ONE random <c>requestId</c> at its start and every replay of that insert POST
/// carries it, so Compute Engine answers a replay with the same operation. A NEW call gets a new id, never one derived from the VM
/// name: Compute replays the ORIGINAL operation for a repeated id within its idempotency window, which would hand a retry after a
/// stockout the old failure, or a re-create of the same <c>deg-&lt;job&gt;</c> after a delete the old DONE operation with no VM made.
/// Instance names are unique per zone, so a NEW call after a lost response is answered 409 <c>alreadyExists</c>
/// (<see cref="CloudErrorKind.AlreadyExists"/>), which the runner adopts. THEORY (unverified): an abandoned create (the provisioner
/// gave up) can still be finishing in Compute, so a later retry may adopt an instance whose original operation then fails.
/// The interface has no project parameter on the calls other than create (which has <see cref="VmSpec.ProjectId"/>), so the
/// project comes from <c>selectedProjectId</c>; none selected is an <see cref="InvalidOperationException"/>, never a guess.
/// </summary>
internal sealed class GoogleComputeGateway : IComputeGateway
{
    /// <summary>The only values a label can hold (the same rule <see cref="VmSpec"/> enforces), so a filter value built from one cannot carry filter syntax.</summary>
    private static readonly Regex LabelValuePattern = new(@"\A[a-z0-9_-]{1,63}\z", RegexOptions.Compiled);

    /// <summary>Which kind wins when one operation reports several errors: the ones a user or the zone ladder can act on first.</summary>
    private static readonly CloudErrorKind[] KindPreference =
    [
        CloudErrorKind.Stockout, CloudErrorKind.Quota, CloudErrorKind.Permission, CloudErrorKind.Billing, CloudErrorKind.ApiDisabled,
        CloudErrorKind.OrgPolicy, CloudErrorKind.AlreadyExists, CloudErrorKind.Network, CloudErrorKind.Other,
    ];

    private readonly ComputeService _service;
    private readonly CloudCallPipeline _pipeline;
    private readonly GoogleCloudOptions _options;
    private readonly Func<string?> _selectedProjectId;

    public GoogleComputeGateway(ComputeService service, CloudCallPipeline pipeline, GoogleCloudOptions options, Func<string?> selectedProjectId)
    {
        _service = service;
        _pipeline = pipeline;
        _options = options;
        _selectedProjectId = selectedProjectId;
    }

    public async Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Hard Rule 10: a spec missing a label or a lifetime limit throws here, before any request exists.
        var labels = spec.ToLabels();

        // Create is the one call that carries its own project (spec.ProjectId); the rest use the selected one. They must agree, or a
        // later find, stop or delete would look in a different project from the one that holds the VM.
        if (_selectedProjectId() is not { Length: > 0 } selected || !string.Equals(selected, spec.ProjectId, StringComparison.Ordinal))
        {
            throw new CloudOperationException(
                new CloudError("PROJECT_MISMATCH", null, "The VM spec names a different Google Cloud project from the selected one, so no VM was created."),
                CloudErrorKind.Other);
        }

        // The worker identity comes from the spec (issue #54, filled by the run path, #606/#609); guessing a name nothing creates
        // would boot a VM that cannot reach its bucket.
        if (string.IsNullOrWhiteSpace(spec.ServiceAccountEmail))
        {
            throw new CloudOperationException(
                new CloudError("WORKER_SERVICE_ACCOUNT_MISSING", null, "The VM spec names no worker service account, so no VM was created."),
                CloudErrorKind.Other);
        }

        var instance = ComputeVmShape.Build(spec, zone, labels, spec.ServiceAccountEmail);

        // One id for this call: every replay of the insert POST by the pipeline carries it (see the class remarks).
        var requestId = Guid.NewGuid().ToString();
        var operation = await CallAsync(
            "compute.instances.insert",
            token =>
            {
                var insert = _service.Instances.Insert(instance, spec.ProjectId, zone);
                insert.RequestId = requestId;
                return insert.ExecuteAsync(token);
            },
            cancellationToken).ConfigureAwait(false);
        // Issue #92: a queued (flex-start) insert stays pending until capacity is found, so its poll is bounded by the wait it asked for plus a margin.
        await AwaitOperationAsync(spec.ProjectId, zone, operation, goneIsDone: false, cancellationToken, deadline: spec.FlexStart ? spec.FlexStartCreateBound : null).ConfigureAwait(false);

        // A 404 here means the outcome is unknown, not that nothing was made (the operation said DONE): Network class, so it is
        // retryable, and a retry lands on 409 alreadyExists if the VM exists. The runner lists by label before trying another zone.
        return await GetAsync(spec.ProjectId, spec.VmName, zone, cancellationToken).ConfigureAwait(false)
            ?? throw new CloudOperationException(
                new CloudError("CREATE_OUTCOME_UNKNOWN", null, $"Compute Engine reported the create of '{spec.VmName}' done, but the VM cannot be found."),
                CloudErrorKind.Network);
    }

    /// <summary>
    /// Issue #670: <c>acceleratorTypes.aggregatedList</c> filtered to the accelerator's name, following the page token, answering every zone
    /// that lists it. It asks for partial success: a zone Compute could not read is simply not in the answer (this is advice for the ladder,
    /// which falls back to the requested zones on an empty answer, not a safety decision like the VM listings).
    /// THEORY (unverified, no live project): that the <c>name = "nvidia-l4"</c> filter syntax matches.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        if (!LabelValuePattern.IsMatch(acceleratorType ?? string.Empty))
        {
            return [];
        }

        var zones = new List<string>();
        string? pageToken = null;
        do
        {
            var requestedToken = pageToken;
            var page = await CallAsync(
                "compute.acceleratorTypes.aggregatedList",
                token =>
                {
                    var request = _service.AcceleratorTypes.AggregatedList(project);
                    request.Filter = $"name = \"{acceleratorType}\"";
                    request.PageToken = requestedToken;
                    request.ModifyRequest += message =>
                        message.RequestUri = new Uri(message.RequestUri + (string.IsNullOrEmpty(message.RequestUri!.Query) ? "?" : "&") + "returnPartialSuccess=true");
                    return request.ExecuteAsync(token);
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var (scope, scoped) in page.Items ?? new Dictionary<string, ComputeData.AcceleratorTypesScopedList>())
            {
                if (scoped?.AcceleratorTypes is { Count: > 0 } found && found.Any(a => string.Equals(a.Name, acceleratorType, StringComparison.Ordinal)) && ZoneName(scope) is { Length: > 0 } zone)
                {
                    zones.Add(zone);
                }
            }

            var next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (next is not null && next == pageToken)
            {
                return zones;
            }

            pageToken = next;
        }
        while (pageToken is not null);

        return zones;
    }

    /// <summary>
    /// Issue #94. Order matters: the label rewrite (the claim) comes FIRST, carrying the label fingerprint read at the start, so of two jobs
    /// racing for one VM only one passes it (the loser fails with a precondition error before it has written anything). Only the winner then
    /// replaces the scheduling limits (<c>setScheduling</c> has no fingerprint, so written first a loser could overwrite the winner's
    /// <c>maxRunDuration</c>) and the metadata (fingerprinted), and starts it. A failure after the claim leaves the VM labelled for this job and
    /// stopped: the caller ends it. THEORY (unverified, no live project): that <c>instances.setScheduling</c> accepts <c>maxRunDuration</c> and
    /// <c>instanceTerminationAction</c> on a stopped instance.
    /// </summary>
    public async Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingVmName);
        var reuse = spec with { ExistingVmName = existingVmName };
        var labels = reuse.ToLabels();
        var project = RequireProject();
        if (!string.Equals(project, spec.ProjectId, StringComparison.Ordinal))
        {
            throw new CloudOperationException(
                new CloudError("PROJECT_MISMATCH", null, "The VM spec names a different Google Cloud project from the selected one, so no VM was started."),
                CloudErrorKind.Other);
        }

        ComputeData.Instance instance;
        try
        {
            instance = await CallAsync("compute.instances.get", token => _service.Instances.Get(project, zone, existingVmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            throw new CloudOperationException(new CloudError("VM_NOT_FOUND", 404, $"The VM '{existingVmName}' is gone."), CloudErrorKind.Other);
        }

        if (instance.Status is not ("TERMINATED" or "STOPPED"))
        {
            throw new CloudOperationException(new CloudError("VM_NOT_STOPPED", null, $"The VM '{existingVmName}' is '{instance.Status}', not stopped."), CloudErrorKind.Other);
        }

        // A reuse that cannot name the boot disk must not start: skipping would leave the old job's labels on a billed disk (Hard Rules 9-10).
        var bootDisk = ParseBootDisk(instance)
            ?? throw new CloudOperationException(new CloudError("VM_NO_BOOT_DISK", null, $"The VM '{existingVmName}' lists no boot disk this app can relabel, so it was not reused."), CloudErrorKind.Other);
        var target = labels.ToDictionary(p => p.Key, p => p.Value);

        // The claim: the one write that carries a fingerprint of the labels, so of two jobs racing for this VM only one passes it.
        // A lost ANSWER to our own write, then a retry, meets 412 because the fingerprint moved; if the labels already equal the target
        // the write did land, so it is accepted (a rival's labels differ, so a lost claim still fails).
        var labelBody = new ComputeData.InstancesSetLabelsRequest { Labels = target, LabelFingerprint = instance.LabelFingerprint };
        try
        {
            var claim = await CallAsync("compute.instances.setLabels", token => _service.Instances.SetLabels(labelBody, project, zone, existingVmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
            await AwaitOperationAsync(project, zone, claim, goneIsDone: false, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 412)
        {
            var current = await CallAsync("compute.instances.get", token => _service.Instances.Get(project, zone, existingVmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
            if (!LabelsEqual(current.Labels, target))
            {
                throw;
            }
        }

        // Issue #678: the boot disk keeps the OLD job's labels from its creation; relabel it (a disk's own label fingerprint) so every resource
        // of this job carries its labels (Hard Rules 9-10). A failure here is a start failure: the VM is not started, the caller ends it.
        await RelabelBootDiskAsync(project, bootDisk, target, cancellationToken).ConfigureAwait(false);
        var scheduling = await CallAsync("compute.instances.setScheduling", token => _service.Instances.SetScheduling(ComputeVmShape.Scheduling(reuse), project, zone, existingVmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        await AwaitOperationAsync(project, zone, scheduling, goneIsDone: false, cancellationToken).ConfigureAwait(false);

        var metadataBody = new ComputeData.Metadata { Items = ComputeVmShape.MetadataItems(reuse), Fingerprint = instance.Metadata?.Fingerprint };
        var metadata = await CallAsync("compute.instances.setMetadata", token => _service.Instances.SetMetadata(metadataBody, project, zone, existingVmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        await AwaitOperationAsync(project, zone, metadata, goneIsDone: false, cancellationToken).ConfigureAwait(false);

        var start = await CallAsync("compute.instances.start", token => _service.Instances.Start(project, zone, existingVmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        await AwaitOperationAsync(project, zone, start, goneIsDone: false, cancellationToken).ConfigureAwait(false);

        return await GetAsync(project, existingVmName, zone, cancellationToken).ConfigureAwait(false)
            ?? throw new CloudOperationException(
                new CloudError("START_OUTCOME_UNKNOWN", null, $"Compute Engine reported the start of '{existingVmName}' done, but the VM cannot be found."),
                CloudErrorKind.Network);
    }

    private readonly record struct BootDisk(string Name, string Location, bool Regional);

    /// <summary>
    /// The boot disk named by the instance's <c>source</c> URL: a zonal <c>.../zones/z/disks/name</c> or a regional <c>.../regions/r/disks/name</c>
    /// (a query, a trailing slash and a relative or beta URL are tolerated), or null when the instance lists no boot disk or the URL names no disk.
    /// </summary>
    private static BootDisk? ParseBootDisk(ComputeData.Instance instance)
    {
        var source = instance.Disks?.FirstOrDefault(d => d.Boot == true)?.Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var cut = source.IndexOfAny(['?', '#']);
        var segments = (cut < 0 ? source : source[..cut]).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 2; i >= 2; i--)
        {
            if (segments[i] == "disks" && segments[i - 2] is "zones" or "regions")
            {
                return new BootDisk(segments[i + 1], segments[i - 1], segments[i - 2] == "regions");
            }
        }

        return null;
    }

    private async Task RelabelBootDiskAsync(string project, BootDisk disk, Dictionary<string, string> target, CancellationToken cancellationToken)
    {
        string? fingerprint;
        if (disk.Regional)
        {
            var current = await CallAsync("compute.regionDisks.get", token => _service.RegionDisks.Get(project, disk.Location, disk.Name).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
            fingerprint = current.LabelFingerprint;
        }
        else
        {
            var current = await CallAsync("compute.disks.get", token => _service.Disks.Get(project, disk.Location, disk.Name).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
            fingerprint = current.LabelFingerprint;
        }

        try
        {
            ComputeData.Operation relabel;
            if (disk.Regional)
            {
                var body = new ComputeData.RegionSetLabelsRequest { Labels = target, LabelFingerprint = fingerprint };
                relabel = await CallAsync("compute.regionDisks.setLabels", token => _service.RegionDisks.SetLabels(body, project, disk.Location, disk.Name).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var body = new ComputeData.ZoneSetLabelsRequest { Labels = target, LabelFingerprint = fingerprint };
                relabel = await CallAsync("compute.disks.setLabels", token => _service.Disks.SetLabels(body, project, disk.Location, disk.Name).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
            }

            await AwaitOperationAsync(project, disk.Location, relabel, goneIsDone: false, cancellationToken, regional: disk.Regional).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 412)
        {
            // A lost answer, then a retry: the fingerprint moved because the write landed. Accept only when the labels already equal the target.
            IDictionary<string, string>? now = disk.Regional
                ? (await CallAsync("compute.regionDisks.get", token => _service.RegionDisks.Get(project, disk.Location, disk.Name).ExecuteAsync(token), cancellationToken).ConfigureAwait(false)).Labels
                : (await CallAsync("compute.disks.get", token => _service.Disks.Get(project, disk.Location, disk.Name).ExecuteAsync(token), cancellationToken).ConfigureAwait(false)).Labels;
            if (!LabelsEqual(now, target))
            {
                throw;
            }
        }
    }

    private static bool LabelsEqual(IDictionary<string, string>? actual, IReadOnlyDictionary<string, string> target)
        => actual is not null && actual.Count == target.Count && target.All(p => actual.TryGetValue(p.Key, out var value) && string.Equals(value, p.Value, StringComparison.Ordinal));

    /// <summary>
    /// Issue #105: <c>instances.start</c> on the VM as it is, polled to <c>DONE</c>. No label, scheduling or metadata write (that is
    /// <see cref="ReuseVmAsync"/>). A 404 is <c>VM_NOT_FOUND</c>, never a silent success: the caller asked for a VM to run.
    /// </summary>
    public async Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        ComputeData.Operation operation;
        try
        {
            operation = await CallAsync("compute.instances.start", token => _service.Instances.Start(project, zone, vmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            throw new CloudOperationException(new CloudError("VM_NOT_FOUND", 404, $"The VM '{vmName}' is gone."), CloudErrorKind.Other);
        }

        await AwaitOperationAsync(project, zone, operation, goneIsDone: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue #105: <c>disks.aggregatedList</c> filtered on the app label and this installation's label, following the page token, asking for partial
    /// success so an unreachable zone is REPORTED (it fails the whole listing, like the VM listings: a short list must never read as "no disks").
    /// The server filter is the query; every disk is checked again here, so one without both labels can never be returned. Regional disks list under
    /// <c>regions/..</c>, and their scope name is returned as the zone.
    /// </summary>
    public async Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        if (!LabelValuePattern.IsMatch(installationId ?? string.Empty))
        {
            return [];
        }

        var filter = $"(labels.app = \"{VmSpec.AppLabelValue}\") AND (labels.installation-id = \"{installationId}\")";
        var found = new List<DiskDescriptor>();
        string? pageToken = null;
        do
        {
            var requestedToken = pageToken;
            var page = await CallAsync(
                "compute.disks.aggregatedList",
                token =>
                {
                    var request = _service.Disks.AggregatedList(project);
                    request.Filter = filter;
                    request.PageToken = requestedToken;
                    request.ModifyRequest += message =>
                        message.RequestUri = new Uri(message.RequestUri + (string.IsNullOrEmpty(message.RequestUri!.Query) ? "?" : "&") + "returnPartialSuccess=true");
                    return request.ExecuteAsync(token);
                },
                cancellationToken).ConfigureAwait(false);

            if (page.Unreachables is { Count: > 0 } unreachable)
            {
                throw new CloudOperationException(
                    new CloudError("ZONES_UNREACHABLE", null, $"Compute Engine could not list these places: {string.Join(", ", unreachable.Select(u => ZoneName(u) ?? u))}."),
                    CloudErrorKind.Network);
            }

            foreach (var (scope, scoped) in page.Items ?? new Dictionary<string, ComputeData.DisksScopedList>())
            {
                foreach (var disk in scoped?.Disks ?? [])
                {
                    if (disk.Labels is { } labels
                        && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
                        && labels.TryGetValue("installation-id", out var owner) && owner == installationId)
                    {
                        found.Add(new DiskDescriptor(
                            disk.Name,
                            ZoneName(disk.Zone ?? disk.Region) ?? ZoneName(scope) ?? string.Empty,
                            (int)Math.Min(disk.SizeGb ?? 0, int.MaxValue),
                            disk.Status ?? string.Empty,
                            new Dictionary<string, string>(labels),
                            ParseTime(disk.CreationTimestamp),
                            (disk.Users ?? []).Select(u => ZoneName(u) ?? u).ToList()));
                    }
                }
            }

            var next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (next is not null && next == pageToken)
            {
                throw new CloudOperationException(
                    new CloudError(CloudErrorCodes.ListPaginationStuck, null, "Compute Engine returned the same page token twice, so the disk list may be incomplete."),
                    CloudErrorKind.Network);
            }

            pageToken = next;
        }
        while (pageToken is not null);

        return found;
    }

    /// <summary>
    /// Issue #105: <c>disks.delete</c>, polled to <c>DONE</c>. <paramref name="zone"/> is the disk's scope as the listing returned it: a zone (<c>us-central1-a</c>) or,
    /// for a regional disk, a region (<c>us-central1</c>), which goes to <c>regionDisks.delete</c> (a zonal delete of a regional disk is a 404, and a 404 here
    /// means "already gone"). Already gone is a success; a disk still attached is the platform's own 400, passed through.
    /// </summary>
    public async Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var regional = !IsZoneName(zone);
        ComputeData.Operation operation;
        try
        {
            operation = regional
                ? await CallAsync("compute.regionDisks.delete", token => _service.RegionDisks.Delete(project, zone, diskName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false)
                : await CallAsync("compute.disks.delete", token => _service.Disks.Delete(project, zone, diskName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            return;
        }

        await AwaitOperationAsync(project, zone, operation, goneIsDone: true, cancellationToken, regional).ConfigureAwait(false);
    }

    /// <summary>A zone name ends in one letter after the region (<c>us-central1-a</c>); a region (<c>us-central1</c>) does not.</summary>
    internal static bool IsZoneName(string location) => System.Text.RegularExpressions.Regex.IsMatch(location ?? string.Empty, "-[a-z]$");

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => GetAsync(RequireProject(), vmName, zone, cancellationToken);

    /// <summary>
    /// Issue #91: <c>zoneOperations.list</c> filtered on the operation TYPE only, then matched on the client to this VM's incarnation: by the instance's numeric id
    /// (<c>targetId</c>, unique per incarnation and form-free) when the run saw the VM alive, else by the target's name and an operation no older than the VM's creation.
    /// With neither an id nor a creation time nothing bounds the question and the answer is false. No server filter names the target, so a difference in how Compute spells
    /// the target link can never hide a real preemption. THEORY (unverified, no live project): that a preempted instance records an operation of type
    /// <c>compute.instances.preempted</c> with <c>targetId</c> set to the instance id, and that the type filter syntax matches (ToTest row #91).
    /// </summary>
    public async Task<bool> WasPreemptedAsync(string vmName, string zone, VmIncarnation incarnation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incarnation);
        if (incarnation.InstanceId is null && incarnation.CreatedAt is null)
        {
            return false;
        }

        var project = RequireProject();
        var targetSuffix = $"/zones/{zone}/instances/{vmName}";
        string? pageToken = null;
        for (var page = 0; page < 5; page++)
        {
            var requestedToken = pageToken;
            var operations = await CallAsync(
                "compute.zoneOperations.list",
                token =>
                {
                    var request = _service.ZoneOperations.List(project, zone);
                    request.Filter = $"operationType = \"{PreemptedOperationType}\"";
                    request.PageToken = requestedToken;
                    return request.ExecuteAsync(token);
                },
                cancellationToken).ConfigureAwait(false);

            if (operations.Items?.Any(o => IsPreemptionOfIncarnation(o, targetSuffix, incarnation)) == true)
            {
                return true;
            }

            pageToken = string.IsNullOrEmpty(operations.NextPageToken) ? null : operations.NextPageToken;
            if (pageToken is null)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsPreemptionOfIncarnation(ComputeData.Operation operation, string targetSuffix, VmIncarnation incarnation)
    {
        if (!string.Equals(operation.OperationType, PreemptedOperationType, StringComparison.Ordinal))
        {
            return false;
        }

        if (incarnation.InstanceId is { } id)
        {
            return operation.TargetId == id;
        }

        return operation.TargetLink?.EndsWith(targetSuffix, StringComparison.Ordinal) == true
            && ParseTime(operation.InsertTime) is { } inserted
            && incarnation.CreatedAt is { } created
            && inserted >= created;
    }

    private const string PreemptedOperationType = "compute.instances.preempted";

    public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => EndVmAsync(vmName, zone, delete: false, onAccepted: null, cancellationToken);

    public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => EndVmAsync(vmName, zone, delete: true, onAccepted: null, cancellationToken);

    /// <summary>
    /// Issue #113: <c>instances.stop</c> or <c>instances.delete</c> in two steps the caller can tell apart: the request (the operation insert), after which
    /// <paramref name="onAccepted"/> is called, and the wait for the operation to reach <c>DONE</c>. A VM that is already gone (404 on the insert) is accepted and done.
    /// </summary>
    public async Task EndVmAsync(string vmName, string zone, bool delete, Action? onAccepted, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        ComputeData.Operation operation;
        try
        {
            operation = delete
                ? await CallAsync("compute.instances.delete", token => _service.Instances.Delete(project, zone, vmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false)
                : await CallAsync("compute.instances.stop", token => _service.Instances.Stop(project, zone, vmName).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            onAccepted?.Invoke();
            return; // Already gone: nothing left to stop, and a delete wants exactly this.
        }

        onAccepted?.Invoke();
        await AwaitOperationAsync(project, zone, operation, goneIsDone: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
        => ListByLabelAsync("job-id", jobId, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken)
        => ListByLabelAsync("installation-id", installationId, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken)
        => ListByLabelAsync(null, null, cancellationToken);

    /// <summary>The app label is always part of the query; with <paramref name="labelKey"/> null it is the whole query (every VM of ours, issue #60).</summary>
    private async Task<IReadOnlyList<VmDescriptor>> ListByLabelAsync(string? labelKey, string? labelValue, CancellationToken cancellationToken)
    {
        var project = RequireProject();

        // A value no label can hold matches no VM, and it must never reach a filter expression.
        if (labelKey is not null && !LabelValuePattern.IsMatch(labelValue ?? string.Empty))
        {
            return [];
        }

        var filter = labelKey is null
            ? $"labels.app = \"{VmSpec.AppLabelValue}\""
            : $"(labels.app = \"{VmSpec.AppLabelValue}\") AND (labels.{labelKey} = \"{labelValue}\")";
        var found = new List<VmDescriptor>();
        string? pageToken = null;
        do
        {
            var requestedToken = pageToken;
            var page = await CallAsync(
                "compute.instances.aggregatedList",
                token =>
                {
                    var request = _service.Instances.AggregatedList(project);
                    request.Filter = filter;
                    request.PageToken = requestedToken;
                    // Without this Compute fails the whole list when one zone is down and never fills `unreachables`. Google.Apis.Compute.v1
                    // has no typed property for it on this call, so it is added to the query by hand.
                    request.ModifyRequest += message =>
                        message.RequestUri = new Uri(message.RequestUri + (string.IsNullOrEmpty(message.RequestUri!.Query) ? "?" : "&") + "returnPartialSuccess=true");
                    return request.ExecuteAsync(token);
                },
                cancellationToken).ConfigureAwait(false);

            // A zone Compute could not reach is NOT an empty zone: returning the rest would let VmTerminator read "no VM" and the runner
            // create a duplicate (or leak one). Fail the whole listing with a retryable (Network-class) error instead.
            if (page.Unreachables is { Count: > 0 } unreachable)
            {
                throw new CloudOperationException(
                    new CloudError("ZONES_UNREACHABLE", null, $"Compute Engine could not list these places: {string.Join(", ", unreachable.Select(u => ZoneName(u) ?? u))}."),
                    CloudErrorKind.Network);
            }

            foreach (var (scope, scoped) in page.Items ?? new Dictionary<string, ComputeData.InstancesScopedList>())
            {
                foreach (var instance in scoped?.Instances ?? [])
                {
                    // The server filter is the query; this is the guard that a VM of another job, installation or app can never be returned.
                    if (instance.Labels is { } labels
                        && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
                        && (labelKey is null || (labels.TryGetValue(labelKey, out var value) && value == labelValue)))
                    {
                        found.Add(ToDescriptor(instance, ZoneName(scope) ?? string.Empty));
                    }
                }
            }

            var next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (next is not null && next == pageToken)
            {
                throw new CloudOperationException(
                    new CloudError(CloudErrorCodes.ListPaginationStuck, null, "Compute Engine returned the same page token twice, so the VM list may be incomplete."),
                    CloudErrorKind.Network);
            }

            pageToken = next;
        }
        while (pageToken is not null);

        return found;
    }

    private async Task<VmDescriptor?> GetAsync(string project, string vmName, string zone, CancellationToken cancellationToken)
    {
        try
        {
            // Issue #691: read the raw answer so its Date header (the platform's own clock) can ride on the descriptor.
            (ComputeData.Instance instance, DateTimeOffset? serverNow) = await CallAsync(
                "compute.instances.get",
                async token =>
                {
                    using var message = _service.Instances.Get(project, zone, vmName).CreateRequest();
                    using var response = await _service.HttpClient.SendAsync(message, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        // ExecuteAsync would have thrown this itself; the raw path must raise the same exception so the classification is unchanged. It is the ONLY
                        // hand-rolled request (ExecuteAsync exposes no response headers), so there is no shared helper to drift from: the parity tests in
                        // GoogleComputeGatewayTests compare its exceptions with the ExecuteAsync path's for each error shape.
                        var error = await _service.DeserializeError(response).ConfigureAwait(false);
                        throw new GoogleApiException(_service.Name, error?.Message) { Error = error, HttpStatusCode = response.StatusCode };
                    }

                    var read = await _service.DeserializeResponse<ComputeData.Instance>(response).ConfigureAwait(false);
                    // A cached or proxied answer replays an old Date (its Age says how old): that is not the platform's time now, so it is dropped.
                    // Precision of the rest: see PlatformClock.Read (one-second resolution, a round-trip behind).
                    return (read, response.Headers.Age is { } age && age > TimeSpan.Zero ? null : response.Headers.Date);
                },
                cancellationToken).ConfigureAwait(false);
            return ToDescriptor(instance, zone) with { ServerNow = serverNow };
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            return null;
        }
    }

    /// <summary>Polls the zone operation until <c>DONE</c> and throws its error. <paramref name="goneIsDone"/>: a stop or delete whose operation says the VM is not found has nothing left to do.</summary>
    private async Task AwaitOperationAsync(string project, string zone, ComputeData.Operation operation, bool goneIsDone, CancellationToken cancellationToken, bool regional = false, TimeSpan? deadline = null)
    {
        if (!IsDone(operation))
        {
            var name = operation.Name;
            var latest = operation;
            var outcome = await OperationPoller.PollAsync(
                async token =>
                {
                    latest = regional
                        ? await CallAsync("compute.regionOperations.get", pollToken => _service.RegionOperations.Get(project, zone, name).ExecuteAsync(pollToken), token).ConfigureAwait(false)
                        : await CallAsync("compute.zoneOperations.get", pollToken => _service.ZoneOperations.Get(project, zone, name).ExecuteAsync(pollToken), token).ConfigureAwait(false);
                    return new OperationPoll<bool>(IsDone(latest), true, null);
                },
                deadline ?? _options.OperationDeadline,
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

            // Every entry is classified and the most specific wins (a generic first entry must not hide a stockout behind it).
            CloudError? chosen = null;
            var chosenKind = CloudErrorKind.Other;
            var chosenRank = int.MaxValue;
            foreach (var entry in errors)
            {
                var candidate = new CloudError(entry.Code, operation.HttpErrorStatusCode, entry.Message ?? operation.HttpErrorMessage ?? string.Empty);
                var kind = CloudErrorClassifier.Classify(candidate);
                var rank = Array.IndexOf(KindPreference, kind);
                if (chosen is null || rank < chosenRank)
                {
                    (chosen, chosenKind, chosenRank) = (candidate, kind, rank);
                }
            }

            throw new CloudOperationException(chosen!, chosenKind);
        }
    }

    private static bool IsDone(ComputeData.Operation operation) => string.Equals(operation.Status, "DONE", StringComparison.Ordinal);

    /// <summary>Runs ONE HTTP request through the resilience pipeline (retry, token refresh, breaker), turning a Google HTTP error into the one exception the app understands.</summary>
    private Task<T> CallAsync<T>(string operation, Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            operation,
            async token =>
            {
                try
                {
                    return await request(token).ConfigureAwait(false);
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken);

    private string RequireProject()
        => _selectedProjectId() is { Length: > 0 } project
            ? project
            : throw new InvalidOperationException("No Google Cloud project is selected, so there is no project to ask Compute Engine about.");

    private static VmDescriptor ToDescriptor(ComputeData.Instance instance, string zone)
        => new(
            instance.Name,
            ZoneName(instance.Zone) ?? zone,
            instance.Status ?? string.Empty,
            instance.StatusMessage,
            ParseTime(instance.CreationTimestamp),
            instance.Labels is null ? null : new Dictionary<string, string>(instance.Labels),
            ParseTime(instance.LastStopTimestamp),
            ZoneName(instance.MachineType),
            ParseTime(instance.LastStartTimestamp),
            InstanceId: instance.Id,
            MaxRunDuration: instance.Scheduling?.MaxRunDuration?.Seconds is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null);

    /// <summary>The zone name from a zone URL (<c>.../zones/us-central1-a</c>) or an aggregated-list key (<c>zones/us-central1-a</c>).</summary>
    private static string? ZoneName(string? url)
        => string.IsNullOrEmpty(url) ? null : url[(url.LastIndexOf('/') + 1)..];

    private static DateTimeOffset? ParseTime(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
}

/// <summary>
/// Issue #92: the instance resource plus the <c>params</c> field. MEASURED 2026-10-16: <c>Google.Apis.Compute.v1</c> 1.77.0.4282 declares
/// <c>InstanceParams</c> (with <c>RequestValidForDuration</c>, "relative deadline for waiting for capacity") but its <c>Instance</c> class has no <c>Params</c>
/// property, so the field is added here and serialized by name. THEORY (unverified, no live project): the v1 insert accepts <c>params.requestValidForDuration</c>.
/// </summary>
internal sealed class ComputeInstanceBody : ComputeData.Instance
{
    [Newtonsoft.Json.JsonProperty("params", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public ComputeData.InstanceParams? Params { get; set; }
}

/// <summary>
/// The shape of the VM a <see cref="VmSpec"/> becomes: the parts <see cref="VmSpec"/> does not carry (boot image, disk, network)
/// are fixed here, in one place. ONE image family for every machine type: <c>worker/vm/startup.sh</c> needs docker, python3,
/// curl and a writable root, which the deep learning VM image ships and Container-Optimized OS does not (DECISION, agent-made,
/// reversible: see the issue linked from docs/cloud_design.md). THEORY (unverified, no live project): that
/// <c>scheduling.maxRunDuration</c> is accepted on a standard-provisioning VM with <c>onHostMaintenance=TERMINATE</c> and
/// <c>automaticRestart=false</c>, and that this image family boots and runs docker on a machine with no GPU.
/// docs/ToTest.md carries the row that proves them on a real VM.
/// </summary>
internal static class ComputeVmShape
{
    public const string Image = "projects/deeplearning-platform-release/global/images/family/pytorch-2-9-cu129-ubuntu-2404-nvidia-580";

    public const int BootDiskGb = 150;

    /// <summary>The metadata items a VM for this spec carries: the spec's own plus the project-wide-SSH block (used by create and by reuse).</summary>
    public static List<ComputeData.Metadata.ItemsData> MetadataItems(VmSpec spec)
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

        return items;
    }

    /// <summary>The lifetime limits every VM carries (Hard Rule 10), for create and for reuse.</summary>
    public static ComputeData.Scheduling Scheduling(VmSpec spec) => new()
    {
        MaxRunDuration = new ComputeData.Duration { Seconds = (long)Math.Ceiling(spec.MaxRunDuration.TotalSeconds) },
        InstanceTerminationAction = spec.TerminationAction,
        OnHostMaintenance = "TERMINATE",
        AutomaticRestart = false,
        // Issue #91. Null for an on-demand VM (Compute's default, STANDARD). THEORY (unverified, no live project): SPOT is accepted together with
        // maxRunDuration and instanceTerminationAction=DELETE on a GPU VM with onHostMaintenance=TERMINATE; docs/ToTest.md carries the row.
        ProvisioningModel = spec.FlexStart ? "FLEX_START" : spec.Spot ? "SPOT" : null,
    };

    public static ComputeInstanceBody Build(VmSpec spec, string zone, IReadOnlyDictionary<string, string> labels, string serviceAccountEmail)
    {
        return new ComputeInstanceBody
        {
            Name = spec.VmName,
            MachineType = $"zones/{zone}/machineTypes/{spec.MachineType}",
            Labels = labels.ToDictionary(p => p.Key, p => p.Value),
            Metadata = new ComputeData.Metadata { Items = MetadataItems(spec) },
            Scheduling = Scheduling(spec),
            // Issue #92: the relative deadline for waiting for capacity (Compute's own doc for this field: "Relevant only for Instances.Insert API").
            // THEORY (unverified, no live project): FLEX_START with this field, maxRunDuration, DELETE and onHostMaintenance=TERMINATE is accepted for a GPU VM; docs/ToTest.md carries the row.
            Params = spec.FlexStart && spec.FlexStartWait is { } wait
                ? new ComputeData.InstanceParams { RequestValidForDuration = new ComputeData.Duration { Seconds = (long)Math.Ceiling(wait.TotalSeconds) } }
                : null,
            Disks =
            [
                new ComputeData.AttachedDisk
                {
                    Boot = true,
                    AutoDelete = true,
                    InitializeParams = new ComputeData.AttachedDiskInitializeParams
                    {
                        SourceImage = Image,
                        DiskSizeGb = BootDiskGb,
                        DiskType = $"zones/{zone}/diskTypes/pd-balanced",
                        Labels = labels.ToDictionary(p => p.Key, p => p.Value),
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
                    Email = serviceAccountEmail,
                    Scopes = ["https://www.googleapis.com/auth/cloud-platform"],
                },
            ],
        };
    }
}
