using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Leave approval follows the ERP's own authorisation chain. The leave row carries Userlevel (the level of whoever last approved it)
// and documentlevel (the level that makes it final). Userid on the leave row is the person it currently sits with, and every hand-over
// or note is a row in inter01.UserwiseTransaction (TransNo, Userid, Auth = that person's level, Descr = note), exactly as the ERP writes them.

public record Approver(string Id, string Name, string RoleName, int Level);
public record LeaveInfo(string EmpCode, string Name, string LeaveType, DateTime FromDate, DateTime ToDate, decimal Days);

public static class Workflow
{
    public const string LeaveType = "458";
    public const string ApprovalOption = "H201";

    // People who can receive a request: they hold the approvals menu in their role at a level above the sender's.
    public static async Task<List<Approver>> Approvers(SqlConnection c, SqlTransaction? tx, int aboveLevel, params string[] exclude) =>
        (await c.QueryAsync<Approver>(@"
SELECT u.userid AS Id, ISNULL(NULLIF(u.name,''), u.username) AS Name, r.rolename AS RoleName, CAST(a.[level] AS int) AS Level
FROM interface.Userdef u
JOIN interface.Roledef r ON r.roleid = u.roleid
JOIN interface.accessright a ON a.roleid = u.roleid AND a.optionid = @opt AND CAST(a.access AS int) = 1
WHERE ISNULL(u.[disable],0) = 0 AND CAST(a.[level] AS int) > @level AND u.userid NOT IN @exclude
ORDER BY a.[level], Name", new { opt = ApprovalOption, level = aboveLevel, exclude = exclude.Length == 0 ? new[] { "" } : exclude }, tx)).ToList();

    public static Task Log(SqlConnection c, SqlTransaction tx, string transNo, string userId, int level, string? note = null) =>
        c.ExecuteAsync("INSERT inter01.UserwiseTransaction (TransNo, TransType, Userid, DateTime, Auth, Descr) VALUES (@transNo, @type, @userId, GETDATE(), @level, @note)",
            new { transNo, type = LeaveType, userId, level, note = note ?? "" }, tx);

    // Hand the leave to another person: the leave row now sits with them and they get a row at their own level.
    public static async Task Push(SqlConnection c, SqlTransaction tx, long id, string transNo, Approver to, string? note = null)
    {
        await c.ExecuteAsync("UPDATE inter01.EMPLOYEELEAVEDETAILS SET Userid=@to WHERE Id=@id", new { id, to = to.Id }, tx);
        await Log(c, tx, transNo, to.Id, to.Level, note);
        var info = await Describe(c, tx, id);
        if (info is not null)
        {
            var back = note?.StartsWith("Sent back", StringComparison.OrdinalIgnoreCase) == true;
            await Notifier.Send(c, tx, to.Id, "leave", back ? "A leave request was sent back to you" : "A leave request is waiting for you",
                Summary(info) + (back ? ". " + note : ""), "admin/approvals");
        }
    }

    public static Task<LeaveInfo?> Describe(SqlConnection c, SqlTransaction? tx, long id) =>
        c.QueryFirstOrDefaultAsync<LeaveInfo?>(@"SELECT l.EmpCode, ISNULL(e.Empname, l.EmpCode) AS Name, l.LeaveType, l.FromDate, l.ToDate, CAST(ISNULL(l.NoofLeavedays,0) AS decimal(9,2)) AS Days
            FROM inter01.EMPLOYEELEAVEDETAILS l LEFT JOIN inter01.EmployeeMaster e ON e.EmpCode = l.EmpCode WHERE l.Id = @id", new { id }, tx);

    // The ERP's leave-delete procedure leaves the accrual rows its insert trigger created. An accrual with no amount has not reached payroll,
    // so it goes with the leave; a posted accrual is never touched (changes are already refused once an accrual is posted).
    public static Task RemoveAccruals(SqlConnection c, SqlTransaction tx, string? transNo) =>
        string.IsNullOrEmpty(transNo) ? Task.CompletedTask
            : c.ExecuteAsync("DELETE inter01.EmpAccuralPayment WHERE LTransno = @transNo AND ISNULL(Amount,0) = 0", new { transNo }, tx);

    // Facts that help the person deciding, worked out from the data alone: notice given, what the balance will be, who else is away, how much
    // has been taken. Advice only; the decision stays with the approver.
    public static async Task<List<string>> Facts(SqlConnection c, ApprovalRow l)
    {
        var facts = new List<string>();
        try
        {
            var notice = l.Applied is null ? (int?)null : (int)(l.FromDate.Date - l.Applied.Value.Date).TotalDays;
            if (notice is < 0) facts.Add($"Requested after the leave started ({-notice} day{(notice == -1 ? "" : "s")} late)");
            else if (notice is >= 0 and <= 1) facts.Add(notice == 0 ? "Starts the same day it was requested" : "Only 1 day of notice");
            else if (notice is not null) facts.Add($"{notice} days of notice");

            var bal = (await LeaveBalances.For(c, l.EmpCode, true)).FirstOrDefault();
            if (bal is { HasPayroll: true })
            {
                if (l.LeaveType == "ALVESAL")
                {
                    // this request is already counted in Pending, so the balance after approval is what is left once it is taken
                    var after = bal.Available;
                    facts.Add(after < 0 ? $"Annual leave would go {-after:0.#} day{(-after == 1 ? "" : "s")} below the balance" : $"Annual leave balance after this request: {after:0.#} days");
                }
                facts.Add($"Taken so far in {bal.Year}: {bal.TakenThisYear:0.#} days");
            }

            var others = (await c.QueryAsync<string>(@"
SELECT ISNULL(e.Empname, l.EmpCode) FROM inter01.EMPLOYEELEAVEDETAILS l LEFT JOIN inter01.EmployeeMaster e ON e.EmpCode = l.EmpCode
WHERE ISNULL(l.Deleteflag,0) = 0 AND l.EmpCode <> @emp AND l.FromDate <= @to AND l.ToDate >= @from ORDER BY e.Empname",
                new { emp = l.EmpCode, from = l.FromDate.Date, to = l.ToDate.Date })).ToList();
            var t = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
            facts.Add(others.Count == 0 ? "Nobody else is on leave on these dates"
                : $"{others.Count} other{(others.Count == 1 ? "" : "s")} on leave on these dates: " + string.Join(", ", others.Take(3).Select(n => t.ToTitleCase(n.ToLowerInvariant()))) + (others.Count > 3 ? $" and {others.Count - 3} more" : ""));
        }
        catch { /* facts are a convenience; a failure here must never block an approval */ }
        return facts;
    }

    public static string Summary(LeaveInfo l) =>
        $"{System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(l.Name.ToLowerInvariant())}: {LeaveTypes.Name(l.LeaveType)}, {l.FromDate:d MMM} to {l.ToDate:d MMM yyyy} ({l.Days:0.#} days)";
}
