using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// "Ask HR": quick answers for the signed-in employee, without any AI service. A question is matched to one of a fixed set of intents by
// keywords, and each intent runs a fixed query. Everything is pinned to the signed-in employee (the same data layer the self-service
// screens use), so an answer can never contain anyone else's records. Anything not understood gets a list of questions that work.

public record AskAnswer(string Intent, string Title, string Text, List<string> Lines, string? Link, List<string> Suggestions);

public static class AskHr
{
    sealed record Intent(string Id, string Question, string[] Phrases, string[] Words, string? Menu = null, bool NeedsEmployee = true);

    static readonly Intent[] Intents =
    [
        new("balance", "How many leave days do I have?", ["leave balance", "how many leave", "leave left", "days left", "leave days", "leave available", "annual leave", "remaining leave"], ["balance", "available", "remaining"]),
        new("leave", "What is the status of my leave requests?", ["leave request", "my leave", "leave status", "pending leave", "leave approved", "leave history"], ["pending", "approved", "rejected"]),
        new("nextleave", "When is my next leave?", ["next leave", "upcoming leave", "planned leave", "leave coming"], ["upcoming"]),
        new("payslip", "What was my last net pay?", ["last payslip", "net pay", "my salary", "my payslip", "pay slip", "salary slip", "how much salary", "take home"], ["payslip", "salary", "pay", "net", "wage"], "H104"),
        new("punchtoday", "Did I punch in today?", ["punch today", "attendance today", "check in", "checked in", "punch in", "clock in", "am i in", "punch out"], ["punch", "punched", "today"], "H106"),
        new("attmonth", "How is my attendance this month?", ["attendance this month", "late this month", "days absent", "absent days", "how many late", "late days", "my attendance"], ["late", "absent", "attendance", "present"], "H106"),
        new("holiday", "When is the next holiday?", ["next holiday", "public holiday", "day off"], ["holiday", "holidays"], "H106", false),
        new("onleave", "Who is on leave this week?", ["on leave", "who is on leave", "who's on leave", "away this week", "team leave"], ["away"], null, false),
        new("approver", "Who approves my leave?", ["who approves", "my approver", "approve my leave", "send my leave"], ["approver", "approvers"], "H102"),
        new("approvals", "What is waiting for my approval?", ["waiting for me", "my approvals", "pending approvals", "to approve", "approval inbox", "approvals inbox"], ["approvals"], null, false),
        new("hello", "Hello", ["good morning", "good afternoon", "good evening"], ["hi", "hello", "hey", "thanks", "thank"], null, false),
    ];

