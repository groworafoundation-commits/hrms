using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Attendance: punch in / punch out with the server clock, daily status (present, late, half day, absent, leave, holiday, weekly off),
// monthly summary and register, correction requests, shifts and holidays. Data lives in hrms.* in HRMSMAIN; approved leave is read from the ERP leave table.

public class ShiftRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int GraceMinutes { get; set; }
    public int FullDayMinutes { get; set; }
    public int HalfDayMinutes { get; set; }
    public string WeeklyOffs { get; set; } = "Sun";
    public bool IsDefault { get; set; }
    public bool Active { get; set; }
}

public class PunchRow
{
    public long Id { get; set; }
    public string EmpCode { get; set; } = "";
    public DateTime PunchTime { get; set; }
    public string Direction { get; set; } = "";
    public string Source { get; set; } = "";
    public string? WorkMode { get; set; }
    public string? Note { get; set; }
}

public class DayStatus
{
    public DateTime Date { get; set; }
    public string Status { get; set; } = "";
    public DateTime? FirstIn { get; set; }
    public DateTime? LastOut { get; set; }
    public int WorkedSeconds { get; set; }
    public int WorkedMinutes => WorkedSeconds / 60;
    public bool Late { get; set; }
    public int LateMinutes { get; set; }
    public bool EarlyOut { get; set; }
    public bool OpenIn { get; set; }
    public bool OffDayWork { get; set; }
    public bool IsOff { get; set; }
    public string? Holiday { get; set; }
    public string? LeaveName { get; set; }
    public int Punches { get; set; }
}

public record LeaveSpan(string EmpCode, DateTime FromDate, DateTime ToDate, string LeaveType);
public record AssignRow(string EmpCode, int ShiftId, DateTime FromDate);
public record HolidayRow(int Id, DateTime HolidayDate, string Name, string Kind);
public record PunchInput(string? WorkMode, string? Note);
public record TodayDto(DateTime Now, ShiftRow Shift, DayStatus Day, List<PunchRow> Punches, bool IsIn, DateTime? InSince, int RemainingMinutes);
public record MonthSummary(string Month, double PresentDays, int HalfDays, int Absent, int Leave, int Holidays, int Off, int Late, int Missing, int WorkedMinutes, int AvgMinutes, int WorkingDays);
public record MonthDto(string Month, List<DayStatus> Days, MonthSummary Summary, ShiftRow Shift);
public record RegularizeInput(DateTime Date, string? InTime, string? OutTime, string Reason);
public class RegularizationRow
{
    public int Id { get; set; }
    public string EmpCode { get; set; } = "";
    public string? Name { get; set; }
    public DateTime WorkDate { get; set; }
    public TimeSpan? InTime { get; set; }
    public TimeSpan? OutTime { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}
public record DecisionNote(string? Note);
public record ManualPunchInput(string Emp, DateTime Date, string Time, string Direction, string? Note);
public record ShiftInput(string Name, string Start, string End, int GraceMinutes, int FullDayMinutes, int HalfDayMinutes, string WeeklyOffs, bool IsDefault, bool Active);
public record AssignInput(List<string> Emps, int ShiftId, DateTime FromDate);
public record HolidayInput(DateTime Date, string Name, string? Kind);
public class BoardRow
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Designation { get; set; }
    public string Shift { get; set; } = "";
    public DayStatus Day { get; set; } = new();
}
public class RegisterRow
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Designation { get; set; }
    public List<string> Days { get; set; } = [];
    public MonthSummary Summary { get; set; } = default!;
}
public record RegisterDto(string Month, int DaysInMonth, List<RegisterRow> Rows);
public record EmpShiftRow(string EmpCode, string Name, string? Designation, string Shift);

public static class AttendanceEngine
{
    public sealed class Ctx
    {
        public List<ShiftRow> Shifts = [];
        public List<AssignRow> Assigns = [];
        public Dictionary<DateTime, string> Holidays = [];
        public ILookup<string, PunchRow> Punches = null!;
        public ILookup<string, LeaveSpan> Leaves = null!;
        public DateTime? StartDate;   // first day anyone punched; earlier days have no attendance data at all
    }

