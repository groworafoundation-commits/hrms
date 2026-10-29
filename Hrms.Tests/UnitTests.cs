using Hrms.Api;
using Xunit;

namespace Hrms.Tests;

public class PasswordTests
{
    [Fact]
    public void Hash_then_verify_round_trips_and_is_salted()
    {
        var a = Passwords.Hash("Secret123");
        var b = Passwords.Hash("Secret123");
        Assert.NotEqual(a, b);                              // a fresh salt each time
        Assert.StartsWith("PBKDF2$SHA256$", a);
        Assert.True(Passwords.Verify("Secret123", a));
        Assert.False(Passwords.Verify("secret123", a));
        Assert.False(Passwords.Verify("Secret123", "garbage"));
    }

    [Theory]
    [InlineData("short1", false)]
    [InlineData("alllettersonly", false)]
    [InlineData("12345678901", false)]
    [InlineData("Good1234", true)]
    [InlineData("a-longer-passphrase-9", true)]
    public void Policy_needs_eight_characters_a_letter_and_a_number(string password, bool allowed) =>
        Assert.Equal(allowed, Passwords.Policy(password) is null);

    [Fact]
    public void Policy_rejects_empty_and_very_long()
    {
        Assert.NotNull(Passwords.Policy(""));
        Assert.NotNull(Passwords.Policy(null));
        Assert.NotNull(Passwords.Policy(new string('a', 70) + "1"));
    }

    [Fact]
    public void VerifyAny_uses_the_hash_when_present_and_the_legacy_value_only_before_migration()
    {
        var hash = Passwords.Hash("Real1234");
        Assert.True(Passwords.VerifyAny("Real1234", hash, "ignored-old-plain", true));
        Assert.False(Passwords.VerifyAny("ignored-old-plain", hash, "ignored-old-plain", true));   // once hashed, the old plain value is dead
        Assert.True(Passwords.VerifyAny("old-plain", null, "old-plain", true));                      // not migrated yet
        Assert.False(Passwords.VerifyAny("anything", null, null, true));
        Assert.False(Passwords.VerifyAny("anything", hash, null, false));                           // unknown user
    }
}

public class AttendanceEngineTests
{
    static readonly DateTime Mon = new(2026, 10, 26);                  // a Monday
    static ShiftRow Shift() => new() { Id = 1, Name = "General", StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(18, 0, 0), GraceMinutes = 10, FullDayMinutes = 480, HalfDayMinutes = 240, WeeklyOffs = "Sun", IsDefault = true, Active = true };
    static PunchRow P(DateTime day, string time, string dir) => new() { EmpCode = "E1", PunchTime = day.Date + TimeSpan.Parse(time), Direction = dir, Source = "App" };
    static readonly DateTime LaterThatWeek = new(2026, 10, 30, 12, 0, 0);

    static DayStatus Day(DateTime day, List<PunchRow> punches, DateTime now, string? holiday = null, string? leave = null, DateTime? start = null) =>
        AttendanceEngine.Compute(day, Shift(), punches, holiday, leave, now, start ?? new DateTime(2026, 1, 1));

    [Fact]
    public void Full_day_on_time_is_present_and_not_late()
    {
        var d = Day(Mon, [P(Mon, "08:55", "IN"), P(Mon, "18:05", "OUT")], LaterThatWeek);
        Assert.Equal("Present", d.Status);
        Assert.False(d.Late);
        Assert.False(d.EarlyOut);
        Assert.Equal(550, d.WorkedMinutes);
    }

    [Fact]
    public void Arriving_after_the_grace_period_is_late_by_the_minutes_after_shift_start()
    {
        var d = Day(Mon, [P(Mon, "09:25", "IN"), P(Mon, "18:30", "OUT")], LaterThatWeek);
        Assert.Equal("Present", d.Status);
        Assert.True(d.Late);
        Assert.Equal(25, d.LateMinutes);
        Assert.False(Day(Mon, [P(Mon, "09:10", "IN"), P(Mon, "18:30", "OUT")], LaterThatWeek).Late);      // inside the 10 minute grace
    }

    [Theory]
    [InlineData("09:00", "13:30", "HalfDay")]      // 4h30
    [InlineData("09:00", "11:00", "Short")]        // 2h
    [InlineData("09:00", "17:00", "Present")]      // exactly 8h
    public void Worked_hours_decide_full_half_or_short(string inT, string outT, string expected) =>
        Assert.Equal(expected, Day(Mon, [P(Mon, inT, "IN"), P(Mon, outT, "OUT")], LaterThatWeek).Status);

