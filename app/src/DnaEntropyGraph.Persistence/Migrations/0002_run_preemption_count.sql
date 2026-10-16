-- Issue #91: how many times Google took back this run's Spot VM (0 or 1: the runner makes one automatic on-demand retry).
ALTER TABLE Runs ADD COLUMN PreemptionCount INTEGER NOT NULL DEFAULT 0;
