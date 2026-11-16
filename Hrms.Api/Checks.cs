using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Rule-based checks for HR: things worth a second look in the latest payroll and this month's attendance. Fixed rules with thresholds
// HR can change; nothing is edited and nothing is sent anywhere. A finding is a prompt to look, not a verdict.

public record CheckFinding(string Area, string Severity, string Check, string EmpCode, string Name, string Detail);
public record SalaryStatus(string Month, string? LastProcessedMonth, bool Processed, int PaidEmployees, bool? Posted, int UnapprovedLeave, int? UnapprovedVariable, int? UnpostedVariable, bool ReadyToProcess);
public record CheckResult(string? PayrollMonth, string? PreviousMonth, string AttendanceMonth, int Employees, List<CheckFinding> Findings, SalaryStatus? Salary = null);

public static class Checks
{
    sealed class Lite { public string EmpCode { get; set; } = ""; public string Name { get; set; } = ""; public string Status { get; set; } = ""; public DateTime? Doj { get; set; } public DateTime? TermDate { get; set; } }

    static string Title(string s) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase((s ?? "").ToLowerInvariant());

    public static async Task<CheckResult> Run(SqlConnection c, string? month, int payChangePercent, int lateDays, int absentDays, int missingPunches)
    {
        payChangePercent = Math.Clamp(payChangePercent, 5, 500); lateDays = Math.Clamp(lateDays, 1, 31); absentDays = Math.Clamp(absentDays, 1, 31); missingPunches = Math.Clamp(missingPunches, 1, 31);
        var findings = new List<CheckFinding>();
        // a code can appear more than once in the master data; keep one row per code, preferring the active one
        var people = (await c.QueryAsync<Lite>(Sql.EmpCte + "SELECT EmpCode, Name, Status, Doj, TermDate FROM emp")).GroupBy(p => p.EmpCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Status == "active" ? 0 : 1).First(), StringComparer.OrdinalIgnoreCase);

