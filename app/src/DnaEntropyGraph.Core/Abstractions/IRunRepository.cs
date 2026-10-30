namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// The <c>Runs</c> table (docs/architecture.md section 6, DDL in
/// docs/superpowers/specs/2026-09-18-appendix-a-app-design.md section 3).
/// Implemented on SQLite in <c>DnaEntropyGraph.Persistence.RunRepository</c>
/// (issue #67) - one file, migrated forward under <c>PRAGMA user_version</c>,
/// real enough that killing the app immediately after pressing Run and
/// relaunching shows the row again (the issue's own "Observable that proves
/// it is wired").
/// </summary>
public interface IRunRepository
{
    Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new row, or replaces every column of an existing one keyed
    /// by <see cref="RunRecord.JobId"/> - never a partial update, so a
    /// caller always upserts the full record it has. <see cref="RunRecord.CreatedUtc"/>
    /// is preserved across an update (a run's creation time never changes).
    /// The write-ahead rule (Hard Rule / docs/architecture.md section 6):
    /// call this with <see cref="Core.JobPhase.Validating"/> before any
    /// network call is made, not after.
    /// </summary>
    Task UpsertAsync(RunRecord run, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #113: the same full-record write as <see cref="UpsertAsync"/>, but ONLY when the stored phase of <paramref name="run"/>'s job is
    /// <paramref name="expectedPhase"/> (the phase the writer read), checked and written as one atomic step. <c>null</c> means "there must be no
    /// row yet". Returns false, and changes nothing, when the stored phase differs (another writer moved the row since the read) or the row is
    /// gone; the caller re-reads and re-validates. This is what stops a writer that read Running from overwriting a Cancelled.
    /// The default is NOT conditional (a plain <see cref="UpsertAsync"/> that reports true): it exists so a test double that has one writer keeps compiling, and a
    /// repository that more than one writer can reach (the SQLite one) MUST override it (<c>RunRepositoryConditionalUpsertTests</c> goes red if the SQLite one stops doing so).
    /// </summary>
    async Task<bool> TryUpsertIfPhaseAsync(RunRecord run, JobPhase? expectedPhase, CancellationToken cancellationToken)
    {
        await UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// One row of the <c>Runs</c> table. Only <paramref name="JobId"/>,
/// <paramref name="Phase"/> and <paramref name="CreatedUtc"/> are required;
/// every other column defaults to the value an in-progress
/// <see cref="Core.JobPhase.Validating"/> row would have, so existing
/// three-argument call sites keep compiling as this grows toward the full
/// DDL (additive per this lane's ownership rule - see WAVE_BRIEF.md).
/// Never holds the sequence, the input file's raw bytes, or the account
/// email - only identifiers, timings, and the input file's *name*, which
/// history legitimately needs to be useful (CLAUDE.md Hard Rule 5 governs
/// worker console/log output and Serilog, not this table; the DDL itself,
/// approved in Appendix A, already puts the file name in <c>RunInputs</c>).
/// </summary>
public sealed record RunRecord(
    string JobId,
    JobPhase Phase,
    DateTimeOffset CreatedUtc,
    string? Name = null,
    bool IsBatch = false,
    string Target = "cloud",
    string? ErrorCode = null,
    string? ErrorDetail = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? VmReadyAt = null,
    DateTimeOffset? FinishedAt = null,
    string OptionsJson = "{}",
    string? ManifestJson = null,
    string? ProjectId = null,
    string? Bucket = null,
    string? JobPrefix = null,
    string? VmName = null,
    string? Zone = null,
    string? MachineType = null,
    string? GpuType = null,
    bool? IsSpot = null,
    bool? VmReused = null,
    int LastProgressSeq = 0,
    DateTimeOffset? LastHeartbeatAt = null,
    int? StatusGeneration = null,
    string? OutputDir = null,
    double? EstimatedCostUsd = null,
    double? ActualCostUsd = null,
    long? VmSeconds = null,
    DateTimeOffset? CloudResultsExpireAt = null,
    bool CloudResultsDeleted = false,
    string? AppVersion = null,
    string? WorkerVersion = null,
    string? WorkerImageDigest = null,
    int? ContractVersion = null,
    string? InstallationId = null,
    bool Imported = false,
    string? Notes = null,
    string? TagsJson = null,
    int PreemptionCount = 0,
    string? AccountSub = null)
{
    // Issue #141: a whole record can reach a log line or a failure message ($"{run}"), and the compiler's ToString prints every member.
    // The user's own free text prints as a fixed marker instead; every other member prints as before (a null as "null").
    private static readonly System.Reflection.PropertyInfo[] PrintedMembers =
        [.. typeof(RunRecord).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)];

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        var first = true;
        foreach (var member in PrintedMembers)
        {
            var value = member.GetValue(this);
            var text = member.Name switch
            {
                nameof(Notes) when value is not null => "<notes withheld>",
                nameof(TagsJson) when value is not null => "<tags withheld>",
            nameof(AccountSub) when value is not null => "<account withheld>",
                _ => value is null ? "null" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            };
            builder.Append(first ? string.Empty : ", ").Append(member.Name).Append(" = ").Append(text);
            first = false;
        }

        return true;
    }

    /// <summary>
    /// <see cref="Imported"/> means two things; this is the earlier command-line version's run (issue #243): a local record
    /// (<see cref="Target"/> is <c>local</c>) whose folder the user already owned. No installation id, bucket or cloud copy.
    /// </summary>
    public bool IsPrototypeImport => Imported && Core.LocalEngine.RunTargets.IsLocalRecord(Target);

    /// <summary>A run found in the bucket that another computer made (issue #111): <see cref="Imported"/> but not a prototype run.</summary>
    public bool IsCloudImport => Imported && !IsPrototypeImport;
}
