using Dapper;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// SQLite-backed <c>Runs</c> table (issue #67). Column-for-column mapping
/// keeps <see cref="RunRecord"/>'s .NET-friendly names (<c>JobId</c>,
/// <c>CreatedUtc</c>) decoupled from the DDL's own names (<c>Id</c>,
/// <c>CreatedAt</c>) rather than relying on Dapper's implicit name matching,
/// so a rename on either side is a compile error here, not a silent NULL.
/// </summary>
public sealed class RunRepository : IRunRepository, IRunHistoryRemover
{
    private const string SelectAllSql = """
        SELECT Id, Name, IsBatch, Target, Phase, ErrorCode, ErrorDetail, CreatedAt, StartedAt, VmReadyAt, FinishedAt,
               OptionsJson, ManifestJson, ProjectId, Bucket, JobPrefix, VmName, Zone, MachineType, GpuType,
               IsSpot, VmReused, LastProgressSeq, LastHeartbeatAt, StatusGeneration, OutputDir,
               EstimatedCostUsd, ActualCostUsd, VmSeconds, CloudResultsExpireAt, CloudResultsDeleted,
               AppVersion, WorkerVersion, WorkerImageDigest, ContractVersion, InstallationId, Imported,
               Notes, TagsJson, PreemptionCount
        FROM Runs;
        """;

    // Notes and TagsJson (issue #141's future columns) are deliberately
    // excluded from ON CONFLICT ... DO UPDATE SET, exactly like CreatedAt:
    // JobEngine will call UpsertAsync on every phase transition with
    // whatever RunRecord it is tracking, which will not carry a user's
    // notes/tags unless it went out of its way to re-read them first. If
    // Upsert overwrote these columns, the very next phase change after a
    // user typed a note would silently erase it - a wired-to-nothing bug
    // that would not show up until someone actually used the feature.
    // When #141 is built, it gets its own UpdateNotesAsync/UpdateTagsAsync
    // rather than going through this general Upsert.
    private const string UpsertSql = """
        INSERT INTO Runs (
            Id, Name, IsBatch, Target, Phase, ErrorCode, ErrorDetail, CreatedAt, StartedAt, VmReadyAt, FinishedAt,
            OptionsJson, ManifestJson, ProjectId, Bucket, JobPrefix, VmName, Zone, MachineType, GpuType,
            IsSpot, VmReused, LastProgressSeq, LastHeartbeatAt, StatusGeneration, OutputDir,
            EstimatedCostUsd, ActualCostUsd, VmSeconds, CloudResultsExpireAt, CloudResultsDeleted,
            AppVersion, WorkerVersion, WorkerImageDigest, ContractVersion, InstallationId, Imported,
            Notes, TagsJson, PreemptionCount)
        VALUES (
            @Id, @Name, @IsBatch, @Target, @Phase, @ErrorCode, @ErrorDetail, @CreatedAt, @StartedAt, @VmReadyAt, @FinishedAt,
            @OptionsJson, @ManifestJson, @ProjectId, @Bucket, @JobPrefix, @VmName, @Zone, @MachineType, @GpuType,
            @IsSpot, @VmReused, @LastProgressSeq, @LastHeartbeatAt, @StatusGeneration, @OutputDir,
            @EstimatedCostUsd, @ActualCostUsd, @VmSeconds, @CloudResultsExpireAt, @CloudResultsDeleted,
            @AppVersion, @WorkerVersion, @WorkerImageDigest, @ContractVersion, @InstallationId, @Imported,
            @Notes, @TagsJson, @PreemptionCount)
        ON CONFLICT(Id) DO UPDATE SET
            Name = excluded.Name, IsBatch = excluded.IsBatch, Target = excluded.Target, Phase = excluded.Phase,
            ErrorCode = excluded.ErrorCode, ErrorDetail = excluded.ErrorDetail,
            StartedAt = excluded.StartedAt, VmReadyAt = excluded.VmReadyAt, FinishedAt = excluded.FinishedAt,
            OptionsJson = excluded.OptionsJson, ManifestJson = excluded.ManifestJson,
            ProjectId = excluded.ProjectId, Bucket = excluded.Bucket, JobPrefix = excluded.JobPrefix,
            VmName = excluded.VmName, Zone = excluded.Zone, MachineType = excluded.MachineType, GpuType = excluded.GpuType,
            IsSpot = excluded.IsSpot, VmReused = excluded.VmReused,
            LastProgressSeq = excluded.LastProgressSeq, LastHeartbeatAt = excluded.LastHeartbeatAt,
            StatusGeneration = excluded.StatusGeneration, OutputDir = excluded.OutputDir,
            EstimatedCostUsd = excluded.EstimatedCostUsd, ActualCostUsd = excluded.ActualCostUsd, VmSeconds = excluded.VmSeconds,
            CloudResultsExpireAt = excluded.CloudResultsExpireAt, CloudResultsDeleted = excluded.CloudResultsDeleted,
            AppVersion = excluded.AppVersion, WorkerVersion = excluded.WorkerVersion, WorkerImageDigest = excluded.WorkerImageDigest,
            ContractVersion = excluded.ContractVersion, InstallationId = excluded.InstallationId, Imported = excluded.Imported,
            PreemptionCount = excluded.PreemptionCount;
        """;

