using DnaEntropyGraph.Core.Cloud;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace DnaEntropyGraph.Cloud;

/// <summary>
/// The one resilience policy every gateway call goes through (issue #258,
/// docs/cloud_design.md section 12):
/// <list type="bullet">
/// <item>Transient failures (HTTP 500/502/503/504, HTTP 429 that is not a
/// quota, a transport failure) retry with exponential backoff and jitter,
/// then give up as <see cref="CloudErrorKind.Network"/>.</item>
/// <item>HTTP 401 refreshes the token once and replays the call; a second 401
/// is <see cref="CloudErrorKind.Permission"/> (sign in again).</item>
/// <item>A circuit breaker opens when most recent calls failed, so a dead
/// network fails fast and <see cref="IsOffline"/> can drive an offline banner.</item>
/// </list>
/// Everything else (403, 409, 412, quota, stockout) passes through untouched:
/// <see cref="CloudErrorClassifier"/> and the zone ladder own those. Retrying
/// a mutating call is safe here because creates carry a deterministic request
/// id (issue #257) and stop/delete are idempotent.
/// </summary>
public sealed class CloudCallPipeline
{
    private readonly ICloudTokenRefresher _refresher;
    private readonly ICloudCallObserver _observer;
    private readonly ResiliencePipeline _pipeline;
    private readonly ResiliencePipeline _cleanupPipeline;
    private volatile bool _offline;

    public CloudCallPipeline(CloudRetryOptions options, ICloudTokenRefresher refresher, ICloudCallObserver observer)
    {
        ArgumentNullException.ThrowIfNull(options);
        _refresher = refresher;
        _observer = observer;

        _pipeline = BuildPipeline(options, withBreaker: true);

        // Cleanup (cancel, stop, delete, find-by-job) must still reach Google
        // while the breaker is open: it is what stops the meter. Same retry
        // policy, no breaker, and its failures are not counted by the one above.
        _cleanupPipeline = BuildPipeline(options, withBreaker: false);
    }

