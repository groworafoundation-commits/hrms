using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Annual leave balance, taken from the ERP's own payroll ledger so both systems always agree:
//   EmployeePaymentProcess.[CAnnual Leave]    days earned in that processed month (2.5 a month)
//   EmployeePaymentProcess.[CTMAnnual Leave]  the running balance after that month. It is never reset at year end, so carry-over is automatic.
// In the ERP only "annual leave with salary" (ALVESAL) draws that balance down. Leave taken after the last processed month has not reached the
// ledger yet, so it is subtracted here, together with requests still waiting for approval, to give what is available today.

public class LeaveBalance
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasPayroll { get; set; }
    public string? AsOfMonth { get; set; }
    public decimal BroughtForward { get; set; }
    public decimal Earned { get; set; }
    public decimal Used { get; set; }
    public decimal Balance { get; set; }
    public decimal Unprocessed { get; set; }
    public decimal Pending { get; set; }
    public decimal Available { get; set; }
    public decimal TakenThisYear { get; set; }
    public int Year { get; set; }
}

public static class LeaveBalances
{
    const string Query = @"
, lastp AS (SELECT Empcode, ProcessedDate, ISNULL([CTMAnnual Leave],0) AS ctm, ROW_NUMBER() OVER (PARTITION BY Empcode ORDER BY ProcessedDate DESC) AS rn FROM inter01.EmployeePaymentProcess),
bf AS (SELECT Empcode, ISNULL([CTMAnnual Leave],0) AS ctm, ROW_NUMBER() OVER (PARTITION BY Empcode ORDER BY ProcessedDate DESC) AS rn FROM inter01.EmployeePaymentProcess WHERE ProcessedDate < @jan1),
earn AS (SELECT Empcode, SUM(ISNULL([CAnnual Leave],0)) AS earned FROM inter01.EmployeePaymentProcess WHERE ProcessedDate >= @jan1 GROUP BY Empcode),
lv AS (
  SELECT l.EmpCode,
    SUM(CASE WHEN l.LeaveType = 'ALVESAL' AND ISNULL(l.Userlevel,0) >= ISNULL(l.documentlevel,0) AND lp.ProcessedDate IS NOT NULL AND l.FromDate > EOMONTH(lp.ProcessedDate) THEN l.NoofLeavedays ELSE 0 END) AS unprocessed,
    SUM(CASE WHEN l.LeaveType = 'ALVESAL' AND ISNULL(l.Userlevel,0) < ISNULL(l.documentlevel,0) THEN l.NoofLeavedays ELSE 0 END) AS pending,
    SUM(CASE WHEN ISNULL(l.Userlevel,0) >= ISNULL(l.documentlevel,0) AND YEAR(l.FromDate) = YEAR(@jan1) THEN l.NoofLeavedays ELSE 0 END) AS takenYear
  FROM inter01.EMPLOYEELEAVEDETAILS l LEFT JOIN lastp lp ON lp.Empcode = l.EmpCode AND lp.rn = 1
  WHERE ISNULL(l.Deleteflag,0) = 0 GROUP BY l.EmpCode)
SELECT e.EmpCode, e.Name, CAST(CASE WHEN lp.Empcode IS NULL THEN 0 ELSE 1 END AS bit) AS HasPayroll, CONVERT(varchar(7), lp.ProcessedDate, 120) AS AsOfMonth,
  CAST(ISNULL(b.ctm,0) AS decimal(9,2)) AS BroughtForward,
  CAST(CASE WHEN lp.ProcessedDate >= @jan1 THEN ISNULL(er.earned,0) ELSE 0 END AS decimal(9,2)) AS Earned,
  CAST(CASE WHEN lp.ProcessedDate >= @jan1 THEN ISNULL(b.ctm,0) + ISNULL(er.earned,0) - ISNULL(lp.ctm,0) ELSE 0 END AS decimal(9,2)) AS Used,
  CAST(ISNULL(lp.ctm,0) AS decimal(9,2)) AS Balance,
  CAST(ISNULL(lv.unprocessed,0) AS decimal(9,2)) AS Unprocessed, CAST(ISNULL(lv.pending,0) AS decimal(9,2)) AS Pending,
  CAST(ISNULL(lp.ctm,0) - ISNULL(lv.unprocessed,0) - ISNULL(lv.pending,0) AS decimal(9,2)) AS Available,
  CAST(ISNULL(lv.takenYear,0) AS decimal(9,2)) AS TakenThisYear, YEAR(@jan1) AS Year
FROM emp e LEFT JOIN lastp lp ON lp.Empcode = e.EmpCode AND lp.rn = 1 LEFT JOIN bf b ON b.Empcode = e.EmpCode AND b.rn = 1
LEFT JOIN earn er ON er.Empcode = e.EmpCode LEFT JOIN lv ON lv.EmpCode = e.EmpCode
WHERE (@emp IS NULL OR e.EmpCode = @emp) AND (@all = 1 OR e.Status = 'active') ORDER BY e.Name";

    public static async Task<List<LeaveBalance>> For(SqlConnection c, string? emp, bool includeInactive = false) =>
        (await c.QueryAsync<LeaveBalance>(Sql.EmpCte + Query, new { jan1 = new DateTime(DateTime.Today.Year, 1, 1), emp, all = includeInactive ? 1 : 0 })).ToList();

    public static void MapLeaveBalances(this WebApplication app)
    {
        app.MapGet("/api/me/leave/balance", async (Db db, HttpContext ctx) =>
        {
            var me = (ctx.Items["user"] as AuthUser)?.EmployeeCode ?? throw new ApiException("Your login is not linked to an employee record.", 403);
            await using var c = db.Open();
            return Results.Ok((await For(c, me, true)).FirstOrDefault());
        });

        app.MapGet("/api/leave/balances", async (Db db) =>
        {
            await using var c = db.Open();
            return Results.Ok(await For(c, null));
        });
    }
}
