using System.Net.Http.Json;
using System.Text.Json;

namespace HrmsApp.Services;

public class ShiftDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int GraceMinutes { get; set; }
    public int FullDayMinutes { get; set; }
    public int HalfDayMinutes { get; set; }
    public string WeeklyOffs { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Active { get; set; }
}

public class PunchDto
{
    public long Id { get; set; }
    public string EmpCode { get; set; } = "";
    public DateTime PunchTime { get; set; }
    public string Direction { get; set; } = "";
    public string Source { get; set; } = "";
    public string? WorkMode { get; set; }
    public string? Note { get; set; }
}

public class DayDto
{
    public DateTime Date { get; set; }
    public string Status { get; set; } = "";
    public DateTime? FirstIn { get; set; }
    public DateTime? LastOut { get; set; }
    public int WorkedSeconds { get; set; }
    public int WorkedMinutes { get; set; }
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

public record TodayInfo(DateTime Now, ShiftDto Shift, DayDto Day, List<PunchDto> Punches, bool IsIn, DateTime? InSince, int RemainingMinutes);
public record MonthSum(string Month, double PresentDays, int HalfDays, int Absent, int Leave, int Holidays, int Off, int Late, int Missing, int WorkedMinutes, int AvgMinutes, int WorkingDays);
public record MonthInfo(string Month, List<DayDto> Days, MonthSum Summary, ShiftDto Shift);
public record DayDetail(DayDto Day, List<PunchDto> Punches);
public record AdminDayDetail(string Emp, string Name, DayDto Day, ShiftDto Shift, List<PunchDto> Punches);

public class FixRequest
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

public class BoardRowDto
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Designation { get; set; }
    public string Shift { get; set; } = "";
    public DayDto Day { get; set; } = new();
}
public record BoardInfo(DateTime Date, DateTime Now, List<BoardRowDto> Rows);

public class RegisterRowDto
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Designation { get; set; }
    public List<string> Days { get; set; } = [];
    public MonthSum Summary { get; set; } = default!;
}
public record RegisterInfo(string Month, int DaysInMonth, List<RegisterRowDto> Rows);
public record HolidayDto(int Id, DateTime HolidayDate, string Name, string Kind);
public record EmpShift(string EmpCode, string Name, string? Designation, string Shift);

public class MachineInfo
{
    public int Id { get; set; }
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public bool Active { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? LastIp { get; set; }
    public int PunchesToday { get; set; }
    public int Unmatched { get; set; }
    public int? SkewSeconds { get; set; }
}
public record MapInfo(int Id, string Pin, string EmpCode, string? Name);
public record UnmappedInfo(string Pin, int Punches, DateTime LastPunch);
public record MapsDto(List<MapInfo> Maps, List<UnmappedInfo> Unmapped);
public class DeviceLogInfo
{
    public long Id { get; set; }
    public string DeviceSerial { get; set; } = "";
    public string? DeviceName { get; set; }
    public string Pin { get; set; } = "";
    public DateTime PunchTime { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int? SkewSeconds { get; set; }
    public string? EmpCode { get; set; }
    public string? Name { get; set; }
    public string Result { get; set; } = "";
}

// Wording, colours and number formats shared by the attendance screens.
public static class Att
{
    public static string Label(string status) => status switch
    {
        "Present" => "Present", "PresentLate" => "Present (late)", "Working" => "Working", "Away" => "Away", "HalfDay" => "Half day", "Short" => "Short hours",
        "Absent" => "Absent", "Leave" => "On leave", "Holiday" => "Holiday", "Off" => "Weekly off", "Missing" => "Missing punch-out", "Pending" => "Not in yet", "NoData" => "No data", "Future" => "", _ => status,
    };

    public static string Css(string status) => status switch
    {
        "Present" => "st-present", "PresentLate" => "st-late", "Working" => "st-working", "Away" => "st-away", "HalfDay" => "st-half", "Short" => "st-short",
        "Absent" => "st-absent", "Leave" => "st-leave", "Holiday" => "st-holiday", "Off" => "st-off", "Missing" => "st-missing", "Pending" => "st-pending", "NoData" => "st-nodata", _ => "st-future",
    };

