using Dapper;

namespace Hrms.Api;

// Employee self-service. Everything here is pinned to the signed-in employee (CurrentUser.SelfCode makes the data layer expose only
// that employee), so these calls can never read or change anyone else's records.

public record LeaveRequestInput(string LeaveType, DateTime FromDate, DateTime ToDate, decimal? Days, bool SalaryWithoutLeave, string? PushTo, string? Reason = null, string? Remarks = null);
public record LeaveTaken(string LeaveType, string TypeName, decimal Days, int Records);
public record MyProfile(EmployeeRow Employee, List<LeaveTaken> Taken, int Pending, int Year);

public static partial class Writes
{
    internal static string? Cut(string? s) { var t = s?.Trim(); return string.IsNullOrEmpty(t) ? null : t.Length > 500 ? t[..500] : t; }
    static string Me(HttpContext ctx) => (ctx.Items["user"] as AuthUser)?.EmployeeCode ?? throw new ApiException("Your login is not linked to an employee record.", 403);

    public static void MapSelf(this WebApplication app)
    {
        app.MapGet("/api/me", async (Db db, HttpContext ctx) =>
        {
            var me = Me(ctx);
            await using var c = db.Open();
            var emp = await c.QuerySingleOrDefaultAsync<EmployeeRow>(Sql.Employee, new { code = me });
            if (emp is null) return Results.NotFound();
            var year = DateTime.Today.Year;
            var leave = (await c.QueryAsync<LeaveRow>(Sql.LeaveForEmployee, new { code = me })).Where(l => l.FromDate.Year == year).ToList();
            var taken = leave.Where(l => l.Status == "Approved").GroupBy(l => l.LeaveType)
                .Select(g => new LeaveTaken(g.Key, LeaveTypes.Name(g.Key), g.Sum(x => x.Days), g.Count())).OrderByDescending(t => t.Days).ToList();
            return Results.Ok(new MyProfile(emp, taken, leave.Count(l => l.Status == "Pending"), year));
        });

        app.MapGet("/api/me/leave", async (Db db, HttpContext ctx, DateTime? from, DateTime? to) =>
        {
            Me(ctx);
            await using var c = db.Open();
            var rows = (await c.QueryAsync<LeaveRow>(Sql.Leave, new { type = (string?)null, start = from ?? DateTime.Today.AddYears(-1), end = to ?? DateTime.Today.AddYears(1), q = (string?)null })).ToList();
            foreach (var l in rows) l.LeaveName = LeaveTypes.Name(l.LeaveType);
            // rejected requests are removed from the ERP's leave table, so they come from the history kept at rejection (negative Id: nothing to cancel or open)
            try
            {
                var me = Me(ctx);
                var rejected = (await c.QueryAsync<LeaveRow>(@"SELECT -r.Id AS Id, r.EmpCode, r.LeaveType, r.FromDate, r.ToDate, ISNULL(r.Days,0) AS Days, r.Applied, 'Rejected' AS Status, r.TransNo, r.Reason AS RejectReason
                    FROM hrms.LeaveRejection r WHERE r.EmpCode = @me AND r.FromDate <= @end AND r.ToDate >= @start", new { me, start = from ?? DateTime.Today.AddYears(-1), end = to ?? DateTime.Today.AddYears(1) })).ToList();
                foreach (var l in rejected) l.LeaveName = LeaveTypes.Name(l.LeaveType);
                rows.AddRange(rejected);
            }
            catch { /* the history table is created on start; until then the list is just the live requests */ }
            await LeaveNotes.Fill(c, rows);
            return rows;
        });

        // the steps a request has been through, oldest first (a rejected request is found through its saved copy, which has a negative id)
        app.MapGet("/api/me/leave/{id:long}/timeline", async (Db db, HttpContext ctx, long id) =>
        {
            var me = Me(ctx);
            await using var c = db.Open();
            var transNo = id > 0
                ? await c.ExecuteScalarAsync<string?>("SELECT Transno FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id AND EmpCode=@me", new { id, me })
                : await c.ExecuteScalarAsync<string?>("SELECT TransNo FROM hrms.LeaveRejection WHERE Id=@rid AND EmpCode=@me", new { rid = -id, me });
            if (transNo is null) return Results.Json(new { error = "Leave record not found." }, statusCode: 404);
            var steps = (await c.QueryAsync<LeaveStep>(@"
SELECT t.Userid AS UserId, ISNULL(NULLIF(u.name,''), ISNULL(u.username, t.Userid)) AS Name, r.rolename AS RoleName, CAST(ISNULL(t.Auth,0) AS int) AS Level, t.DateTime AS At,
       NULLIF(LTRIM(RTRIM(CAST(t.Descr AS nvarchar(max)))), '') AS Note
FROM inter01.UserwiseTransaction t LEFT JOIN interface.Userdef u ON u.userid = t.Userid LEFT JOIN interface.Roledef r ON r.roleid = u.roleid
WHERE t.TransNo = @transNo ORDER BY t.id", new { transNo })).ToList();
            // where the request stands in the chain: who holds it, the level it ends at, and who is still to come
            var upcoming = new List<UpcomingLevel>();
            var finalLevel = 0; var finalNames = new List<string>();
            try
            {
                var row = id > 0 ? await c.QueryFirstOrDefaultAsync<(string? HeldBy, int Level, int Final)>(
                    "SELECT Userid AS HeldBy, CAST(ISNULL(Userlevel,0) AS int) AS Level, CAST(ISNULL(documentlevel,0) AS int) AS Final FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id AND EmpCode=@me", new { id, me }) : default;
                finalLevel = row.Final;
                if (finalLevel > 0)
                {
                    var everyone = await Workflow.Approvers(c, null, 0, "");
                    finalNames = everyone.Where(a => a.Level == finalLevel && !string.Equals(a.Id, me, StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).Distinct().ToList();
                    if (row.Level < finalLevel && row.HeldBy is not null)
                    {
                        var heldLevel = await c.ExecuteScalarAsync<int?>(@"SELECT TOP 1 CAST(a.[level] AS int) FROM interface.Userdef u JOIN interface.accessright a ON a.roleid = u.roleid AND a.optionid = @opt
                            WHERE u.userid = @held", new { opt = Workflow.ApprovalOption, held = row.HeldBy }) ?? 0;
                        foreach (var g in everyone.Where(a => a.Level > heldLevel && a.Level <= finalLevel && !string.Equals(a.Id, me, StringComparison.OrdinalIgnoreCase)).GroupBy(a => a.Level).OrderBy(g => g.Key))
                            upcoming.Add(new UpcomingLevel(g.Key, g.Select(a => a.Name).Distinct().ToList(), g.Key == finalLevel));
                        if (upcoming.Count == 0 || upcoming[^1].Level != finalLevel) upcoming.Add(new UpcomingLevel(finalLevel, finalNames, true));
                    }
                }
            }
            catch { /* the trail still shows without the road ahead */ }
            return Results.Ok(new LeaveTimeline(steps, upcoming, finalLevel, finalNames));
        });

        app.MapGet("/api/me/approvers", async (Db db, HrmsSession s, HttpContext ctx) =>
        {
            var user = (AuthUser)ctx.Items["user"]!;
            await using var c = db.Open();
            return (await LeaveRouting.For(c, null, s, user, Me(ctx))).Allowed;
        });

        app.MapPost("/api/me/leave", async (Db db, HrmsSession s, HttpContext ctx, LeaveRequestInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var me = Me(ctx);
            var last = await LastProcessedMonthEnd(c, tx);
            if (last is not null && x.FromDate.Date <= last) throw new ApiException("Payroll is already processed for these dates. Please contact HR.", 409);
            var user = (AuthUser)ctx.Items["user"]!;
            var route = await LeaveRouting.For(c, tx, s, user, me);
            // a fixed route (the department head, or HR for a manager) is used even if the form sent nothing; otherwise the person must pick from the allowed list
            var target = route.Allowed.FirstOrDefault(a => a.Id == x.PushTo) ?? (route.Allowed.Count == 1 ? route.Allowed[0] : null);
            // the top of the chain (the Director today): nobody is above them, so their leave is approved as it is saved
            var topOfChain = route.Allowed.Count == 0 && route.OwnLevel >= route.DocLevel;
            if (target is null && !topOfChain) throw new ApiException(string.IsNullOrWhiteSpace(x.PushTo) ? "Choose who should approve this request." : "That person cannot approve leave requests.");
            var input = new LeaveInput(me, x.LeaveType, x.FromDate, x.ToDate, DateTime.Today, null, x.Days, x.SalaryWithoutLeave);
            await ValidateLeave(c, tx, input, null);
            var transNo = await NextNo(c, tx, s, me, "458", "Employee Leave Apply");
            var days = x.Days ?? (decimal)(x.ToDate.Date - x.FromDate.Date).TotalDays + 1;
            // User level below the document level is what the ERP shows as "pending authorisation"; a manager approves it in Leave Approvals.
            await c.ExecuteAsync("inter01.EmployeeLeaveInsertNEW", new
            {
                EmpCode = me, FromDate = x.FromDate.Date, Todate = x.ToDate.Date, LeaveType = x.LeaveType, Applieddate = DateTime.Today, RejoiningDate = x.ToDate.AddDays(1).Date,
                Userid = s.UserId, Userlevel = route.OwnLevel, Documentlevel = route.DocLevel, Transtype = "458", Transno = transNo, Deleteflag = false,
                LeaveSalWithoutLeave = x.SalaryWithoutLeave ? 1 : 0, LeaveSalWithLeave = 1, Lnoofdays = days
            }, tx, commandType: System.Data.CommandType.StoredProcedure);
            await LogTransaction(c, tx, s, transNo, "458");
            var leaveId = await c.ExecuteScalarAsync<long>("SELECT Id FROM inter01.EMPLOYEELEAVEDETAILS WHERE Transno=@transNo AND ISNULL(Deleteflag,0)=0", new { transNo }, tx);
            if (!string.IsNullOrWhiteSpace(x.Reason) || !string.IsNullOrWhiteSpace(x.Remarks))
                await c.ExecuteAsync("INSERT hrms.LeaveNote (TransNo, Reason, Remarks) VALUES (@transNo, @reason, @remarks)",
                    new { transNo, reason = Cut(x.Reason) ?? "", remarks = Cut(x.Remarks) }, tx);
            if (target is not null) await Workflow.Push(c, tx, leaveId, transNo, target);
            return new { TransNo = transNo, Days = days, Pending = route.OwnLevel < route.DocLevel, With = target?.Name ?? "approved automatically" };
        }));

        app.MapDelete("/api/me/leave/{id:long}", async (Db db, HttpContext ctx, long id, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var me = Me(ctx);
            var cur = await c.QueryFirstOrDefaultAsync<(string EmpCode, DateTime FromDate, DateTime ToDate, int Pending)>(
                "SELECT EmpCode, FromDate, ToDate, CASE WHEN ISNULL(Userlevel,0) < ISNULL(documentlevel,0) THEN 1 ELSE 0 END AS Pending FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id AND ISNULL(Deleteflag,0)=0",
                new { id }, tx);
            if (cur.EmpCode != me) throw new ApiException("Leave record not found.", 404);
            if (cur.Pending == 0) throw new ApiException("Approved leave can only be cancelled by HR.", 409);
            await GuardLeaveChange(c, tx, id, cur.FromDate, cur.ToDate);
            var transNo = await c.ExecuteScalarAsync<string?>("SELECT Transno FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id", new { id }, tx);
            await c.ExecuteAsync("inter01.SPDELETELeaveDetails", new { Id = id }, tx, commandType: System.Data.CommandType.StoredProcedure);
            await Workflow.RemoveAccruals(c, tx, transNo);
            return new { Id = id };
        }));

        app.MapGet("/api/me/payslips", async (Db db, HttpContext ctx) =>
        {
            var me = Me(ctx);
            await using var c = db.Open();
            return (await c.QueryAsync<PayslipRow>(Sql.PayrollForEmployee, new { code = me })).ToList();
        });

        app.MapGet("/api/me/payslip", async (Db db, HttpContext ctx, string? month) =>
        {
            var me = Me(ctx);
            await using var c = db.Open();
            try { return Results.Ok(await Reports.Run(c, "payslip", new ReportQuery(month, me, null, null, "all", null, false))); }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        });
    }
}
