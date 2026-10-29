using System.Net.Http.Json;
using System.Text.Json;
using HrmsApp.Models;

namespace HrmsApp.Services;

public record LeaveTaken(string LeaveType, string TypeName, decimal Days, int Records);
public record MySummary(EmployeeRow Employee, List<LeaveTaken> Taken, int Pending, int Year);
public record UserDto(string UserId, string UserName, string? FullName, string? RoleId, string? RoleName, string? Email, string? Mobile, bool Disabled, string? EmployeeCode, string? EmployeeName, int Scope, bool Locked = false, bool MustChange = false);
public record RoleDto(string RoleId, string RoleName, int AuthLevel, int Users);
public record Plain(string Id, string Name);
public class LeaveBalance { public string EmpCode { get; set; } = ""; public string Name { get; set; } = ""; public bool HasPayroll { get; set; } public string? AsOfMonth { get; set; } public decimal BroughtForward { get; set; } public decimal Earned { get; set; } public decimal Used { get; set; } public decimal Balance { get; set; } public decimal Unprocessed { get; set; } public decimal Pending { get; set; } public decimal Available { get; set; } public decimal TakenThisYear { get; set; } public int Year { get; set; } }
public class NotificationItem { public long Id { get; set; } public string Kind { get; set; } = ""; public string Title { get; set; } = ""; public string? Body { get; set; } public string? Link { get; set; } public DateTime CreatedAt { get; set; } public DateTime? ReadAt { get; set; } }
public record NotificationList(int Unread, List<NotificationItem> Items);
public class OutboxItem { public long Id { get; set; } public string? ToUser { get; set; } public string ToAddress { get; set; } = ""; public string Subject { get; set; } = ""; public string Status { get; set; } = ""; public int Attempts { get; set; } public DateTime CreatedAt { get; set; } public DateTime? SentAt { get; set; } public string? Error { get; set; } }
public record EmailOverview(bool Configured, string? From, string? Host, List<OutboxItem> Rows);
public record Approver(string Id, string Name, string RoleName, int Level);
public class ApprovalRow : LeaveRow { public int FromLevel { get; set; } public string? FromUser { get; set; } public string? Note { get; set; } }
public record ApprovalOptions(bool Final, int Level, int DocLevel, List<Approver> Push, List<Approver> Back);
public class RightDto
{
    public string OptionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public bool View { get; set; }
    public bool Add { get; set; }
    public bool Edit { get; set; }
    public bool Delete { get; set; }
    public bool Print { get; set; }
    public int Level { get; set; }
}
public record MenuDto(string Id, string Name, string Route, string Icon, int Sequence, bool Active, bool Custom);

// Employee self-service and administration calls.
public sealed class PortalClient(HttpClient http)
{
    static string Q(string? s) => Uri.EscapeDataString(s ?? "");