    public static async Task<Ctx> Load(SqlConnection c, IReadOnlyCollection<string> emps, DateTime from, DateTime to, SqlTransaction? tx = null)
    {
        var list = emps.Count == 0 ? new[] { "" } : emps.ToArray();
        var ctx = new Ctx
        {
            Shifts = (await c.QueryAsync<ShiftRow>("SELECT Id, Name, StartTime, EndTime, GraceMinutes, FullDayMinutes, HalfDayMinutes, WeeklyOffs, IsDefault, Active FROM hrms.AttShift", transaction: tx)).ToList(),
            Assigns = (await c.QueryAsync<AssignRow>("SELECT EmpCode, ShiftId, FromDate FROM hrms.AttShiftAssign WHERE EmpCode IN @list ORDER BY FromDate", new { list }, tx)).ToList(),
            Holidays = (await c.QueryAsync<HolidayRow>("SELECT Id, HolidayDate, Name, Kind FROM hrms.AttHoliday WHERE HolidayDate BETWEEN @from AND @to", new { from = from.Date, to = to.Date }, tx))
                .GroupBy(h => h.HolidayDate.Date).ToDictionary(g => g.Key, g => g.First().Name),
        };
        ctx.Punches = (await c.QueryAsync<PunchRow>(@"SELECT Id, EmpCode, PunchTime, Direction, Source, WorkMode, Note FROM hrms.AttPunch
            WHERE Deleted = 0 AND EmpCode IN @list AND PunchTime >= @from AND PunchTime < @next ORDER BY PunchTime, Id", new { list, from = from.Date, next = to.Date.AddDays(1) }, tx)).ToLookup(p => p.EmpCode, StringComparer.OrdinalIgnoreCase);
        ctx.Leaves = (await c.QueryAsync<LeaveSpan>(@"SELECT EmpCode, FromDate, ToDate, LeaveType FROM inter01.EMPLOYEELEAVEDETAILS
            WHERE ISNULL(Deleteflag,0) = 0 AND ISNULL(Userlevel,0) >= ISNULL(documentlevel,0) AND EmpCode IN @list AND FromDate <= @to AND ToDate >= @from", new { list, from = from.Date, to = to.Date }, tx))
            .ToLookup(l => l.EmpCode, StringComparer.OrdinalIgnoreCase);
        ctx.StartDate = await c.ExecuteScalarAsync<DateTime?>("SELECT CAST(MIN(PunchTime) AS date) FROM hrms.AttPunch WHERE Deleted = 0", transaction: tx);
        return ctx;
    }

    public static ShiftRow ShiftFor(Ctx ctx, string emp, DateTime day)
    {
        var assigned = ctx.Assigns.Where(a => string.Equals(a.EmpCode, emp, StringComparison.OrdinalIgnoreCase) && a.FromDate.Date <= day.Date).OrderBy(a => a.FromDate).LastOrDefault();
        return ctx.Shifts.FirstOrDefault(s => assigned is not null && s.Id == assigned.ShiftId)
            ?? ctx.Shifts.FirstOrDefault(s => s.IsDefault && s.Active) ?? ctx.Shifts.FirstOrDefault(s => s.Active) ?? ctx.Shifts.First();
    }

    public static DayStatus Day(Ctx ctx, string emp, DateTime day, DateTime now)
    {
        var shift = ShiftFor(ctx, emp, day);
        var punches = ctx.Punches[emp].Where(p => p.PunchTime.Date == day.Date).ToList();
        var leave = ctx.Leaves[emp].FirstOrDefault(l => l.FromDate.Date <= day.Date && l.ToDate.Date >= day.Date);
        ctx.Holidays.TryGetValue(day.Date, out var holiday);
        return Compute(day, shift, punches, holiday, leave is null ? null : LeaveTypes.Name(leave.LeaveType), now, ctx.StartDate);
    }

    public static DayStatus Compute(DateTime day, ShiftRow s, List<PunchRow> punches, string? holiday, string? leaveName, DateTime now, DateTime? startDate = null)
    {
        var d = new DayStatus { Date = day.Date, Punches = punches.Count, Holiday = holiday, LeaveName = leaveName };
        DateTime? open = null; var secs = 0;
        foreach (var p in punches)
        {
            if (p.Direction == "IN") { open ??= p.PunchTime; }
            else if (open is not null) { secs += (int)(p.PunchTime - open.Value).TotalSeconds; open = null; }
        }
        d.FirstIn = punches.FirstOrDefault(p => p.Direction == "IN")?.PunchTime;
        d.LastOut = punches.LastOrDefault(p => p.Direction == "OUT")?.PunchTime;
        var today = day.Date == now.Date;
        if (open is not null) { d.OpenIn = true; if (today) secs += Math.Max(0, (int)(now - open.Value).TotalSeconds); }
        d.WorkedSeconds = secs;

        d.IsOff = s.WeeklyOffs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(day.DayOfWeek.ToString()[..3], StringComparer.OrdinalIgnoreCase);
        var nonWorking = d.IsOff || holiday is not null;

        if (day.Date > now.Date) { d.Status = holiday is not null ? "Holiday" : d.IsOff ? "Off" : leaveName is not null ? "Leave" : "Future"; return d; }

        if (punches.Count > 0)
        {
            var mins = secs / 60;
            if (d.OpenIn) d.Status = today ? "Working" : "Missing";
            else if (today && mins < s.FullDayMinutes && now < day.Date + s.EndTime) d.Status = "Away";
            else if (mins >= s.FullDayMinutes) d.Status = "Present";
            else if (mins >= s.HalfDayMinutes) d.Status = "HalfDay";
            else d.Status = "Short";
            d.OffDayWork = nonWorking;
            if (!nonWorking && d.FirstIn is not null && d.FirstIn > day.Date + s.StartTime + TimeSpan.FromMinutes(s.GraceMinutes))
            {
                d.Late = true; d.LateMinutes = (int)(d.FirstIn.Value - (day.Date + s.StartTime)).TotalMinutes;
            }
            if (!nonWorking && d.LastOut is not null && !d.OpenIn && d.LastOut < day.Date + s.EndTime && mins < s.FullDayMinutes && !(today && now < day.Date + s.EndTime)) d.EarlyOut = true;
            return d;
        }

        d.Status = holiday is not null ? "Holiday" : d.IsOff ? "Off" : leaveName is not null ? "Leave"
            : startDate is null || day.Date < startDate.Value.Date ? "NoData"
            : today && now < day.Date + s.EndTime ? "Pending" : "Absent";
        return d;
    }

    public static MonthSummary Summarise(string month, List<DayStatus> days)
    {
        var worked = days.Where(d => d.Punches > 0).Sum(d => d.WorkedMinutes);
        var punched = days.Count(d => d.Punches > 0 && d.WorkedMinutes > 0);
        return new MonthSummary(month,
            days.Count(d => d.Status is "Present" or "Working") + days.Count(d => d.Status == "HalfDay") * 0.5,
            days.Count(d => d.Status == "HalfDay"),
            days.Count(d => d.Status is "Absent" or "Short"),
            days.Count(d => d.Status == "Leave"),
            days.Count(d => d.Status == "Holiday"),
            days.Count(d => d.Status == "Off"),
            days.Count(d => d.Late),
            days.Count(d => d.Status == "Missing"),
            worked, punched == 0 ? 0 : worked / punched,
            days.Count(d => d.Status is not ("Holiday" or "Off" or "Future" or "NoData")));
    }
}

public static class AttendanceEndpoints
{
    static string Me(HttpContext ctx) => (ctx.Items["user"] as AuthUser)?.EmployeeCode ?? throw new ApiException("Your login is not linked to an employee record.", 403);
    static AuthUser Caller(HttpContext ctx) => (AuthUser)ctx.Items["user"]!;
    static string? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static DateTime MonthStart(string? month) => DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) ? m : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    static bool Time(string? text, out TimeSpan t) => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);

    static async Task<TodayDto> Today(SqlConnection c, string emp, DateTime now)
    {
        var ctx = await AttendanceEngine.Load(c, [emp], now.Date, now.Date);
        var day = AttendanceEngine.Day(ctx, emp, now.Date, now);
        var shift = AttendanceEngine.ShiftFor(ctx, emp, now.Date);
        var punches = ctx.Punches[emp].Where(p => p.PunchTime.Date == now.Date).ToList();
        var last = punches.LastOrDefault();
        var isIn = last?.Direction == "IN";
        return new TodayDto(now, shift, day, punches, isIn, isIn ? last!.PunchTime : null, Math.Max(0, shift.FullDayMinutes - day.WorkedMinutes));
    }

    static async Task<bool> InScope(Db db, string emp)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>(Sql.EmpCte + "SELECT COUNT(*) FROM emp WHERE EmpCode = @emp", new { emp }) > 0;
    }

    public static void MapAttendance(this WebApplication app)
    {
        // ================= employee =================
        app.MapGet("/api/me/attendance/today", async (Db db, HttpContext ctx) =>
        {
            var me = Me(ctx);
            await using var c = db.Open();
            return Results.Ok(await Today(c, me, DateTime.Now));
        });

        app.MapPost("/api/me/attendance/punch", async (Db db, HttpContext ctx, PunchInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Me(ctx);
            var mode = Nz(x.WorkMode);
            if (mode is not null && !new[] { "Office", "Remote", "Site" }.Contains(mode)) throw new ApiException("Choose Office, Remote or Site.");
            var now = await c.ExecuteScalarAsync<DateTime>("SELECT GETDATE()", transaction: tx);
            var last = await c.QueryFirstOrDefaultAsync<PunchRow>(@"SELECT TOP 1 Id, EmpCode, PunchTime, Direction, Source, WorkMode, Note FROM hrms.AttPunch
                WHERE EmpCode=@me AND Deleted=0 AND PunchTime >= @day AND PunchTime < @next ORDER BY PunchTime DESC, Id DESC", new { me, day = now.Date, next = now.Date.AddDays(1) }, tx);
            if (last is not null && (now - last.PunchTime).TotalSeconds < 30) throw new ApiException("You punched a moment ago. Please wait a few seconds.", 409);
            var dir = last?.Direction == "IN" ? "OUT" : "IN";
            await c.ExecuteAsync(@"INSERT hrms.AttPunch (EmpCode, PunchTime, Direction, Source, WorkMode, Note, DeviceInfo, CreatedBy)
                VALUES (@me, @now, @dir, 'App', @mode, @note, @device, @user)",
                new { me, now, dir, mode, note = Nz(x.Note), device = ctx.Request.Headers.UserAgent.ToString() is { Length: > 200 } ua ? ua[..200] : ctx.Request.Headers.UserAgent.ToString(), user = Caller(ctx).UserId }, tx);
            return new { Direction = dir, Time = now };
        }));

        app.MapGet("/api/me/attendance/month", async (Db db, HttpContext ctx, string? month) =>
        {
            var me = Me(ctx);
            var from = MonthStart(month); var to = from.AddMonths(1).AddDays(-1);
            await using var c = db.Open();
            var data = await AttendanceEngine.Load(c, [me], from, to);
            var now = DateTime.Now;
            var days = Enumerable.Range(0, (to - from).Days + 1).Select(i => AttendanceEngine.Day(data, me, from.AddDays(i), now)).ToList();
            return Results.Ok(new MonthDto(from.ToString("yyyy-MM"), days, AttendanceEngine.Summarise(from.ToString("yyyy-MM"), days), AttendanceEngine.ShiftFor(data, me, now.Date)));
        });

        app.MapGet("/api/me/attendance/day", async (Db db, HttpContext ctx, DateTime date) =>
        {
            var me = Me(ctx);
            await using var c = db.Open();
            var data = await AttendanceEngine.Load(c, [me], date, date);
            return Results.Ok(new { Day = AttendanceEngine.Day(data, me, date, DateTime.Now), Punches = data.Punches[me].Where(p => p.PunchTime.Date == date.Date).ToList() });
        });

        app.MapGet("/api/me/attendance/requests", async (Db db, HttpContext ctx) =>
        {
            var me = Me(ctx);
            await using var c = db.Open();
            return (await c.QueryAsync<RegularizationRow>(@"SELECT Id, EmpCode, WorkDate, InTime, OutTime, Reason, Status, RequestedAt, DecidedBy, DecidedAt, DecisionNote
                FROM hrms.AttRegularization WHERE EmpCode=@me AND RequestedAt >= DATEADD(day,-90,GETDATE()) ORDER BY Id DESC", new { me })).ToList();
        });

        app.MapPost("/api/me/attendance/requests", async (Db db, HttpContext ctx, RegularizeInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Me(ctx);
            if (x.Date.Date > DateTime.Today) throw new ApiException("You can only correct today or a past day.");
            if (x.Date.Date < DateTime.Today.AddDays(-31)) throw new ApiException("Corrections are limited to the last 31 days. Please contact HR.");
            if (string.IsNullOrWhiteSpace(x.Reason)) throw new ApiException("Write the reason for the correction.");
            TimeSpan? tin = null, tout = null;
            if (Nz(x.InTime) is { } si) { if (!Time(si, out var t)) throw new ApiException("The in time is not valid."); tin = t; }
            if (Nz(x.OutTime) is { } so) { if (!Time(so, out var t)) throw new ApiException("The out time is not valid."); tout = t; }
            if (tin is null && tout is null) throw new ApiException("Enter the in time, the out time, or both.");
            if (tin is not null && tout is not null && tout <= tin) throw new ApiException("The out time must be after the in time.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttRegularization WHERE EmpCode=@me AND WorkDate=@d AND Status='Pending'", new { me, d = x.Date.Date }, tx) > 0)
                throw new ApiException("You already have a pending correction for that day.", 409);
            await c.ExecuteAsync("INSERT hrms.AttRegularization (EmpCode, WorkDate, InTime, OutTime, Reason) VALUES (@me, @d, @tin, @tout, @reason)",
                new { me, d = x.Date.Date, tin, tout, reason = x.Reason.Trim() }, tx);
            var who = await c.ExecuteScalarAsync<string?>("SELECT Empname FROM inter01.EmployeeMaster WHERE EmpCode=@me", new { me }, tx) ?? me;
            foreach (var holder in await Notifier.HoldersOf(c, tx, "H206", me))
                await Notifier.Send(c, tx, holder, "attendance", "An attendance correction needs review", $"{System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(who.ToLowerInvariant())}: {x.Date:d MMM yyyy}. {x.Reason.Trim()}", "admin/attendance/requests");
            return new { Date = x.Date.Date };
        }));

        app.MapDelete("/api/me/attendance/requests/{id:int}", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Me(ctx);
            if (await c.ExecuteAsync("UPDATE hrms.AttRegularization SET Status='Cancelled', DecidedAt=GETDATE() WHERE Id=@id AND EmpCode=@me AND Status='Pending'", new { id, me }, tx) == 0)
                throw new ApiException("Only your pending corrections can be cancelled.", 409);
            return new { Id = id };
        }));

