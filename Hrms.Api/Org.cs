using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Departments and who a leave request goes to. A department has one first approver (its head). The company-wide rule says at which approval level a
// request counts as fully approved: an ordinary employee's request ends with HR, a manager's goes HR and then on to the Director. Levels are the same
// numbers as the leave-approvals right (H201) on each role, so the chain is changed by changing roles and levels, not code.

public record Department(int Id, string Name, string? ApproverUserId, string? ApproverName, bool Active, int Members);
public record LeaveStep(string UserId, string Name, string? RoleName, int Level, DateTime At, string? Note);
// The rest of the chain for a request still waiting: each level above its holder up to where it ends, and who can give the final approval.
public record UpcomingLevel(int Level, List<string> Names, bool Final);
public record LeaveTimeline(List<LeaveStep> Steps, List<UpcomingLevel> Upcoming, int FinalLevel, List<string> FinalApprovers);
public record DepartmentInput(string Name, string? ApproverUserId, bool? Active);
public record DeptMember(string EmpCode, string Name, string? Designation, int? DeptId, string? DeptName);
public record DeptAssignInput(int? DeptId, List<string> EmpCodes);
public record LeaveRule(int EmployeeFinal, int ManagerFinal);
public record DepartmentOverview(List<Department> Departments, LeaveRule Rule, List<Approver> Approvers);
public record PendingAll(List<LeaveRow> Rows, List<Approver> Assignable);
public record LeaveRoute(List<Approver> Allowed, int DocLevel, string Why, int OwnLevel);

public static class LeaveNotes
{
    // Attach each request's reason. If the table is not there yet the lists simply show no reason.
    public static async Task Fill(SqlConnection c, IEnumerable<LeaveRow> rows)
    {
        var list = rows.Where(r => !string.IsNullOrEmpty(r.TransNo)).ToList();
        if (list.Count == 0) return;
        try
        {
            var map = (await c.QueryAsync<(string TransNo, string Reason, string? Remarks)>("SELECT TransNo, Reason, Remarks FROM hrms.LeaveNote WHERE TransNo IN @t", new { t = list.Select(r => r.TransNo!).Distinct().ToArray() }))
                .ToDictionary(x => x.TransNo, x => x);
            foreach (var r in list) if (map.TryGetValue(r.TransNo!, out var v)) { r.Reason = string.IsNullOrEmpty(v.Reason) ? null : v.Reason; r.Remarks = v.Remarks; }
        }
        catch { }
    }
}

public static class LeaveRouting
{
    public static async Task<LeaveRule> Rule(SqlConnection c, SqlTransaction? tx)
    {
        try
        {
            var r = await c.QueryFirstOrDefaultAsync<LeaveRule?>("SELECT TOP 1 EmployeeFinalLevel AS EmployeeFinal, ManagerFinalLevel AS ManagerFinal FROM hrms.LeaveRule", transaction: tx);
            if (r is not null) return r;
        }
        catch { /* the table is created on start */ }
        return new LeaveRule(8, 10);
    }

    static bool HoldsApprovals(AuthUser u) => u.Rights.TryGetValue(Workflow.ApprovalOption, out var r) && r.View;
    internal static int ApprovalLevel(AuthUser u) => u.Rights.TryGetValue(Workflow.ApprovalOption, out var r) ? r.Level : 0;

    // Who this person's request can go to, and the level at which it counts as approved.
    public static async Task<LeaveRoute> For(SqlConnection c, SqlTransaction? tx, HrmsSession s, AuthUser user, string empCode)
    {
        var rule = await Rule(c, tx);
        var manager = HoldsApprovals(user);
        if (manager)
        {
            // a manager's request goes to HR, then HR passes it to the Director; HR and above go to whoever sits above them
            // the level on the Leave Approvals right only: the My Leave authority level is not a position in this chain (it is often 9 for everyone)
            var own = ApprovalLevel(user);
            var above = await Workflow.Approvers(c, tx, own, user.UserId, empCode);
            var allowed = own < rule.EmployeeFinal ? above.Where(a => a.Level == rule.EmployeeFinal).ToList() : above;
            return new LeaveRoute(allowed.Count > 0 ? allowed : above, rule.ManagerFinal, "manager", own);
        }

        // an ordinary employee has approved nothing yet: everyone who can approve leave is above them, whatever authority level their role carries
        var list = await Workflow.Approvers(c, tx, 0, user.UserId, empCode);
        string? head = null;
        try
        {
            head = await c.ExecuteScalarAsync<string?>(@"SELECT d.ApproverUserId FROM hrms.EmployeeDepartment ed JOIN hrms.Department d ON d.Id = ed.DeptId
                WHERE ed.EmpCode = @empCode AND d.Active = 1 AND d.ApproverUserId IS NOT NULL", new { empCode }, tx);
        }
        catch { /* no department tables yet: the employee picks, as before */ }
        var first = head is null ? null : list.FirstOrDefault(a => string.Equals(a.Id, head, StringComparison.OrdinalIgnoreCase));
        return first is not null ? new LeaveRoute([first], rule.EmployeeFinal, "department", 0) : new LeaveRoute(list, rule.EmployeeFinal, "open", 0);
    }
}

public static class OrgEndpoints
{
    static AuthUser Caller(HttpContext ctx) => (AuthUser)ctx.Items["user"]!;

