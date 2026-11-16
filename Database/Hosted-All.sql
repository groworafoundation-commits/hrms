-- Growora HRMS: all schema/menu/rights scripts for the single hosted database (db79415). Safe to run again; adds objects only.
-- Order: Seed-Access, Security, Attendance, AttendanceDevices, Notifications. Local-Synonyms.sql is local-only and left out.

-- ################ Seed-Access.sql ################
-- HRMS menus, roles and rights (run once against HRMSINTER; safe to run again).
-- HRMS menu options live in interface.interface with LEVELCODE = 'HRMS'. PRGNAME holds the in-app route, PICTURE1 the icon name.
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM interface.Application WHERE Code = 'HRMS')
    INSERT interface.Application (Code, AppName, AppExeName, Locationcode, flag) VALUES ('HRMS', 'HRMS', 'HrmsApp.exe', 26, 1);

DECLARE @o TABLE (id nvarchar(20), name nvarchar(80), route nvarchar(500), icon nvarchar(100), seq int);
INSERT @o VALUES
 ('H101', 'My Dashboard',    'my',                'home',           1),
 ('H102', 'My Leave',        'my/leave',          'calendar',       2),
 ('H103', 'My Calendar',     'my/calendar',       'calendar-check', 3),
 ('H104', 'My Payslips',     'my/payslips',       'wallet',         4),
 ('H105', 'My Profile',      'my/profile',        'user',           5),
 ('H201', 'Leave Approvals', 'admin/approvals',   'check',         20),
 ('H202', 'Users',           'admin/users',       'users',         21),
 ('H203', 'Roles and Access','admin/roles',       'shield',        22),
 ('H204', 'Menus',           'admin/menus',       'menu',          23);

INSERT interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
SELECT id, name, '1', seq, 'HRMS', '1', route, '0', 'HRMS', 0, icon, 0, 0, '', 0, 0, 0 FROM @o o
WHERE NOT EXISTS (SELECT 1 FROM interface.interface i WHERE i.OPTIONID = o.id);

-- Roles: employee self-service, manager, administrator.
INSERT interface.Roledef (roleid, rolename, authlevel, Discount, Transactions)
SELECT r.id, r.name, r.lvl, 0, '0' FROM (VALUES ('HRM-EMP', 'HRMS Employee', 1), ('HRM-MGR', 'HRMS Manager', 5), ('HRM-ADM', 'HRMS Administrator', 10)) r(id, name, lvl)
WHERE NOT EXISTS (SELECT 1 FROM interface.Roledef d WHERE d.roleid = r.id);

-- Rights: (role, option, view, add, modify, delete, print, level)
DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES
 ('HRM-EMP','H101',1,0,0,0,0,1), ('HRM-EMP','H102',1,1,1,1,0,1), ('HRM-EMP','H103',1,0,0,0,0,1), ('HRM-EMP','H104',1,0,0,0,1,1), ('HRM-EMP','H105',1,0,0,0,0,1),
 ('HRM-MGR','H101',1,0,0,0,0,5), ('HRM-MGR','H102',1,1,1,1,0,5), ('HRM-MGR','H103',1,0,0,0,0,5), ('HRM-MGR','H104',1,0,0,0,1,5), ('HRM-MGR','H105',1,0,0,0,0,5),
 ('HRM-MGR','H201',1,1,1,1,0,5), ('HRM-MGR','137',1,0,0,0,1,5), ('HRM-MGR','210',1,1,1,0,1,5), ('HRM-MGR','162',1,0,0,0,1,5), ('HRM-MGR','164',1,0,0,0,1,5),
 ('HRM-ADM','H101',1,1,1,1,1,10), ('HRM-ADM','H102',1,1,1,1,1,10), ('HRM-ADM','H103',1,1,1,1,1,10), ('HRM-ADM','H104',1,1,1,1,1,10), ('HRM-ADM','H105',1,1,1,1,1,10),
 ('HRM-ADM','H201',1,1,1,1,1,10), ('HRM-ADM','H202',1,1,1,1,1,10), ('HRM-ADM','H203',1,1,1,1,1,10), ('HRM-ADM','H204',1,1,1,1,1,10),
 ('HRM-ADM','137',1,1,1,1,1,10), ('HRM-ADM','207',1,1,1,1,1,10), ('HRM-ADM','208',1,1,1,1,1,10), ('HRM-ADM','209',1,1,1,1,1,10), ('HRM-ADM','210',1,1,1,1,1,10),
 ('HRM-ADM','1012',1,1,1,1,1,10), ('HRM-ADM','1765',1,1,1,1,1,10), ('HRM-ADM','162',1,1,1,1,1,10), ('HRM-ADM','164',1,1,1,1,1,10);

