using Dapper;

namespace Hrms.Api;

// Tables, menus and rights for asset handover and clearance. The hosted database cannot be reached from outside, so the API creates what it
// needs itself when it starts. Every statement is safe to run again and only adds things; nothing existing is changed or removed.
public static class AssetSchema
{
    // Menu options (interface.interface): H107 for employees, H301 and H302 for HR.
    const string Menus = @"
DECLARE @o TABLE (id nvarchar(20), name nvarchar(80), route nvarchar(500), icon nvarchar(100), seq int);
INSERT @o VALUES
 ('H107', 'My Assets',  'my/assets',       'laptop',  7),
 ('H301', 'Assets',     'admin/assets',    'laptop', 27),
 ('H302', 'Clearance',  'admin/clearance', 'check',  28);
INSERT interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
SELECT id, name, '1', seq, 'HRMS', '1', route, '0', 'HRMS', 0, icon, 0, 0, '', 0, 0, 0 FROM @o o
WHERE NOT EXISTS (SELECT 1 FROM interface.interface i WHERE i.OPTIONID = o.id);

-- (role, option, view, add, modify, delete, print): the level is copied from the role's attendance right so approval levels stay consistent
DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit);
INSERT @r VALUES
 ('HRM-EMP','H107',1,1,0,0,0),
 ('HRM-MGR','H107',1,1,0,0,0), ('HRM-MGR','H301',1,1,1,0,1), ('HRM-MGR','H302',1,1,1,0,1),
 ('HRM-ADM','H107',1,1,1,1,1), ('HRM-ADM','H301',1,1,1,1,1), ('HRM-ADM','H302',1,1,1,1,1);
INSERT interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT x.role, x.opt, x.a, x.m, x.d, x.v,
       CASE WHEN x.role = 'HRM-ADM' AND x.opt IN ('H301','H302') THEN 10
            ELSE ISNULL((SELECT TOP 1 CAST(ar.[level] AS int) FROM interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid IN ('H106','H201') ORDER BY CASE ar.optionid WHEN 'H106' THEN 0 ELSE 1 END), 10) END,
       x.p, 0
FROM @r x
WHERE EXISTS (SELECT 1 FROM interface.Roledef d WHERE d.roleid = x.role)
  AND NOT EXISTS (SELECT 1 FROM interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);";

    static readonly string[] Batches =
    [
        "IF SCHEMA_ID(N'hrms') IS NULL EXEC('CREATE SCHEMA hrms')",

        @"IF OBJECT_ID(N'hrms.Asset') IS NULL
CREATE TABLE hrms.Asset (
    Id int IDENTITY(1,1) PRIMARY KEY,
    AssetCode nvarchar(30) NOT NULL,
    Category nvarchar(30) NOT NULL,
    Name nvarchar(150) NOT NULL,
    SerialNo nvarchar(100) NOT NULL,
    Value decimal(12,2) NULL,
    PurchaseDate date NULL,
    Status nvarchar(20) NOT NULL CONSTRAINT DF_Asset_Status DEFAULT 'InStock',     -- InStock, Issued, Disposed
    Notes nvarchar(300) NULL,
    CreatedAt datetime NOT NULL CONSTRAINT DF_Asset_Created DEFAULT GETDATE(),
    CreatedBy nvarchar(50) NULL,
    Deleted bit NOT NULL CONSTRAINT DF_Asset_Deleted DEFAULT 0)",

        // one tag and one serial number per asset, as the ERP's register requires (serials are not duplicated)
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Asset_Code' AND object_id = OBJECT_ID(N'hrms.Asset')) CREATE UNIQUE INDEX UX_Asset_Code ON hrms.Asset (AssetCode) WHERE Deleted = 0",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Asset_Serial' AND object_id = OBJECT_ID(N'hrms.Asset')) CREATE UNIQUE INDEX UX_Asset_Serial ON hrms.Asset (SerialNo) WHERE Deleted = 0",

        // one row per handover: the issue and, later, its return (the ERP's Asset Issue document and the Asset Return pulled from it)
        @"IF OBJECT_ID(N'hrms.AssetCustody') IS NULL
