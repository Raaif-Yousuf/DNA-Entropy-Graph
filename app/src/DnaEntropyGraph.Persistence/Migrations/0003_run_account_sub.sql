-- Issue #830: the Google account (Accounts.Sub) a run was made as. NULL means the current account: a row whose project has no recorded account is never given a guessed one.
ALTER TABLE Runs ADD COLUMN AccountSub TEXT;
UPDATE Runs SET AccountSub = (SELECT p.AccountSub FROM Projects p WHERE p.ProjectId = Runs.ProjectId) WHERE AccountSub IS NULL AND ProjectId IS NOT NULL;
