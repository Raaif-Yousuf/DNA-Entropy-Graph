using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud.Audit;

/// <summary>Runs one mutating gateway call and records it: time, installation, operation, resource, ids, outcome class and duration. Reads are never passed here.</summary>
public sealed partial class CloudAuditor(ICloudAuditLog log, Func<string?> installationId, TimeProvider time)
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();

    /// <summary>The job id inside a VM name the app made (<c>deg-&lt;job id&gt;</c>), or null for any other name.</summary>
    public static string? JobIdFromVmName(string? vmName)
        => vmName is not null && vmName.StartsWith(WorkerIdentityNames.VmNamePrefix, StringComparison.Ordinal) && SafeId().IsMatch(vmName[WorkerIdentityNames.VmNamePrefix.Length..])
            ? vmName[WorkerIdentityNames.VmNamePrefix.Length..]
            : null;

    /// <summary>The first two segments of an object key when it is under <c>jobs/&lt;id&gt;/</c>, which is all the audit ever names (a file name below it can be the user's).</summary>
    public static (string Prefix, string? JobId) ObjectPrefix(string objectKey)
    {
        var parts = (objectKey ?? string.Empty).Split('/');
        if (parts.Length >= 3 && parts[0] == "jobs" && SafeId().IsMatch(parts[1]))
        {
            return ("jobs/" + parts[1], parts[1]);
        }

        return ("(other)", null);
    }

    public async Task<T> RunAsync<T>(string operation, string resource, string? jobId, Func<Task<T>> call, Func<T, string?>? resourceFrom = null)
    {
        var start = time.GetTimestamp();
        using var scope = CloudAuditContext.Begin();
        try
        {
            var result = await call().ConfigureAwait(false);
            Record(operation, resourceFrom?.Invoke(result) ?? resource, jobId, scope.Holder, "ok", null, start);
            return result;
        }
        catch (Exception ex)
        {
            var (outcome, code) = ex switch
            {
                CloudOperationException cloud => (Name(cloud.Kind), cloud.Error.Code),
                OperationCanceledException => ("cancelled", null),
                _ => ("other", null),
            };
            Record(operation, resource, jobId, scope.Holder, outcome, code, start);
            throw;
        }
    }

    public async Task RunAsync(string operation, string resource, string? jobId, Func<Task> call)
        => await RunAsync<bool>(operation, resource, jobId, async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    public static string Name(CloudErrorKind kind) => kind switch
    {
        CloudErrorKind.Billing => "billing",
        CloudErrorKind.ApiDisabled => "api_disabled",
        CloudErrorKind.Quota => "quota",
        CloudErrorKind.Stockout => "stockout",
        CloudErrorKind.AlreadyExists => "already_exists",
        CloudErrorKind.Permission => "permission",
        CloudErrorKind.OrgPolicy => "org_policy",
        CloudErrorKind.Network => "network",
        _ => "other",
    };

    private void Record(string operation, string resource, string? jobId, CloudAuditContext.Holder ids, string outcome, string? code, long start)
    {
        try
        {
            string? installation = null;
            try
            {
                installation = installationId();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An unusable installation id file loses that field, not the line.
            }

            log.Record(new CloudAuditEntry(
                time.GetUtcNow(),
                installation,
                jobId,
                operation,
                resource,
                ids.RequestId,
                ids.OperationId,
                outcome,
                code,
                (long)time.GetElapsedTime(start).TotalMilliseconds));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The audit log is evidence, never a dependency of the run.
        }
    }
}
