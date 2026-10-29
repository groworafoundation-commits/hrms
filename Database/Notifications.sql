-- In-app notifications and an email outbox (run against HRMSMAIN; safe to run again).
SET NOCOUNT ON;
IF SCHEMA_ID('hrms') IS NULL EXEC('CREATE SCHEMA hrms');

IF OBJECT_ID('hrms.Notification') IS NULL
BEGIN
    CREATE TABLE hrms.Notification (
        Id bigint IDENTITY(1,1) PRIMARY KEY,
        UserId nvarchar(50) NOT NULL,
        Kind nvarchar(30) NOT NULL,                 -- leave, attendance, system
        Title nvarchar(200) NOT NULL,
        Body nvarchar(500) NULL,
        Link nvarchar(200) NULL,                    -- page to open when it is clicked
        CreatedAt datetime NOT NULL CONSTRAINT DF_Notification_At DEFAULT GETDATE(),
        ReadAt datetime NULL
    );
    CREATE INDEX IX_Notification_User ON hrms.Notification (UserId, ReadAt, Id);
END;

IF OBJECT_ID('hrms.EmailOutbox') IS NULL
BEGIN
    CREATE TABLE hrms.EmailOutbox (
        Id bigint IDENTITY(1,1) PRIMARY KEY,
        ToUser nvarchar(50) NULL,
        ToAddress nvarchar(200) NOT NULL,
        Subject nvarchar(200) NOT NULL,
        Body nvarchar(max) NOT NULL,
        Status nvarchar(12) NOT NULL CONSTRAINT DF_EmailOutbox_Status DEFAULT 'Pending',   -- Pending, Sent, Failed, Skipped
        Attempts int NOT NULL CONSTRAINT DF_EmailOutbox_Att DEFAULT 0,
        CreatedAt datetime NOT NULL CONSTRAINT DF_EmailOutbox_At DEFAULT GETDATE(),
        SentAt datetime NULL,
        Error nvarchar(400) NULL
    );
    CREATE INDEX IX_EmailOutbox_Status ON hrms.EmailOutbox (Status, Id);
END;

-- menu and rights (HRMSINTER): an admin page to see the outbox and send a test email
IF NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.interface WHERE OPTIONID = 'H209')
    INSERT HRMSINTER.interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
    VALUES ('H209', 'Email and Alerts', '1', 28, 'HRMS', '1', 'admin/email', '0', 'HRMS', 0, 'bell', 0, 0, '', 0, 0, 0);
DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES ('HRM-MGR','H209',1,1,1,0,0,10), ('HRM-ADM','H209',1,1,1,1,0,9);
INSERT HRMSINTER.interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT (SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('hrms') AND name IN ('Notification','EmailOutbox')) AS tables,
       (SELECT COUNT(*) FROM HRMSINTER.interface.interface WHERE OPTIONID = 'H209') AS menu;