    private readonly SqliteDatabase _database;

    private readonly DataPathMapper _paths;
    private readonly IDiagnosticsLog _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warned = new(StringComparer.Ordinal);

    /// <param name="paths">Portable mode (issue #144): stores paths under the portable folder relative to it. Null stores every path as given.</param>
    public RunRepository(SqliteDatabase database, DataPathMapper? paths = null, IDiagnosticsLog? log = null)
    {
        _log = log ?? NullDiagnosticsLog.Instance;
        _database = database;
        _paths = paths ?? new DataPathMapper(null);
    }

    public async Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(SelectAllSql, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<RunRow>(command).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(UpsertSql, ToRow(run), cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    /// <summary>Test seam: runs inside <see cref="TryUpsertIfPhaseAsync"/> between its read of the stored phase and its write, so a test can force another writer into that gap. Null in production.</summary>
    internal Func<Task>? BetweenPhaseReadAndWrite { get; set; }

    /// <summary>
    /// Issue #113 (H2): the check and the write are ONE step, under <c>BEGIN IMMEDIATE</c> (the write lock is taken before the read, so two writers
    /// that read the same phase cannot both pass the check). A row that is gone is never resurrected: expecting a phase needs that row.
    /// </summary>
    public async Task<bool> TryUpsertIfPhaseAsync(RunRecord run, JobPhase? expectedPhase, CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        try
        {
            var stored = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition("SELECT Phase FROM Runs WHERE Id = @Id;", new { Id = run.JobId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (BetweenPhaseReadAndWrite is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            if (stored != expectedPhase?.ToString())
            {
                await connection.ExecuteAsync("ROLLBACK;").ConfigureAwait(false);
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(UpsertSql, ToRow(run), cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync("COMMIT;").ConfigureAwait(false);
            return true;
        }
        catch
        {
            try
            {
                await connection.ExecuteAsync("ROLLBACK;").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // No transaction left to roll back (the failure was the COMMIT or the BEGIN itself); the connection is disposed next.
            }

            throw;
        }
    }

    public async Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition("DELETE FROM Runs WHERE Id = @Id;", new { Id = jobId }, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    /// <summary>
    /// A stored output folder that does not expand inside the portable folder reads back as null. The next upsert of the row writes that
    /// null over the stored text, so the loss is logged here, once per run id for the life of this repository (a History refresh reads every row again): the run id and a fixed class name, never the stored text.
    /// </summary>
    private string? MapOutputDir(RunRow row)
    {
        var mapped = _paths.FromStored(row.OutputDir);
        if (mapped is null && row.OutputDir is not null && _warned.TryAdd(row.Id, true))
        {
            _log.Warning("run-repository", row.Id, "OutputDirOutsidePortableFolder");
        }

        return mapped;
    }

    private RunRecord ToRecord(RunRow row) => new(
        JobId: row.Id,
        Phase: Enum.Parse<JobPhase>(row.Phase),
        CreatedUtc: Iso.Parse(row.CreatedAt)!.Value,
        Name: row.Name,
        IsBatch: row.IsBatch != 0,
        Target: row.Target,
        ErrorCode: row.ErrorCode,
        ErrorDetail: row.ErrorDetail,
        StartedAt: Iso.Parse(row.StartedAt),
        VmReadyAt: Iso.Parse(row.VmReadyAt),
        FinishedAt: Iso.Parse(row.FinishedAt),
        OptionsJson: row.OptionsJson,
        ManifestJson: row.ManifestJson,
        ProjectId: row.ProjectId,
        Bucket: row.Bucket,
        JobPrefix: row.JobPrefix,
        VmName: row.VmName,
        Zone: row.Zone,
        MachineType: row.MachineType,
        GpuType: row.GpuType,
        IsSpot: Iso.ToBool(row.IsSpot),
        VmReused: Iso.ToBool(row.VmReused),
        LastProgressSeq: (int)row.LastProgressSeq,
        LastHeartbeatAt: Iso.Parse(row.LastHeartbeatAt),
        StatusGeneration: row.StatusGeneration is null ? null : (int)row.StatusGeneration,
        OutputDir: MapOutputDir(row),
        EstimatedCostUsd: row.EstimatedCostUsd,
        ActualCostUsd: row.ActualCostUsd,
        VmSeconds: row.VmSeconds,
        CloudResultsExpireAt: Iso.Parse(row.CloudResultsExpireAt),
        CloudResultsDeleted: row.CloudResultsDeleted != 0,
        AppVersion: row.AppVersion,
        WorkerVersion: row.WorkerVersion,
        WorkerImageDigest: row.WorkerImageDigest,
        ContractVersion: row.ContractVersion is null ? null : (int)row.ContractVersion,
        InstallationId: row.InstallationId,
        Imported: row.Imported != 0,
        Notes: row.Notes,
        TagsJson: row.TagsJson,
        PreemptionCount: (int)row.PreemptionCount);

    private RunRow ToRow(RunRecord run) => new()
    {
        Id = run.JobId,
        Name = run.Name ?? run.JobId,
        IsBatch = run.IsBatch ? 1 : 0,
        Target = run.Target,
        Phase = run.Phase.ToString(),
        ErrorCode = run.ErrorCode,
        ErrorDetail = run.ErrorDetail,
        CreatedAt = Iso.Format(run.CreatedUtc)!,
        StartedAt = Iso.Format(run.StartedAt),
        VmReadyAt = Iso.Format(run.VmReadyAt),
        FinishedAt = Iso.Format(run.FinishedAt),
        OptionsJson = run.OptionsJson,
        ManifestJson = run.ManifestJson,
        ProjectId = run.ProjectId,
        Bucket = run.Bucket,
        JobPrefix = run.JobPrefix,
        VmName = run.VmName,
        Zone = run.Zone,
        MachineType = run.MachineType,
        GpuType = run.GpuType,
        IsSpot = Iso.ToLong(run.IsSpot),
        VmReused = Iso.ToLong(run.VmReused),
        LastProgressSeq = run.LastProgressSeq,
        LastHeartbeatAt = Iso.Format(run.LastHeartbeatAt),
        StatusGeneration = run.StatusGeneration,
        OutputDir = _paths.ToStored(run.OutputDir),
        EstimatedCostUsd = run.EstimatedCostUsd,
        ActualCostUsd = run.ActualCostUsd,
        VmSeconds = run.VmSeconds,
        CloudResultsExpireAt = Iso.Format(run.CloudResultsExpireAt),
        CloudResultsDeleted = run.CloudResultsDeleted ? 1 : 0,
        AppVersion = run.AppVersion,
        WorkerVersion = run.WorkerVersion,
        WorkerImageDigest = run.WorkerImageDigest,
        ContractVersion = run.ContractVersion,
        InstallationId = run.InstallationId,
        Imported = run.Imported ? 1 : 0,
        Notes = run.Notes,
        TagsJson = run.TagsJson,
        PreemptionCount = run.PreemptionCount,
    };

    private sealed class RunRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long IsBatch { get; set; }
        public string Target { get; set; } = "";
        public string Phase { get; set; } = "";
        public string? ErrorCode { get; set; }
        public string? ErrorDetail { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? StartedAt { get; set; }
        public string? VmReadyAt { get; set; }
        public string? FinishedAt { get; set; }
        public string OptionsJson { get; set; } = "{}";
        public string? ManifestJson { get; set; }
        public string? ProjectId { get; set; }
        public string? Bucket { get; set; }
        public string? JobPrefix { get; set; }
        public string? VmName { get; set; }
        public string? Zone { get; set; }
        public string? MachineType { get; set; }
        public string? GpuType { get; set; }
        public long? IsSpot { get; set; }
        public long? VmReused { get; set; }
        public long LastProgressSeq { get; set; }
        public string? LastHeartbeatAt { get; set; }
        public long? StatusGeneration { get; set; }
        public string? OutputDir { get; set; }
        public double? EstimatedCostUsd { get; set; }
        public double? ActualCostUsd { get; set; }
        public long? VmSeconds { get; set; }
        public string? CloudResultsExpireAt { get; set; }
        public long CloudResultsDeleted { get; set; }
        public string? AppVersion { get; set; }
        public string? WorkerVersion { get; set; }
        public string? WorkerImageDigest { get; set; }
        public long? ContractVersion { get; set; }
        public string? InstallationId { get; set; }
        public long Imported { get; set; }
        public long PreemptionCount { get; set; }
        public string? Notes { get; set; }
        public string? TagsJson { get; set; }
    }
}
