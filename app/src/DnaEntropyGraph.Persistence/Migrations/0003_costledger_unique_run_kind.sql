-- Issue #109: one ledger row per (run, kind). Two writers recording the same ended run at once (the runner and the reconciler)
-- must not both book it; the repository inserts with INSERT OR IGNORE and this index is what makes the second one a no-op.
-- Rows with no run (RunId NULL) are not covered: nothing writes them today and they may repeat.
-- Numbered 0003 because 0002 belongs to the Spot-preemption lane (run preemption count).
CREATE UNIQUE INDEX IF NOT EXISTS ux_CostLedger_RunId_Kind ON CostLedger (RunId, Kind) WHERE RunId IS NOT NULL;