CREATE TABLE hrms.AssetCustody (
    Id int IDENTITY(1,1) PRIMARY KEY,
    DocNo nvarchar(30) NULL,
    AssetId int NOT NULL REFERENCES hrms.Asset(Id),
    EmpCode nvarchar(50) NOT NULL,
    IssueDate date NOT NULL,
    ConditionOut nvarchar(100) NULL,
    IssueNote nvarchar(300) NULL,
    IssuedBy nvarchar(50) NOT NULL,
    IssueStatus nvarchar(15) NOT NULL,                  -- Pending, Approved, Rejected
    IssueApprovedBy nvarchar(50) NULL,
    IssueApprovedAt datetime NULL,
    IssueDecisionNote nvarchar(300) NULL,
    AcceptedAt datetime NULL,                           -- the employee confirmed receiving it
    ReturnDocNo nvarchar(30) NULL,
    ReturnDate date NULL,
    ConditionIn nvarchar(100) NULL,
    ReturnNote nvarchar(300) NULL,
    ReturnedTo nvarchar(50) NULL,
    ReturnStatus nvarchar(15) NULL,                     -- Pending, Approved, Rejected
    ReturnApprovedBy nvarchar(50) NULL,
    ReturnApprovedAt datetime NULL,
    ReturnDecisionNote nvarchar(300) NULL,
    CreatedAt datetime NOT NULL CONSTRAINT DF_Custody_Created DEFAULT GETDATE())",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Custody_Emp' AND object_id = OBJECT_ID(N'hrms.AssetCustody')) CREATE INDEX IX_Custody_Emp ON hrms.AssetCustody (EmpCode)",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Custody_Asset' AND object_id = OBJECT_ID(N'hrms.AssetCustody')) CREATE INDEX IX_Custody_Asset ON hrms.AssetCustody (AssetId)",

        @"IF OBJECT_ID(N'hrms.Clearance') IS NULL
CREATE TABLE hrms.Clearance (
    Id int IDENTITY(1,1) PRIMARY KEY,
    EmpCode nvarchar(50) NOT NULL,
    LastWorkingDay date NULL,
    Reason nvarchar(200) NULL,
    Status nvarchar(15) NOT NULL,                       -- Open, Cleared, Cancelled
    StartedBy nvarchar(50) NOT NULL,
    StartedAt datetime NOT NULL CONSTRAINT DF_Clearance_Started DEFAULT GETDATE(),
    ClearedBy nvarchar(50) NULL,
    ClearedAt datetime NULL)",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Clearance_Emp' AND object_id = OBJECT_ID(N'hrms.Clearance')) CREATE INDEX IX_Clearance_Emp ON hrms.Clearance (EmpCode)",

        @"IF OBJECT_ID(N'hrms.ClearanceItem') IS NULL
CREATE TABLE hrms.ClearanceItem (
    Id int IDENTITY(1,1) PRIMARY KEY,
    ClearanceId int NOT NULL REFERENCES hrms.Clearance(Id),
    Title nvarchar(150) NOT NULL,
    Done bit NOT NULL CONSTRAINT DF_ClItem_Done DEFAULT 0,
    DoneBy nvarchar(50) NULL,
    DoneAt datetime NULL,
    Note nvarchar(300) NULL)",

        // transfer between employees: the ERP's Asset Transfer (613) and the receiver's Asset Transfer Acceptance (619)
        @"IF OBJECT_ID(N'hrms.AssetTransfer') IS NULL
