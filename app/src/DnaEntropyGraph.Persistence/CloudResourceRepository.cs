using Dapper;

namespace DnaEntropyGraph.Persistence;

/// <summary>SQLite-backed <see cref="ICloudResourceRepository"/>. See that file for design notes.</summary>
public sealed class CloudResourceRepository : ICloudResourceRepository
{
    private const string SelectAllSql = """
        SELECT Kind, Name, ProjectId, Location, CreatedAt, CreatedByInstall, LastKnownState, LastCheckedAt,
               MachineType, GpuType, IsSpot, DiskGb, HourlyRateUsd, RunningSecondsAccum, LastStartedAt,
               CurrentJobId, LifecyclePolicy, KeepUntilUtc, DeletedAt, LabelsJson
        FROM CloudResources;
        """;

    private const string UpsertSql = """
        INSERT INTO CloudResources (
            Kind, Name, ProjectId, Location, CreatedAt, CreatedByInstall, LastKnownState, LastCheckedAt,
            MachineType, GpuType, IsSpot, DiskGb, HourlyRateUsd, RunningSecondsAccum, LastStartedAt,
            CurrentJobId, LifecyclePolicy, KeepUntilUtc, DeletedAt, LabelsJson)
        VALUES (
            @Kind, @Name, @ProjectId, @Location, @CreatedAt, @CreatedByInstall, @LastKnownState, @LastCheckedAt,
            @MachineType, @GpuType, @IsSpot, @DiskGb, @HourlyRateUsd, @RunningSecondsAccum, @LastStartedAt,
            @CurrentJobId, @LifecyclePolicy, @KeepUntilUtc, @DeletedAt, @LabelsJson)
        ON CONFLICT(Kind, ProjectId, Name) DO UPDATE SET
            Location = excluded.Location, LastKnownState = excluded.LastKnownState, LastCheckedAt = excluded.LastCheckedAt,
            MachineType = excluded.MachineType, GpuType = excluded.GpuType, IsSpot = excluded.IsSpot, DiskGb = excluded.DiskGb,
            HourlyRateUsd = excluded.HourlyRateUsd, RunningSecondsAccum = excluded.RunningSecondsAccum,
            LastStartedAt = excluded.LastStartedAt, CurrentJobId = excluded.CurrentJobId,
            LifecyclePolicy = excluded.LifecyclePolicy, KeepUntilUtc = excluded.KeepUntilUtc,
            DeletedAt = excluded.DeletedAt, LabelsJson = excluded.LabelsJson;
        """;

    private readonly SqliteDatabase _database;

    public CloudResourceRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<CloudResourceRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(SelectAllSql, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<Row>(command).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task UpsertAsync(CloudResourceRecord resource, CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(UpsertSql, ToRow(resource), cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    private static CloudResourceRecord ToRecord(Row row) => new(
        Kind: row.Kind,
        Name: row.Name,
        ProjectId: row.ProjectId,
        CreatedAt: Iso.Parse(row.CreatedAt)!.Value,
        Location: row.Location,
        CreatedByInstall: row.CreatedByInstall,
        LastKnownState: row.LastKnownState,
        LastCheckedAt: Iso.Parse(row.LastCheckedAt),
        MachineType: row.MachineType,
        GpuType: row.GpuType,
        IsSpot: Iso.ToBool(row.IsSpot),
        DiskGb: row.DiskGb is null ? null : (int)row.DiskGb,
        HourlyRateUsd: row.HourlyRateUsd,
        RunningSecondsAccum: row.RunningSecondsAccum,
        LastStartedAt: Iso.Parse(row.LastStartedAt),
        CurrentJobId: row.CurrentJobId,
        LifecyclePolicy: row.LifecyclePolicy,
        KeepUntilUtc: Iso.Parse(row.KeepUntilUtc),
        DeletedAt: Iso.Parse(row.DeletedAt),
        LabelsJson: row.LabelsJson);

    private static Row ToRow(CloudResourceRecord resource) => new()
    {
        Kind = resource.Kind,
        Name = resource.Name,
        ProjectId = resource.ProjectId,
        Location = resource.Location,
        CreatedAt = Iso.Format(resource.CreatedAt)!,
        CreatedByInstall = resource.CreatedByInstall,
        LastKnownState = resource.LastKnownState,
        LastCheckedAt = Iso.Format(resource.LastCheckedAt),
        MachineType = resource.MachineType,
        GpuType = resource.GpuType,
        IsSpot = Iso.ToLong(resource.IsSpot),
        DiskGb = resource.DiskGb,
        HourlyRateUsd = resource.HourlyRateUsd,
        RunningSecondsAccum = resource.RunningSecondsAccum,
        LastStartedAt = Iso.Format(resource.LastStartedAt),
        CurrentJobId = resource.CurrentJobId,
        LifecyclePolicy = resource.LifecyclePolicy,
        KeepUntilUtc = Iso.Format(resource.KeepUntilUtc),
        DeletedAt = Iso.Format(resource.DeletedAt),
        LabelsJson = resource.LabelsJson,
    };

    private sealed class Row
    {
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ProjectId { get; set; }
        public string? Location { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? CreatedByInstall { get; set; }
        public string? LastKnownState { get; set; }
        public string? LastCheckedAt { get; set; }
        public string? MachineType { get; set; }
        public string? GpuType { get; set; }
        public long? IsSpot { get; set; }
        public long? DiskGb { get; set; }
        public double? HourlyRateUsd { get; set; }
        public long RunningSecondsAccum { get; set; }
        public string? LastStartedAt { get; set; }
        public string? CurrentJobId { get; set; }
        public string? LifecyclePolicy { get; set; }
        public string? KeepUntilUtc { get; set; }
        public string? DeletedAt { get; set; }
        public string? LabelsJson { get; set; }
    }
}
