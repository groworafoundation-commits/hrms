using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Xunit;
using static Hrms.Tests.H;

namespace Hrms.Tests;

static class H
{
    public static string D(DateTime d) => d.ToString("yyyy-MM-dd");
    public static List<string> Directions(JsonElement punches) => punches.EnumerateArray().Select(p => p.GetProperty("direction").GetString()!).ToList();
}

[Collection("api")]
public class AttendanceTests(ApiFixture f)
{
    [Fact]
    public async Task Punching_toggles_in_and_out_and_a_double_touch_is_refused()
    {
        await f.ClearAttendance();
        var emp = await f.LoginAs(f.Employee);
        var before = (await emp.Get("/api/me/attendance/today")).Body;
        Assert.False(before.GetProperty("isIn").GetBoolean());
        Assert.Equal("General shift", before.GetProperty("shift").GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await emp.Post("/api/me/attendance/punch", new { workMode = "Moon" })).Status);   // only Office, Remote or Site
        var first = await emp.Post("/api/me/attendance/punch", new { workMode = "Remote", note = "test" });
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("IN", first.Body.GetProperty("result").GetProperty("direction").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await emp.Post("/api/me/attendance/punch", new { })).Status);                       // 30 second guard

        var during = (await emp.Get("/api/me/attendance/today")).Body;
        Assert.True(during.GetProperty("isIn").GetBoolean());
        Assert.Equal("Working", during.GetProperty("day").GetProperty("status").GetString());

        await using (var c = f.Open())
            await c.ExecuteAsync("UPDATE hrms.AttPunch SET PunchTime = DATEADD(minute, -5, PunchTime) WHERE EmpCode = @e AND Deleted = 0", new { e = f.Employee });
        var second = await emp.Post("/api/me/attendance/punch", new { });
        Assert.Equal("OUT", second.Body.GetProperty("result").GetProperty("direction").GetString());

        var after = (await emp.Get("/api/me/attendance/today")).Body;
        Assert.False(after.GetProperty("isIn").GetBoolean());
        Assert.Equal(["IN", "OUT"], Directions(after.GetProperty("punches")));
        Assert.True(after.GetProperty("day").GetProperty("workedSeconds").GetInt32() >= 290);
    }

    [Fact]
    public async Task A_missed_punch_is_fixed_by_a_correction_a_manager_approves()
    {
        await f.ClearAttendance();
        var emp = await f.LoginAs(f.Employee); var mgr = await f.LoginAs(ApiFixture.Manager);
        var day = DateTime.Today.AddDays(-3);

        Assert.Equal(HttpStatusCode.BadRequest, (await emp.Post("/api/me/attendance/requests", new { date = DateTime.Today.AddDays(2), inTime = "09:00", outTime = "18:00", reason = "x" })).Status);   // not the future
        Assert.Equal(HttpStatusCode.BadRequest, (await emp.Post("/api/me/attendance/requests", new { date = day, inTime = "09:00", outTime = "18:00", reason = "" })).Status);                 // a reason is needed
        Assert.Equal(HttpStatusCode.BadRequest, (await emp.Post("/api/me/attendance/requests", new { date = day, inTime = "18:00", outTime = "09:00", reason = "x" })).Status);               // out after in
        Assert.Equal(HttpStatusCode.BadRequest, (await emp.Post("/api/me/attendance/requests", new { date = DateTime.Today.AddDays(-60), inTime = "09:00", outTime = "18:00", reason = "x" })).Status);   // too old
        Assert.Equal(HttpStatusCode.OK, (await emp.Post("/api/me/attendance/requests", new { date = day, inTime = "09:00", outTime = "18:00", reason = "Forgot to punch" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await emp.Post("/api/me/attendance/requests", new { date = day, inTime = "09:00", outTime = "18:00", reason = "again" })).Status);   // one at a time

        var pending = (await mgr.Get("/api/admin/attendance/requests?status=Pending")).Body.EnumerateArray().First(r => r.GetProperty("empCode").GetString() == f.Employee);
        var id = pending.GetProperty("id").GetInt32();
        Assert.Contains((await mgr.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray(), n => n.GetProperty("title").GetString() == "An attendance correction needs review");

        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Post($"/api/admin/attendance/requests/{id}/approve", new { })).Status);   // employees cannot decide
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post($"/api/admin/attendance/requests/{id}/reject", new { })).Status);   // a reason is needed to reject
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/admin/attendance/requests/{id}/approve", new { note = "ok" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Post($"/api/admin/attendance/requests/{id}/approve", new { })).Status);    // already decided

        var fixedDay = (await emp.Get($"/api/me/attendance/day?date={D(day)}")).Body;
        Assert.Equal("Present", fixedDay.GetProperty("day").GetProperty("status").GetString());
        Assert.Equal(540, fixedDay.GetProperty("day").GetProperty("workedMinutes").GetInt32());
        Assert.Equal(["IN", "OUT"], Directions(fixedDay.GetProperty("punches")));
        Assert.Contains((await emp.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray(), n => n.GetProperty("title").GetString() == "Your attendance correction was approved");
    }

    [Fact]
    public async Task A_correction_with_both_times_replaces_what_was_recorded_and_a_rejection_changes_nothing()
    {
        await f.ClearAttendance();
        var emp = await f.LoginAs(f.Employee); var mgr = await f.LoginAs(ApiFixture.Manager);
        var day = DateTime.Today.AddDays(-5);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post("/api/admin/attendance/punches", new { emp = f.Employee, date = day, time = "10:00", direction = "IN", note = "stray" })).Status);

        await emp.Post("/api/me/attendance/requests", new { date = day, inTime = "09:00", outTime = "17:30", reason = "Wrong time recorded" });
        var id = (await mgr.Get("/api/admin/attendance/requests?status=Pending")).Body.EnumerateArray().First(r => r.GetProperty("empCode").GetString() == f.Employee).GetProperty("id").GetInt32();
        await mgr.Post($"/api/admin/attendance/requests/{id}/approve", new { });
        var punches = (await emp.Get($"/api/me/attendance/day?date={D(day)}")).Body.GetProperty("punches");
        Assert.Equal(2, punches.GetArrayLength());                                                           // the stray punch was replaced, not added to
        Assert.Equal("09:00", punches[0].GetProperty("punchTime").GetDateTime().ToString("HH:mm"));

        var other = DateTime.Today.AddDays(-6);
        await emp.Post("/api/me/attendance/requests", new { date = other, inTime = "09:00", outTime = "18:00", reason = "Was at a client" });
        var id2 = (await mgr.Get("/api/admin/attendance/requests?status=Pending")).Body.EnumerateArray().First(r => r.GetProperty("empCode").GetString() == f.Employee).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/admin/attendance/requests/{id2}/reject", new { note = "No client visit logged" })).Status);
        Assert.Equal(0, (await emp.Get($"/api/me/attendance/day?date={D(other)}")).Body.GetProperty("punches").GetArrayLength());
        var rejected = (await emp.Get("/api/me/attendance/requests")).Body.EnumerateArray().First(r => r.GetProperty("id").GetInt32() == id2);
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("No client visit logged", rejected.GetProperty("decisionNote").GetString());
    }

    [Fact]
    public async Task Managers_can_add_and_remove_punches_but_not_in_the_future()
    {
        await f.ClearAttendance();
        var mgr = await f.LoginAs(ApiFixture.Manager); var emp = await f.LoginAs(f.Employee);
        var day = DateTime.Today.AddDays(-2);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post("/api/admin/attendance/punches", new { emp = f.Employee, date = DateTime.Today.AddDays(1), time = "09:00", direction = "IN" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post("/api/admin/attendance/punches", new { emp = f.Employee, date = day, time = "09:00", direction = "SIDEWAYS" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post("/api/admin/attendance/punches", new { emp = f.Employee, date = day, time = "25:99", direction = "IN" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post("/api/admin/attendance/punches", new { emp = f.Employee, date = day, time = "09:00", direction = "IN" })).Status);
        var view = (await mgr.Get($"/api/admin/attendance/day?emp={f.Employee}&date={D(day)}")).Body;
        var punchId = view.GetProperty("punches")[0].GetProperty("id").GetInt64();
        Assert.Equal("Manual", view.GetProperty("punches")[0].GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.OK, (await mgr.Delete($"/api/admin/attendance/punches/{punchId}")).Status);
        Assert.Equal(0, (await emp.Get($"/api/me/attendance/day?date={D(day)}")).Body.GetProperty("punches").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Delete($"/api/admin/attendance/punches/{punchId}")).Status);       // already gone
    }

    [Fact]
    public async Task The_board_and_register_list_people_and_only_managers_may_open_them()
    {
        await f.ClearAttendance();
        var mgr = await f.LoginAs(ApiFixture.Manager); var emp = await f.LoginAs(f.Employee);
        await emp.Post("/api/me/attendance/punch", new { workMode = "Office" });
        var board = (await mgr.Get("/api/admin/attendance/today")).Body.GetProperty("rows");
        var mine = board.EnumerateArray().First(r => r.GetProperty("empCode").GetString() == f.Employee);
        Assert.Equal(1, mine.GetProperty("day").GetProperty("punches").GetInt32());
        Assert.Equal("Working", mine.GetProperty("day").GetProperty("status").GetString());

        var register = (await mgr.Get($"/api/admin/attendance/register?month={DateTime.Today:yyyy-MM}")).Body;
        var row = register.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("empCode").GetString() == f.Employee);
        Assert.Equal(register.GetProperty("daysInMonth").GetInt32(), row.GetProperty("days").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/admin/attendance/today")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get($"/api/admin/attendance/register?month={DateTime.Today:yyyy-MM}")).Status);
    }

    [Fact]
    public async Task Holidays_shifts_and_weekly_offs_change_how_a_day_is_judged()
    {
        await f.ClearAttendance();
        var mgr = await f.LoginAs(ApiFixture.Manager); var emp = await f.LoginAs(f.Employee);
        var holiday = DateTime.Today.AddDays(-4);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post("/api/admin/attendance/setup/holidays", new { date = holiday, name = "TST holiday", kind = "Public" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Post("/api/admin/attendance/setup/holidays", new { date = holiday, name = "TST again" })).Status);
        var day = (await emp.Get($"/api/me/attendance/day?date={D(holiday)}")).Body.GetProperty("day");
        Assert.Equal("Holiday", day.GetProperty("status").GetString());
        Assert.Equal("TST holiday", day.GetProperty("holiday").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post("/api/admin/attendance/setup/shifts", new { name = "TST bad", start = "18:00", end = "09:00", graceMinutes = 5, fullDayMinutes = 480, halfDayMinutes = 240, weeklyOffs = "Sun", isDefault = false, active = true })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post("/api/admin/attendance/setup/shifts", new { name = "TST bad", start = "07:00", end = "16:00", graceMinutes = 5, fullDayMinutes = 200, halfDayMinutes = 300, weeklyOffs = "Sun", isDefault = false, active = true })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post("/api/admin/attendance/setup/shifts", new { name = "TST bad", start = "07:00", end = "16:00", graceMinutes = 5, fullDayMinutes = 480, halfDayMinutes = 240, weeklyOffs = "Funday", isDefault = false, active = true })).Status);
        var made = await mgr.Post("/api/admin/attendance/setup/shifts", new { name = "TST early", start = "07:00", end = "16:00", graceMinutes = 5, fullDayMinutes = 480, halfDayMinutes = 240, weeklyOffs = "Mon", isDefault = false, active = true });
        Assert.Equal(HttpStatusCode.OK, made.Status);
        var shiftId = made.Body.GetProperty("result").GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Post("/api/admin/attendance/setup/assign", new { emps = new[] { f.Employee }, shiftId, fromDate = DateTime.Today.AddDays(-30) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post("/api/admin/attendance/setup/assign", new { emps = new[] { f.Employee }, shiftId, fromDate = DateTime.Today.AddDays(-30) })).Status);
        Assert.Equal("TST early", (await emp.Get("/api/me/attendance/today")).Body.GetProperty("shift").GetProperty("name").GetString());

        var monday = DateTime.Today.AddDays(-8); while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(-1);
        var off = (await emp.Get($"/api/me/attendance/day?date={D(monday)}")).Body.GetProperty("day");
        Assert.Equal("Off", off.GetProperty("status").GetString());                                          // Monday is this shift's weekly off
    }
}

[Collection("api")]
public class MachineTests(ApiFixture f)
{
    const string Serial = "TST-DEV1";

    Task<HttpResponseMessage> Machine(HttpClient http, params string[] lines) =>
        http.PostAsync($"/iclock/cdata?SN={Serial}&table=ATTLOG&Stamp=1", new StringContent(string.Join("\r\n", lines), Encoding.UTF8, "text/plain"));
    static string Line(string pin, DateTime when) => $"{pin}\t{when:yyyy-MM-dd HH:mm:ss}\t0\t1";

    async Task<int> Approve(Session mgr, bool active = true)
    {
        var id = (await mgr.Get("/api/admin/attendance/devices")).Body.EnumerateArray().First(d => d.GetProperty("serial").GetString() == Serial).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await mgr.Put($"/api/admin/attendance/devices/{id}", new { serial = Serial, name = "TST gate", location = "Test", active })).Status);
        return id;
    }

    [Fact]
    public async Task A_new_machine_must_be_approved_before_its_punches_are_accepted()
    {
        await f.ClearAttendance();
        var http = f.Anonymous(); var mgr = await f.LoginAs(ApiFixture.Manager);
        var hello = await http.GetAsync($"/iclock/cdata?SN={Serial}&options=all&pushver=2.4.1");
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        Assert.StartsWith("GET OPTION FROM: " + Serial, await hello.Content.ReadAsStringAsync());
        var listed = (await mgr.Get("/api/admin/attendance/devices")).Body.EnumerateArray().First(d => d.GetProperty("serial").GetString() == Serial);
        Assert.False(listed.GetProperty("active").GetBoolean());                                             // waiting for approval

        Assert.Equal(HttpStatusCode.Forbidden, (await Machine(http, Line(f.Employee, DateTime.Today.AddDays(-1).AddHours(9)))).StatusCode);   // refused, so the machine keeps and retries
        await Approve(mgr);
        var ok = await Machine(http, Line(f.Employee, DateTime.Today.AddDays(-1).AddHours(9)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("OK:1", (await ok.Content.ReadAsStringAsync()).Trim());

        await Approve(mgr, active: false);                                                                   // switching it off stops it again
        Assert.Equal(HttpStatusCode.Forbidden, (await Machine(http, Line(f.Employee, DateTime.Today.AddDays(-1).AddHours(10)))).StatusCode);
    }

    [Fact]
    public async Task Punches_are_stored_once_in_time_order_and_flip_between_in_and_out()
    {
        await f.ClearAttendance();
        var http = f.Anonymous(); var mgr = await f.LoginAs(ApiFixture.Manager); var emp = await f.LoginAs(f.Employee);
        await http.GetAsync($"/iclock/cdata?SN={Serial}"); await Approve(mgr);
        var day = DateTime.Today.AddDays(-1);

        // a backlog sent backwards, with one punch repeated
        var res = await Machine(http, Line(f.Employee, day.AddHours(17.5)), Line(f.Employee, day.AddHours(9.1)), Line(f.Employee, day.AddHours(9.1)));
        Assert.Equal("OK:3", (await res.Content.ReadAsStringAsync()).Trim());
        var view = (await emp.Get($"/api/me/attendance/day?date={D(day)}")).Body;
        Assert.Equal(["IN", "OUT"], Directions(view.GetProperty("punches")));
        Assert.All(view.GetProperty("punches").EnumerateArray(), p => Assert.Equal("Machine", p.GetProperty("source").GetString()));
        await using var c = f.Open();
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttDeviceLog WHERE DeviceSerial = @Serial", new { Serial }));      // the repeat was not stored twice

        // a second touch seconds later is a double touch, not a new punch
        var now = DateTime.Today.AddHours(8);
        await Machine(http, Line(f.Employee, now)); await Machine(http, Line(f.Employee, now.AddSeconds(20)));
        var results = (await c.QueryAsync<string>("SELECT Result FROM hrms.AttDeviceLog WHERE DeviceSerial = @Serial AND PunchTime >= @now ORDER BY PunchTime", new { Serial, now })).ToList();
        Assert.Equal(["Imported", "Ignored"], results);
    }

    [Fact]
    public async Task An_unknown_machine_number_waits_until_it_is_linked_and_then_replays()
    {
        await f.ClearAttendance();
        var http = f.Anonymous(); var mgr = await f.LoginAs(ApiFixture.Manager); var emp = await f.LoginAs(f.Employee);
        await http.GetAsync($"/iclock/cdata?SN={Serial}"); await Approve(mgr);
        var day = DateTime.Today.AddDays(-2);

        await Machine(http, Line("TST-77", day.AddHours(9)));
        var maps = (await mgr.Get("/api/admin/attendance/devices/mappings")).Body;
        Assert.Contains(maps.GetProperty("unmapped").EnumerateArray(), u => u.GetProperty("pin").GetString() == "TST-77");
        Assert.Equal(0, (await emp.Get($"/api/me/attendance/day?date={D(day)}")).Body.GetProperty("punches").GetArrayLength());     // kept, but no one's punch yet

        Assert.Equal(HttpStatusCode.OK, (await mgr.Post("/api/admin/attendance/devices/mappings", new { pin = "TST-77", empCode = f.Employee })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Post("/api/admin/attendance/devices/mappings", new { pin = "TST-77", empCode = f.Employee })).Status);   // one number, one person
        var replay = (await mgr.Post("/api/admin/attendance/devices/reprocess")).Body.GetProperty("result");
        Assert.True(replay.GetProperty("matched").GetInt32() >= 1);
        Assert.Equal(1, (await emp.Get($"/api/me/attendance/day?date={D(day)}")).Body.GetProperty("punches").GetArrayLength());

        var log = (await mgr.Get("/api/admin/attendance/devices/log?result=All&limit=20")).Body.EnumerateArray().First(l => l.GetProperty("pin").GetString() == "TST-77");
        Assert.Equal("Imported", log.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Employees_cannot_see_or_manage_machines()
    {
        var emp = await f.LoginAs(f.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/admin/attendance/devices")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Post("/api/admin/attendance/devices/mappings", new { pin = "TST-1", empCode = f.Employee })).Status);
    }
}

[Collection("api")]
public class LeaveBalanceTests(ApiFixture f)
{
    [Fact]
    public async Task Balances_agree_with_the_payroll_ledger_the_erp_keeps()
    {
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var rows = (await mgr.Get("/api/leave/balances")).Body.EnumerateArray().Where(r => r.GetProperty("hasPayroll").GetBoolean()).Take(25).ToList();
        Assert.NotEmpty(rows);
        await using var c = f.Open();
        foreach (var r in rows)
        {
            var code = r.GetProperty("empCode").GetString();
            var ledger = await c.ExecuteScalarAsync<decimal>("SELECT TOP 1 ISNULL([CTMAnnual Leave],0) FROM inter01.EmployeePaymentProcess WHERE Empcode = @code ORDER BY ProcessedDate DESC", new { code });
            Assert.Equal(Math.Round(ledger, 2, MidpointRounding.AwayFromZero), r.GetProperty("balance").GetDecimal());   // the ERP's running balance (shown to 2 decimals)
            Assert.Equal(r.GetProperty("balance").GetDecimal() - r.GetProperty("unprocessed").GetDecimal() - r.GetProperty("pending").GetDecimal(), r.GetProperty("available").GetDecimal());
            if (r.GetProperty("asOfMonth").GetString()!.StartsWith(DateTime.Today.Year.ToString()))
                Assert.InRange(r.GetProperty("balance").GetDecimal() - (r.GetProperty("broughtForward").GetDecimal() + r.GetProperty("earned").GetDecimal() - r.GetProperty("used").GetDecimal()), -0.02m, 0.02m);   // brought forward + earned - used
        }
    }

    [Fact]
    public async Task An_employee_sees_only_their_own_balance_and_a_pending_request_counts_against_it()
    {
        var emp = await f.LoginAs(f.Employee);
        var before = (await emp.Get("/api/me/leave/balance")).Body;
        Assert.Equal(f.Employee, before.GetProperty("empCode").GetString());
        var from = DateTime.Today.AddDays(1100);
        Assert.Equal(HttpStatusCode.OK, (await emp.Post("/api/me/leave", new { leaveType = "ALVESAL", fromDate = from, toDate = from.AddDays(2), salaryWithoutLeave = false, pushTo = ApiFixture.Manager })).Status);
        var after = (await emp.Get("/api/me/leave/balance")).Body;
        Assert.Equal(before.GetProperty("pending").GetDecimal() + 3, after.GetProperty("pending").GetDecimal());
        Assert.Equal(before.GetProperty("available").GetDecimal() - 3, after.GetProperty("available").GetDecimal());
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/leave/balances")).Status);                        // the whole list is for people with the leave right
    }
}