CREATE TABLE hrms.AssetTransfer (
    Id int IDENTITY(1,1) PRIMARY KEY,
    DocNo nvarchar(30) NULL,
    AssetId int NOT NULL REFERENCES hrms.Asset(Id),
    FromCustodyId int NOT NULL REFERENCES hrms.AssetCustody(Id),
    FromEmp nvarchar(50) NOT NULL,
    ToEmp nvarchar(50) NOT NULL,
    TransferDate date NOT NULL,
    Condition nvarchar(100) NULL,
    Note nvarchar(300) NULL,
    PreparedBy nvarchar(50) NOT NULL,
    Status nvarchar(15) NOT NULL,                       -- Pending (waiting approval), Accepting (waiting for the receiver), Completed, Rejected
    ApprovedBy nvarchar(50) NULL,
    ApprovedAt datetime NULL,
    DecisionNote nvarchar(300) NULL,
    AcceptedBy nvarchar(50) NULL,
    AcceptedAt datetime NULL,
    NewCustodyId int NULL,
    CreatedAt datetime NOT NULL CONSTRAINT DF_Transfer_Created DEFAULT GETDATE())",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transfer_Custody' AND object_id = OBJECT_ID(N'hrms.AssetTransfer')) CREATE INDEX IX_Transfer_Custody ON hrms.AssetTransfer (FromCustodyId, Status)",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transfer_To' AND object_id = OBJECT_ID(N'hrms.AssetTransfer')) CREATE INDEX IX_Transfer_To ON hrms.AssetTransfer (ToEmp, Status)",

        // photos and signed forms, kept inside the database so they are backed up with everything else
        @"IF OBJECT_ID(N'hrms.AssetFile') IS NULL
CREATE TABLE hrms.AssetFile (
    Id int IDENTITY(1,1) PRIMARY KEY,
    AssetId int NOT NULL REFERENCES hrms.Asset(Id),
    CustodyId int NULL,
    Kind nvarchar(20) NOT NULL,                         -- Photo, Handover form, Other
    FileName nvarchar(200) NOT NULL,
    ContentType nvarchar(100) NOT NULL,
    SizeBytes int NOT NULL,
    Data varbinary(max) NOT NULL,
    UploadedBy nvarchar(50) NOT NULL,
    UploadedAt datetime NOT NULL CONSTRAINT DF_AssetFile_At DEFAULT GETDATE(),
    Deleted bit NOT NULL CONSTRAINT DF_AssetFile_Del DEFAULT 0)",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AssetFile_Asset' AND object_id = OBJECT_ID(N'hrms.AssetFile')) CREATE INDEX IX_AssetFile_Asset ON hrms.AssetFile (AssetId)",

        // one warning per person and last working day, so HR is not told again every few hours
        @"IF OBJECT_ID(N'hrms.AssetAlert') IS NULL
CREATE TABLE hrms.AssetAlert (
    EmpCode nvarchar(50) NOT NULL,
    TermDate date NOT NULL,
    NotifiedAt datetime NOT NULL CONSTRAINT DF_AssetAlert_At DEFAULT GETDATE(),
    PRIMARY KEY (EmpCode, TermDate))",

        @"IF OBJECT_ID(N'hrms.Migration') IS NULL
CREATE TABLE hrms.Migration (
    Name nvarchar(100) NOT NULL PRIMARY KEY,
    AppliedAt datetime NOT NULL CONSTRAINT DF_Migration_At DEFAULT GETDATE())",

        Menus,

        // once: the administrator role is at the document level (10) for assets and clearance, so it can approve asset handovers itself
        @"IF NOT EXISTS (SELECT 1 FROM hrms.Migration WHERE Name = 'asset-admin-level-10')
BEGIN
    UPDATE interface.accessright SET [level] = 10 WHERE roleid = 'HRM-ADM' AND optionid IN ('H301','H302') AND CAST([level] AS int) < 10;
    INSERT hrms.Migration (Name) VALUES ('asset-admin-level-10');
END",
    ];

    public static async Task Ensure(Db db, ILogger log)
    {
        try
        {
            await using var c = db.OpenWrite();
            foreach (var sql in Batches) await c.ExecuteAsync(sql);
            log.LogInformation("Asset tables and menus are in place.");
        }
        catch (Exception ex)
        {
            // never stop the site from starting; the asset screens will report the problem when used
            log.LogError(ex, "Could not set up the asset tables and menus.");
        }
    }
}
