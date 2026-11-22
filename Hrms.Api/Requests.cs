using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Requests other than ERP leave: On Duty, Work From Home, Short Break and Comp Off (earning a credit, and using one). They are kept in HRMS's own tables, follow the
// same approval route as leave (department head, then HR; managers go to HR and then the Director), count in attendance, and never write to payroll or the ERP.
// Supporting documents (for leave and for these) are kept inside the database, like the asset photos.

public static class RequestSchema
{
    public static readonly string[] Batches =
    [
        @"IF OBJECT_ID('hrms.Request') IS NULL
CREATE TABLE hrms.Request (
    Id int IDENTITY(1,1) PRIMARY KEY,
    TransNo nvarchar(30) NOT NULL,
    EmpCode nvarchar(50) NOT NULL,
    [Type] nvarchar(6) NOT NULL,
    FromDate date NOT NULL,
    ToDate date NOT NULL,
    FromTime time(0) NULL,
    ToTime time(0) NULL,
    Days decimal(9,2) NOT NULL CONSTRAINT DF_Request_Days DEFAULT 0,
    Minutes int NULL,
    Reason nvarchar(500) NULL,
    Remarks nvarchar(500) NULL,
    [Status] nvarchar(12) NOT NULL CONSTRAINT DF_Request_Status DEFAULT 'Pending',
    HeldBy nvarchar(50) NULL,
    [Level] int NOT NULL CONSTRAINT DF_Request_Level DEFAULT 0,
    FinalLevel int NOT NULL CONSTRAINT DF_Request_Final DEFAULT 0,
    DecisionNote nvarchar(500) NULL,
    DecidedAt datetime NULL,
    CreatedAt datetime NOT NULL CONSTRAINT DF_Request_Created DEFAULT GETDATE())",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Request_Emp' AND object_id = OBJECT_ID('hrms.Request')) CREATE INDEX IX_Request_Emp ON hrms.Request (EmpCode, FromDate)",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Request_Held' AND object_id = OBJECT_ID('hrms.Request')) CREATE INDEX IX_Request_Held ON hrms.Request (HeldBy, [Status])",
        @"IF OBJECT_ID('hrms.RequestStep') IS NULL
CREATE TABLE hrms.RequestStep (
    Id int IDENTITY(1,1) PRIMARY KEY,
    RequestId int NOT NULL,
    UserId nvarchar(50) NOT NULL,
    [Level] int NOT NULL CONSTRAINT DF_RequestStep_Level DEFAULT 0,
    [Action] nvarchar(12) NOT NULL,
    Note nvarchar(500) NULL,
    [At] datetime NOT NULL CONSTRAINT DF_RequestStep_At DEFAULT GETDATE())",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RequestStep_Req' AND object_id = OBJECT_ID('hrms.RequestStep')) CREATE INDEX IX_RequestStep_Req ON hrms.RequestStep (RequestId)",
        @"IF OBJECT_ID('hrms.CompOff') IS NULL
CREATE TABLE hrms.CompOff (
    Id int IDENTITY(1,1) PRIMARY KEY,
    EmpCode nvarchar(50) NOT NULL,
    RequestId int NULL,
    Credits decimal(9,2) NOT NULL,
    EarnedOn date NOT NULL,
    ExpiresOn date NOT NULL,
    Used decimal(9,2) NOT NULL CONSTRAINT DF_CompOff_Used DEFAULT 0)",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CompOff_Emp' AND object_id = OBJECT_ID('hrms.CompOff')) CREATE INDEX IX_CompOff_Emp ON hrms.CompOff (EmpCode, ExpiresOn)",
        @"IF OBJECT_ID('hrms.Attachment') IS NULL
CREATE TABLE hrms.Attachment (
    Id int IDENTITY(1,1) PRIMARY KEY,
    OwnerType nvarchar(10) NOT NULL,
    OwnerKey nvarchar(50) NOT NULL,
    EmpCode nvarchar(50) NOT NULL,
    FileName nvarchar(200) NOT NULL,
    ContentType nvarchar(100) NOT NULL,
    SizeBytes int NOT NULL,
    Data varbinary(max) NOT NULL,
    UploadedBy nvarchar(50) NULL,
    UploadedAt datetime NOT NULL CONSTRAINT DF_Attachment_At DEFAULT GETDATE(),
    Deleted bit NOT NULL CONSTRAINT DF_Attachment_Del DEFAULT 0)",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Attachment_Owner' AND object_id = OBJECT_ID('hrms.Attachment')) CREATE INDEX IX_Attachment_Owner ON hrms.Attachment (OwnerType, OwnerKey)",
        "IF COL_LENGTH('hrms.LeaveNote', 'Remarks') IS NULL ALTER TABLE hrms.LeaveNote ADD Remarks nvarchar(500) NULL",
    ];
}