    public static string Letter(string status) => status switch
    {
        "Present" => "P", "PresentLate" => "L", "Working" => "P", "Away" => "P", "HalfDay" => "H", "Short" => "S", "Absent" => "A", "Leave" => "V",
        "Holiday" => "HD", "Off" => "O", "Missing" => "M", "Pending" => "-", "NoData" => "", _ => "",
    };

    public static string Short(int minutes) => minutes < 60 ? $"{minutes} min" : Hm(minutes);
    public static string Hm(int minutes) => minutes <= 0 ? "0h 00m" : $"{minutes / 60}h {minutes % 60:00}m";
    public static string Clock(int seconds) { seconds = Math.Max(0, seconds); return $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}"; }
    public static string Time(DateTime? t) => t?.ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
    public static string Time(TimeSpan? t) => t is null ? "-" : DateTime.Today.Add(t.Value).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    public static string Hhmm(TimeSpan t) => $"{t.Hours:00}:{t.Minutes:00}";
}

public sealed class AttendanceClient(HttpClient http)
{
    static string Q(string? s) => Uri.EscapeDataString(s ?? "");

    async Task<HrmsApp.Models.WriteResult> Send(HttpMethod method, string url, object? body = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            string? error = null;
            try { using var doc = JsonDocument.Parse(text); if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString(); }
            catch (JsonException) { }
            return res.IsSuccessStatusCode ? new HrmsApp.Models.WriteResult(true, null, null) : new HrmsApp.Models.WriteResult(false, error ?? $"The service returned {(int)res.StatusCode}.", null);
        }
        catch (Exception ex) { return new HrmsApp.Models.WriteResult(false, "Couldn't reach the HRMS service: " + ex.Message, null); }
    }

    // ----- me -----
    public Task<TodayInfo?> Today() => http.GetFromJsonAsync<TodayInfo>("api/me/attendance/today");
    public Task<HrmsApp.Models.WriteResult> Punch(string? workMode, string? note) => Send(HttpMethod.Post, "api/me/attendance/punch", new { workMode, note });
    public Task<MonthInfo?> Month(string month) => http.GetFromJsonAsync<MonthInfo>($"api/me/attendance/month?month={Q(month)}");
    public Task<DayDetail?> Day(DateTime date) => http.GetFromJsonAsync<DayDetail>($"api/me/attendance/day?date={date:yyyy-MM-dd}");
    public Task<List<FixRequest>?> MyRequests() => http.GetFromJsonAsync<List<FixRequest>>("api/me/attendance/requests");
    public Task<HrmsApp.Models.WriteResult> RequestFix(DateTime date, string? inTime, string? outTime, string reason) => Send(HttpMethod.Post, "api/me/attendance/requests", new { date, inTime, outTime, reason });
    public Task<HrmsApp.Models.WriteResult> CancelFix(int id) => Send(HttpMethod.Delete, $"api/me/attendance/requests/{id}");

