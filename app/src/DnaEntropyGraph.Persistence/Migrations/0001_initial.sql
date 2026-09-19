-- Migration 0001: initial schema.
-- Verbatim from docs/superpowers/specs/2026-09-18-appendix-a-app-design.md
-- section 3 ("Local state model"), the authoritative DDL source per that
-- file's own header and docs/architecture.md section 6. Do not hand-edit a
-- column here without updating that section too (issue #67).
--
-- SqliteDatabase applies this inside one transaction and then sets
-- PRAGMA user_version = 1 itself (see SqliteDatabase.cs); this file does not
-- set user_version so migrations stay pure schema and can be replayed in
-- tests without a live connection's pragma state.

CREATE TABLE Accounts (
  Sub TEXT PRIMARY KEY, Email TEXT, DisplayName TEXT, LastSignedInUtc TEXT);

CREATE TABLE Projects (
  ProjectId TEXT PRIMARY KEY, ProjectNumber TEXT, DisplayName TEXT, AccountSub TEXT REFERENCES Accounts(Sub),
  HomeRegionGroup TEXT, Bucket TEXT, WorkerSaEmail TEXT, BillingEnabled INTEGER,
  ApisEnabledJson TEXT, QuotaJson TEXT, QuotaCheckedAt TEXT, LastGoodZone TEXT,
  SetupCompletedAt TEXT, SmokeTestPassedAt TEXT, IsActive INTEGER);

CREATE TABLE Runs (
  Id TEXT PRIMARY KEY,                     -- jobId yyyymmdd-hhmmss-xxxxxx
  Name TEXT NOT NULL, IsBatch INTEGER NOT NULL DEFAULT 0,
  Target TEXT NOT NULL,                    -- cloud | local
  Phase TEXT NOT NULL,                     -- see JobPhase
  ErrorCode TEXT, ErrorDetail TEXT,
  CreatedAt TEXT NOT NULL, StartedAt TEXT, VmReadyAt TEXT, FinishedAt TEXT,
  OptionsJson TEXT NOT NULL, ManifestJson TEXT,
  ProjectId TEXT REFERENCES Projects(ProjectId), Bucket TEXT, JobPrefix TEXT,   -- jobs/<Id>/
  VmName TEXT, Zone TEXT, MachineType TEXT, GpuType TEXT, IsSpot INTEGER, VmReused INTEGER,
  LastProgressSeq INTEGER NOT NULL DEFAULT 0, LastHeartbeatAt TEXT, StatusGeneration INTEGER,
  OutputDir TEXT, EstimatedCostUsd REAL, ActualCostUsd REAL, VmSeconds INTEGER,
  CloudResultsExpireAt TEXT, CloudResultsDeleted INTEGER NOT NULL DEFAULT 0,
  AppVersion TEXT, WorkerVersion TEXT, WorkerImageDigest TEXT, ContractVersion INTEGER,
  InstallationId TEXT, Imported INTEGER NOT NULL DEFAULT 0,
  -- Deviation from the appendix's literal DDL, made here rather than in a
  -- later migration: issue #141 (notes and tags on runs, searchable) has no
  -- column to land in otherwise, and this table has zero production rows
  -- to migrate tonight - the cheapest possible time to add them. See the
  -- DECISION issue this lane filed (WAVE_BRIEF.md Lane D report).
  Notes TEXT, TagsJson TEXT);

CREATE TABLE RunInputs (
  Id INTEGER PRIMARY KEY, RunId TEXT REFERENCES Runs(Id) ON DELETE CASCADE, Idx INTEGER,
  OriginalPath TEXT, OriginalName TEXT, LocalCopyPath TEXT, Kind TEXT, Sha256 TEXT, SizeBytes INTEGER,
  RecordCount INTEGER, TotalNt INTEGER, GeneCount INTEGER, RunName TEXT, CloudObject TEXT,
  Status TEXT, ErrorCode TEXT, ErrorDetail TEXT, StatsJson TEXT, NoticesJson TEXT);

CREATE TABLE RunOutputs (
  Id INTEGER PRIMARY KEY, RunId TEXT REFERENCES Runs(Id) ON DELETE CASCADE, InputIdx INTEGER,
  Kind TEXT, FileName TEXT, CloudObject TEXT, LocalPath TEXT, SizeBytes INTEGER, Sha256 TEXT,
  Downloaded INTEGER NOT NULL DEFAULT 0);

CREATE TABLE RunEvents (                    -- app-side + worker progress, for the log view and support bundle
  Id INTEGER PRIMARY KEY, RunId TEXT REFERENCES Runs(Id) ON DELETE CASCADE, Seq INTEGER,
  Ts TEXT, Source TEXT, Stage TEXT, Level TEXT, Message TEXT, DataJson TEXT);
CREATE INDEX IX_RunEvents_Run ON RunEvents(RunId, Seq);

CREATE TABLE CloudResources (
  Id INTEGER PRIMARY KEY, Kind TEXT,        -- vm | disk | bucket | serviceaccount | image
  Name TEXT, ProjectId TEXT, Location TEXT, CreatedAt TEXT, CreatedByInstall TEXT,
  LastKnownState TEXT, LastCheckedAt TEXT, MachineType TEXT, GpuType TEXT, IsSpot INTEGER, DiskGb INTEGER,
  HourlyRateUsd REAL, RunningSecondsAccum INTEGER DEFAULT 0, LastStartedAt TEXT, CurrentJobId TEXT,
  LifecyclePolicy TEXT, KeepUntilUtc TEXT, DeletedAt TEXT, LabelsJson TEXT, UNIQUE(Kind, ProjectId, Name));

CREATE TABLE CostLedger (
  Id INTEGER PRIMARY KEY, RunId TEXT NULL, ResourceId INTEGER NULL, Kind TEXT,  -- vm_runtime | disk | storage
  StartedUtc TEXT, EndedUtc TEXT, UsdEst REAL);

CREATE TABLE MonthlySpend (Month TEXT PRIMARY KEY, EstimatedUsd REAL);   -- denormalised for the cap banner

CREATE TABLE LocalEngine (
  Id INTEGER PRIMARY KEY CHECK (Id = 1), InstallPath TEXT, Mode TEXT,   -- native | wsl2
  PythonVersion TEXT, TorchVersion TEXT, Evo2Version TEXT, CudaVersion TEXT, GpuName TEXT, VramGb REAL,
  InstalledUtc TEXT, LastHealthUtc TEXT, HealthJson TEXT);