public static class RequestTypes
{
    public static readonly Dictionary<string, string> Names = new() { ["OD"] = "On Duty", ["WFH"] = "Work From Home", ["SB"] = "Short Break", ["COC"] = "Comp Off Credit", ["COL"] = "Comp Off Leave" };
    public static string Name(string t) => Names.TryGetValue(t, out var n) ? n : t;
    public static string Prefix(string t) => t;
}

public class RequestRow
{
    public int Id { get; set; }
    public string TransNo { get; set; } = "";
    public string EmpCode { get; set; } = "";
    public string? Name { get; set; }
    public string Type { get; set; } = "";
    public string TypeName => RequestTypes.Name(Type);
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public TimeSpan? FromTime { get; set; }
    public TimeSpan? ToTime { get; set; }
    public decimal Days { get; set; }
    public int? Minutes { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
    public string Status { get; set; } = "";
    public string? HeldBy { get; set; }
    public string? HeldByName { get; set; }
    public string? HeldByRole { get; set; }
    public int Level { get; set; }
    public int FinalLevel { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Files { get; set; }
}

public record RequestInput(string Type, DateTime FromDate, DateTime ToDate, string? FromTime, string? ToTime, decimal? Days, string? Reason, string? Remarks, string? PushTo);
public record CompOffCredit(DateTime EarnedOn, DateTime ExpiresOn, decimal Credits, decimal Used);
public record CompOffBalance(decimal Available, decimal Pending, int ValidDays, List<CompOffCredit> Credits);
public record RequestOptions(bool Final, int Level, int DocLevel, List<Approver> Push);
public record AttachmentRow(int Id, string FileName, string ContentType, int SizeBytes, string? UploadedBy, DateTime UploadedAt);
public record ReqSpan(string EmpCode, DateTime FromDate, DateTime ToDate, string Type);

public static class RequestEndpoints
{
    const int MaxFileBytes = 5 * 1024 * 1024, MaxFilesPerOwner = 5, ShortBreakMaxMinutes = 240, CompOffValidDays = 90;

    const string Select = @"
SELECT r.Id, r.TransNo, r.EmpCode, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = r.EmpCode) AS Name, r.[Type], r.FromDate, r.ToDate, r.FromTime, r.ToTime, r.Days, r.Minutes,
       r.Reason, r.Remarks, r.[Status], r.HeldBy, (SELECT TOP 1 ISNULL(NULLIF(u.name,''), u.username) FROM interface.Userdef u WHERE u.userid = r.HeldBy) AS HeldByName,
       (SELECT TOP 1 ro.rolename FROM interface.Userdef u JOIN interface.Roledef ro ON ro.roleid = u.roleid WHERE u.userid = r.HeldBy) AS HeldByRole,
       r.[Level], r.FinalLevel, r.DecisionNote, r.CreatedAt,
       (SELECT COUNT(*) FROM hrms.Attachment a WHERE a.OwnerType = 'request' AND a.OwnerKey = r.TransNo AND a.Deleted = 0) AS Files
FROM hrms.Request r";

    static AuthUser User(HttpContext ctx) => (AuthUser)ctx.Items["user"]!;
    static string Emp(HttpContext ctx) => User(ctx).EmployeeCode ?? throw new ApiException("Your login is not linked to an employee record.", 403);

    static TimeSpan? Time(string? s) => TimeSpan.TryParse(s, out var t) ? t : null;

    // ---------- comp off ----------
    static async Task<CompOffBalance> Balance(SqlConnection c, SqlTransaction? tx, string emp)
    {
        var today = DateTime.Today;
        var credits = (await c.QueryAsync<CompOffCredit>("SELECT EarnedOn, ExpiresOn, Credits, Used FROM hrms.CompOff WHERE EmpCode = @emp ORDER BY ExpiresOn", new { emp }, tx)).ToList();
        var available = credits.Where(x => x.ExpiresOn.Date >= today).Sum(x => x.Credits - x.Used);
        var pending = await c.ExecuteScalarAsync<decimal>("SELECT ISNULL(SUM(Days),0) FROM hrms.Request WHERE EmpCode = @emp AND [Type] = 'COL' AND [Status] = 'Pending'", new { emp }, tx);
        return new CompOffBalance(available, pending, CompOffValidDays, credits);
    }

    static async Task SpendCredits(SqlConnection c, SqlTransaction tx, string emp, decimal days)
    {
        var rows = (await c.QueryAsync<(int Id, decimal Left)>("SELECT Id, Credits - Used AS [Left] FROM hrms.CompOff WHERE EmpCode = @emp AND ExpiresOn >= CAST(GETDATE() AS date) AND Credits > Used ORDER BY ExpiresOn, Id", new { emp }, tx)).ToList();
        if (rows.Sum(r => r.Left) < days) throw new ApiException("There is not enough comp off balance left to approve this.", 409);
        foreach (var r in rows)
        {
            if (days <= 0) break;
            var take = Math.Min(days, r.Left);
            await c.ExecuteAsync("UPDATE hrms.CompOff SET Used = Used + @take WHERE Id = @id", new { take, id = r.Id }, tx);
            days -= take;
        }
    }

    // What an approved request does: a comp off credit adds to the balance, comp off leave uses it up.
    static async Task ApplyApproved(SqlConnection c, SqlTransaction tx, RequestRow r)
    {
        if (r.Type == "COC")
            await c.ExecuteAsync("INSERT hrms.CompOff (EmpCode, RequestId, Credits, EarnedOn, ExpiresOn) VALUES (@emp, @id, @days, @on, @exp)",
                new { emp = r.EmpCode, id = r.Id, days = r.Days, on = r.FromDate.Date, exp = r.FromDate.Date.AddDays(CompOffValidDays) }, tx);
        else if (r.Type == "COL") await SpendCredits(c, tx, r.EmpCode, r.Days);
    }

    static Task AddStep(SqlConnection c, SqlTransaction tx, int requestId, string userId, int level, string action, string? note) =>
        c.ExecuteAsync("INSERT hrms.RequestStep (RequestId, UserId, [Level], [Action], Note) VALUES (@requestId, @userId, @level, @action, @note)", new { requestId, userId, level, action, note }, tx);

    static async Task<RequestRow> Load(SqlConnection c, SqlTransaction? tx, int id) =>
        await c.QueryFirstOrDefaultAsync<RequestRow>(Select + " WHERE r.Id = @id", new { id }, tx) ?? throw new ApiException("Request not found.", 404);

    // ---------- the checks a request must pass ----------
    static async Task Validate(SqlConnection c, SqlTransaction tx, string emp, RequestInput x, string type)
    {
        var today = DateTime.Today;
        var from = x.FromDate.Date; var to = x.ToDate.Date;
        if (to < from) throw new ApiException("The end date cannot be before the start date.");
        if (from < today.AddDays(-60) || to > today.AddDays(366)) throw new ApiException("Choose dates within the last 60 days and the next year.");
        var reason = Writes.Cut(x.Reason);
        if (type is "OD" or "WFH" or "COC" && (reason is null || reason.Length < 3)) throw new ApiException("Please give a reason (a few words).");

        if (type == "SB")
        {
            if (from != to) throw new ApiException("A short break is for one day.");
            var a = Time(x.FromTime); var b = Time(x.ToTime);
            if (a is null || b is null) throw new ApiException("Enter the time you leave and the time you are back.");
            var mins = (int)(b.Value - a.Value).TotalMinutes;
            if (mins < 15) throw new ApiException("A short break is at least 15 minutes.");
            if (mins > ShortBreakMaxMinutes) throw new ApiException($"A short break can be at most {ShortBreakMaxMinutes / 60} hours. Request leave for longer.");
            if (await c.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM hrms.Request WHERE EmpCode = @emp AND [Type] = 'SB' AND [Status] IN ('Pending','Approved') AND FromDate = @from AND FromTime < @b AND ToTime > @a",
                new { emp, from, a, b }, tx) > 0) throw new ApiException("You already have a short break at that time.", 409);
        }
        else if (type == "COC")
        {
            if (from != to) throw new ApiException("Comp off credit is for the one day you worked.");
            if (from > today) throw new ApiException("You can only ask for credit for a day you have already worked.");
            if (x.Days is not (0.5m or 1m)) throw new ApiException("Credit is half a day or a full day.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Request WHERE EmpCode = @emp AND [Type] = 'COC' AND [Status] IN ('Pending','Approved') AND FromDate = @from", new { emp, from }, tx) > 0)
                throw new ApiException("You already asked for comp off credit for that day.", 409);
        }
        else   // OD, WFH, COL: whole days that must not clash with other time off
        {
            if (await c.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM hrms.Request WHERE EmpCode = @emp AND [Type] IN ('OD','WFH','COL') AND [Status] IN ('Pending','Approved') AND FromDate <= @to AND ToDate >= @from",
                new { emp, from, to }, tx) > 0) throw new ApiException("You already have an On Duty, Work From Home or Comp Off request on some of these dates.", 409);
            if (await c.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM inter01.EMPLOYEELEAVEDETAILS WHERE EmpCode = @emp AND ISNULL(Deleteflag,0) = 0 AND FromDate <= @to AND ToDate >= @from",
                new { emp, from, to }, tx) > 0) throw new ApiException("You already have leave on some of these dates.", 409);
        }
        if (type == "COL")
        {
            var days = x.Days ?? (decimal)(to - from).TotalDays + 1;
            var bal = await Balance(c, tx, emp);
            if (days > bal.Available - bal.Pending) throw new ApiException($"You have {(bal.Available - bal.Pending).ToString("0.##")} comp off day(s) available.", 409);
        }
    }

    // ---------- the road ahead for a request still waiting, same idea as for leave ----------
    static async Task<(List<UpcomingLevel> Upcoming, int FinalLevel, List<string> FinalNames)> Road(SqlConnection c, RequestRow r)
    {
        var upcoming = new List<UpcomingLevel>(); var finalNames = new List<string>();
        if (r.FinalLevel <= 0) return (upcoming, r.FinalLevel, finalNames);
        var everyone = await Workflow.Approvers(c, null, 0, "");
        var skip = (Func<Approver, bool>)(a => !string.Equals(a.Id, r.EmpCode, StringComparison.OrdinalIgnoreCase));
        finalNames = everyone.Where(a => a.Level == r.FinalLevel && skip(a)).Select(a => a.Name).Distinct().ToList();
        if (r.Status == "Pending" && r.HeldBy is not null)
        {
            var held = await c.ExecuteScalarAsync<int?>("SELECT TOP 1 CAST(a.[level] AS int) FROM interface.Userdef u JOIN interface.accessright a ON a.roleid = u.roleid AND a.optionid = @opt WHERE u.userid = @h",
                new { opt = Workflow.ApprovalOption, h = r.HeldBy }) ?? 0;
            foreach (var g in everyone.Where(a => a.Level > held && a.Level <= r.FinalLevel && skip(a)).GroupBy(a => a.Level).OrderBy(g => g.Key))
                upcoming.Add(new UpcomingLevel(g.Key, g.Select(a => a.Name).Distinct().ToList(), g.Key == r.FinalLevel));
            if (upcoming.Count == 0 || upcoming[^1].Level != r.FinalLevel) upcoming.Add(new UpcomingLevel(r.FinalLevel, finalNames, true));
        }
        return (upcoming, r.FinalLevel, finalNames);
    }

    // ---------- files ----------
    static bool LooksLike(byte[] d, string type) => type switch
    {
        "application/pdf" => d.Length > 4 && d[0] == 0x25 && d[1] == 0x50 && d[2] == 0x44 && d[3] == 0x46,
        "image/png" => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47,
        "image/jpeg" => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF,
        "image/webp" => d.Length > 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50,
        _ => false,
    };

    // The person it belongs to, and whether it is still waiting (so the owner may still change what is attached).
    static async Task<(string? Owner, bool Pending)> OwnerOf(SqlConnection c, string type, string key)
    {
        if (type == "leave")
        {
            var l = await c.QueryFirstOrDefaultAsync<(string EmpCode, int Pending)?>("SELECT TOP 1 EmpCode, CASE WHEN ISNULL(Userlevel,0) < ISNULL(documentlevel,0) THEN 1 ELSE 0 END FROM inter01.EMPLOYEELEAVEDETAILS WHERE Transno = @key AND ISNULL(Deleteflag,0) = 0", new { key });
            return l is null ? (null, false) : (l.Value.EmpCode, l.Value.Pending == 1);
        }
        var r = await c.QueryFirstOrDefaultAsync<(string EmpCode, string Status)?>("SELECT TOP 1 EmpCode, [Status] FROM hrms.Request WHERE TransNo = @key", new { key });
        return r is null ? (null, false) : (r.Value.EmpCode, r.Value.Status == "Pending");
    }

    static bool CanSee(AuthUser u, string owner) =>
        string.Equals(u.EmployeeCode, owner, StringComparison.OrdinalIgnoreCase) || (u.Rights.TryGetValue(Workflow.ApprovalOption, out var r) && r.View) || u.IsAdmin;

    public static void MapRequests(this WebApplication app)
    {
        // ----- mine -----
        app.MapGet("/api/me/requests", async (Db db, HttpContext ctx, DateTime? from, DateTime? to) =>
        {
            var me = Emp(ctx);
            await using var c = db.Open();
            var rows = (await c.QueryAsync<RequestRow>(Select + " WHERE r.EmpCode = @me AND r.[Status] <> 'Cancelled' AND r.FromDate <= @end AND r.ToDate >= @start ORDER BY r.FromDate DESC, r.Id DESC",
                new { me, start = from ?? DateTime.Today.AddYears(-1), end = to ?? DateTime.Today.AddYears(1) })).ToList();
            return Results.Ok(rows);
        });

        app.MapGet("/api/me/compoff", async (Db db, HttpContext ctx) =>
        {
            var me = Emp(ctx);
            await using var c = db.Open();
            return Results.Ok(await Balance(c, null, me));
        });

        app.MapPost("/api/me/requests", async (Db db, HrmsSession s, HttpContext ctx, RequestInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var user = User(ctx); var me = Emp(ctx);
            var type = (x.Type ?? "").ToUpperInvariant();
            if (!RequestTypes.Names.ContainsKey(type)) throw new ApiException("Choose what you are asking for.");
            await Validate(c, tx, me, x, type);

            var route = await LeaveRouting.For(c, tx, s, user, me);
            var target = route.Allowed.FirstOrDefault(a => a.Id == x.PushTo) ?? (route.Allowed.Count == 1 ? route.Allowed[0] : null);
            var top = route.Allowed.Count == 0 && route.OwnLevel >= route.DocLevel;
            if (target is null && !top) throw new ApiException(string.IsNullOrWhiteSpace(x.PushTo) ? "Choose who should approve this request." : "That person cannot approve this.");

            var from = x.FromDate.Date; var to = type is "SB" or "COC" ? from : x.ToDate.Date;
            var ft = type is "SB" or "OD" ? Time(x.FromTime) : null; var tt = type is "SB" or "OD" ? Time(x.ToTime) : null;
            if (type == "OD" && ((ft is null) != (tt is null) || (ft is not null && (from != to || tt <= ft)))) throw new ApiException("For a time on duty, enter both times on a single day, with the end after the start.");
            int? minutes = type == "SB" ? (int)(tt!.Value - ft!.Value).TotalMinutes : null;
            var days = type == "SB" ? 0m : x.Days is > 0 ? Math.Min(x.Days.Value, 366m) : (decimal)(to - from).TotalDays + 1;
            if (type == "COC") days = x.Days ?? 1m;
            var status = top ? "Approved" : "Pending";

            var id = await c.ExecuteScalarAsync<int>(@"INSERT hrms.Request (TransNo, EmpCode, [Type], FromDate, ToDate, FromTime, ToTime, Days, Minutes, Reason, Remarks, [Status], HeldBy, [Level], FinalLevel, DecidedAt)
                OUTPUT INSERTED.Id VALUES ('', @me, @type, @from, @to, @ft, @tt, @days, @minutes, @reason, @remarks, @status, @held, @level, @final, @decided)",
                new { me, type, from, to, ft, tt, days, minutes, reason = Writes.Cut(x.Reason), remarks = Writes.Cut(x.Remarks), status, held = target?.Id ?? user.UserId, level = route.OwnLevel, final = route.DocLevel, decided = top ? (DateTime?)DateTime.Now : null }, tx);
            var transNo = $"{RequestTypes.Prefix(type)}{DateTime.Today:yy}-{id:00000}";
            await c.ExecuteAsync("UPDATE hrms.Request SET TransNo = @transNo WHERE Id = @id", new { transNo, id }, tx);
            await AddStep(c, tx, id, user.UserId, route.OwnLevel, "Submitted", null);
            var row = await Load(c, tx, id);
            if (top) await ApplyApproved(c, tx, row);
            else
            {
                await AddStep(c, tx, id, target!.Id, target.Level, "Received", null);
                await Notifier.Send(c, tx, target.Id, "request", "A request is waiting for you", $"{RequestTypes.Name(type)} from {user.FullName} ({Fmt(row)})", "approvals");
            }
            return new { Id = id, TransNo = transNo, Pending = !top, With = target?.Name ?? "approved automatically" };
        }));

        app.MapDelete("/api/me/requests/{id:int}", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Emp(ctx); var user = User(ctx);
            var r = await Load(c, tx, id);
            if (!string.Equals(r.EmpCode, me, StringComparison.OrdinalIgnoreCase)) throw new ApiException("Request not found.", 404);
            if (r.Status != "Pending") throw new ApiException(r.Status == "Approved" ? "An approved request can only be cancelled by HR." : "That request is already closed.", 409);
            await c.ExecuteAsync("UPDATE hrms.Request SET [Status] = 'Cancelled', DecidedAt = GETDATE() WHERE Id = @id", new { id }, tx);
            await AddStep(c, tx, id, user.UserId, r.Level, "Cancelled", null);
            return new { Id = id };
        }));

        app.MapGet("/api/me/requests/{id:int}/timeline", async (Db db, HttpContext ctx, int id) =>
        {
            var me = Emp(ctx);
            await using var c = db.Open();
            var r = await Load(c, null, id);
            if (!string.Equals(r.EmpCode, me, StringComparison.OrdinalIgnoreCase)) return Results.Json(new { error = "Request not found." }, statusCode: 404);
            var steps = (await c.QueryAsync<LeaveStep>(@"
SELECT s.UserId, ISNULL(NULLIF(u.name,''), ISNULL(u.username, s.UserId)) AS Name, ro.rolename AS RoleName, s.[Level] AS Level, s.[At] AS At,
       CASE s.[Action] WHEN 'Rejected' THEN 'Rejected: ' + ISNULL(s.Note, '') WHEN 'Cancelled' THEN 'Cancelled' ELSE NULLIF(s.Note, '') END AS Note
FROM hrms.RequestStep s LEFT JOIN interface.Userdef u ON u.userid = s.UserId LEFT JOIN interface.Roledef ro ON ro.roleid = u.roleid
WHERE s.RequestId = @id ORDER BY s.Id", new { id })).ToList();
            var (up, fin, names) = await Road(c, r);
            return Results.Ok(new LeaveTimeline(steps, up, fin, names));
        });

        // ----- files for leave and for requests -----
        app.MapGet("/api/attachments", async (Db db, HttpContext ctx, string ownerType, string ownerKey) =>
        {
            await using var c = db.Open();
            var (owner, _) = await OwnerOf(c, ownerType, ownerKey);
            if (owner is null || !CanSee(User(ctx), owner)) return Results.Ok(new List<AttachmentRow>());
            return Results.Ok((await c.QueryAsync<AttachmentRow>("SELECT Id, FileName, ContentType, SizeBytes, UploadedBy, UploadedAt FROM hrms.Attachment WHERE OwnerType = @ownerType AND OwnerKey = @ownerKey AND Deleted = 0 ORDER BY Id", new { ownerType, ownerKey })).ToList());
        });

        app.MapGet("/api/attachments/{id:int}", async (Db db, HttpContext ctx, int id) =>
        {
            await using var c = db.Open();
            var f = await c.QueryFirstOrDefaultAsync<(string OwnerType, string OwnerKey, string FileName, string ContentType, byte[] Data)?>("SELECT OwnerType, OwnerKey, FileName, ContentType, Data FROM hrms.Attachment WHERE Id = @id AND Deleted = 0", new { id });
            if (f is null) return Results.NotFound();
            var (owner, _) = await OwnerOf(c, f.Value.OwnerType, f.Value.OwnerKey);
            return owner is null || !CanSee(User(ctx), owner) ? Results.NotFound() : Results.File(f.Value.Data, f.Value.ContentType, f.Value.FileName);
        });

        app.MapPost("/api/me/attachments", async (Db db, HttpContext ctx, IFormFile file, string ownerType, string ownerKey) =>
        {
            try
            {
                var me = Emp(ctx);
                if (ownerType is not ("leave" or "request")) throw new ApiException("Unknown request kind.");
                var type = (file.ContentType ?? "").ToLowerInvariant();
                if (file.Length == 0) throw new ApiException("That file is empty.");
                if (file.Length > MaxFileBytes) throw new ApiException($"That file is larger than {MaxFileBytes / 1024 / 1024} MB.");
                if (type is not ("image/jpeg" or "image/png" or "image/webp" or "application/pdf")) throw new ApiException("Attach a PDF or a photo (JPEG, PNG or WebP).");
                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var data = ms.ToArray();
                if (!LooksLike(data, type)) throw new ApiException("That file is not really a " + (type == "application/pdf" ? "PDF" : "photo") + ".");
                await using var c = db.OpenWrite();
                var (owner, pending) = await OwnerOf(c, ownerType, ownerKey);
                if (owner is null || !string.Equals(owner, me, StringComparison.OrdinalIgnoreCase)) throw new ApiException("Request not found.", 404);
                if (!pending) throw new ApiException("Documents can only be added while the request is waiting.", 409);
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Attachment WHERE OwnerType = @ownerType AND OwnerKey = @ownerKey AND Deleted = 0", new { ownerType, ownerKey }) >= MaxFilesPerOwner)
                    throw new ApiException($"At most {MaxFilesPerOwner} documents can be attached.", 409);
                var name = Path.GetFileName(file.FileName ?? "document");
                var fid = await c.ExecuteScalarAsync<int>("INSERT hrms.Attachment (OwnerType, OwnerKey, EmpCode, FileName, ContentType, SizeBytes, Data, UploadedBy) OUTPUT INSERTED.Id VALUES (@ownerType, @ownerKey, @me, @name, @type, @size, @data, @user)",
                    new { ownerType, ownerKey, me, name = name.Length > 200 ? name[..200] : name, type, size = data.Length, data, user = User(ctx).UserId });
                return Results.Ok(new { ok = true, result = new { Id = fid } });
            }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        }).DisableAntiforgery();

        app.MapDelete("/api/me/attachments/{id:int}", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Emp(ctx);
            var f = await c.QueryFirstOrDefaultAsync<(string OwnerType, string OwnerKey)?>("SELECT OwnerType, OwnerKey FROM hrms.Attachment WHERE Id = @id AND Deleted = 0", new { id }, tx);
            if (f is null) throw new ApiException("File not found.", 404);
            var (owner, pending) = await OwnerOf(c, f.Value.OwnerType, f.Value.OwnerKey);
            if (owner is null || !string.Equals(owner, me, StringComparison.OrdinalIgnoreCase)) throw new ApiException("File not found.", 404);
            if (!pending) throw new ApiException("Documents can only be removed while the request is waiting.", 409);
            await c.ExecuteAsync("UPDATE hrms.Attachment SET Deleted = 1 WHERE Id = @id", new { id }, tx);
            return new { Id = id };
        }));

        // ----- approving -----
        app.MapGet("/api/requests/pending", async (Db db, HttpContext ctx) =>
        {
            var me = User(ctx);
            await using var c = db.Open();
            return Results.Ok((await c.QueryAsync<RequestRow>(Select + " WHERE r.HeldBy = @me AND r.[Status] = 'Pending' ORDER BY r.CreatedAt", new { me = me.UserId })).ToList());
        });

        app.MapGet("/api/requests/{id:int}/options", async (Db db, HttpContext ctx, int id) =>
        {
            var me = User(ctx);
            await using var c = db.Open();
            try
            {
                var r = await Mine(c, null, me, id);
                var level = LeaveRouting.ApprovalLevel(me);
                var push = level >= r.FinalLevel ? [] : (await Workflow.Approvers(c, null, level, me.UserId, r.EmpCode)).Where(a => a.Level <= r.FinalLevel).ToList();
                return Results.Ok(new RequestOptions(level >= r.FinalLevel, level, r.FinalLevel, push));
            }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapPost("/api/requests/{id:int}/approve", async (Db db, HttpContext ctx, int id, DecisionInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = User(ctx);
            var r = await Mine(c, tx, me, id);
            var level = LeaveRouting.ApprovalLevel(me);
            var note = Writes.Cut(x.Note);
            if (level >= r.FinalLevel)
            {
                await ApplyApproved(c, tx, r);
                await c.ExecuteAsync("UPDATE hrms.Request SET [Status] = 'Approved', [Level] = @level, DecidedAt = GETDATE(), DecisionNote = @note WHERE Id = @id", new { id, level, note }, tx);
                await AddStep(c, tx, id, me.UserId, level, "Approved", note);
                await Notifier.Send(c, tx, r.EmpCode, "request", $"Your {r.TypeName} request was approved", Fmt(r), "my/leave");
                return new { Id = id, Final = true };
            }
            if (string.IsNullOrWhiteSpace(x.To)) throw new ApiException("Choose who this goes to next.");
            var next = (await Workflow.Approvers(c, tx, level, me.UserId, r.EmpCode)).FirstOrDefault(a => a.Id == x.To && a.Level <= r.FinalLevel) ?? throw new ApiException("That person cannot approve requests above you.");
            await c.ExecuteAsync("UPDATE hrms.Request SET HeldBy = @to, [Level] = @level WHERE Id = @id", new { id, to = next.Id, level }, tx);
            await AddStep(c, tx, id, me.UserId, level, "Approved", note);
            await AddStep(c, tx, id, next.Id, next.Level, "Received", null);
            await Notifier.Send(c, tx, next.Id, "request", "A request is waiting for you", $"{r.TypeName} from {r.Name} ({Fmt(r)})", "approvals");
            await Notifier.Send(c, tx, r.EmpCode, "request", $"Your {r.TypeName} request moved on", $"It is now with {next.Name}.", "my/leave");
            return new { Id = id, Final = false, With = next.Name };
        }));

        app.MapPost("/api/requests/{id:int}/reject", async (Db db, HttpContext ctx, int id, DecisionInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = User(ctx);
            var r = await Mine(c, tx, me, id);
            var note = Writes.Cut(x.Note) ?? throw new ApiException("Write the reason for rejecting this request.");
            await c.ExecuteAsync("UPDATE hrms.Request SET [Status] = 'Rejected', DecidedAt = GETDATE(), DecisionNote = @note WHERE Id = @id", new { id, note }, tx);
            await AddStep(c, tx, id, me.UserId, LeaveRouting.ApprovalLevel(me), "Rejected", note);
            await Notifier.Send(c, tx, r.EmpCode, "request", $"Your {r.TypeName} request was rejected", $"{Fmt(r)}. Reason: {note}", "my/leave");
            return new { Id = id };
        }));
    }

    // A request that is waiting with this person, and not their own.
    static async Task<RequestRow> Mine(SqlConnection c, SqlTransaction? tx, AuthUser me, int id)
    {
        var r = await Load(c, tx, id);
        if (r.Status != "Pending") throw new ApiException("Nothing is waiting for a decision on that request.", 409);
        if (!string.Equals(r.HeldBy, me.UserId, StringComparison.OrdinalIgnoreCase)) throw new ApiException("That request is not with you.", 403);
        if (string.Equals(r.EmpCode, me.EmployeeCode, StringComparison.OrdinalIgnoreCase)) throw new ApiException("You cannot decide your own request.", 403);
        return r;
    }

    static string Fmt(RequestRow r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var d = r.FromDate.Date == r.ToDate.Date ? r.FromDate.ToString("d MMM yyyy", inv) : $"{r.FromDate.ToString("d MMM", inv)} - {r.ToDate.ToString("d MMM yyyy", inv)}";
        return r.Type == "SB" && r.FromTime is not null && r.ToTime is not null ? $"{d}, {r.FromTime:hh\\:mm} - {r.ToTime:hh\\:mm}" : d;
    }
}