        // ----- payroll: the chosen (default latest) month against the one before it -----
        var months = (await c.QueryAsync<string>("SELECT DISTINCT CONVERT(varchar(7),ProcessedDate,120) FROM inter01.EmployeePaymentProcess ORDER BY 1 DESC")).ToList();
        var cur = month is not null && months.Contains(month) ? month : months.FirstOrDefault();
        var prev = cur is null ? null : months.SkipWhile(m => m != cur).Skip(1).FirstOrDefault();
        if (cur is not null)
        {
            var now = (await c.QueryAsync<PayslipRow>(Sql.PayrollForMonth, new { month = cur })).ToList();
            var before = prev is null ? new Dictionary<string, PayslipRow>() : (await c.QueryAsync<PayslipRow>(Sql.PayrollForMonth, new { month = prev })).GroupBy(r => r.EmpCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var monthStart = DateTime.ParseExact(cur + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var monthLabel = monthStart.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

            foreach (var r in now)
            {
                var name = Title(r.Name);
                if (r.Net <= 0 && r.Basic > 0)
                    findings.Add(new("Payroll", "High", "Zero or negative net pay", r.EmpCode, name, $"Net pay {r.Net:N0} with basic {r.Basic:N0} in {monthLabel}"));

                if (before.TryGetValue(r.EmpCode, out var p) && p.Net > 0)
                {
                    var change = (double)((r.Net - p.Net) / p.Net) * 100;
                    if (Math.Abs(change) >= payChangePercent)
                        findings.Add(new("Payroll", Math.Abs(change) >= 50 ? "High" : "Medium", change > 0 ? "Net pay jumped" : "Net pay dropped", r.EmpCode, name,
                            $"{r.Net:N0} against {p.Net:N0} last month ({(change > 0 ? "+" : "")}{change:0}%)"));
                }

                if (people.TryGetValue(r.EmpCode, out var e) && e.TermDate is { } left && left < monthStart && r.Net > 0)
                    findings.Add(new("Payroll", "High", "Paid after leaving", r.EmpCode, name, $"Left on {left:d MMM yyyy} but paid {r.Net:N0} for {monthLabel}"));
            }

            // paid last month but missing from this one (and still on the books)
            var paidNow = now.Select(r => r.EmpCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, p) in before)
                if (!paidNow.Contains(code) && people.TryGetValue(code, out var e) && e.Status == "active")
                    findings.Add(new("Payroll", "Medium", "Not paid this month", code, Title(p.Name), $"Paid {p.Net:N0} last month but has no payslip for {monthLabel}"));
        }

        // ----- attendance: this month so far -----
        var today = DateTime.Today;
        var from = new DateTime(today.Year, today.Month, 1);
        var active = people.Values.Where(p => p.Status == "active").ToList();
        if (active.Count > 0)
        {
            var data = await AttendanceEngine.Load(c, active.Select(p => p.EmpCode).ToList(), from, today);
            var nowT = DateTime.Now;
            foreach (var p in active)
            {
                var days = Enumerable.Range(0, (today - from).Days + 1).Select(i => AttendanceEngine.Day(data, p.EmpCode, from.AddDays(i), nowT)).ToList();
                var sum = AttendanceEngine.Summarise(from.ToString("yyyy-MM"), days);
                if (sum.PresentDays == 0 && sum.WorkedMinutes == 0 && sum.Missing == 0) continue;   // not using attendance this month: nothing meaningful to say
                var name = Title(p.Name);
                if (sum.Late >= lateDays) findings.Add(new("Attendance", sum.Late >= lateDays * 2 ? "High" : "Medium", "Often late", p.EmpCode, name, $"Late on {sum.Late} days this month"));
                if (sum.Absent >= absentDays) findings.Add(new("Attendance", sum.Absent >= absentDays * 2 ? "High" : "Medium", "Absent days", p.EmpCode, name, $"{sum.Absent} absent days this month with no leave"));
                if (sum.Missing >= missingPunches) findings.Add(new("Attendance", "Low", "Missing punches", p.EmpCode, name, $"{sum.Missing} days with a missing punch (they can ask for a correction)"));
            }
        }

        // people leaving soon (or already gone) who still hold company assets and have no clearance
        try
        {
            foreach (var l in await AssetExtras.Leaving(c, null, includeWithoutAssets: false))
                findings.Add(new("Offboarding", l.TermDate <= DateTime.Today.AddDays(7) ? "High" : "Medium", "Leaving with company assets", l.EmpCode, Title(l.Name),
                    $"Last working day {l.TermDate:d MMM yyyy}, still holds {l.AssetsOut} asset{(l.AssetsOut == 1 ? "" : "s")} and no clearance has been started"));
        }
        catch { /* the asset tables may not exist yet; the other checks still stand */ }

        var salary = await SalaryChecks(c, cur, people, findings);

        static int Rank(string s) => s == "High" ? 0 : s == "Medium" ? 1 : 2;
        findings = findings.OrderBy(f => Rank(f.Severity)).ThenBy(f => f.Area).ThenBy(f => f.Name).ToList();
        return new CheckResult(cur, prev, from.ToString("yyyy-MM"), people.Count, findings, salary);
    }