INSERT interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT 'options' AS what, COUNT(*) AS n FROM interface.interface WHERE LEVELCODE = 'HRMS'
UNION ALL SELECT 'roles', COUNT(*) FROM interface.Roledef WHERE roleid LIKE 'HRM-%'
UNION ALL SELECT 'rights', COUNT(*) FROM interface.accessright WHERE roleid LIKE 'HRM-%';

-- Approval hierarchy: employee 8 -> administrator 9 -> manager 10 (10 is the leave document level, so a manager's approval is final).
-- Only the HRMS menus carry the hierarchy level; the ERP payroll screens stay at level 10 so entries made there are never left unauthorised.
UPDATE interface.Roledef SET authlevel = CASE roleid WHEN 'HRM-EMP' THEN 8 WHEN 'HRM-ADM' THEN 9 WHEN 'HRM-MGR' THEN 10 END WHERE roleid IN ('HRM-EMP', 'HRM-ADM', 'HRM-MGR');
UPDATE interface.accessright SET [level] = CASE roleid WHEN 'HRM-EMP' THEN 8 WHEN 'HRM-ADM' THEN 9 WHEN 'HRM-MGR' THEN 10 END WHERE roleid IN ('HRM-EMP', 'HRM-ADM', 'HRM-MGR') AND optionid LIKE 'H%';
UPDATE interface.accessright SET [level] = 10 WHERE roleid = 'HRM-MGR' AND optionid NOT LIKE 'H%';

GO

-- ################ Security.sql ################
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

GO

-- ################ Attendance.sql ################
-- Attendance for the HRMS: tables in HRMSMAIN (schema hrms), menus and rights in HRMSINTER. Safe to run again.
-- Run this file against HRMSMAIN; it reaches HRMSINTER by three-part name.
SET NOCOUNT ON;

IF SCHEMA_ID('hrms') IS NULL EXEC('CREATE SCHEMA hrms');

IF OBJECT_ID('hrms.AttShift') IS NULL
CREATE TABLE hrms.AttShift (
    Id int IDENTITY(1,1) PRIMARY KEY,
    Name nvarchar(80) NOT NULL,
    StartTime time(0) NOT NULL,
    EndTime time(0) NOT NULL,
    GraceMinutes int NOT NULL CONSTRAINT DF_AttShift_Grace DEFAULT 10,
    FullDayMinutes int NOT NULL CONSTRAINT DF_AttShift_Full DEFAULT 480,     -- worked minutes that count as a full day
    HalfDayMinutes int NOT NULL CONSTRAINT DF_AttShift_Half DEFAULT 240,     -- worked minutes that count as a half day
    WeeklyOffs nvarchar(40) NOT NULL CONSTRAINT DF_AttShift_Off DEFAULT 'Sun', -- e.g. 'Sat,Sun'
    IsDefault bit NOT NULL CONSTRAINT DF_AttShift_Def DEFAULT 0,
    Active bit NOT NULL CONSTRAINT DF_AttShift_Act DEFAULT 1
);

IF OBJECT_ID('hrms.AttShiftAssign') IS NULL
BEGIN
    CREATE TABLE hrms.AttShiftAssign (
        Id int IDENTITY(1,1) PRIMARY KEY,
        EmpCode nvarchar(50) NOT NULL,
        ShiftId int NOT NULL,
        FromDate date NOT NULL
    );
    CREATE INDEX IX_AttShiftAssign_Emp ON hrms.AttShiftAssign (EmpCode, FromDate);
END;

IF OBJECT_ID('hrms.AttHoliday') IS NULL
CREATE TABLE hrms.AttHoliday (
    Id int IDENTITY(1,1) PRIMARY KEY,
    HolidayDate date NOT NULL,
    Name nvarchar(120) NOT NULL,
    Kind nvarchar(20) NOT NULL CONSTRAINT DF_AttHoliday_Kind DEFAULT 'Public'
);

IF OBJECT_ID('hrms.AttPunch') IS NULL
BEGIN
    CREATE TABLE hrms.AttPunch (
        Id bigint IDENTITY(1,1) PRIMARY KEY,
        EmpCode nvarchar(50) NOT NULL,
        PunchTime datetime NOT NULL,                -- always the server clock, so an employee cannot back-date a punch
        Direction varchar(3) NOT NULL,              -- IN / OUT
        Source nvarchar(20) NOT NULL CONSTRAINT DF_AttPunch_Src DEFAULT 'App',   -- App, Manual, Regularized
        WorkMode nvarchar(20) NULL,                 -- Office, Remote, Site
        Note nvarchar(300) NULL,
        DeviceInfo nvarchar(200) NULL,
        CreatedBy nvarchar(50) NULL,
        Deleted bit NOT NULL CONSTRAINT DF_AttPunch_Del DEFAULT 0,
        DeletedBy nvarchar(50) NULL
    );
    CREATE INDEX IX_AttPunch_Emp ON hrms.AttPunch (EmpCode, PunchTime) INCLUDE (Direction, Deleted);
    CREATE INDEX IX_AttPunch_Time ON hrms.AttPunch (PunchTime) INCLUDE (EmpCode, Direction, Deleted);
END;

IF OBJECT_ID('hrms.AttRegularization') IS NULL
CREATE TABLE hrms.AttRegularization (
    Id int IDENTITY(1,1) PRIMARY KEY,
    EmpCode nvarchar(50) NOT NULL,
    WorkDate date NOT NULL,
    InTime time(0) NULL,
    OutTime time(0) NULL,
    Reason nvarchar(300) NOT NULL,
    Status nvarchar(12) NOT NULL CONSTRAINT DF_AttReg_Status DEFAULT 'Pending',   -- Pending, Approved, Rejected, Cancelled
    RequestedAt datetime NOT NULL CONSTRAINT DF_AttReg_At DEFAULT GETDATE(),
    DecidedBy nvarchar(50) NULL,
    DecidedAt datetime NULL,
    DecisionNote nvarchar(300) NULL
);

IF NOT EXISTS (SELECT 1 FROM hrms.AttShift)
    INSERT hrms.AttShift (Name, StartTime, EndTime, GraceMinutes, FullDayMinutes, HalfDayMinutes, WeeklyOffs, IsDefault)
    VALUES ('General shift', '09:00', '18:00', 10, 480, 240, 'Sun', 1);

-- ===== menus and rights (HRMSINTER) =====
UPDATE interface.interface SET SEQUENCE = CASE OPTIONID WHEN 'H101' THEN 1 WHEN 'H106' THEN 2 WHEN 'H102' THEN 3 WHEN 'H103' THEN 4 WHEN 'H104' THEN 5 WHEN 'H105' THEN 6 ELSE SEQUENCE END
WHERE OPTIONID IN ('H101','H102','H103','H104','H105','H106');

DECLARE @o TABLE (id nvarchar(20), name nvarchar(80), route nvarchar(500), icon nvarchar(100), seq int);
INSERT @o VALUES
 ('H106', 'My Attendance',       'my/attendance',            'clock',    2),
 ('H205', 'Attendance',          'admin/attendance',         'clock',   24),
 ('H206', 'Attendance Requests', 'admin/attendance/requests','check',   25),
 ('H207', 'Shifts and Holidays', 'admin/attendance/setup',   'settings',26);

INSERT interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
SELECT id, name, '1', seq, 'HRMS', '1', route, '0', 'HRMS', 0, icon, 0, 0, '', 0, 0, 0 FROM @o o
WHERE NOT EXISTS (SELECT 1 FROM interface.interface i WHERE i.OPTIONID = o.id);

-- (role, option, view, add, modify, delete, print, level): punch = add, correction request = modify, cancel request = delete
DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES
 ('HRM-EMP','H106',1,1,1,1,0,8),
 ('HRM-MGR','H106',1,1,1,1,0,10), ('HRM-MGR','H205',1,1,1,1,1,10), ('HRM-MGR','H206',1,1,1,1,0,10), ('HRM-MGR','H207',1,1,1,1,0,10),
 ('HRM-ADM','H106',1,1,1,1,0,9),  ('HRM-ADM','H205',1,1,1,1,1,9),  ('HRM-ADM','H206',1,1,1,1,1,9),  ('HRM-ADM','H207',1,1,1,1,1,9);

INSERT interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT 'tables' AS what, COUNT(*) AS n FROM sys.tables WHERE schema_id = SCHEMA_ID('hrms')
UNION ALL SELECT 'shifts', COUNT(*) FROM hrms.AttShift
UNION ALL SELECT 'menus', COUNT(*) FROM interface.interface WHERE OPTIONID IN ('H106','H205','H206','H207')
UNION ALL SELECT 'rights', COUNT(*) FROM interface.accessright WHERE optionid IN ('H106','H205','H206','H207');

GO

-- ################ AttendanceDevices.sql ################
-- Punching machines (ZKTeco-style push) for the HRMS attendance. Run against HRMSMAIN; safe to run again.
SET NOCOUNT ON;

IF OBJECT_ID('hrms.AttDevice') IS NULL
CREATE TABLE hrms.AttDevice (
    Id int IDENTITY(1,1) PRIMARY KEY,
    Serial nvarchar(50) NOT NULL,                 -- the machine's serial number (SN), how it identifies itself
    Name nvarchar(80) NOT NULL,
    Location nvarchar(120) NULL,
    Active bit NOT NULL CONSTRAINT DF_AttDevice_Active DEFAULT 0,   -- a machine that has just appeared must be approved first
    FirstSeen datetime NOT NULL CONSTRAINT DF_AttDevice_First DEFAULT GETDATE(),
    LastSeen datetime NULL,
    LastIp nvarchar(60) NULL,
    CONSTRAINT UQ_AttDevice_Serial UNIQUE (Serial)
);

IF OBJECT_ID('hrms.AttDeviceUser') IS NULL
CREATE TABLE hrms.AttDeviceUser (
    Id int IDENTITY(1,1) PRIMARY KEY,
    Pin nvarchar(30) NOT NULL,                    -- the number the machine knows the person by
    EmpCode nvarchar(50) NOT NULL,
    CONSTRAINT UQ_AttDeviceUser_Pin UNIQUE (Pin),
    CONSTRAINT UQ_AttDeviceUser_Emp UNIQUE (EmpCode)
);

IF OBJECT_ID('hrms.AttDeviceLog') IS NULL
BEGIN
    CREATE TABLE hrms.AttDeviceLog (
        Id bigint IDENTITY(1,1) PRIMARY KEY,
        DeviceSerial nvarchar(50) NOT NULL,
        Pin nvarchar(30) NOT NULL,
        PunchTime datetime NOT NULL,              -- as stamped by the machine
        RawStatus nvarchar(10) NULL,
        Verify nvarchar(10) NULL,                 -- finger, face, card, password
        ReceivedAt datetime NOT NULL CONSTRAINT DF_AttDeviceLog_Rec DEFAULT GETDATE(),
        SkewSeconds int NULL,                     -- received minus machine time; large means the machine clock is wrong (or a backlog)
        EmpCode nvarchar(50) NULL,
        PunchId bigint NULL,
        Result nvarchar(20) NOT NULL              -- Imported, Unmatched, Ignored
    );
    CREATE UNIQUE INDEX UX_AttDeviceLog_Once ON hrms.AttDeviceLog (DeviceSerial, Pin, PunchTime);   -- a re-sent punch is never stored twice
    CREATE INDEX IX_AttDeviceLog_Result ON hrms.AttDeviceLog (Result, ReceivedAt);
END;

-- menu and rights (HRMSINTER)
IF NOT EXISTS (SELECT 1 FROM interface.interface WHERE OPTIONID = 'H208')
    INSERT interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
    VALUES ('H208', 'Attendance Devices', '1', 27, 'HRMS', '1', 'admin/attendance/devices', '0', 'HRMS', 0, 'laptop', 0, 0, '', 0, 0, 0);

DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES ('HRM-MGR','H208',1,1,1,1,0,10), ('HRM-ADM','H208',1,1,1,1,0,9);
INSERT interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT 'tables' AS what, COUNT(*) AS n FROM sys.tables WHERE schema_id = SCHEMA_ID('hrms') AND name LIKE 'AttDevice%'
UNION ALL SELECT 'menu', COUNT(*) FROM interface.interface WHERE OPTIONID = 'H208'
UNION ALL SELECT 'rights', COUNT(*) FROM interface.accessright WHERE optionid = 'H208';

GO

-- ################ Notifications.sql ################
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
IF NOT EXISTS (SELECT 1 FROM interface.interface WHERE OPTIONID = 'H209')
    INSERT interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
    VALUES ('H209', 'Email and Alerts', '1', 28, 'HRMS', '1', 'admin/email', '0', 'HRMS', 0, 'bell', 0, 0, '', 0, 0, 0);
DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES ('HRM-MGR','H209',1,1,1,0,0,10), ('HRM-ADM','H209',1,1,1,1,0,9);
INSERT interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT (SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('hrms') AND name IN ('Notification','EmailOutbox')) AS tables,
       (SELECT COUNT(*) FROM interface.interface WHERE OPTIONID = 'H209') AS menu;

GO
