using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.ServiceUsage.v1;
using UsageData = Google.Apis.ServiceUsage.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IServiceEnablementGateway"/> over Service Usage v1 REST (issue #52). <c>services:batchEnable</c>
/// returns an operation, polled every 5 seconds until the deadline (5 minutes in production); once it reports done,
/// <c>services.get</c> is asked every 5 seconds until each service reads ENABLED, because a finished operation is not
/// the same as a service that is ready. No retry of its own: it is wrapped by the resilience pipeline.
/// </summary>
internal sealed class GoogleServiceUsageGateway : IServiceEnablementGateway
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ServiceUsageService _service;
    private readonly GoogleCloudOptions _options;

    public GoogleServiceUsageGateway(ServiceUsageService service, GoogleCloudOptions options)
    {
        _service = service;
        _options = options;
    }

    public async Task<bool> IsServiceEnabledAsync(string projectId, string serviceId, CancellationToken cancellationToken)
    {
        try
        {
            var service = await _service.Services.Get($"projects/{projectId}/services/{serviceId}").ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(service.State, "ENABLED", StringComparison.Ordinal);
        }
        catch (GoogleApiException ex)
        {
            throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
        }
    }

    public async Task EnableServicesAsync(string projectId, IReadOnlyList<string> serviceIds, CancellationToken cancellationToken)
    {
        UsageData.Operation operation;
        try
        {
            var body = new UsageData.BatchEnableServicesRequest { ServiceIds = serviceIds.ToList() };
            operation = await _service.Services.BatchEnable(body, "projects/" + projectId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw ToEnableException(GoogleApiErrors.FromApiException(ex));
        }

        var finished = await OperationPoller.PollAsync(
            token => PollOperationAsync(operation, token),
            _options.OperationDeadline,
            cancellationToken,
            _options.Delay,
            PollInterval).ConfigureAwait(false);
        ThrowIfFailed(finished);

        var ready = await OperationPoller.PollAsync(
            token => PollServicesAsync(projectId, serviceIds, token),
            _options.OperationDeadline,
            cancellationToken,
            _options.Delay,
            PollInterval).ConfigureAwait(false);
        ThrowIfFailed(ready);
    }

    private static void ThrowIfFailed<T>(OperationOutcome<T> outcome)
    {
        if (!outcome.Success)
        {
            throw new CloudOperationException(outcome.Error!, CloudErrorClassifier.Classify(outcome.Error!));
        }
    }

    private async Task<OperationPoll<bool>> PollOperationAsync(UsageData.Operation operation, CancellationToken cancellationToken)
    {
        // The first look is at the operation batchEnable returned; later looks ask Google for it again.
        if (operation.Done != true)
        {
            try
            {
                operation = await _service.Operations.Get(operation.Name).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
            }
        }

        if (operation.Done != true)
        {
            return new OperationPoll<bool>(false, false, null);
        }

        if (operation.Error is { } error)
        {
            throw ToEnableException(GoogleApiErrors.FromOperationError(error.Code, error.Message, error.Details));
        }

        return new OperationPoll<bool>(true, true, null);
    }

    private async Task<OperationPoll<bool>> PollServicesAsync(string projectId, IReadOnlyList<string> serviceIds, CancellationToken cancellationToken)
    {
        foreach (var serviceId in serviceIds)
        {
            if (!await IsServiceEnabledAsync(projectId, serviceId, cancellationToken).ConfigureAwait(false))
            {
                return new OperationPoll<bool>(false, false, null);
            }
        }

        return new OperationPoll<bool>(true, true, null);
    }

    /// <summary>
    /// A 403 while enabling means the user may use the project but not change it: not an Owner. Decided on the status
    /// (a gRPC PERMISSION_DENIED in an operation maps to 403 too), because the classifier would read a message that
    /// mentions "service" or "billing" as something else. THEORY (unverified, no live project): that Service Usage
    /// answers a non-Owner with 403 on <c>services:batchEnable</c> itself, not only inside the operation.
    /// </summary>
    private static CloudOperationException ToEnableException(RpcStatus status)
        => status.HttpStatus == 403
            ? new CloudOperationException(new CloudError(SetupErrorCodes.NotProjectOwner, 403, status.Message), CloudErrorKind.Permission)
            : GoogleApiErrors.ToException(status);
}
