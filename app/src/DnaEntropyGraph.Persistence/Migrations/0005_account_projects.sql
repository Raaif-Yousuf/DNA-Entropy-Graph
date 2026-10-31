-- Issue #260: the Google Cloud projects each account on this PC has chosen. Projects.AccountSub holds one account only (the first to choose the
-- project, set once), so it cannot say that two accounts both use the same lab project; this table can. Written when a project is chosen
-- (Settings switcher, for both the project it leaves and the one it picks; the wizard's project step; a run starting) and whenever the Settings
-- list is built (it records the account's current project, so one chosen before this table existed is picked up the first time Settings opens).
-- Read by the Settings project list, which also reads the projects of the account's own run rows, so history from before this table is still offered.
CREATE TABLE AccountProjects (
  AccountSub TEXT NOT NULL REFERENCES Accounts(Sub),
  ProjectId TEXT NOT NULL REFERENCES Projects(ProjectId),
  PRIMARY KEY (AccountSub, ProjectId));
