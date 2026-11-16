using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Offboarding clearance. HR starts a clearance for someone who is leaving; it carries a checklist, and it cannot be marked cleared while any
// company asset is still with the employee (or waiting to be issued to them) or while a checklist item is open. Loans and pending leave are
// shown as warnings only: they are settled in the final settlement, so they do not hold clearance back.

public record StartClearance(string? EmpCode, DateTime? LastWorkingDay, string? Reason);
public record ToggleItem(bool Done, string? Note);
public record AddItem(string? Title);

public class ClearanceRow
{
    public int Id { get; set; }
    public string EmpCode { get; set; } = "";
    public string? EmpName { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = "";
    public string StartedBy { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string? ClearedBy { get; set; }
    public DateTime? ClearedAt { get; set; }
    public int AssetsOut { get; set; }
    public int OpenItems { get; set; }
}

public class ClearanceItemRow
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public string? DoneBy { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? Note { get; set; }
}

public record ClearanceCheck(string Key, string Title, bool Ok, bool Blocking, string Detail);
public record ClearanceDetail(string EmpCode, string? EmpName, ClearanceRow? Clearance, List<ClearanceItemRow> Items, List<ClearanceCheck> Checks, List<CustodyRow> Assets, bool CanClear, List<string> Blockers);

public static class ClearanceEndpoints
{
    static readonly string[] DefaultItems =
    [
        "Work handed over to a colleague",
        "Email and system access closed",
        "Exit interview done",
        "Final settlement reviewed by payroll",
    ];

    sealed class EmpName { public string? Name { get; set; } }
    sealed class Pending { public int N { get; set; } }

    static AuthUser Caller(HttpContext ctx) => (AuthUser)ctx.Items["user"]!;
    static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    const string RowSelect = @"
SELECT cl.Id, cl.EmpCode, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = cl.EmpCode) AS EmpName, cl.LastWorkingDay, cl.Reason, cl.Status, cl.StartedBy, cl.StartedAt, cl.ClearedBy, cl.ClearedAt,
       (SELECT COUNT(*) FROM hrms.AssetCustody c WHERE c.EmpCode = cl.EmpCode AND c.IssueStatus IN ('Pending','Approved') AND ISNULL(c.ReturnStatus,'') <> 'Approved') AS AssetsOut,
       (SELECT COUNT(*) FROM hrms.ClearanceItem i WHERE i.ClearanceId = cl.Id AND i.Done = 0) AS OpenItems
FROM hrms.Clearance cl";

    public static async Task<ClearanceDetail> Detail(SqlConnection c, SqlTransaction? tx, string emp)
    {
        var name = await c.ExecuteScalarAsync<string?>("SELECT TOP 1 Empname FROM inter01.EmployeeMaster WHERE EmpCode = @emp", new { emp }, tx);
        var cl = await c.QueryFirstOrDefaultAsync<ClearanceRow>(RowSelect + " WHERE cl.EmpCode = @emp ORDER BY CASE WHEN cl.Status = 'Open' THEN 0 ELSE 1 END, cl.Id DESC", new { emp }, tx);
        var items = cl is null ? [] : (await c.QueryAsync<ClearanceItemRow>("SELECT Id, Title, Done, DoneBy, DoneAt, Note FROM hrms.ClearanceItem WHERE ClearanceId = @id ORDER BY Id", new { id = cl.Id }, tx)).ToList();
        var assets = await AssetEndpoints.Holdings(c, tx, emp);

        var checks = new List<ClearanceCheck>
        {
            new("assets", "All company assets returned", assets.Count == 0, true,
                assets.Count == 0 ? "Nothing is issued to this employee." : $"{assets.Count} item{(assets.Count == 1 ? "" : "s")} still out: " + string.Join(", ", assets.Take(4).Select(a => $"{a.AssetName} ({a.AssetCode})")) + (assets.Count > 4 ? " and more" : "")),
        };

        var loan = (await c.QueryAsync<LoanBalance>(Sql.LoanBalances, new { status = "all", q = emp }, tx)).FirstOrDefault(l => string.Equals(l.EmpCode, emp, StringComparison.OrdinalIgnoreCase));
        checks.Add(new("loans", "No outstanding loan or advance", loan is null || loan.Outstanding <= 0, false,
            loan is null || loan.Outstanding <= 0 ? "No loan or advance is outstanding." : $"{loan.Outstanding:N0} is still outstanding. It is recovered in the final settlement."));

        var leave = await c.QueryFirstAsync<Pending>("SELECT COUNT(*) AS N FROM inter01.EMPLOYEELEAVEDETAILS WHERE EmpCode = @emp AND ISNULL(Deleteflag,0) = 0 AND ISNULL(Userlevel,0) < ISNULL(documentlevel,0)", new { emp }, tx);
        checks.Add(new("leave", "No leave request waiting for a decision", leave.N == 0, false, leave.N == 0 ? "No leave request is pending." : $"{leave.N} leave request{(leave.N == 1 ? " is" : "s are")} still waiting for a decision."));

        var blockers = new List<string>();
        if (cl is not null && cl.Status == "Open")
        {
            if (assets.Count > 0) blockers.Add(checks[0].Detail);
            var open = items.Where(i => !i.Done).ToList();
            if (open.Count > 0) blockers.Add($"{open.Count} checklist item{(open.Count == 1 ? " is" : "s are")} not done: " + string.Join(", ", open.Take(3).Select(i => i.Title)) + (open.Count > 3 ? " and more" : ""));
        }
        return new ClearanceDetail(emp, name, cl, items, checks, assets, cl is { Status: "Open" } && blockers.Count == 0, blockers);
    }

    sealed class LoanBalance { public string EmpCode { get; set; } = ""; public decimal Outstanding { get; set; } }

    public static void MapClearance(this WebApplication app)
    {
        app.MapGet("/api/clearance", async (Db db, string? status) =>
        {
            await using var c = db.Open();
            return Results.Ok((await c.QueryAsync<ClearanceRow>(RowSelect + " WHERE (@status IS NULL OR cl.Status = @status) ORDER BY CASE WHEN cl.Status = 'Open' THEN 0 ELSE 1 END, cl.LastWorkingDay, cl.Id DESC", new { status = Nz(status) })).Take(300).ToList());
        });

        // What the clearance for one employee looks like now; also works before one is started, to show what is still out.
        app.MapGet("/api/clearance/employee/{emp}", async (Db db, string emp) =>
        {
            await using var c = db.Open();
            return Results.Ok(await Detail(c, null, emp));
        });

        app.MapPost("/api/clearance", async (Db db, HttpContext ctx, StartClearance x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var emp = Nz(x.EmpCode) ?? throw new ApiException("Choose the employee who is leaving.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmployeeMaster WHERE EmpCode = @emp", new { emp }, tx) == 0) throw new ApiException($"Employee {emp} does not exist.", 404);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Clearance WHERE EmpCode = @emp AND Status = 'Open'", new { emp }, tx) > 0) throw new ApiException("A clearance is already open for this employee.", 409);
            var id = await c.ExecuteScalarAsync<int>("INSERT hrms.Clearance (EmpCode, LastWorkingDay, Reason, Status, StartedBy) OUTPUT INSERTED.Id VALUES (@emp, @lwd, @reason, 'Open', @user)",
                new { emp, lwd = x.LastWorkingDay?.Date, reason = Nz(x.Reason), user = Caller(ctx).UserId }, tx);
            foreach (var title in DefaultItems) await c.ExecuteAsync("INSERT hrms.ClearanceItem (ClearanceId, Title) VALUES (@id, @title)", new { id, title }, tx);
            return new { Id = id };
        }));