        // ================= admin: board and register =================
        app.MapGet("/api/admin/attendance/today", async (Db db, DateTime? date) =>
        {
            var day = (date ?? DateTime.Today).Date;
            await using var c = db.Open();
            var people = (await c.QueryAsync<Person>(Sql.EmpCte + "SELECT EmpCode, Name, Designation, Doj FROM emp WHERE Status='active' ORDER BY Name")).ToList();
            var data = await AttendanceEngine.Load(c, people.Select(p => p.EmpCode).ToList(), day, day);
            var now = DateTime.Now;
            var rows = people.Select(p => new BoardRow { EmpCode = p.EmpCode, Name = p.Name, Designation = p.Designation, Shift = AttendanceEngine.ShiftFor(data, p.EmpCode, day).Name, Day = AttendanceEngine.Day(data, p.EmpCode, day, now) }).ToList();
            return Results.Ok(new { Date = day, Now = now, Rows = rows });
        });

        app.MapGet("/api/admin/attendance/register", async (Db db, string? month) =>
        {
            var from = MonthStart(month); var to = from.AddMonths(1).AddDays(-1);
            await using var c = db.Open();
            var people = (await c.QueryAsync<Person>(Sql.EmpCte + "SELECT EmpCode, Name, Designation, Doj FROM emp WHERE Status='active' ORDER BY Name")).ToList();
            var data = await AttendanceEngine.Load(c, people.Select(p => p.EmpCode).ToList(), from, to);
            var now = DateTime.Now;
            var m = from.ToString("yyyy-MM");
            var rows = people.Select(p =>
            {
                var days = Enumerable.Range(0, (to - from).Days + 1).Select(i => AttendanceEngine.Day(data, p.EmpCode, from.AddDays(i), now)).ToList();
                return new RegisterRow { EmpCode = p.EmpCode, Name = p.Name, Designation = p.Designation, Days = days.Select(d => d.Late && d.Status == "Present" ? "PresentLate" : d.Status).ToList(), Summary = AttendanceEngine.Summarise(m, days) };
            }).ToList();
            return Results.Ok(new RegisterDto(m, (to - from).Days + 1, rows));
        });

