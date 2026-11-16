using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Company assets (laptop, phone, SIM...) issued to employees, following the ERP's asset rules:
//  - every asset has a tag and a serial number, and a serial number is never duplicated (as in the ERP's asset register);
//  - an asset can only be issued while it is in stock, and to an active employee (the ERP's stock check);
//  - an issue (ERP type 800) and a return (ERP types 801/726, pulled from the issue) each need authorising: a document counts only once the
//    person deciding is at the document level (the ERP's userlevel = documentauthlevel rule), and nobody authorises their own;
//  - issue and return dates obey the ERP's closing date and its allowed days back and ahead, read from the ERP's own transaction settings.
// What an employee holds is every approved issue that has no approved return.

public record AssetInput(string? AssetCode, string? Category, string? Name, string? SerialNo, decimal? Value, DateTime? PurchaseDate, string? Notes);
public record IssueInput(string? EmpCode, DateTime Date, string? Condition, string? Note);
public record ReturnInput(DateTime Date, string? Condition, string? Note);
public record DecideNote(string? Note);

public class AssetRow
{
    public int Id { get; set; }
    public string AssetCode { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string SerialNo { get; set; } = "";
    public decimal? Value { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public string Status { get; set; } = "";
    public string? Notes { get; set; }
    public int? CustodyId { get; set; }
    public string? HolderCode { get; set; }
    public string? HolderName { get; set; }
    public DateTime? Since { get; set; }
    public string? State { get; set; }
}

public class CustodyRow
{
    public int Id { get; set; }
    public string? DocNo { get; set; }
    public int AssetId { get; set; }
    public string AssetCode { get; set; } = "";
    public string Category { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string SerialNo { get; set; } = "";
    public string EmpCode { get; set; } = "";
    public string? EmpName { get; set; }
    public DateTime IssueDate { get; set; }
    public string? ConditionOut { get; set; }
    public string? IssueNote { get; set; }
    public string IssuedBy { get; set; } = "";
    public string IssueStatus { get; set; } = "";
    public string? IssueDecisionNote { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? ReturnDocNo { get; set; }
    public DateTime? ReturnDate { get; set; }
    public string? ConditionIn { get; set; }
    public string? ReturnNote { get; set; }
    public string? ReturnStatus { get; set; }
    public string? ReturnDecisionNote { get; set; }
    public string? TransferStatus { get; set; }   // Pending or Accepting while a transfer to someone else is open
    // what it is now: Waiting approval, With employee, Return waiting approval, Returned, Rejected
    public string State => IssueStatus == "Rejected" ? "Rejected" : IssueStatus == "Pending" ? "Issue waiting approval"
        : ReturnStatus == "Approved" ? "Returned" : ReturnStatus == "Pending" ? "Return waiting approval"
        : TransferStatus == "Pending" ? "Transfer waiting approval" : TransferStatus == "Accepting" ? "Transfer waiting acceptance" : "With employee";
}

public static class AssetEndpoints
{
    public static readonly string[] Categories = ["Laptop", "Desktop", "Mobile phone", "Tablet", "SIM card", "Access card", "Vehicle", "Tools and equipment", "Other"];
    public const string IssueType = "800", ReturnType = "801";

    internal sealed class Mini { public string Name { get; set; } = ""; public string Status { get; set; } = ""; public string AssetCode { get; set; } = ""; }
    sealed class DateRule { public DateTime? Closing { get; set; } public int? Interval { get; set; } public int? Future { get; set; } }

    internal static AuthUser Caller(HttpContext ctx) => (AuthUser)ctx.Items["user"]!;
    internal static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    internal static int DocLevel(IConfiguration cfg, HrmsSession s) => cfg.GetValue<int?>("Hrms:AssetDocumentLevel") ?? s.DocLevel;

    const string AssetSelect = @"
SELECT a.Id, a.AssetCode, a.Category, a.Name, a.SerialNo, a.Value, a.PurchaseDate, a.Status, a.Notes,
       c.Id AS CustodyId, c.EmpCode AS HolderCode, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = c.EmpCode) AS HolderName, c.IssueDate AS Since,
       CASE WHEN c.Id IS NULL THEN NULL WHEN c.IssueStatus = 'Pending' THEN 'Issue waiting approval' WHEN c.ReturnStatus = 'Pending' THEN 'Return waiting approval' WHEN c.TransferStatus = 'Pending' THEN 'Transfer waiting approval' WHEN c.TransferStatus = 'Accepting' THEN 'Transfer waiting acceptance' ELSE 'With employee' END AS State
FROM hrms.Asset a
OUTER APPLY (SELECT TOP 1 x.Id, x.EmpCode, x.IssueDate, x.IssueStatus, x.ReturnStatus, (SELECT TOP 1 t.Status FROM hrms.AssetTransfer t WHERE t.FromCustodyId = x.Id AND t.Status IN ('Pending','Accepting') ORDER BY t.Id DESC) AS TransferStatus FROM hrms.AssetCustody x
             WHERE x.AssetId = a.Id AND x.IssueStatus IN ('Pending','Approved') AND ISNULL(x.ReturnStatus,'') <> 'Approved' ORDER BY x.Id DESC) c
WHERE a.Deleted = 0";

    internal const string CustodySelect = @"
SELECT c.Id, c.DocNo, c.AssetId, a.AssetCode, a.Category, a.Name AS AssetName, a.SerialNo, c.EmpCode, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = c.EmpCode) AS EmpName,
       c.IssueDate, c.ConditionOut, c.IssueNote, c.IssuedBy, c.IssueStatus, c.IssueDecisionNote, c.AcceptedAt,
       c.ReturnDocNo, c.ReturnDate, c.ConditionIn, c.ReturnNote, c.ReturnStatus, c.ReturnDecisionNote,
       (SELECT TOP 1 t.Status FROM hrms.AssetTransfer t WHERE t.FromCustodyId = c.Id AND t.Status IN ('Pending','Accepting') ORDER BY t.Id DESC) AS TransferStatus
FROM hrms.AssetCustody c JOIN hrms.Asset a ON a.Id = c.AssetId";

    // Dates must follow the ERP's own limits for that document type: after its closing date, within the allowed days back and ahead.
    internal static async Task CheckDate(SqlConnection c, SqlTransaction? tx, string transType, DateTime date, string what)
    {
        var rule = await c.QueryFirstOrDefaultAsync<DateRule>(
            "SELECT TOP 1 ClosingDate AS Closing, TRY_CAST(DateInterval AS int) AS Interval, TRY_CAST(FuturedateAllow AS int) AS Future FROM inter01.transtype WHERE code = @transType", new { transType }, tx);
        var d = date.Date; var today = DateTime.Today;
        if (rule?.Closing is { } closing && d <= closing.Date) throw new ApiException($"The {what} date is on or before {closing:d MMM yyyy}, which the ERP has closed.", 409);
        if (rule?.Interval is > 0 and var back && d < today.AddDays(-back)) throw new ApiException($"The {what} date is more than {back} days back, which the ERP does not allow.", 409);
        if (rule?.Future is >= 0 and var ahead && d > today.AddDays(ahead)) throw new ApiException($"The {what} date is more than {ahead} days ahead, which the ERP does not allow.", 409);
    }

    internal static async Task<string?> ActiveEmployeeName(SqlConnection c, SqlTransaction tx, string code) =>
        await c.QueryFirstOrDefaultAsync<Mini>(Sql.EmpCte + "SELECT TOP 1 Name, Status FROM emp WHERE EmpCode = @code ORDER BY CASE WHEN Status = 'active' THEN 0 ELSE 1 END", new { code }, tx)
            is { Status: "active" } e ? e.Name : null;

    // The handover row, or 404.
    internal static async Task<CustodyRow> Load(SqlConnection c, SqlTransaction? tx, int id) =>
        await c.QueryFirstOrDefaultAsync<CustodyRow>(CustodySelect + " WHERE c.Id = @id", new { id }, tx) ?? throw new ApiException("That handover does not exist.", 404);

    // What a person holds, or is waiting to receive, for the clearance check as well as the screens.
    public static async Task<List<CustodyRow>> Holdings(SqlConnection c, SqlTransaction? tx, string emp) =>
        (await c.QueryAsync<CustodyRow>(CustodySelect + " WHERE c.EmpCode = @emp AND c.IssueStatus IN ('Pending','Approved') AND ISNULL(c.ReturnStatus,'') <> 'Approved' ORDER BY c.IssueDate", new { emp }, tx)).ToList();

    // People who hold the assets menu at or above the document level: they are the ones who can approve. Told through the bell, and by email when the user has one.
    internal static async Task NotifyApprovers(SqlConnection c, SqlTransaction tx, int needLevel, string exceptUser, string title, string body)
    {
        var users = await c.QueryAsync<string>(@"SELECT DISTINCT u.userid FROM interface.Userdef u
            JOIN interface.accessright a ON a.roleid = u.roleid AND a.optionid = 'H301' AND CAST(a.access AS int) = 1 AND CAST(a.[level] AS int) >= @needLevel
            WHERE ISNULL(u.[disable],0) = 0 AND u.userid <> @exceptUser", new { needLevel, exceptUser }, tx);
        foreach (var u in users) await Notifier.Send(c, tx, u, "asset", title, body, "approvals");
    }

    static async Task<string> UniqueGuard(Func<Task<string>> work)
    {
        try { return await work(); }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { throw new ApiException("An asset with that code or serial number already exists.", 409); }
    }

    public static void MapAssets(this WebApplication app)
    {
        app.MapGet("/api/assets/categories", () => Results.Ok(Categories));

        // ----- the register -----
        app.MapGet("/api/assets", async (Db db, string? status, string? category, string? q) =>
        {
            await using var c = db.Open();
            var sql = AssetSelect + @" AND (@status IS NULL OR a.Status = @status) AND (@category IS NULL OR a.Category = @category)
                AND (@q IS NULL OR a.AssetCode LIKE @q OR a.Name LIKE @q OR a.SerialNo LIKE @q OR c.EmpCode LIKE @q OR EXISTS (SELECT 1 FROM inter01.EmployeeMaster e WHERE e.EmpCode = c.EmpCode AND e.Empname LIKE @q))
                ORDER BY a.Category, a.AssetCode";
            return Results.Ok((await c.QueryAsync<AssetRow>(sql, new { status = Nz(status), category = Nz(category), q = Nz(q) is { } t ? $"%{t}%" : null })).ToList());
        });

        app.MapPost("/api/assets", async (Db db, HttpContext ctx, AssetInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var (code, name, serial) = (Nz(x.AssetCode), Nz(x.Name), Nz(x.SerialNo));
            if (code is null || name is null) throw new ApiException("Give the asset a tag and a name.");
            if (serial is null) throw new ApiException("A serial number is required, as in the ERP's asset register.");
            if (x.Category is null || !Categories.Contains(x.Category)) throw new ApiException("Choose a category.");
            if (x.Value < 0) throw new ApiException("The value cannot be negative.");
            var id = 0;
            await UniqueGuard(async () =>
            {
                id = await c.ExecuteScalarAsync<int>(@"INSERT hrms.Asset (AssetCode, Category, Name, SerialNo, Value, PurchaseDate, Notes, CreatedBy)
                    OUTPUT INSERTED.Id VALUES (@code, @cat, @name, @serial, @value, @pd, @notes, @user)",
                    new { code, cat = x.Category, name, serial, value = x.Value, pd = x.PurchaseDate?.Date, notes = Nz(x.Notes), user = Caller(ctx).UserId }, tx);
                return "";
            });
            return new { Id = id };
        }));

        app.MapPut("/api/assets/{id:int}", async (Db db, int id, AssetInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var (code, name, serial) = (Nz(x.AssetCode), Nz(x.Name), Nz(x.SerialNo));
            if (code is null || name is null || serial is null) throw new ApiException("The tag, name and serial number are all required.");
            if (x.Category is null || !Categories.Contains(x.Category)) throw new ApiException("Choose a category.");
            await UniqueGuard(async () =>
            {
                var n = await c.ExecuteAsync("UPDATE hrms.Asset SET AssetCode=@code, Category=@cat, Name=@name, SerialNo=@serial, Value=@value, PurchaseDate=@pd, Notes=@notes WHERE Id=@id AND Deleted=0",
                    new { id, code, cat = x.Category, name, serial, value = x.Value, pd = x.PurchaseDate?.Date, notes = Nz(x.Notes) }, tx);
                if (n == 0) throw new ApiException("That asset does not exist.", 404);
                return "";
            });
            return new { Id = id };
        }));

        // Disposal (the ERP's Asset Disposal): only an asset that is in stock can be written off.
        app.MapDelete("/api/assets/{id:int}", async (Db db, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var status = await c.ExecuteScalarAsync<string?>("SELECT Status FROM hrms.Asset WHERE Id=@id AND Deleted=0", new { id }, tx) ?? throw new ApiException("That asset does not exist.", 404);
            if (status == "Issued") throw new ApiException("This asset is issued. Get it returned before disposing of it.", 409);
            await c.ExecuteAsync("UPDATE hrms.Asset SET Status='Disposed' WHERE Id=@id", new { id }, tx);
            return new { Id = id };
        }));

        // ----- handovers -----
        app.MapGet("/api/assets/custody", async (Db db, string? scope, string? emp) =>
        {
            await using var c = db.Open();
            var where = (scope ?? "all") switch
            {
                "pending" => "c.IssueStatus = 'Pending' OR c.ReturnStatus = 'Pending'",
                "held" => "c.IssueStatus = 'Approved' AND ISNULL(c.ReturnStatus,'') <> 'Approved'",
                "history" => "c.ReturnStatus = 'Approved' OR c.IssueStatus = 'Rejected'",
                _ => "1 = 1",
            };
            return Results.Ok((await c.QueryAsync<CustodyRow>(CustodySelect + $" WHERE ({where}) AND (@emp IS NULL OR c.EmpCode = @emp) ORDER BY c.Id DESC", new { emp = Nz(emp) })).Take(500).ToList());
        });

        app.MapPost("/api/assets/{id:int}/issue", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id, IssueInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var emp = Nz(x.EmpCode) ?? throw new ApiException("Choose the employee.");
            var user = Caller(ctx);
            var asset = await c.QueryFirstOrDefaultAsync<Mini>("SELECT Status, Name, AssetCode FROM hrms.Asset WHERE Id=@id AND Deleted=0", new { id }, tx)
                ?? throw new ApiException("That asset does not exist.", 404);
            if (asset.Status != "InStock") throw new ApiException($"{asset.AssetCode} is {(asset.Status == "Issued" ? "already issued" : "not in stock")}, so it cannot be issued.", 409);
            var name = await ActiveEmployeeName(c, tx, emp) ?? throw new ApiException($"Employee {emp} is not an active employee.", 409);
            await CheckDate(c, tx, IssueType, x.Date, "issue");

            var approved = s.UserLevel >= DocLevel(cfg, s);
            var custody = await c.ExecuteScalarAsync<int>(@"INSERT hrms.AssetCustody (AssetId, EmpCode, IssueDate, ConditionOut, IssueNote, IssuedBy, IssueStatus, IssueApprovedBy, IssueApprovedAt)
                OUTPUT INSERTED.Id VALUES (@id, @emp, @date, @cond, @note, @user, @status, @approver, @at)",
                new { id, emp, date = x.Date.Date, cond = Nz(x.Condition), note = Nz(x.Note), user = user.UserId, status = approved ? "Approved" : "Pending", approver = approved ? user.UserId : null, at = approved ? (DateTime?)DateTime.Now : null }, tx);
            await c.ExecuteAsync("UPDATE hrms.AssetCustody SET DocNo = @doc WHERE Id = @custody", new { custody, doc = $"AEIS{DateTime.Today:yy}-{custody:00000}" }, tx);
            if (await c.ExecuteAsync("UPDATE hrms.Asset SET Status='Issued' WHERE Id=@id AND Status='InStock'", new { id }, tx) != 1) throw new ApiException("Someone else just issued that asset.", 409);

            await Notifier.Send(c, tx, emp, "asset", approved ? "An asset was issued to you" : "An asset is being issued to you",
                $"{asset.Name} ({asset.AssetCode}){(approved ? ". Please confirm you received it." : ", waiting for approval.")}", "my/assets");
            if (!approved) await NotifyApprovers(c, tx, DocLevel(cfg, s), user.UserId, "An asset issue needs your approval", $"{asset.Name} ({asset.AssetCode}) to {name}");
            return new { Id = custody, DocNo = $"AEIS{DateTime.Today:yy}-{custody:00000}", Pending = !approved, Employee = name };
        }));

        app.MapPost("/api/assets/custody/{id:int}/return", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id, ReturnInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var user = Caller(ctx);
            var cu = await Load(c, tx, id);
            if (cu.IssueStatus != "Approved") throw new ApiException("Only an approved handover can be returned.", 409);
            if (cu.ReturnStatus is "Approved" or "Pending") throw new ApiException(cu.ReturnStatus == "Approved" ? "This asset has already been returned." : "A return is already waiting for approval.", 409);
            if (cu.TransferStatus is not null) throw new ApiException("A transfer to another employee is in progress for this asset. Finish or reject it first.", 409);
            if (x.Date.Date < cu.IssueDate.Date) throw new ApiException("The return date cannot be before the issue date.");
            await CheckDate(c, tx, ReturnType, x.Date, "return");

            var approved = s.UserLevel >= DocLevel(cfg, s);
            await c.ExecuteAsync(@"UPDATE hrms.AssetCustody SET ReturnDate=@date, ConditionIn=@cond, ReturnNote=@note, ReturnedTo=@user, ReturnStatus=@status, ReturnApprovedBy=@approver, ReturnApprovedAt=@at,
                ReturnDocNo=@doc, ReturnDecisionNote=NULL WHERE Id=@id",
                new { id, date = x.Date.Date, cond = Nz(x.Condition), note = Nz(x.Note), user = user.UserId, status = approved ? "Approved" : "Pending", approver = approved ? user.UserId : null, at = approved ? (DateTime?)DateTime.Now : null, doc = $"AERT{DateTime.Today:yy}-{id:00000}" }, tx);
            if (approved) await c.ExecuteAsync("UPDATE hrms.Asset SET Status='InStock' WHERE Id=@asset", new { asset = cu.AssetId }, tx);
            else await NotifyApprovers(c, tx, DocLevel(cfg, s), user.UserId, "An asset return needs your approval", $"{cu.AssetName} ({cu.AssetCode}) from {cu.EmpName ?? cu.EmpCode}");
            return new { Id = id, Pending = !approved };
        }));

        app.MapPost("/api/assets/custody/{id:int}/approve", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id, DecideNote x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var (cu, user) = await Decidable(c, tx, s, cfg, ctx, id);
            if (cu.IssueStatus == "Pending")
            {
                await c.ExecuteAsync("UPDATE hrms.AssetCustody SET IssueStatus='Approved', IssueApprovedBy=@user, IssueApprovedAt=GETDATE(), IssueDecisionNote=@note WHERE Id=@id", new { id, user = user.UserId, note = Nz(x.Note) }, tx);
                await Notifier.Send(c, tx, cu.EmpCode, "asset", "An asset was issued to you", $"{cu.AssetName} ({cu.AssetCode}). Please confirm you received it.", "my/assets");
            }
            else
            {
                await c.ExecuteAsync("UPDATE hrms.AssetCustody SET ReturnStatus='Approved', ReturnApprovedBy=@user, ReturnApprovedAt=GETDATE(), ReturnDecisionNote=@note WHERE Id=@id", new { id, user = user.UserId, note = Nz(x.Note) }, tx);
                await c.ExecuteAsync("UPDATE hrms.Asset SET Status='InStock' WHERE Id=@asset", new { asset = cu.AssetId }, tx);
            }
            return new { Id = id };
        }));

        app.MapPost("/api/assets/custody/{id:int}/reject", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id, DecideNote x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (Nz(x.Note) is null) throw new ApiException("Please give a reason for rejecting.");
            var (cu, user) = await Decidable(c, tx, s, cfg, ctx, id);
            if (cu.IssueStatus == "Pending")
            {
                await c.ExecuteAsync("UPDATE hrms.AssetCustody SET IssueStatus='Rejected', IssueApprovedBy=@user, IssueApprovedAt=GETDATE(), IssueDecisionNote=@note WHERE Id=@id", new { id, user = user.UserId, note = Nz(x.Note) }, tx);
                await c.ExecuteAsync("UPDATE hrms.Asset SET Status='InStock' WHERE Id=@asset", new { asset = cu.AssetId }, tx);   // the stock is released again
            }
            else
                await c.ExecuteAsync("UPDATE hrms.AssetCustody SET ReturnStatus='Rejected', ReturnApprovedBy=@user, ReturnApprovedAt=GETDATE(), ReturnDecisionNote=@note WHERE Id=@id", new { id, user = user.UserId, note = Nz(x.Note) }, tx);
            return new { Id = id };
        }));

        // The person who prepared a request that is still waiting can take it back (the ERP lets you delete your own unapproved document).
        app.MapPost("/api/assets/custody/{id:int}/withdraw", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var user = Caller(ctx);
            var cu = await Load(c, tx, id);
            if (cu.IssueStatus == "Pending")
            {
                if (!string.Equals(cu.IssuedBy, user.UserId, StringComparison.OrdinalIgnoreCase)) throw new ApiException("Only the person who prepared it can withdraw it.", 403);
                await c.ExecuteAsync("UPDATE hrms.AssetCustody SET IssueStatus='Rejected', IssueApprovedBy=@user, IssueApprovedAt=GETDATE(), IssueDecisionNote='Withdrawn by ' + @user WHERE Id=@id", new { id, user = user.UserId }, tx);
                await c.ExecuteAsync("UPDATE hrms.Asset SET Status='InStock' WHERE Id=@asset", new { asset = cu.AssetId }, tx);   // the stock is released
            }
            else if (cu.ReturnStatus == "Pending")
            {
                var by = await c.ExecuteScalarAsync<string?>("SELECT ReturnedTo FROM hrms.AssetCustody WHERE Id=@id", new { id }, tx);
                if (!string.Equals(by, user.UserId, StringComparison.OrdinalIgnoreCase)) throw new ApiException("Only the person who prepared it can withdraw it.", 403);
                await c.ExecuteAsync("UPDATE hrms.AssetCustody SET ReturnStatus=NULL, ReturnDate=NULL, ConditionIn=NULL, ReturnNote=NULL, ReturnedTo=NULL, ReturnDocNo=NULL, ReturnDecisionNote=NULL WHERE Id=@id", new { id }, tx);
            }
            else throw new ApiException("Nothing is waiting on that handover.", 409);
            return new { Id = id };
        }));

        // ----- the employee's own view -----
        app.MapGet("/api/me/assets", async (Db db, HttpContext ctx) =>
        {
            var me = Caller(ctx).EmployeeCode!;
            await using var c = db.Open();
            return Results.Ok((await c.QueryAsync<CustodyRow>(CustodySelect + " WHERE c.EmpCode = @me AND c.IssueStatus <> 'Rejected' ORDER BY CASE WHEN c.ReturnStatus = 'Approved' THEN 1 ELSE 0 END, c.IssueDate DESC", new { me })).ToList());
        });

        // The employee confirms receiving the asset (like the ERP's transfer acceptance).
        app.MapPost("/api/me/assets/{id:int}/accept", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx).EmployeeCode!;
            var n = await c.ExecuteAsync("UPDATE hrms.AssetCustody SET AcceptedAt = GETDATE() WHERE Id = @id AND EmpCode = @me AND IssueStatus = 'Approved' AND AcceptedAt IS NULL", new { id, me }, tx);
            if (n == 0) throw new ApiException("There is nothing for you to confirm on that item.", 404);
            return new { Id = id };
        }));
    }

    // Shared checks for approving or rejecting: it must be waiting, the person must be at the document level, and not decide their own.
    static async Task<(CustodyRow Row, AuthUser User)> Decidable(SqlConnection c, SqlTransaction tx, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id)
    {
        var user = Caller(ctx);
        var cu = await Load(c, tx, id);
        var issuePhase = cu.IssueStatus == "Pending";
        if (!issuePhase && cu.ReturnStatus != "Pending") throw new ApiException("Nothing is waiting for a decision on that handover.", 409);
        var need = DocLevel(cfg, s);
        if (s.UserLevel < need) throw new ApiException($"Deciding needs approval level {need}. Yours is {s.UserLevel}.", 403);
        var prepared = issuePhase ? cu.IssuedBy : await c.ExecuteScalarAsync<string?>("SELECT ReturnedTo FROM hrms.AssetCustody WHERE Id=@id", new { id }, tx);
        if (string.Equals(prepared, user.UserId, StringComparison.OrdinalIgnoreCase)) throw new ApiException("You cannot approve a handover you prepared yourself.", 403);
        if (string.Equals(cu.EmpCode, user.EmployeeCode, StringComparison.OrdinalIgnoreCase)) throw new ApiException("You cannot decide your own asset handover.", 403);
        return (cu, user);
    }
}