    async Task<WriteResult> Send(HttpMethod method, string url, object? body = null)
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
            return res.IsSuccessStatusCode ? new WriteResult(true, null, null) : new WriteResult(false, error ?? $"The service returned {(int)res.StatusCode}.", null);
        }
        catch (Exception ex) { return new WriteResult(false, "Couldn't reach the HRMS service: " + ex.Message, null); }
    }

    // ----- me -----
    public Task<MySummary?> Me() => http.GetFromJsonAsync<MySummary>("api/me");
    public Task<List<LeaveRow>?> MyLeave(DateTime from, DateTime to) => http.GetFromJsonAsync<List<LeaveRow>>($"api/me/leave?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
    public Task<WriteResult> RequestLeave(object body) => Send(HttpMethod.Post, "api/me/leave", body);
    public Task<WriteResult> CancelLeave(long id) => Send(HttpMethod.Delete, $"api/me/leave/{id}");
    public Task<List<PayslipRow>?> MyPayslips() => http.GetFromJsonAsync<List<PayslipRow>>("api/me/payslips");
    public Task<ReportResult?> MyPayslip(string month) => http.GetFromJsonAsync<ReportResult>($"api/me/payslip?month={Q(month)}");

    // ----- admin -----
    public Task<LeaveBalance?> MyBalance() => http.GetFromJsonAsync<LeaveBalance>("api/me/leave/balance");
    public Task<List<LeaveBalance>?> Balances() => http.GetFromJsonAsync<List<LeaveBalance>>("api/leave/balances");
    public Task<NotificationList?> Notifications() => http.GetFromJsonAsync<NotificationList>("api/notifications");
    public Task<WriteResult> MarkRead(long id) => Send(HttpMethod.Post, $"api/notifications/{id}/read");
    public Task<WriteResult> ReadAllNotifications() => Send(HttpMethod.Post, "api/notifications/read-all");
    public Task<EmailOverview?> EmailOverview() => http.GetFromJsonAsync<EmailOverview>("api/admin/email");
    public Task<WriteResult> TestEmail(string to) => Send(HttpMethod.Post, "api/admin/email/test", new { to });
    public Task<WriteResult> ChangePassword(string current, string newPassword) => Send(HttpMethod.Post, "api/auth/change-password", new { current, newPassword });
    public Task<WriteResult> Unlock(string id) => Send(HttpMethod.Post, $"api/admin/users/{Q(id)}/unlock");
    public Task<List<UserDto>?> Users(string? q, string? role) => http.GetFromJsonAsync<List<UserDto>>($"api/admin/users?q={Q(q)}&role={Q(role)}");
    public Task<List<Plain>?> EmployeesWithoutLogin() => http.GetFromJsonAsync<List<Plain>>("api/admin/users/employees-without-login");
    public Task<WriteResult> CreateUser(object body) => Send(HttpMethod.Post, "api/admin/users", body);
    public Task<WriteResult> UpdateUser(string id, object body) => Send(HttpMethod.Put, $"api/admin/users/{Q(id)}", body);

    public Task<List<RoleDto>?> Roles() => http.GetFromJsonAsync<List<RoleDto>>("api/admin/roles");
    public Task<WriteResult> CreateRole(object body) => Send(HttpMethod.Post, "api/admin/roles", body);
    public Task<WriteResult> UpdateRole(string id, object body) => Send(HttpMethod.Put, $"api/admin/roles/{Q(id)}", body);
    public Task<List<RightDto>?> Rights(string role) => http.GetFromJsonAsync<List<RightDto>>($"api/admin/roles/{Q(role)}/rights");
    public Task<WriteResult> SaveRights(string role, List<RightDto> rights) =>
        Send(HttpMethod.Put, $"api/admin/roles/{Q(role)}/rights", rights.Select(r => new { r.OptionId, r.View, r.Add, r.Edit, r.Delete, r.Print, r.Level }));

    public Task<List<MenuDto>?> Menus() => http.GetFromJsonAsync<List<MenuDto>>("api/admin/menus");
    public Task<WriteResult> CreateMenu(object body) => Send(HttpMethod.Post, "api/admin/menus", body);
    public Task<WriteResult> UpdateMenu(string id, object body) => Send(HttpMethod.Put, $"api/admin/menus/{Q(id)}", body);
    public Task<WriteResult> DeleteMenu(string id) => Send(HttpMethod.Delete, $"api/admin/menus/{Q(id)}");

    public Task<List<ApprovalRow>?> Approvals() => http.GetFromJsonAsync<List<ApprovalRow>>("api/admin/approvals");
    public Task<ApprovalOptions?> ApprovalOptions(long id) => http.GetFromJsonAsync<ApprovalOptions>($"api/admin/approvals/{id}/options");
    public Task<WriteResult> Approve(long id, string? to, string? note) => Send(HttpMethod.Post, $"api/admin/approvals/{id}/approve", new { to, note });
    public Task<WriteResult> SendBack(long id, string to, string note) => Send(HttpMethod.Post, $"api/admin/approvals/{id}/return", new { to, note });
    public Task<WriteResult> Reject(long id, string note) => Send(HttpMethod.Post, $"api/admin/approvals/{id}/reject", new { note });
    public Task<List<Approver>?> Approvers() => http.GetFromJsonAsync<List<Approver>>("api/me/approvers");
}
