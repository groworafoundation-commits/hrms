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
IF NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.interface WHERE OPTIONID = 'H208')
    INSERT HRMSINTER.interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
    VALUES ('H208', 'Attendance Devices', '1', 27, 'HRMS', '1', 'admin/attendance/devices', '0', 'HRMS', 0, 'laptop', 0, 0, '', 0, 0, 0);

DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES ('HRM-MGR','H208',1,1,1,1,0,10), ('HRM-ADM','H208',1,1,1,1,0,9);
INSERT HRMSINTER.interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT 'tables' AS what, COUNT(*) AS n FROM sys.tables WHERE schema_id = SCHEMA_ID('hrms') AND name LIKE 'AttDevice%'
UNION ALL SELECT 'menu', COUNT(*) FROM HRMSINTER.interface.interface WHERE OPTIONID = 'H208'
UNION ALL SELECT 'rights', COUNT(*) FROM HRMSINTER.interface.accessright WHERE optionid = 'H208';
