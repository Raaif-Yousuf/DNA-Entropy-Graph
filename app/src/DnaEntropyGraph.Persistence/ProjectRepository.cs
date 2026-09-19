using Dapper;

namespace DnaEntropyGraph.Persistence;

/// <summary>SQLite-backed <see cref="IProjectRepository"/>. See that file for design notes.</summary>
public sealed class ProjectRepository : IProjectRepository
{
    private const string SelectAllSql = """
        SELECT ProjectId, ProjectNumber, DisplayName, AccountSub, HomeRegionGroup, Bucket, WorkerSaEmail,
               BillingEnabled, ApisEnabledJson, QuotaJson, QuotaCheckedAt, LastGoodZone,
               SetupCompletedAt, SmokeTestPassedAt, IsActive
        FROM Projects;
        """;

    private const string UpsertSql = """
        INSERT INTO Projects (
            ProjectId, ProjectNumber, DisplayName, AccountSub, HomeRegionGroup, Bucket, WorkerSaEmail,
            BillingEnabled, ApisEnabledJson, QuotaJson, QuotaCheckedAt, LastGoodZone,
            SetupCompletedAt, SmokeTestPassedAt, IsActive)
        VALUES (
            @ProjectId, @ProjectNumber, @DisplayName, @AccountSub, @HomeRegionGroup, @Bucket, @WorkerSaEmail,
            @BillingEnabled, @ApisEnabledJson, @QuotaJson, @QuotaCheckedAt, @LastGoodZone,
            @SetupCompletedAt, @SmokeTestPassedAt, @IsActive)
        ON CONFLICT(ProjectId) DO UPDATE SET
            ProjectNumber = excluded.ProjectNumber, DisplayName = excluded.DisplayName, AccountSub = excluded.AccountSub,
            HomeRegionGroup = excluded.HomeRegionGroup, Bucket = excluded.Bucket, WorkerSaEmail = excluded.WorkerSaEmail,
            BillingEnabled = excluded.BillingEnabled, ApisEnabledJson = excluded.ApisEnabledJson, QuotaJson = excluded.QuotaJson,
            QuotaCheckedAt = excluded.QuotaCheckedAt, LastGoodZone = excluded.LastGoodZone,
            SetupCompletedAt = excluded.SetupCompletedAt, SmokeTestPassedAt = excluded.SmokeTestPassedAt, IsActive = excluded.IsActive;
        """;

    private readonly SqliteDatabase _database;

    public ProjectRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<ProjectRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(SelectAllSql, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<Row>(command).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task UpsertAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(UpsertSql, ToRow(project), cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    private static ProjectRecord ToRecord(Row row) => new(
        ProjectId: row.ProjectId,
        ProjectNumber: row.ProjectNumber,
        DisplayName: row.DisplayName,
        AccountSub: row.AccountSub,
        HomeRegionGroup: row.HomeRegionGroup,
        Bucket: row.Bucket,
        WorkerSaEmail: row.WorkerSaEmail,
        BillingEnabled: Iso.ToBool(row.BillingEnabled),
        ApisEnabledJson: row.ApisEnabledJson,
        QuotaJson: row.QuotaJson,
        QuotaCheckedAt: Iso.Parse(row.QuotaCheckedAt),
        LastGoodZone: row.LastGoodZone,
        SetupCompletedAt: Iso.Parse(row.SetupCompletedAt),
        SmokeTestPassedAt: Iso.Parse(row.SmokeTestPassedAt),
        IsActive: Iso.ToBool(row.IsActive));

    private static Row ToRow(ProjectRecord project) => new()
    {
        ProjectId = project.ProjectId,
        ProjectNumber = project.ProjectNumber,
        DisplayName = project.DisplayName,
        AccountSub = project.AccountSub,
        HomeRegionGroup = project.HomeRegionGroup,
        Bucket = project.Bucket,
        WorkerSaEmail = project.WorkerSaEmail,
        BillingEnabled = Iso.ToLong(project.BillingEnabled),
        ApisEnabledJson = project.ApisEnabledJson,
        QuotaJson = project.QuotaJson,
        QuotaCheckedAt = Iso.Format(project.QuotaCheckedAt),
        LastGoodZone = project.LastGoodZone,
        SetupCompletedAt = Iso.Format(project.SetupCompletedAt),
        SmokeTestPassedAt = Iso.Format(project.SmokeTestPassedAt),
        IsActive = Iso.ToLong(project.IsActive),
    };

    private sealed class Row
    {
        public string ProjectId { get; set; } = "";
        public string? ProjectNumber { get; set; }
        public string? DisplayName { get; set; }
        public string? AccountSub { get; set; }
        public string? HomeRegionGroup { get; set; }
        public string? Bucket { get; set; }
        public string? WorkerSaEmail { get; set; }
        public long? BillingEnabled { get; set; }
        public string? ApisEnabledJson { get; set; }
        public string? QuotaJson { get; set; }
        public string? QuotaCheckedAt { get; set; }
        public string? LastGoodZone { get; set; }
        public string? SetupCompletedAt { get; set; }
        public string? SmokeTestPassedAt { get; set; }
        public long? IsActive { get; set; }
    }
}