        app.MapGet("/api/admin/attendance/day", async (Db db, string emp, DateTime date) =>
        {
            if (!await InScope(db, emp)) return Results.Json(new { ok = false, error = "That employee is not in your list." }, statusCode: 404);
            await using var c = db.Open();
            var data = await AttendanceEngine.Load(c, [emp], date, date);
            var name = await c.ExecuteScalarAsync<string>("SELECT Empname FROM inter01.EmployeeMaster WHERE EmpCode=@emp", new { emp });
            return Results.Ok(new { Emp = emp, Name = name, Day = AttendanceEngine.Day(data, emp, date, DateTime.Now), Shift = AttendanceEngine.ShiftFor(data, emp, date), Punches = data.Punches[emp].Where(p => p.PunchTime.Date == date.Date).ToList() });
        });

        app.MapPost("/api/admin/attendance/punches", async (Db db, HttpContext ctx, ManualPunchInput x, bool dryRun = false) =>
        {
            if (!await InScope(db, x.Emp)) return Results.Json(new { ok = false, error = "That employee is not in your list." }, statusCode: 404);
            return await Writes.Run(db, dryRun, async (c, tx) =>
            {
                if (!Time(x.Time, out var t)) throw new ApiException("The time is not valid.");
                if (x.Direction is not ("IN" or "OUT")) throw new ApiException("Choose In or Out.");
                var when = x.Date.Date + t;
                if (when > DateTime.Now) throw new ApiException("A punch cannot be in the future.");
                await c.ExecuteAsync("INSERT hrms.AttPunch (EmpCode, PunchTime, Direction, Source, Note, CreatedBy) VALUES (@emp, @when, @dir, 'Manual', @note, @user)",
                    new { emp = x.Emp, when, dir = x.Direction, note = Nz(x.Note), user = Caller(ctx).UserId }, tx);
                return new { Emp = x.Emp, Time = when };
            });
        });

