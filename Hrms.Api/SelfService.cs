using Dapper;

namespace Hrms.Api;

// Employee self-service. Everything here is pinned to the signed-in employee (CurrentUser.SelfCode makes the data layer expose only
// that employee), so these calls can never read or change anyone else's records.

public record LeaveRequestInput(string LeaveType, DateTime FromDate, DateTime ToDate, decimal? Days, bool SalaryWithoutLeave, string? PushTo);
public record LeaveTaken(string LeaveType, string TypeName, decimal Days, int Records);
public record MyProfile(EmployeeRow Employee, List<LeaveTaken> Taken, int Pending, int Year);

public static partial class Writes
{
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
            return rows;
        });

        app.MapGet("/api/me/approvers", async (Db db, HrmsSession s, HttpContext ctx) =>
        {
            var user = (AuthUser)ctx.Items["user"]!;
            await using var c = db.Open();
            return await Workflow.Approvers(c, null, s.UserLevel, user.UserId, Me(ctx));
        });

        app.MapPost("/api/me/leave", async (Db db, HrmsSession s, HttpContext ctx, LeaveRequestInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var me = Me(ctx);
            var last = await LastProcessedMonthEnd(c, tx);
            if (last is not null && x.FromDate.Date <= last) throw new ApiException("Payroll is already processed for these dates. Please contact HR.", 409);
            var user = (AuthUser)ctx.Items["user"]!;
            if (string.IsNullOrWhiteSpace(x.PushTo)) throw new ApiException("Choose who should approve this request.");
            var target = (await Workflow.Approvers(c, tx, s.UserLevel, user.UserId, me)).FirstOrDefault(a => a.Id == x.PushTo)
                ?? throw new ApiException("That person cannot approve leave requests.");
            var input = new LeaveInput(me, x.LeaveType, x.FromDate, x.ToDate, DateTime.Today, null, x.Days, x.SalaryWithoutLeave);
            await ValidateLeave(c, tx, input, null);
            var transNo = await NextNo(c, tx, s, me, "458", "Employee Leave Apply");
            var days = x.Days ?? (decimal)(x.ToDate.Date - x.FromDate.Date).TotalDays + 1;
            // User level below the document level is what the ERP shows as "pending authorisation"; a manager approves it in Leave Approvals.
            await c.ExecuteAsync("inter01.EmployeeLeaveInsertNEW", new
            {
                EmpCode = me, FromDate = x.FromDate.Date, Todate = x.ToDate.Date, LeaveType = x.LeaveType, Applieddate = DateTime.Today, RejoiningDate = x.ToDate.AddDays(1).Date,
                Userid = s.UserId, Userlevel = s.UserLevel, Documentlevel = s.DocLevel, Transtype = "458", Transno = transNo, Deleteflag = false,
                LeaveSalWithoutLeave = x.SalaryWithoutLeave ? 1 : 0, LeaveSalWithLeave = 1, Lnoofdays = days
            }, tx, commandType: System.Data.CommandType.StoredProcedure);
            await LogTransaction(c, tx, s, transNo, "458");
            var leaveId = await c.ExecuteScalarAsync<long>("SELECT Id FROM inter01.EMPLOYEELEAVEDETAILS WHERE Transno=@transNo AND ISNULL(Deleteflag,0)=0", new { transNo }, tx);
            await Workflow.Push(c, tx, leaveId, transNo, target);
            return new { TransNo = transNo, Days = days, Pending = s.UserLevel < s.DocLevel, With = target.Name };
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
