-- Login security for the HRMS (run against HRMSINTER; safe to run again).
-- Passwords move to a salted PBKDF2 hash in PasswordHash. The plain-text password column is emptied by the API the first time it starts
-- (it hashes every existing password, then clears the original), so no readable password is left in HRMSINTER.
SET NOCOUNT ON;

IF COL_LENGTH('interface.Userdef', 'PasswordHash') IS NULL ALTER TABLE interface.Userdef ADD PasswordHash nvarchar(200) NULL;
IF COL_LENGTH('interface.Userdef', 'MustChange') IS NULL ALTER TABLE interface.Userdef ADD MustChange bit NOT NULL CONSTRAINT DF_Userdef_MustChange DEFAULT 0;
IF COL_LENGTH('interface.Userdef', 'FailedLogins') IS NULL ALTER TABLE interface.Userdef ADD FailedLogins int NOT NULL CONSTRAINT DF_Userdef_Failed DEFAULT 0;
IF COL_LENGTH('interface.Userdef', 'LockedUntil') IS NULL ALTER TABLE interface.Userdef ADD LockedUntil datetime NULL;
IF COL_LENGTH('interface.Userdef', 'LastLogin') IS NULL ALTER TABLE interface.Userdef ADD LastLogin datetime NULL;
IF COL_LENGTH('interface.Userdef', 'PasswordChangedAt') IS NULL ALTER TABLE interface.Userdef ADD PasswordChangedAt datetime NULL;

EXEC('SELECT COUNT(*) AS users, SUM(CASE WHEN PasswordHash IS NULL THEN 1 ELSE 0 END) AS still_plain FROM interface.Userdef');
