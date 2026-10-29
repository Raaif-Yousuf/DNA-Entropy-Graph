using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// How a run ends in a recorded terminal state (Hard Rule 11): <see cref="FailAsync"/> for a failure, and
/// <see cref="FinishAsync"/> from a <c>result.json</c> in hand: judge it, download what it lists, THEN end and verify the VM.
/// </summary>
internal sealed class RunOutcomeRecorder(RunRowStore rows, RunTransfer transfer, VmTerminator terminator)
{
    public async Task<CloudJobResult> FailAsync(string jobId, CloudErrorKind kind, string code, string? detail, string? vmEndNote = null)
    {
        if (vmEndNote is not null)
        {
            // The VM may still be billing: that is what the user must hear, the original cause stays in the detail.
            detail = $"{detail} Original code: {code}.{vmEndNote}";
            code = RunErrorCodes.VmEndUnconfirmed;
        }

        // CancellationToken.None: recording the terminal state must not itself be cancellable.
        await rows.SetPhaseAsync(jobId, JobPhase.Failed, CancellationToken.None, code, detail).ConfigureAwait(false);
        await transfer.RemoveEmptyRunFolderAsync(jobId).ConfigureAwait(false);
        return new CloudJobResult(JobPhase.Failed, kind, detail, code);
    }

    /// <summary>
    /// The bucket outlives the VM, so the download never waits on the lifecycle (the worker has usually
    /// stopped or deleted its own VM by now), and a lifecycle check that fails after a good download
    /// records that on the finished run instead of discarding the results (Hard Rules 11 and 14).
    /// </summary>
    public async Task<CloudJobResult> FinishAsync(CloudJobRequest request, string bucket, string? zone, string resultText, CancellationToken cancellationToken)
    {
        WorkerResultDocument result;
        try
        {
            result = WorkerResultReader.Parse(resultText);
        }
        catch (InvalidDataException ex)
        {
            var note = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
            return await FailAsync(request.JobId, CloudErrorKind.Other, RunErrorCodes.WorkerFailed, "result.json could not be read: " + ex.Message, note).ConfigureAwait(false);
        }

        var tally = new DownloadTally();

        // A finished input must have a track: one that lists no files is not finished, whatever it says.
        var doneInputs = result.Inputs.Count(i => i.Status == "done" && i.Files.Count > 0);
        if (result.Status != "done" || doneInputs == 0)
        {
            // Partial results are always kept, never discarded (docs/job_contract.md section 6): a failed or cancelled
            // input lists the partial files it uploaded, so they are fetched before the run is recorded Failed.
            var keptPartial = false;
            if (result.Inputs.Any(i => i.Files.Count > 0))
            {
                try
                {
                    await transfer.DownloadAsync(request, bucket, result, cancellationToken, tally).ConfigureAwait(false);
                    keptPartial = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The failure itself is the run's story; the partial files stay in the bucket.
                }
            }

            var note = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
            var workerCode = result.ErrorCode ?? result.Inputs.FirstOrDefault(i => i.Status != "done")?.ErrorCode;
            return await FailAsync(
                request.JobId,
                CloudErrorKind.Other,
                RunErrorCodes.ForWorkerStatusCode(workerCode) ?? RunErrorCodes.WorkerFailed,
                DescribeWorkerFailure(result) + (keptPartial ? " Partial files were kept in the output folder." : string.Empty) + AsideNote(tally.KeptAside),
                note).ConfigureAwait(false);
        }

        await rows.SetPhaseAsync(request.JobId, JobPhase.Finalizing, cancellationToken).ConfigureAwait(false);
        await rows.SetPhaseAsync(request.JobId, JobPhase.Downloading, cancellationToken).ConfigureAwait(false);
        try
        {
            await transfer.DownloadAsync(request, bucket, result, cancellationToken, tally).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The results are still in the bucket; the VM must not keep billing while the user sorts out the folder.
            var downloadNote = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
            // A file renamed aside before the failure is still the user's to hear about: it rides on the detail, the failure code stays the headline.
            throw VmEndNotes.Apply(WithAsideNote(ex, tally.KeptAside), downloadNote);
        }

        var lifecycleError = request.AfterTask == AfterTaskAction.KeepAlive
            ? null
            : await terminator.EnsureVmEndedAsync(request, zone, request.AfterTask, cancellationToken).ConfigureAwait(false);

        var finalPhase = doneInputs == result.Inputs.Count ? JobPhase.Completed : JobPhase.PartiallyCompleted;
        if (lifecycleError is not null)
        {
            // A good result with an unconfirmed VM: finished, with the code that tells the user to check the Cloud page.
            await rows.SetPhaseAsync(request.JobId, finalPhase, CancellationToken.None, RunErrorCodes.LifecycleUnverified, lifecycleError + AsideNote(tally.KeptAside)).ConfigureAwait(false);
            return new CloudJobResult(finalPhase, CloudErrorKind.Other, lifecycleError + AsideNote(tally.KeptAside), RunErrorCodes.LifecycleUnverified);
        }

        if (tally.KeptAside > 0)
        {
            // Hard Rule 14: a file the user had changed was renamed aside, not overwritten. A finished run still, with the code that tells the user where to look (the count is not text a user reads).
            await rows.SetPhaseAsync(request.JobId, finalPhase, CancellationToken.None, RunErrorCodes.ChangedFilesKeptAside, AsideNote(tally.KeptAside).TrimStart()).ConfigureAwait(false);
            return new CloudJobResult(finalPhase, null, null, RunErrorCodes.ChangedFilesKeptAside);
        }

        await rows.SetPhaseAsync(request.JobId, finalPhase, cancellationToken).ConfigureAwait(false);
        return new CloudJobResult(finalPhase);
    }

    /// <summary>Detail text (never the message the user reads, Hard Rule 13) saying how many of the user's changed files were renamed aside; empty when none.</summary>
    internal static string AsideNote(int count) => count > 0 ? $" {count} changed file(s) in the output folder were kept under a changed name." : string.Empty;

    private static Exception WithAsideNote(Exception ex, int count)
        => count > 0 && ex is RunFailureException failure
            ? new RunFailureException(failure.Kind, failure.Code, failure.Message + AsideNote(count)) { SpotPreempted = failure.SpotPreempted }
            : ex;

    private static string DescribeWorkerFailure(WorkerResultDocument result)
    {
        var failed = result.Inputs.FirstOrDefault(i => i.Status != "done");
        var code = result.ErrorCode ?? failed?.ErrorCode ?? "unknown";

        // The worker's own message is free text that can carry a record name from the user's file; the code is enough
        // (the run record goes to SQLite and the diagnostics zip, CLAUDE.md "Logs").
        var emptyDone = result.Inputs.Any(i => i.Status == "done" && i.Files.Count == 0);
        return $"The worker reported status '{result.Status}' ({code}).{(emptyDone ? " A finished input listed no result files." : string.Empty)}";
    }
}
