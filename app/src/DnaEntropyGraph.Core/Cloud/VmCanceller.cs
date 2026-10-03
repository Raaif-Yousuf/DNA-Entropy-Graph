namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Cancelling a run: <see cref="JobPhase.Cancelling"/>, wait (bounded) for any create the provisioner still has in flight,
/// delete every VM this job's id owns (by label lookup, not a remembered zone), then record exactly one terminal state.
/// The in-flight state stays owned by <see cref="VmProvisioner"/>; this only asks it to settle.
/// </summary>
internal sealed class VmCanceller(RunRowStore rows, VmProvisioner provisioner, VmTerminator terminator)
{
    public async Task CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var existing = await rows.LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            // A job id this repository never saw: nothing to cancel.
            return;
        }

        var current = existing.Phase;
        if (JobStateMachine.IsTerminal(current))
        {
            return;
        }

        if (current == JobPhase.Draft)
        {
            // Cancelled during preflight, before the first phase: no VM can
            // exist yet, and Draft -> Cancelling is not a legal transition.
            await rows.SetPhaseAsync(jobId, JobPhase.Cancelled, cancellationToken).ConfigureAwait(false);
            return;
        }

        await rows.SetPhaseAsync(jobId, JobPhase.Cancelling, CancellationToken.None).ConfigureAwait(false);

        // A create the caller gave up on can still land. Wait for it (bounded) before looking, or the VM appears
        // after the run is recorded Cancelled and nothing ever deletes it.
        var settled = await provisioner.SettleInflightCreatesAsync(jobId).ConfigureAwait(false);
        string? failure;
        try
        {
            failure = await terminator.DeleteOwnedVmsAndVerifyAsync(jobId, settled, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The caller gave up on the cancel, but the run must not stay in Cancelling with a VM that may be billing: one
            // last attempt that the caller's token cannot cut, then the terminal state is recorded from what it finds.
            try
            {
                failure = await terminator.DeleteOwnedVmsAndVerifyAsync(jobId, settled, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        // Hard Rule 11: a cancel ends in exactly one recorded terminal state. Failed names the VM to delete by hand.
        await RecordCancelOutcomeAsync(jobId, failure).ConfigureAwait(false);
    }

    /// <summary>
    /// Records how a cancel ended: Cancelled when the VMs are confirmed gone, else Failed under <see cref="RunErrorCodes.CancelFailed"/>.
    /// A row that is already terminal (a second cancel that raced the first) is left exactly as it is, and nothing here throws.
    /// </summary>
    public async Task RecordCancelOutcomeAsync(string jobId, string? failure)
    {
        try
        {
            var row = await rows.LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (row is not null && JobStateMachine.IsTerminal(row.Phase))
            {
                return;
            }

            if (failure is null)
            {
                await rows.SetPhaseAsync(jobId, JobPhase.Cancelled, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await rows.SetPhaseAsync(jobId, JobPhase.Failed, CancellationToken.None, RunErrorCodes.CancelFailed, failure).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The repository itself is failing: nothing more can be recorded, and a throw here would only hide the run's state.
        }
    }
}
