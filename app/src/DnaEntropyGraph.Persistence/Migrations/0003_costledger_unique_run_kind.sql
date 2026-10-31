-- Issue #109: one ledger row per (run, kind). Two writers recording the same ended run at once (the runner and the reconciler)
-- must not both book it; the repository inserts with INSERT OR IGNORE and this index is what makes the second one a no-op.
-- Rows with no run (RunId NULL) are not covered: nothing writes them today and they may repeat.
-- Numbered 0003 because 0002 belongs to the Spot-preemption lane (run preemption count).
-- A file that already holds duplicates (two writers racing before this index existed) would make CREATE UNIQUE INDEX fail on every
-- launch, so the later rows of each (RunId, Kind) are removed first, keeping the lowest Id.
DELETE FROM CostLedger
WHERE RunId IS NOT NULL
  AND Id NOT IN (SELECT MIN(Id) FROM CostLedger WHERE RunId IS NOT NULL GROUP BY RunId, Kind);

CREATE UNIQUE INDEX IF NOT EXISTS ux_CostLedger_RunId_Kind ON CostLedger (RunId, Kind) WHERE RunId IS NOT NULL;
