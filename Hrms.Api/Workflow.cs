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
FROM HRMSINTER.interface.Userdef u
JOIN HRMSINTER.interface.Roledef r ON r.roleid = u.roleid
JOIN HRMSINTER.interface.accessright a ON a.roleid = u.roleid AND a.optionid = @opt AND CAST(a.access AS int) = 1
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

    public static string Summary(LeaveInfo l) =>
        $"{System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(l.Name.ToLowerInvariant())}: {LeaveTypes.Name(l.LeaveType)}, {l.FromDate:d MMM} to {l.ToDate:d MMM yyyy} ({l.Days:0.#} days)";
}
