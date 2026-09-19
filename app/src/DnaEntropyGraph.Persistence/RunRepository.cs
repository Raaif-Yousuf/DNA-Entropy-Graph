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
public sealed class RunRepository : IRunRepository
{
    private const string SelectAllSql = """
        SELECT Id, Name, IsBatch, Target, Phase, ErrorCode, ErrorDetail, CreatedAt, StartedAt, VmReadyAt, FinishedAt,
               OptionsJson, ManifestJson, ProjectId, Bucket, JobPrefix, VmName, Zone, MachineType, GpuType,
               IsSpot, VmReused, LastProgressSeq, LastHeartbeatAt, StatusGeneration, OutputDir,
               EstimatedCostUsd, ActualCostUsd, VmSeconds, CloudResultsExpireAt, CloudResultsDeleted,
               AppVersion, WorkerVersion, WorkerImageDigest, ContractVersion, InstallationId, Imported,
               Notes, TagsJson
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
            Notes, TagsJson)
        VALUES (
            @Id, @Name, @IsBatch, @Target, @Phase, @ErrorCode, @ErrorDetail, @CreatedAt, @StartedAt, @VmReadyAt, @FinishedAt,
            @OptionsJson, @ManifestJson, @ProjectId, @Bucket, @JobPrefix, @VmName, @Zone, @MachineType, @GpuType,
            @IsSpot, @VmReused, @LastProgressSeq, @LastHeartbeatAt, @StatusGeneration, @OutputDir,
            @EstimatedCostUsd, @ActualCostUsd, @VmSeconds, @CloudResultsExpireAt, @CloudResultsDeleted,
            @AppVersion, @WorkerVersion, @WorkerImageDigest, @ContractVersion, @InstallationId, @Imported,
            @Notes, @TagsJson)
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
            ContractVersion = excluded.ContractVersion, InstallationId = excluded.InstallationId, Imported = excluded.Imported;
        """;

    private readonly SqliteDatabase _database;

    public RunRepository(SqliteDatabase database) => _database = database;

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

    private static RunRecord ToRecord(RunRow row) => new(
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
        OutputDir: row.OutputDir,
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
        TagsJson: row.TagsJson);

    private static RunRow ToRow(RunRecord run) => new()
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
        OutputDir = run.OutputDir,
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
        public string? Notes { get; set; }
        public string? TagsJson { get; set; }
    }
}