    [Fact]
    public void Leaving_early_without_enough_hours_is_flagged_early_out()
    {
        var d = Day(Mon, [P(Mon, "09:00", "IN"), P(Mon, "14:00", "OUT")], LaterThatWeek);
        Assert.Equal("HalfDay", d.Status);
        Assert.True(d.EarlyOut);
    }

    [Fact]
    public void Several_in_out_pairs_add_up()
    {
        var d = Day(Mon, [P(Mon, "09:00", "IN"), P(Mon, "13:00", "OUT"), P(Mon, "14:00", "IN"), P(Mon, "18:00", "OUT")], LaterThatWeek);
        Assert.Equal(480, d.WorkedMinutes);
        Assert.Equal("Present", d.Status);
    }

    [Fact]
    public void Open_in_on_a_past_day_is_a_missing_punch_out_and_counts_no_hours()
    {
        var d = Day(Mon, [P(Mon, "09:00", "IN")], LaterThatWeek);
        Assert.Equal("Missing", d.Status);
        Assert.True(d.OpenIn);
        Assert.Equal(0, d.WorkedMinutes);
    }

    [Fact]
    public void Open_in_today_counts_live_time_and_is_working()
    {
        var now = Mon.AddHours(12);
        var d = Day(Mon, [P(Mon, "09:00", "IN")], now);
        Assert.Equal("Working", d.Status);
        Assert.Equal(180, d.WorkedMinutes);
    }

    [Fact]
    public void Punched_out_for_a_break_before_shift_end_is_away_not_absent()
    {
        var d = Day(Mon, [P(Mon, "09:00", "IN"), P(Mon, "12:00", "OUT")], Mon.AddHours(13));
        Assert.Equal("Away", d.Status);
    }

    [Fact]
    public void No_punches_is_absent_on_a_past_working_day_and_not_in_yet_today()
    {
        Assert.Equal("Absent", Day(Mon, [], LaterThatWeek).Status);
        Assert.Equal("Pending", Day(Mon, [], Mon.AddHours(10)).Status);
        Assert.Equal("Absent", Day(Mon, [], Mon.AddHours(19)).Status);                  // the shift is over
        Assert.Equal("Future", Day(LaterThatWeek.AddDays(3), [], LaterThatWeek).Status);
    }

    [Fact]
    public void Weekly_off_holiday_and_approved_leave_are_not_absences()
    {
        var sunday = new DateTime(2026, 10, 25);
        Assert.Equal("Off", Day(sunday, [], LaterThatWeek).Status);
        Assert.Equal("Holiday", Day(Mon, [], LaterThatWeek, holiday: "National Day").Status);
        Assert.Equal("Holiday", Day(sunday, [], LaterThatWeek, holiday: "National Day").Status);       // a holiday wins over the weekly off
        Assert.Equal("Leave", Day(Mon, [], LaterThatWeek, leave: "Annual leave").Status);
    }

    [Fact]
    public void Working_on_a_day_off_counts_and_is_flagged()
    {
        var sunday = new DateTime(2026, 10, 25);
        var d = Day(sunday, [P(sunday, "10:00", "IN"), P(sunday, "18:00", "OUT")], LaterThatWeek);
        Assert.Equal("Present", d.Status);
        Assert.True(d.OffDayWork);
        Assert.False(d.Late);                           // no lateness on a day off
    }

    [Fact]
    public void Days_before_attendance_went_live_have_no_data_instead_of_absent()
    {
        var d = Day(Mon, [], LaterThatWeek, start: new DateTime(2026, 10, 29));
        Assert.Equal("NoData", d.Status);
        Assert.Equal("Off", Day(new DateTime(2026, 10, 25), [], LaterThatWeek, start: new DateTime(2026, 10, 29)).Status);   // off days still show as off
    }

    [Fact]
    public void Month_summary_counts_the_statuses()
    {
        var days = new List<DayStatus>
        {
            Day(Mon, [P(Mon, "09:00", "IN"), P(Mon, "18:00", "OUT")], LaterThatWeek),
            Day(Mon.AddDays(1), [P(Mon.AddDays(1), "09:30", "IN"), P(Mon.AddDays(1), "18:00", "OUT")], LaterThatWeek),
            Day(Mon.AddDays(2), [P(Mon.AddDays(2), "09:00", "IN"), P(Mon.AddDays(2), "13:30", "OUT")], LaterThatWeek),
            Day(Mon.AddDays(3), [], LaterThatWeek),
        };
        var s = AttendanceEngine.Summarise("2026-10", days);
        Assert.Equal(2.5, s.PresentDays);
        Assert.Equal(1, s.HalfDays);
        Assert.Equal(1, s.Absent);
        Assert.Equal(1, s.Late);
        Assert.Equal(4, s.WorkingDays);
    }
}