        app.MapPost("/api/clearance/items/{id:int}/toggle", async (Db db, HttpContext ctx, int id, ToggleItem x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var open = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.ClearanceItem i JOIN hrms.Clearance cl ON cl.Id = i.ClearanceId WHERE i.Id = @id AND cl.Status = 'Open'", new { id }, tx);
            if (open == 0) throw new ApiException("That checklist item does not exist or its clearance is closed.", 404);
            await c.ExecuteAsync("UPDATE hrms.ClearanceItem SET Done = @done, DoneBy = CASE WHEN @done = 1 THEN @user END, DoneAt = CASE WHEN @done = 1 THEN GETDATE() END, Note = @note WHERE Id = @id",
                new { id, done = x.Done, user = Caller(ctx).UserId, note = Nz(x.Note) }, tx);
            return new { Id = id };
        }));

        app.MapPost("/api/clearance/{id:int}/items", async (Db db, int id, AddItem x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var title = Nz(x.Title) ?? throw new ApiException("Give the checklist item a name.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Clearance WHERE Id = @id AND Status = 'Open'", new { id }, tx) == 0) throw new ApiException("That clearance does not exist or is closed.", 404);
            var item = await c.ExecuteScalarAsync<int>("INSERT hrms.ClearanceItem (ClearanceId, Title) OUTPUT INSERTED.Id VALUES (@id, @title)", new { id, title = title.Length > 150 ? title[..150] : title }, tx);
            return new { Id = item };
        }));

        // Final clearance: refused while an asset is out or a checklist item is open.
        app.MapPost("/api/clearance/{id:int}/clear", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var emp = await c.ExecuteScalarAsync<string?>("SELECT EmpCode FROM hrms.Clearance WHERE Id = @id AND Status = 'Open'", new { id }, tx) ?? throw new ApiException("That clearance does not exist or is already closed.", 404);
            var d = await Detail(c, tx, emp);
            if (!d.CanClear) throw new ApiException("This employee cannot be cleared yet. " + string.Join(" ", d.Blockers.Select(b => b.TrimEnd('.') + ".")), 409);
            await c.ExecuteAsync("UPDATE hrms.Clearance SET Status = 'Cleared', ClearedBy = @user, ClearedAt = GETDATE() WHERE Id = @id", new { id, user = Caller(ctx).UserId }, tx);
            return new { Id = id };
        }));

        app.MapPost("/api/clearance/{id:int}/cancel", async (Db db, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (await c.ExecuteAsync("UPDATE hrms.Clearance SET Status = 'Cancelled' WHERE Id = @id AND Status = 'Open'", new { id }, tx) == 0) throw new ApiException("That clearance does not exist or is already closed.", 404);
            return new { Id = id };
        }));
    }
}
