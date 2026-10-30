namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What the late clean-up of a create may do with a VM it found (see <see cref="AttemptLedger.TryBeginDelete"/>).</summary>
internal enum DeleteDecision
{
    /// <summary>Nothing newer owns the VM and no newer attempt is looking: send the delete.</summary>
    Proceed,

    /// <summary>A newer attempt of the job has the VM: it is that attempt's now.</summary>
    Superseded,

    /// <summary>A newer attempt of the job is still provisioning (it may be about to adopt this VM): look again later.</summary>
    Defer,
}

/// <summary>
/// Issue #938: which attempt of a job owns which VM, and the one lock that orders a late clean-up's delete against a newer attempt's adoption. One <c>ProvisionAsync</c> is one attempt
/// (a Spot attempt 2, a resume, a run again after a cancel). An attempt is PENDING from its start (before its label listing) until it returns, and owns a VM name in a zone only
/// when it HAS that VM (its create answered, or it adopted or reused it): merely sending a create owns nothing. The clean-up of an earlier attempt's create deletes under
/// <see cref="TryBeginDelete"/>, which refuses while a newer attempt is pending (it may be listing or adopting the very VM) and once a newer attempt owns the VM; a newer attempt
/// does not begin while such a delete is underway, so it can never adopt a VM that is being deleted.
/// </summary>
internal sealed class AttemptLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<long>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Job, string Name, string Zone), long> _owners = new();
    private readonly Dictionary<string, int> _deleting = new(StringComparer.Ordinal);
    private TaskCompletionSource _deleteEnded = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The job's current attempt number (0 before the first).</summary>
    public long Current(string jobId)
    {
        lock (_gate)
        {
            return _generations.TryGetValue(jobId, out var generation) ? generation : 0;
        }
    }

    /// <summary>Starts a new attempt of the job and returns its number. Waits (bounded by the watch's own deadlines, and by <paramref name="cancellationToken"/>) while a late clean-up is deleting one of the job's VMs.</summary>
    public async Task<long> BeginAttemptAsync(string jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (!_deleting.TryGetValue(jobId, out var deleting) || deleting == 0)
                {
                    var generation = _generations.TryGetValue(jobId, out var old) ? old + 1 : 1;
                    _generations[jobId] = generation;
                    if (!_pending.TryGetValue(jobId, out var pending))
                    {
                        _pending[jobId] = pending = [];
                    }

                    pending.Add(generation);
                    return generation;
                }

                wait = _deleteEnded.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Round 4 M2: marks one delete (or stop-then-delete) of the job's VM as underway until the returned handle is disposed, so no newer attempt begins (and adopts the VM being deleted) in the meantime. Every
    /// delete of a job's VM the app sends takes this or <see cref="TryBeginDelete"/>: a cancel's, its retry's, the run page's, a failed run's and the late watch's.
    /// </summary>
    public IDisposable HoldDelete(string jobId)
    {
        lock (_gate)
        {
            _deleting[jobId] = _deleting.TryGetValue(jobId, out var count) ? count + 1 : 1;
        }

        return new DeleteHold(this, jobId);
    }

    private sealed class DeleteHold(AttemptLedger ledger, string jobId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ledger.ReleaseDelete(jobId);
            }
        }
    }

    /// <summary>Ends one delete underway for the job; wakes the attempts waiting for the last one.</summary>
    private void ReleaseDelete(string jobId)
    {
        TaskCompletionSource? signal = null;
        lock (_gate)
        {
            var left = _deleting.TryGetValue(jobId, out var count) ? count - 1 : 0;
            if (left <= 0)
            {
                _deleting.Remove(jobId);
                signal = _deleteEnded;
                _deleteEnded = NewSignal();
            }
            else
            {
                _deleting[jobId] = left;
            }
        }

        signal?.TrySetResult();
    }
    /// <summary>The attempt has returned (with a VM or without one).</summary>
    public void EndAttempt(string jobId, long generation)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(jobId, out var pending))
            {
                pending.Remove(generation);
                if (pending.Count == 0)
                {
                    _pending.Remove(jobId);
                }
            }
        }
    }

    /// <summary>The attempt HAS this VM (its create answered, or it adopted or reused it). Never lowers an owner.</summary>
    public void NoteOwned(string jobId, string name, string zone, long generation)
    {
        lock (_gate)
        {
            var key = (jobId, name, zone);
            _owners[key] = _owners.TryGetValue(key, out var old) ? Math.Max(old, generation) : generation;
        }
    }

    /// <summary>
    /// Decides, atomically, whether the clean-up of <paramref name="sent"/> may send a delete for its VM. <see cref="DeleteDecision.Proceed"/> marks a delete as underway
    /// (<see cref="EndDelete"/> ends it), so no newer attempt starts until it has.
    /// </summary>
    public DeleteDecision TryBeginDelete(SentCreate sent)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue((sent.JobId, sent.Name, sent.Zone), out var owner) && owner > sent.Generation)
            {
                return DeleteDecision.Superseded;
            }

            if (_pending.TryGetValue(sent.JobId, out var pending) && pending.Any(g => g > sent.Generation))
            {
                return DeleteDecision.Defer;
            }

            if (!sent.DeleteBegun || sent.DeleteEnded)
            {
                // A new span of deletes: the first one's, or a later one after the first ended (round 4 M2: the watch's deletes after the cancel's own).
                sent.DeleteBegun = true;
                sent.DeleteEnded = false;
                _deleting[sent.JobId] = _deleting.TryGetValue(sent.JobId, out var count) ? count + 1 : 1;
            }

            return DeleteDecision.Proceed;
        }
    }

    /// <summary>The delete <see cref="TryBeginDelete"/> began has ended (the VM reads gone, or the watch stopped waiting for it). Safe to call for a create that never began one.</summary>
    public void EndDelete(SentCreate sent)
    {
        lock (_gate)
        {
            if (!sent.DeleteBegun || sent.DeleteEnded)
            {
                return;
            }

            sent.DeleteEnded = true;
        }

        ReleaseDelete(sent.JobId);
    }
    /// <summary>Drops what is remembered of the job when no attempt is pending and no delete is underway (the caller says no create of the job is still held).</summary>
    public void Prune(string jobId)
    {
        lock (_gate)
        {
            if (_pending.ContainsKey(jobId) || _deleting.ContainsKey(jobId))
            {
                return;
            }

            _generations.Remove(jobId);
            foreach (var key in _owners.Keys.Where(k => k.Job == jobId).ToList())
            {
                _owners.Remove(key);
            }
        }
    }

    /// <summary>Tests: how many entries are still remembered.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _generations.Count + _pending.Count + _owners.Count + _deleting.Count;
            }
        }
    }
}
