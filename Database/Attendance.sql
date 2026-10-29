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
UPDATE HRMSINTER.interface.interface SET SEQUENCE = CASE OPTIONID WHEN 'H101' THEN 1 WHEN 'H106' THEN 2 WHEN 'H102' THEN 3 WHEN 'H103' THEN 4 WHEN 'H104' THEN 5 WHEN 'H105' THEN 6 ELSE SEQUENCE END
WHERE OPTIONID IN ('H101','H102','H103','H104','H105','H106');

DECLARE @o TABLE (id nvarchar(20), name nvarchar(80), route nvarchar(500), icon nvarchar(100), seq int);
INSERT @o VALUES
 ('H106', 'My Attendance',       'my/attendance',            'clock',    2),
 ('H205', 'Attendance',          'admin/attendance',         'clock',   24),
 ('H206', 'Attendance Requests', 'admin/attendance/requests','check',   25),
 ('H207', 'Shifts and Holidays', 'admin/attendance/setup',   'settings',26);

INSERT HRMSINTER.interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
SELECT id, name, '1', seq, 'HRMS', '1', route, '0', 'HRMS', 0, icon, 0, 0, '', 0, 0, 0 FROM @o o
WHERE NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.interface i WHERE i.OPTIONID = o.id);

-- (role, option, view, add, modify, delete, print, level): punch = add, correction request = modify, cancel request = delete
DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES
 ('HRM-EMP','H106',1,1,1,1,0,8),
 ('HRM-MGR','H106',1,1,1,1,0,10), ('HRM-MGR','H205',1,1,1,1,1,10), ('HRM-MGR','H206',1,1,1,1,0,10), ('HRM-MGR','H207',1,1,1,1,0,10),
 ('HRM-ADM','H106',1,1,1,1,0,9),  ('HRM-ADM','H205',1,1,1,1,1,9),  ('HRM-ADM','H206',1,1,1,1,1,9),  ('HRM-ADM','H207',1,1,1,1,1,9);

INSERT HRMSINTER.interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT 'tables' AS what, COUNT(*) AS n FROM sys.tables WHERE schema_id = SCHEMA_ID('hrms')
UNION ALL SELECT 'shifts', COUNT(*) FROM hrms.AttShift
UNION ALL SELECT 'menus', COUNT(*) FROM HRMSINTER.interface.interface WHERE OPTIONID IN ('H106','H205','H206','H207')
UNION ALL SELECT 'rights', COUNT(*) FROM HRMSINTER.interface.accessright WHERE optionid IN ('H106','H205','H206','H207');