    // ----- admin -----
    public Task<BoardInfo?> Board(DateTime date) => http.GetFromJsonAsync<BoardInfo>($"api/admin/attendance/today?date={date:yyyy-MM-dd}");
    public Task<RegisterInfo?> Register(string month) => http.GetFromJsonAsync<RegisterInfo>($"api/admin/attendance/register?month={Q(month)}");
    public Task<AdminDayDetail?> AdminDay(string emp, DateTime date) => http.GetFromJsonAsync<AdminDayDetail>($"api/admin/attendance/day?emp={Q(emp)}&date={date:yyyy-MM-dd}");
    public Task<HrmsApp.Models.WriteResult> AddPunch(string emp, DateTime date, string time, string direction, string? note) => Send(HttpMethod.Post, "api/admin/attendance/punches", new { emp, date, time, direction, note });
    public Task<HrmsApp.Models.WriteResult> DeletePunch(long id) => Send(HttpMethod.Delete, $"api/admin/attendance/punches/{id}");
    public Task<List<FixRequest>?> Requests(string status) => http.GetFromJsonAsync<List<FixRequest>>($"api/admin/attendance/requests?status={Q(status)}");
    public Task<HrmsApp.Models.WriteResult> Approve(int id, string? note) => Send(HttpMethod.Post, $"api/admin/attendance/requests/{id}/approve", new { note });
    public Task<HrmsApp.Models.WriteResult> Reject(int id, string note) => Send(HttpMethod.Post, $"api/admin/attendance/requests/{id}/reject", new { note });
    public Task<List<ShiftDto>?> Shifts() => http.GetFromJsonAsync<List<ShiftDto>>("api/admin/attendance/setup/shifts");
    public Task<HrmsApp.Models.WriteResult> SaveShift(int? id, object body) => id is null ? Send(HttpMethod.Post, "api/admin/attendance/setup/shifts", body) : Send(HttpMethod.Put, $"api/admin/attendance/setup/shifts/{id}", body);
    public Task<List<EmpShift>?> SetupEmployees() => http.GetFromJsonAsync<List<EmpShift>>("api/admin/attendance/setup/employees");
    public Task<HrmsApp.Models.WriteResult> Assign(List<string> emps, int shiftId, DateTime fromDate) => Send(HttpMethod.Post, "api/admin/attendance/setup/assign", new { emps, shiftId, fromDate });
    public Task<List<HolidayDto>?> Holidays(int year) => http.GetFromJsonAsync<List<HolidayDto>>($"api/admin/attendance/setup/holidays?year={year}");
    public Task<HrmsApp.Models.WriteResult> SaveHoliday(int? id, object body) => id is null ? Send(HttpMethod.Post, "api/admin/attendance/setup/holidays", body) : Send(HttpMethod.Put, $"api/admin/attendance/setup/holidays/{id}", body);
    // ----- machines -----
    public Task<List<MachineInfo>?> Devices() => http.GetFromJsonAsync<List<MachineInfo>>("api/admin/attendance/devices");
    public Task<HrmsApp.Models.WriteResult> SaveDevice(int? id, object body) => id is null ? Send(HttpMethod.Post, "api/admin/attendance/devices", body) : Send(HttpMethod.Put, $"api/admin/attendance/devices/{id}", body);
    public Task<MapsDto?> DeviceMaps() => http.GetFromJsonAsync<MapsDto>("api/admin/attendance/devices/mappings");
    public Task<HrmsApp.Models.WriteResult> AddMap(string pin, string empCode) => Send(HttpMethod.Post, "api/admin/attendance/devices/mappings", new { pin, empCode });
    public Task<HrmsApp.Models.WriteResult> DeleteMap(int id) => Send(HttpMethod.Delete, $"api/admin/attendance/devices/mappings/{id}");
    public Task<List<DeviceLogInfo>?> DeviceLog(string result) => http.GetFromJsonAsync<List<DeviceLogInfo>>($"api/admin/attendance/devices/log?result={Q(result)}&limit=200");
    public Task<HrmsApp.Models.WriteResult> Reprocess() => Send(HttpMethod.Post, "api/admin/attendance/devices/reprocess");

    // The simulator speaks to the same address a real machine uses, in the format a real machine sends.
    public async Task<(bool Ok, string Reply)> SimulatePunch(string serial, string pin, DateTime time)
    {
        try
        {
            using var res = await http.PostAsync($"iclock/cdata?SN={Q(serial)}&table=ATTLOG&Stamp=1", new StringContent($"{pin}	{time:yyyy-MM-dd HH:mm:ss}	0	1", System.Text.Encoding.UTF8, "text/plain"));
            return (res.IsSuccessStatusCode, $"{(int)res.StatusCode} {(await res.Content.ReadAsStringAsync()).Trim()}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
    public async Task<(bool Ok, string Reply)> SimulateHello(string serial)
    {
        try { using var res = await http.GetAsync($"iclock/cdata?SN={Q(serial)}&options=all&pushver=2.4.1"); return (res.IsSuccessStatusCode, $"{(int)res.StatusCode} handshake"); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public Task<HrmsApp.Models.WriteResult> DeleteHoliday(int id) => Send(HttpMethod.Delete, $"api/admin/attendance/setup/holidays/{id}");
}