    static async Task<DepartmentOverview> Overview(SqlConnection c)
    {
        var rows = (await c.QueryAsync<Department>(@"
SELECT d.Id, d.Name, d.ApproverUserId, ISNULL(NULLIF(u.name,''), u.username) AS ApproverName, CAST(d.Active AS bit) AS Active,
       (SELECT COUNT(*) FROM hrms.EmployeeDepartment e WHERE e.DeptId = d.Id) AS Members
FROM hrms.Department d LEFT JOIN interface.Userdef u ON u.userid = d.ApproverUserId ORDER BY d.Name")).ToList();
        return new DepartmentOverview(rows, await LeaveRouting.Rule(c, null), await Workflow.Approvers(c, null, 0, ""));
    }

    public static void MapOrg(this WebApplication app)
    {
        app.MapGet("/api/admin/departments", async (Db db) =>
        {
            await using var c = db.Open();
            return Results.Ok(await Overview(c));
        });

        app.MapGet("/api/admin/departments/members", async (Db db) =>
        {
            await using var c = db.Open();
            return Results.Ok((await c.QueryAsync<DeptMember>(Sql.EmpCte + @"
SELECT e.EmpCode, e.Name, e.Designation, d.Id AS DeptId, d.Name AS DeptName
FROM emp e LEFT JOIN hrms.EmployeeDepartment ed ON ed.EmpCode = e.EmpCode LEFT JOIN hrms.Department d ON d.Id = ed.DeptId
WHERE e.Status = 'active' ORDER BY e.Name")).ToList());
        });

        app.MapPost("/api/admin/departments", async (Db db, DepartmentInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var name = (x.Name ?? "").Trim();
            if (name.Length == 0) throw new ApiException("Enter the department name.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Department WHERE Name = @name", new { name }, tx) > 0) throw new ApiException("That department already exists.", 409);
            var id = await c.ExecuteScalarAsync<int>("INSERT hrms.Department (Name, ApproverUserId) OUTPUT INSERTED.Id VALUES (@name, @a)", new { name, a = string.IsNullOrWhiteSpace(x.ApproverUserId) ? null : x.ApproverUserId }, tx);
            return new { Id = id };
        }));

        app.MapPut("/api/admin/departments/{id:int}", async (Db db, int id, DepartmentInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var name = (x.Name ?? "").Trim();
            if (name.Length == 0) throw new ApiException("Enter the department name.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Department WHERE Name = @name AND Id <> @id", new { name, id }, tx) > 0) throw new ApiException("Another department has that name.", 409);
            var n = await c.ExecuteAsync("UPDATE hrms.Department SET Name=@name, ApproverUserId=@a, Active=@active WHERE Id=@id",
                new { id, name, a = string.IsNullOrWhiteSpace(x.ApproverUserId) ? null : x.ApproverUserId, active = x.Active ?? true }, tx);
            if (n == 0) throw new ApiException("Department not found.", 404);
            return new { Id = id };
        }));

        app.MapDelete("/api/admin/departments/{id:int}", async (Db db, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.EmployeeDepartment WHERE DeptId = @id", new { id }, tx) > 0)
                throw new ApiException("People are still in this department. Move them first.", 409);
            await c.ExecuteAsync("DELETE FROM hrms.Department WHERE Id = @id", new { id }, tx);
            return new { Id = id };
        }));

        app.MapPost("/api/admin/departments/assign", async (Db db, HttpContext ctx, DeptAssignInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (x.EmpCodes is null || x.EmpCodes.Count == 0) throw new ApiException("Choose at least one employee.");
            var me = Caller(ctx).UserId;
            if (x.DeptId is null)
                await c.ExecuteAsync("DELETE FROM hrms.EmployeeDepartment WHERE EmpCode IN @codes", new { codes = x.EmpCodes }, tx);
            else
            {
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Department WHERE Id = @id", new { id = x.DeptId }, tx) == 0) throw new ApiException("Department not found.", 404);
                foreach (var code in x.EmpCodes.Distinct(StringComparer.OrdinalIgnoreCase))
                    await c.ExecuteAsync(@"MERGE hrms.EmployeeDepartment AS t USING (SELECT @code AS EmpCode) s ON t.EmpCode = s.EmpCode
                        WHEN MATCHED THEN UPDATE SET DeptId = @dept, UpdatedBy = @me, UpdatedAt = GETDATE()
                        WHEN NOT MATCHED THEN INSERT (EmpCode, DeptId, UpdatedBy) VALUES (@code, @dept, @me);", new { code, dept = x.DeptId, me }, tx);
            }
            return new { Count = x.EmpCodes.Count };
        }));

        app.MapPut("/api/admin/departments/rule", async (Db db, LeaveRule x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (x.EmployeeFinal is < 1 or > 10 || x.ManagerFinal is < 1 or > 10) throw new ApiException("Levels are between 1 and 10.");
            if (x.ManagerFinal <= x.EmployeeFinal) throw new ApiException("The Director's level must be above HR's level.");
            await c.ExecuteAsync(@"UPDATE hrms.LeaveRule SET EmployeeFinalLevel = @e, ManagerFinalLevel = @m;
                IF @@ROWCOUNT = 0 INSERT hrms.LeaveRule (EmployeeFinalLevel, ManagerFinalLevel) VALUES (@e, @m)", new { e = x.EmployeeFinal, m = x.ManagerFinal }, tx);
            return x;
        }));

        // ----- HR: see every waiting request and move one to someone else -----
        app.MapGet("/api/admin/approvals/all", async (Db db, HttpContext ctx) =>
        {
            await using var c = db.Open();
            var me = Caller(ctx);
            var rule = await LeaveRouting.Rule(c, null);
            if (LeaveRouting.ApprovalLevel(me) < rule.EmployeeFinal) return Results.Json(new { error = "Only HR can see every waiting request." }, statusCode: 403);
            var rows = (await c.QueryAsync<LeaveRow>(@"
SELECT l.Id AS Id, l.EmpCode, e.Empname AS Name, l.LeaveType, l.FromDate, l.ToDate, l.NoofLeavedays AS Days, l.Applieddate AS Applied, l.Transno AS TransNo, 'Pending' AS Status,
       l.Userid AS HeldBy, (SELECT TOP 1 ISNULL(NULLIF(u.name,''), u.username) FROM interface.Userdef u WHERE u.userid = l.Userid) AS HeldByName
FROM inter01.EMPLOYEELEAVEDETAILS l JOIN inter01.EmployeeMaster e ON e.EmpCode = l.EmpCode
WHERE ISNULL(l.Deleteflag,0) = 0 AND ISNULL(l.Userlevel,0) < ISNULL(l.documentlevel,0) ORDER BY l.Applieddate, l.FromDate")).ToList();
            foreach (var l in rows) l.LeaveName = LeaveTypes.Name(l.LeaveType);
            await LeaveNotes.Fill(c, rows);
            return Results.Ok(new PendingAll(rows, await Workflow.Approvers(c, null, 0, "")));
        });

        app.MapPost("/api/admin/approvals/{id:long}/reassign", async (Db db, HttpContext ctx, long id, DecisionInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx);
            var rule = await LeaveRouting.Rule(c, tx);
            if (LeaveRouting.ApprovalLevel(me) < rule.EmployeeFinal) throw new ApiException("Only HR can move a request to someone else.", 403);
            var cu = await c.QueryFirstOrDefaultAsync<(string? EmpCode, string? TransNo, int Pending)>(
                "SELECT EmpCode, Transno AS TransNo, CASE WHEN ISNULL(Userlevel,0) < ISNULL(documentlevel,0) THEN 1 ELSE 0 END AS Pending FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id = @id AND ISNULL(Deleteflag,0) = 0",
                new { id }, tx);
            if (cu.EmpCode is null) throw new ApiException("Leave request not found.", 404);
            if (cu.Pending == 0) throw new ApiException("That request is already approved.", 409);
            if (string.IsNullOrWhiteSpace(x.To)) throw new ApiException("Choose who it goes to.");
            var to = (await Workflow.Approvers(c, tx, 0, "", cu.EmpCode)).FirstOrDefault(a => a.Id == x.To) ?? throw new ApiException("That person cannot approve leave requests.");
            await Workflow.Push(c, tx, id, cu.TransNo ?? "", to, "Moved by " + me.FullName + (string.IsNullOrWhiteSpace(x.Note) ? "" : ": " + x.Note.Trim()));
            await Notifier.Send(c, tx, cu.EmpCode, "leave", "Your leave request moved on", "It is now with " + to.Name + " (moved by HR).", "my/leave");
            return new { Id = id, With = to.Name };
        }));
    }
}