        app.MapDelete("/api/admin/attendance/punches/{id:long}", async (Db db, HttpContext ctx, long id, bool dryRun = false) =>
        {
            string? emp;
            await using (var rc = db.Open()) emp = await rc.ExecuteScalarAsync<string?>("SELECT EmpCode FROM hrms.AttPunch WHERE Id=@id AND Deleted=0", new { id });
            if (emp is null || !await InScope(db, emp)) return Results.Json(new { ok = false, error = "Punch not found." }, statusCode: 404);
            return await Writes.Run(db, dryRun, async (c, tx) =>
            {
                await c.ExecuteAsync("UPDATE hrms.AttPunch SET Deleted=1, DeletedBy=@user WHERE Id=@id", new { id, user = Caller(ctx).UserId }, tx);
                return new { Id = id };
            });
        });

        // ================= admin: correction requests =================
        app.MapGet("/api/admin/attendance/requests", async (Db db, string? status) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<RegularizationRow>(Sql.EmpCte + @"SELECT r.Id, r.EmpCode, e.Name, r.WorkDate, r.InTime, r.OutTime, r.Reason, r.Status, r.RequestedAt, r.DecidedBy, r.DecidedAt, r.DecisionNote
                FROM hrms.AttRegularization r JOIN emp e ON e.EmpCode = r.EmpCode
                WHERE (@status IS NULL OR r.Status = @status) AND r.RequestedAt >= DATEADD(day,-120,GETDATE()) ORDER BY CASE WHEN r.Status='Pending' THEN 0 ELSE 1 END, r.Id DESC",
                new { status = string.IsNullOrWhiteSpace(status) || status == "All" ? null : status })).ToList();
        });

        async Task<IResult> Decide(Db db, HttpContext ctx, int id, bool approve, DecisionNote x, bool dryRun)
        {
            RegularizationRow? req;
            await using (var rc = db.Open())
                req = await rc.QueryFirstOrDefaultAsync<RegularizationRow>(Sql.EmpCte + @"SELECT r.Id, r.EmpCode, e.Name, r.WorkDate, r.InTime, r.OutTime, r.Reason, r.Status, r.RequestedAt
                    FROM hrms.AttRegularization r JOIN emp e ON e.EmpCode = r.EmpCode WHERE r.Id=@id", new { id });
            if (req is null) return Results.Json(new { ok = false, error = "That request is not in your list." }, statusCode: 404);
            if (req.Status != "Pending") return Results.Json(new { ok = false, error = "That request has already been decided." }, statusCode: 409);
            if (string.Equals(req.EmpCode, Caller(ctx).EmployeeCode, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { ok = false, error = "You cannot decide your own correction." }, statusCode: 403);
            if (!approve && string.IsNullOrWhiteSpace(x.Note)) return Results.Json(new { ok = false, error = "Write the reason for rejecting." }, statusCode: 400);

            return await Writes.Run(db, dryRun, async (c, tx) =>
            {
                var user = Caller(ctx).UserId;
                if (approve)
                {
                    // Both times given: the requested day replaces what was recorded. One time given: it fills in the missing punch.
                    if (req.InTime is not null && req.OutTime is not null)
                        await c.ExecuteAsync("UPDATE hrms.AttPunch SET Deleted=1, DeletedBy=@user WHERE EmpCode=@emp AND Deleted=0 AND PunchTime >= @d AND PunchTime < @next", new { emp = req.EmpCode, d = req.WorkDate.Date, next = req.WorkDate.Date.AddDays(1), user }, tx);
                    if (req.InTime is not null)
                        await c.ExecuteAsync("INSERT hrms.AttPunch (EmpCode, PunchTime, Direction, Source, Note, CreatedBy) VALUES (@emp, @t, 'IN', 'Regularized', @note, @user)", new { emp = req.EmpCode, t = req.WorkDate.Date + req.InTime.Value, note = req.Reason, user }, tx);
                    if (req.OutTime is not null)
                        await c.ExecuteAsync("INSERT hrms.AttPunch (EmpCode, PunchTime, Direction, Source, Note, CreatedBy) VALUES (@emp, @t, 'OUT', 'Regularized', @note, @user)", new { emp = req.EmpCode, t = req.WorkDate.Date + req.OutTime.Value, note = req.Reason, user }, tx);
                }
                await c.ExecuteAsync("UPDATE hrms.AttRegularization SET Status=@s, DecidedBy=@user, DecidedAt=GETDATE(), DecisionNote=@note WHERE Id=@id",
                    new { id, s = approve ? "Approved" : "Rejected", user, note = Nz(x.Note) }, tx);
                await Notifier.Send(c, tx, req.EmpCode, "attendance", approve ? "Your attendance correction was approved" : "Your attendance correction was rejected",
                    $"{req.WorkDate:d MMM yyyy}" + (Nz(x.Note) is { } n ? ". " + n : ""), "my/attendance");
                return new { Id = id, Approved = approve };
            });
        }
        app.MapPost("/api/admin/attendance/requests/{id:int}/approve", (Db db, HttpContext ctx, int id, DecisionNote x, bool dryRun = false) => Decide(db, ctx, id, true, x, dryRun));
        app.MapPost("/api/admin/attendance/requests/{id:int}/reject", (Db db, HttpContext ctx, int id, DecisionNote x, bool dryRun = false) => Decide(db, ctx, id, false, x, dryRun));

        // ================= admin: shifts, assignments, holidays =================
        app.MapGet("/api/admin/attendance/setup/shifts", async (Db db) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<ShiftRow>("SELECT Id, Name, StartTime, EndTime, GraceMinutes, FullDayMinutes, HalfDayMinutes, WeeklyOffs, IsDefault, Active FROM hrms.AttShift ORDER BY IsDefault DESC, Name")).ToList();
        });

        static void CheckShift(ShiftInput x, out TimeSpan start, out TimeSpan end)
        {
            if (string.IsNullOrWhiteSpace(x.Name)) throw new ApiException("Shift name is required.");
            if (!Time(x.Start, out start) || !Time(x.End, out end)) throw new ApiException("Start and end time are required.");
            if (end <= start) throw new ApiException("The shift must end after it starts. Night shifts across midnight are not supported yet.");
            if (x.FullDayMinutes < 30 || x.HalfDayMinutes < 15 || x.HalfDayMinutes > x.FullDayMinutes) throw new ApiException("Full day and half day minutes are not valid.");
            if (x.GraceMinutes is < 0 or > 180) throw new ApiException("Grace minutes must be between 0 and 180.");
            var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            foreach (var d in (x.WeeklyOffs ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (!days.Contains(d, StringComparer.OrdinalIgnoreCase)) throw new ApiException("Weekly off days must be like Fri,Sat.");
        }

        app.MapPost("/api/admin/attendance/setup/shifts", async (Db db, ShiftInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            CheckShift(x, out var start, out var end);
            if (x.IsDefault) await c.ExecuteAsync("UPDATE hrms.AttShift SET IsDefault=0", transaction: tx);
            var id = await c.ExecuteScalarAsync<int>(@"INSERT hrms.AttShift (Name, StartTime, EndTime, GraceMinutes, FullDayMinutes, HalfDayMinutes, WeeklyOffs, IsDefault, Active)
                OUTPUT INSERTED.Id VALUES (@name, @start, @end, @g, @f, @h, @off, @def, @act)",
                new { name = x.Name.Trim(), start, end, g = x.GraceMinutes, f = x.FullDayMinutes, h = x.HalfDayMinutes, off = (x.WeeklyOffs ?? "").Trim(), def = x.IsDefault, act = x.Active }, tx);
            return new { Id = id };
        }));

        app.MapPut("/api/admin/attendance/setup/shifts/{id:int}", async (Db db, int id, ShiftInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            CheckShift(x, out var start, out var end);
            if (!x.Active && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttShift WHERE Active=1 AND Id<>@id", new { id }, tx) == 0) throw new ApiException("Keep at least one active shift.", 409);
            if (x.IsDefault) await c.ExecuteAsync("UPDATE hrms.AttShift SET IsDefault=0", transaction: tx);
            if (await c.ExecuteAsync(@"UPDATE hrms.AttShift SET Name=@name, StartTime=@start, EndTime=@end, GraceMinutes=@g, FullDayMinutes=@f, HalfDayMinutes=@h, WeeklyOffs=@off, IsDefault=@def, Active=@act WHERE Id=@id",
                new { id, name = x.Name.Trim(), start, end, g = x.GraceMinutes, f = x.FullDayMinutes, h = x.HalfDayMinutes, off = (x.WeeklyOffs ?? "").Trim(), def = x.IsDefault, act = x.Active }, tx) == 0)
                throw new ApiException("Shift not found.", 404);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttShift WHERE IsDefault=1", transaction: tx) == 0)
                await c.ExecuteAsync("UPDATE TOP (1) hrms.AttShift SET IsDefault=1 WHERE Active=1", transaction: tx);
            return new { Id = id };
        }));

        app.MapGet("/api/admin/attendance/setup/employees", async (Db db) =>
        {
            await using var c = db.Open();
            var people = (await c.QueryAsync<Person>(Sql.EmpCte + "SELECT EmpCode, Name, Designation, Doj FROM emp WHERE Status='active' ORDER BY Name")).ToList();
            var data = await AttendanceEngine.Load(c, people.Select(p => p.EmpCode).ToList(), DateTime.Today, DateTime.Today);
            return people.Select(p => new EmpShiftRow(p.EmpCode, p.Name, p.Designation, AttendanceEngine.ShiftFor(data, p.EmpCode, DateTime.Today).Name)).ToList();
        });

        app.MapPost("/api/admin/attendance/setup/assign", async (Db db, AssignInput x, bool dryRun = false) =>
        {
            List<string> allowed;
            await using (var rc = db.Open()) allowed = (await rc.QueryAsync<string>(Sql.EmpCte + "SELECT EmpCode FROM emp")).ToList();
            var emps = x.Emps.Where(e => allowed.Contains(e, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
            if (emps.Count == 0) return Results.Json(new { ok = false, error = "Choose at least one employee from your list." }, statusCode: 400);
            return await Writes.Run(db, dryRun, async (c, tx) =>
            {
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttShift WHERE Id=@id AND Active=1", new { id = x.ShiftId }, tx) == 0) throw new ApiException("Choose an active shift.");
                foreach (var e in emps)
                {
                    await c.ExecuteAsync("DELETE hrms.AttShiftAssign WHERE EmpCode=@e AND FromDate=@d", new { e, d = x.FromDate.Date }, tx);
                    await c.ExecuteAsync("INSERT hrms.AttShiftAssign (EmpCode, ShiftId, FromDate) VALUES (@e, @s, @d)", new { e, s = x.ShiftId, d = x.FromDate.Date }, tx);
                }
                return new { Assigned = emps.Count };
            });
        });

        app.MapGet("/api/admin/attendance/setup/holidays", async (Db db, int? year) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<HolidayRow>("SELECT Id, HolidayDate, Name, Kind FROM hrms.AttHoliday WHERE YEAR(HolidayDate) = @year ORDER BY HolidayDate", new { year = year ?? DateTime.Today.Year })).ToList();
        });

        app.MapPost("/api/admin/attendance/setup/holidays", async (Db db, HolidayInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (string.IsNullOrWhiteSpace(x.Name)) throw new ApiException("Holiday name is required.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttHoliday WHERE HolidayDate=@d", new { d = x.Date.Date }, tx) > 0) throw new ApiException("There is already a holiday on that date.", 409);
            var id = await c.ExecuteScalarAsync<int>("INSERT hrms.AttHoliday (HolidayDate, Name, Kind) OUTPUT INSERTED.Id VALUES (@d, @name, @kind)", new { d = x.Date.Date, name = x.Name.Trim(), kind = Nz(x.Kind) ?? "Public" }, tx);
            return new { Id = id };
        }));

        app.MapPut("/api/admin/attendance/setup/holidays/{id:int}", async (Db db, int id, HolidayInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (string.IsNullOrWhiteSpace(x.Name)) throw new ApiException("Holiday name is required.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttHoliday WHERE HolidayDate=@d AND Id<>@id", new { d = x.Date.Date, id }, tx) > 0) throw new ApiException("There is already a holiday on that date.", 409);
            if (await c.ExecuteAsync("UPDATE hrms.AttHoliday SET HolidayDate=@d, Name=@name, Kind=@kind WHERE Id=@id", new { id, d = x.Date.Date, name = x.Name.Trim(), kind = Nz(x.Kind) ?? "Public" }, tx) == 0) throw new ApiException("Holiday not found.", 404);
            return new { Id = id };
        }));

        app.MapDelete("/api/admin/attendance/setup/holidays/{id:int}", async (Db db, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (await c.ExecuteAsync("DELETE hrms.AttHoliday WHERE Id=@id", new { id }, tx) == 0) throw new ApiException("Holiday not found.", 404);
            return new { Id = id };
        }));
    }
}