    private ResiliencePipeline BuildPipeline(CloudRetryOptions options, bool withBreaker)
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = options.TimeProvider };
        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = options.BaseDelay,
                MaxDelay = options.MaxDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args => new ValueTask<bool>(args.Outcome.Exception is { } e && IsTransient(e, args.Context.CancellationToken)),
                OnRetry = args =>
                {
                    var (status, kind, message) = Describe(args.Outcome.Exception);
                    _observer.OnRetry(new CloudRetryEvent(
                        args.Context.OperationKey ?? "cloud call", args.AttemptNumber + 1, args.RetryDelay, status, kind, message));
                    return default;
                },
            });
        }

        if (withBreaker)
        {
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.BreakerFailureRatio,
                MinimumThroughput = options.BreakerMinimumThroughput,
                SamplingDuration = options.BreakerSamplingDuration,
                BreakDuration = options.BreakDuration,
                ShouldHandle = args => new ValueTask<bool>(args.Outcome.Exception is { } e && IsTransient(e, args.Context.CancellationToken)),
                OnOpened = _ =>
                {
                    SetOffline(true);
                    return default;
                },
                OnClosed = _ =>
                {
                    SetOffline(false);
                    return default;
                },
            });
        }

        return builder.Build();
    }

    /// <summary>True while the breaker is open: recent calls mostly failed, so new calls fail fast.</summary>
    public bool IsOffline => _offline;

    public event EventHandler? OfflineChanged;

    /// <param name="operation">A stable name for the call, e.g. <c>Compute.CreateVm</c>, shown in the retry log.</param>
    /// <param name="action">The gateway call.</param>
    /// <param name="cancellationToken">Cancels the call and any wait between retries.</param>
    /// <param name="bypassBreaker">True for cleanup that must still reach Google while the breaker is open (cancel, stop, delete, find-by-job): same retries, no breaker.</param>
    /// <param name="replayable">False when the call consumes something that cannot be replayed (a non-seekable upload stream): it then gets neither retries nor a 401 replay.</param>
    public async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken, bool replayable = true, bool bypassBreaker = false)
    {
        try
        {
            return await RunAsync(operation, action, cancellationToken, replayable, bypassBreaker).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 401 && replayable)
        {
            await _refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _observer.OnTokenRefreshed(operation);
            try
            {
                return await RunAsync(operation, action, cancellationToken, replayable, bypassBreaker).ConfigureAwait(false);
            }
            catch (CloudOperationException again) when (again.Error.HttpStatus == 401)
            {
                throw new CloudOperationException(again.Error, CloudErrorKind.Permission);
            }
        }
    }

    public async Task ExecuteAsync(string operation, Func<CancellationToken, Task> action, CancellationToken cancellationToken, bool replayable = true, bool bypassBreaker = false)
        => await ExecuteAsync<bool>(
            operation,
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            replayable,
            bypassBreaker).ConfigureAwait(false);

    private async Task<T> RunAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken, bool replayable, bool bypassBreaker)
    {
        if (!replayable)
        {
            return await TranslateAsync(() => action(cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var pipeline = bypassBreaker ? _cleanupPipeline : _pipeline;

        var context = ResilienceContextPool.Shared.Get(operation, cancellationToken);
        try
        {
            return await TranslateAsync(
                async () => await pipeline.ExecuteAsync(async ctx => await action(ctx.CancellationToken).ConfigureAwait(false), context).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private static async Task<T> TranslateAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (BrokenCircuitException)
        {
            // A diagnostic, not user text: the UI shows the resource string
            // for the run's "network" error code (Hard Rule 13).
            throw new CloudOperationException(new CloudError("OFFLINE", null, "circuit open"), CloudErrorKind.Network);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A deadline (HttpClient timeout, gRPC DEADLINE_EXCEEDED), not the
            // caller cancelling: the service is not answering.
            throw new CloudOperationException(new CloudError("TIMEOUT", null, "request timed out"), CloudErrorKind.Network);
        }
        catch (CloudOperationException ex) when (IsTransient(ex, cancellationToken))
        {
            // Retries exhausted: whatever the HTTP status was, the problem
            // for the caller is "the service is not reachable right now".
            throw new CloudOperationException(ex.Error, CloudErrorKind.Network);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudOperationException(new CloudError(null, null, ex.Message), CloudErrorKind.Network);
        }
    }

    /// <summary>
    /// The transient test shared by retry and breaker. Classify first: a
    /// stockout, quota, billing, API-off, permission, org-policy or
    /// already-exists error is permanent for this call whatever its HTTP
    /// status (Compute reports stockouts as 503 with
    /// <c>ZONE_RESOURCE_POOL_EXHAUSTED</c>). Retrying one only delays the zone
    /// ladder, and counting it would trip the breaker after a few zones and
    /// make a stockout read as "offline".
    /// </summary>
    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is HttpRequestException)
        {
            return true;
        }

        if (exception is OperationCanceledException)
        {
            // A timeout, unless the caller is the one cancelling.
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is not CloudOperationException cloud)
        {
            return false;
        }

        // A spent poll deadline is not a flaky call: replaying the call that started the operation would start it a
        // second time (a duplicate create answers 409) and wait the whole deadline again, once per retry.
        if (cloud.Error.Code == OperationPoller.TimeoutCode)
        {
            return false;
        }

        if (IsPermanent(cloud.Kind) || IsPermanent(CloudErrorClassifier.Classify(cloud.Error)))
        {
            return false;
        }

        var status = cloud.Error.HttpStatus;
        if (status is 429 or 500 or 502 or 503 or 504)
        {
            return true;
        }

        return status is null && cloud.Kind == CloudErrorKind.Network;
    }

    private static bool IsPermanent(CloudErrorKind kind)
        => kind is CloudErrorKind.Stockout or CloudErrorKind.Quota or CloudErrorKind.Billing or CloudErrorKind.ApiDisabled
            or CloudErrorKind.Permission or CloudErrorKind.OrgPolicy or CloudErrorKind.AlreadyExists;

    private static (int? Status, CloudErrorKind Kind, string Message) Describe(Exception? exception)
        => exception switch
        {
            CloudOperationException cloud => (cloud.Error.HttpStatus, cloud.Kind, cloud.Error.Message),
            null => (null, CloudErrorKind.Other, string.Empty),
            _ => (null, CloudErrorKind.Network, exception.GetType().Name),
        };

    private void SetOffline(bool offline)
    {
        if (_offline == offline)
        {
            return;
        }

        _offline = offline;
        _observer.OnConnectivityChanged(offline);
        OfflineChanged?.Invoke(this, EventArgs.Empty);
    }
}