    static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1, ["feb"] = 2, ["february"] = 2, ["mar"] = 3, ["march"] = 3, ["apr"] = 4, ["april"] = 4, ["may"] = 5, ["jun"] = 6, ["june"] = 6,
        ["jul"] = 7, ["july"] = 7, ["aug"] = 8, ["august"] = 8, ["sep"] = 9, ["sept"] = 9, ["september"] = 9, ["oct"] = 10, ["october"] = 10, ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace("'", ""), @"[^a-z0-9\s]", " ");

    static bool CanView(AuthUser user, string option) => user.IsAdmin || (user.Rights.TryGetValue(option, out var r) && r.View);

    static bool Allowed(Intent i, AuthUser user)
    {
        if (i.NeedsEmployee && string.IsNullOrEmpty(user.EmployeeCode)) return false;
        return i.Menu is null || user.Menu.Any(m => m.Id == i.Menu);
    }

    // The questions offered as buttons: only those this person can actually get an answer to.
    public static List<string> Suggestions(AuthUser user) =>
        Intents.Where(i => i.Id != "hello" && Allowed(i, user) && (i.Id != "approvals" || CanView(user, "H201") || CanView(user, "H206")))
            .Select(i => i.Question).ToList();

    static Intent? Match(string q, AuthUser user)
    {
        var text = " " + Norm(q) + " ";
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        Intent? best = null; var top = 0;
        foreach (var i in Intents)
        {
            var score = i.Phrases.Count(p => text.Contains(Norm(p), StringComparison.Ordinal)) * 3 + i.Words.Count(w => tokens.Contains(w));
            // the intent's own button text always wins for itself
            if (Norm(i.Question).Trim() == text.Trim()) score += 10;
            if (score > top) { top = score; best = i; }
        }
        return best;
    }

    public static async Task<AskAnswer> Answer(SqlConnection c, HrmsSession s, AuthUser user, string? question)
    {
        var q = (question ?? "").Trim();
        var suggestions = Suggestions(user);
        if (q.Length == 0) return Help(user, suggestions, "Ask me about your leave, payslips or attendance.");
        if (q.Length > 300) q = q[..300];

        var intent = Match(q, user);
        if (intent is null) return Help(user, suggestions, "I did not understand that. I can answer these questions:");
        if (!Allowed(intent, user))
            return new AskAnswer(intent.Id, "Not available", string.IsNullOrEmpty(user.EmployeeCode)
                ? "Your login is not linked to an employee record, so I cannot look up personal details."
                : "That information is not available for your role.", [], null, suggestions);

        var me = user.EmployeeCode;
        var today = DateTime.Today;
        switch (intent.Id)
        {
            case "hello":
                return new AskAnswer("hello", "Hello " + Fmt(user.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? user.UserName), "What would you like to know?", [], null, suggestions);

            case "balance":
            {
                var b = (await LeaveBalances.For(c, me, true)).FirstOrDefault();
                if (b is null || !b.HasPayroll) return new AskAnswer("balance", "Leave balance", "No leave balance is available yet. It appears after your first payroll is processed.", [], "my/leave", suggestions);
                var lines = new List<string> { $"Balance as of {b.AsOfMonth}: {b.Balance:0.#} days", $"Waiting for approval: {b.Pending:0.#} days" };
                if (b.Unprocessed > 0) lines.Add($"Approved but not yet in payroll: {b.Unprocessed:0.#} days");
                lines.Add($"Taken in {b.Year}: {b.TakenThisYear:0.#} days");
                return new AskAnswer("balance", "Leave balance", $"You have {b.Available:0.#} leave days available.", lines, "my/leave", suggestions);
            }

            case "leave":
            {
                var rows = (await c.QueryAsync<LeaveRow>(Sql.LeaveForEmployee, new { code = me })).OrderByDescending(l => l.FromDate).Take(5).ToList();
                if (rows.Count == 0) return new AskAnswer("leave", "Leave requests", "You have no leave requests yet.", [], "my/leave", suggestions);
                var lines = rows.Select(l => $"{LeaveTypes.Name(l.LeaveType)}, {l.FromDate:d MMM} to {l.ToDate:d MMM yyyy} ({l.Days:0.#} days): {l.Status}").ToList();
                var pending = rows.Count(l => l.Status == "Pending");
                return new AskAnswer("leave", "Your latest leave", pending == 0 ? "Nothing is waiting for approval." : $"{pending} request{(pending == 1 ? " is" : "s are")} waiting for approval.", lines, "my/leave", suggestions);
            }

            case "nextleave":
            {
                var next = (await c.QueryAsync<LeaveRow>(Sql.LeaveForEmployee, new { code = me })).Where(l => l.ToDate >= today && l.Status != "Rejected").OrderBy(l => l.FromDate).FirstOrDefault();
                return next is null
                    ? new AskAnswer("nextleave", "Next leave", "You have no upcoming leave.", [], "my/leave", suggestions)
                    : new AskAnswer("nextleave", "Next leave", $"{LeaveTypes.Name(next.LeaveType)} from {next.FromDate:d MMM} to {next.ToDate:d MMM yyyy} ({next.Days:0.#} days), {next.Status.ToLowerInvariant()}.", [], "my/leave", suggestions);
            }

            case "payslip":
            {
                var slips = (await c.QueryAsync<PayslipRow>(Sql.PayrollForEmployee, new { code = me })).OrderByDescending(p => p.Month).ToList();
                if (slips.Count == 0) return new AskAnswer("payslip", "Payslip", "No payslip has been processed for you yet.", [], "my/payslips", suggestions);
                var pick = PickMonth(q, slips) ?? slips[0];
                var label = DateTime.TryParse(pick.Month + "-01", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : pick.Month;
                var lines = new List<string> { $"Worked days: {pick.WorkedDays:0.#}", $"Additions: {pick.Addition:N0}", $"Deductions: {pick.Deduction:N0}" };
                return new AskAnswer("payslip", "Payslip " + label, $"Your net pay for {label} was {pick.Net:N0}.", lines, "my/payslips", suggestions);
            }

            case "punchtoday":
            {
                var data = await AttendanceEngine.Load(c, [me], today, today);
                var day = AttendanceEngine.Day(data, me, today, DateTime.Now);
                if (day.FirstIn is null) return new AskAnswer("punchtoday", "Today", $"You have not punched in today ({day.Status.ToLowerInvariant()}).", [], "my/attendance", suggestions);
                var lines = new List<string> { $"First in: {day.FirstIn:hh:mm tt}" };
                if (day.LastOut is not null && !day.OpenIn) lines.Add($"Last out: {day.LastOut:hh:mm tt}");
                lines.Add($"Worked so far: {day.WorkedMinutes / 60}h {day.WorkedMinutes % 60:00}m");
                if (day.Late) lines.Add($"Late by {day.LateMinutes} min");
                return new AskAnswer("punchtoday", "Today", day.OpenIn ? $"You punched in at {day.FirstIn:hh:mm tt} and have not punched out yet." : $"You punched in at {day.FirstIn:hh:mm tt}.", lines, "my/attendance", suggestions);
            }

            case "attmonth":
            {
                var from = new DateTime(today.Year, today.Month, 1);
                var data = await AttendanceEngine.Load(c, [me], from, today);
                var days = Enumerable.Range(0, (today - from).Days + 1).Select(i => AttendanceEngine.Day(data, me, from.AddDays(i), DateTime.Now)).ToList();
                var m = AttendanceEngine.Summarise(from.ToString("yyyy-MM"), days);
                var lines = new List<string> { $"Present: {m.PresentDays:0.#} days", $"Late: {m.Late} day{(m.Late == 1 ? "" : "s")}", $"Absent: {m.Absent} day{(m.Absent == 1 ? "" : "s")}", $"On leave: {m.Leave} day{(m.Leave == 1 ? "" : "s")}" };
                if (m.Missing > 0) lines.Add($"Missing punches: {m.Missing} (you can ask for a correction)");
                return new AskAnswer("attmonth", "Attendance, " + from.ToString("MMMM", CultureInfo.InvariantCulture), $"So far this month: {m.PresentDays:0.#} days present, {m.Late} late, {m.Absent} absent.", lines, "my/attendance", suggestions);
            }

            case "holiday":
            {
                var rows = (await c.QueryAsync<HolidayRow>("SELECT TOP 3 Id, HolidayDate, Name, Kind FROM hrms.AttHoliday WHERE HolidayDate >= @today ORDER BY HolidayDate", new { today })).ToList();
                return rows.Count == 0
                    ? new AskAnswer("holiday", "Holidays", "No upcoming holiday has been set up.", [], null, suggestions)
                    : new AskAnswer("holiday", "Next holiday", $"{rows[0].Name} on {rows[0].HolidayDate:dddd, d MMMM yyyy}.", rows.Skip(1).Select(h => $"{h.Name}: {h.HolidayDate:ddd d MMM yyyy}").ToList(), null, suggestions);
            }

            case "onleave":
            {
                var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                var sunday = monday.AddDays(6);
                var rows = (await c.QueryAsync<LeaveRow>(Sql.Leave, new { type = (string?)null, start = monday, end = sunday, q = (string?)null }))
                    .Where(l => l.Status != "Rejected" && l.ToDate >= monday && l.FromDate <= sunday).OrderBy(l => l.FromDate).Take(15).ToList();
                if (rows.Count == 0) return new AskAnswer("onleave", "On leave this week", "Nobody you can see is on leave this week.", [], null, suggestions);
                var lines = rows.Select(l => $"{Fmt(l.Name)}: {LeaveTypes.Name(l.LeaveType)}, {l.FromDate:d MMM} to {l.ToDate:d MMM} ({l.Status.ToLowerInvariant()})").ToList();
                return new AskAnswer("onleave", "On leave this week", $"{rows.Count} leave record{(rows.Count == 1 ? "" : "s")} this week.", lines, null, suggestions);
            }

            case "approver":
            {
                var list = await Workflow.Approvers(c, null, s.UserLevel, user.UserId, me);
                return list.Count == 0
                    ? new AskAnswer("approver", "Approvers", "No approver is set up above your level. Please contact HR.", [], "my/leave", suggestions)
                    : new AskAnswer("approver", "Who approves your leave", "You can send a leave request to any of these people.", list.Take(10).Select(a => $"{Fmt(a.Name)} ({a.RoleName})").ToList(), "my/leave", suggestions);
            }

            default: // approvals
                return new AskAnswer("approvals", "Approvals inbox", "Open your approvals inbox to see what is waiting for your decision.", [], "approvals", suggestions);
        }
    }

    static AskAnswer Help(AuthUser user, List<string> suggestions, string text) => new("help", "Ask HR", text, [], null, suggestions);

    // "net pay in october", "payslip 2026-08": the matching month if the question names one.
    static PayslipRow? PickMonth(string q, List<PayslipRow> slips)
    {
        var m = Regex.Match(q, @"\b(20\d\d)[-/ ](0?[1-9]|1[0-2])\b");
        if (m.Success) return slips.FirstOrDefault(p => p.Month == $"{m.Groups[1].Value}-{int.Parse(m.Groups[2].Value):00}");
        foreach (var w in Norm(q).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Months.TryGetValue(w, out var month)) return slips.FirstOrDefault(p => p.Month.EndsWith($"-{month:00}"));
        return null;
    }

    static string Fmt(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    public static void MapAskHr(this WebApplication app)
    {
        app.MapGet("/api/me/ask/suggestions", (HttpContext ctx) => Results.Ok(Suggestions((AuthUser)ctx.Items["user"]!)));

        // a GET: asking only reads, so it needs the view right and nothing more
        app.MapGet("/api/me/ask", async (Db db, HrmsSession s, HttpContext ctx, string? q) =>
        {
            var user = (AuthUser)ctx.Items["user"]!;
            await using var c = db.Open();
            return Results.Ok(await Answer(c, s, user, q));
        });
    }
}