    // The ERP's Salary Calculation screen refuses to run while leave or variable salary in the month is unapproved, and posts to the ledger only once.
    // This repeats those checks for the next month to be processed so HR can clear them first. It only reads; processing and posting stay in the ERP.
    static async Task<SalaryStatus?> SalaryChecks(SqlConnection c, string? lastProcessed, Dictionary<string, Lite> people, List<CheckFinding> findings)
    {
        try
        {
            var start = lastProcessed is null ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
                : DateTime.ParseExact(lastProcessed + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddMonths(1);
            var end = start.AddMonths(1).AddDays(-1);
            var key = start.ToString("yyyy-MM");
            var label = start.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

            var paid = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmployeePaymentProcess WHERE ProcessedDate >= @start AND ProcessedDate < DATEADD(day,1,@end)", new { start, end });

            // leave in the month that has not reached the document level
            var leave = (await c.QueryAsync<(string EmpCode, string LeaveType, DateTime FromDate, DateTime ToDate, string? TransNo)>(
                @"SELECT EmpCode, LeaveType, FromDate, ToDate, Transno FROM inter01.EMPLOYEELEAVEDETAILS
                  WHERE ISNULL(Deleteflag,0)=0 AND ISNULL(Userlevel,0) < ISNULL(documentlevel,0) AND FromDate <= @end AND ToDate >= @start", new { start, end }))
                .Where(l => people.ContainsKey(l.EmpCode)).ToList();
            foreach (var l in leave)
                findings.Add(new("Salary processing", "High", "Leave not approved", l.EmpCode, Title(people[l.EmpCode].Name),
                    $"{l.LeaveType} {l.FromDate:d MMM} to {l.ToDate:d MMM yyyy} ({l.TransNo}) must be approved before {label} salary is processed"));

            // variable salary (loans, advances, additions, deductions) in the month: needs the table's approval and posting columns
            var cols = (await c.QueryAsync<string>("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('inter01.EMPVARIABLESALARY')")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int? unapproved = null, unposted = null;
            var baseWhere = "ISNULL(Deleteflag,0)=0 AND ISNULL(Variabledelete,0)=0 AND [Date] >= @start AND [Date] < DATEADD(day,1,@end)";
            var hasLevels = cols.Contains("UserLevel") && cols.Contains("DocumentLevel");
            if (hasLevels)
            {
                var rows = (await c.QueryAsync<(string EmpCode, string Type, decimal Amount)>(
                    $"SELECT Empcode AS EmpCode, [Type], Amount FROM inter01.EMPVARIABLESALARY WHERE {baseWhere} AND ISNULL(TRY_CAST(UserLevel AS int),0) < ISNULL(TRY_CAST(DocumentLevel AS int),0)", new { start, end }))
                    .Where(r => people.ContainsKey(r.EmpCode)).ToList();
                unapproved = rows.Count;
                foreach (var g in rows.GroupBy(r => r.EmpCode))
                    findings.Add(new("Salary processing", "High", "Variable salary not approved", g.Key, Title(people[g.Key].Name),
                        $"{g.Count()} entr{(g.Count() == 1 ? "y" : "ies")} ({string.Join(", ", g.Select(r => r.Type).Distinct())}) for {label} still need approval"));
            }
            var posted = new[] { "Posted", "PostFlag", "IsPosted", "Postingflag" }.FirstOrDefault(cols.Contains);
            if (hasLevels && posted is not null)
            {
                var rows = (await c.QueryAsync<(string EmpCode, string Type)>(
                    $"SELECT Empcode AS EmpCode, [Type] FROM inter01.EMPVARIABLESALARY WHERE {baseWhere} AND ISNULL(TRY_CAST(UserLevel AS int),0) >= ISNULL(TRY_CAST(DocumentLevel AS int),0) AND ISNULL(TRY_CAST([{posted}] AS int),0)=0", new { start, end }))
                    .Where(r => people.ContainsKey(r.EmpCode)).ToList();
                unposted = rows.Count;
                foreach (var g in rows.GroupBy(r => r.EmpCode))
                    findings.Add(new("Salary processing", "Medium", "Variable salary not posted", g.Key, Title(people[g.Key].Name),
                        $"{g.Count()} approved entr{(g.Count() == 1 ? "y" : "ies")} ({string.Join(", ", g.Select(r => r.Type).Distinct())}) for {label} are not posted yet"));
            }

            // has the ERP already posted this month's salary to the ledger (voucher type 751)? Only answerable where the ledger tables exist
            bool? isPosted = null;
            if (await c.ExecuteScalarAsync<int?>("SELECT OBJECT_ID('inter01.ledger_header')") is not null && await c.ExecuteScalarAsync<int?>("SELECT OBJECT_ID('inter01.ledger_footer')") is not null)
                isPosted = await c.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM inter01.ledger_header WHERE dateT >= @start AND dateT < DATEADD(day,1,@end) AND ISNULL(deleteflag,0)=0 AND vouchertype='751'
                    AND srno IN (SELECT srno FROM inter01.ledger_footer WHERE descrp='Salary Posting')", new { start, end }) > 0;

            var ready = paid == 0 && leave.Count == 0 && (unapproved ?? 0) == 0 && (unposted ?? 0) == 0;
            return new SalaryStatus(key, lastProcessed, paid > 0, paid, isPosted, leave.Count, unapproved, unposted, ready);
        }
        catch { return null; }   // the checks are advice; if a table is missing the rest of the page still works
    }

    public static void MapChecks(this WebApplication app)
    {
        // guarded by the payroll reports right (see Auth.Guards)
        app.MapGet("/api/admin/checks", async (Db db, string? month, int? payChange, int? late, int? absent, int? missing) =>
        {
            await using var c = db.Open();
            return Results.Ok(await Run(c, month, payChange ?? 20, late ?? 5, absent ?? 3, missing ?? 3));
        });
    }
}
