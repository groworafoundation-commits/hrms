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
public class RequestRow
{
    public int Id { get; set; } public string TransNo { get; set; } = ""; public string EmpCode { get; set; } = ""; public string? Name { get; set; }
    public string Type { get; set; } = ""; public string TypeName { get; set; } = "";
    public DateTime FromDate { get; set; } public DateTime ToDate { get; set; } public TimeSpan? FromTime { get; set; } public TimeSpan? ToTime { get; set; }
    public decimal Days { get; set; } public int? Minutes { get; set; } public string? Reason { get; set; } public string? Remarks { get; set; }
    public string Status { get; set; } = ""; public string? HeldBy { get; set; } public string? HeldByName { get; set; } public string? HeldByRole { get; set; }
    public int Level { get; set; } public int FinalLevel { get; set; } public string? DecisionNote { get; set; } public DateTime CreatedAt { get; set; } public int Files { get; set; }
}
public record CompOffCredit(DateTime EarnedOn, DateTime ExpiresOn, decimal Credits, decimal Used);
public record CompOffBalance(decimal Available, decimal Pending, int ValidDays, List<CompOffCredit> Credits);
public record RequestOptions(bool Final, int Level, int DocLevel, List<Approver> Push);
public record AttachmentRow(int Id, string FileName, string ContentType, int SizeBytes, string? UploadedBy, DateTime UploadedAt);
public record OrgNode(string Id, string Name, string? Title, string? RoleName, int Level, string? Dept);
public record OrgView(OrgNode Me, List<OrgNode> Chain, string? Dept, List<OrgNode> Team, List<OrgNode> Reports, string? Note);
public record OrgDept(int Id, string Name, OrgNode? Head, List<OrgNode> Members);
public record OrgCompany(List<OrgNode> Leaders, List<OrgNode> Hr, List<OrgDept> Departments, List<OrgNode> Unassigned);
public record LeaveStep(string UserId, string Name, string? RoleName, int Level, DateTime At, string? Note);
// The rest of the chain for a request still waiting: each level above its holder up to where it ends, and who can give the final approval.
public record UpcomingLevel(int Level, List<string> Names, bool Final);
public record LeaveTimeline(List<LeaveStep> Steps, List<UpcomingLevel> Upcoming, int FinalLevel, List<string> FinalApprovers);
public record Department(int Id, string Name, string? ApproverUserId, string? ApproverName, bool Active, int Members);
public record DeptMember(string EmpCode, string Name, string? Designation, int? DeptId, string? DeptName);
public record LeaveRule(int EmployeeFinal, int ManagerFinal);
public record DepartmentOverview(List<Department> Departments, LeaveRule Rule, List<Approver> Approvers);
public record PendingAll(List<LeaveRow> Rows, List<Approver> Assignable);
public class ApprovalRow : LeaveRow { public int FromLevel { get; set; } public string? FromUser { get; set; } public string? Note { get; set; } public List<string> Facts { get; set; } = []; }
public record AskAnswer(string Intent, string Title, string Text, List<string> Lines, string? Link, List<string> Suggestions);
public record CheckFinding(string Area, string Severity, string Check, string EmpCode, string Name, string Detail);
public record SalaryStatus(string Month, string? LastProcessedMonth, bool Processed, int PaidEmployees, bool? Posted, int UnapprovedLeave, int? UnapprovedVariable, int? UnpostedVariable, bool ReadyToProcess);
public record CheckResult(string? PayrollMonth, string? PreviousMonth, string AttendanceMonth, int Employees, List<CheckFinding> Findings, SalaryStatus? Salary = null);
public class AssetRow
{
    public int Id { get; set; } public string AssetCode { get; set; } = ""; public string Category { get; set; } = ""; public string Name { get; set; } = ""; public string SerialNo { get; set; } = "";
    public decimal? Value { get; set; } public DateTime? PurchaseDate { get; set; } public string Status { get; set; } = ""; public string? Notes { get; set; }
    public int? CustodyId { get; set; } public string? HolderCode { get; set; } public string? HolderName { get; set; } public DateTime? Since { get; set; } public string? State { get; set; }
}
public class CustodyRow
{
    public int Id { get; set; } public string? DocNo { get; set; } public int AssetId { get; set; } public string AssetCode { get; set; } = ""; public string Category { get; set; } = ""; public string AssetName { get; set; } = ""; public string SerialNo { get; set; } = "";
    public string EmpCode { get; set; } = ""; public string? EmpName { get; set; } public DateTime IssueDate { get; set; } public string? ConditionOut { get; set; } public string? IssueNote { get; set; } public string IssuedBy { get; set; } = "";
    public string IssueStatus { get; set; } = ""; public string? IssueDecisionNote { get; set; } public DateTime? AcceptedAt { get; set; }
    public string? ReturnDocNo { get; set; } public DateTime? ReturnDate { get; set; } public string? ConditionIn { get; set; } public string? ReturnNote { get; set; } public string? ReturnStatus { get; set; } public string? ReturnDecisionNote { get; set; }
    public string State { get; set; } = "";
}
public class ClearanceRow
{
    public int Id { get; set; } public string EmpCode { get; set; } = ""; public string? EmpName { get; set; } public DateTime? LastWorkingDay { get; set; } public string? Reason { get; set; } public string Status { get; set; } = "";
    public string StartedBy { get; set; } = ""; public DateTime StartedAt { get; set; } public string? ClearedBy { get; set; } public DateTime? ClearedAt { get; set; } public int AssetsOut { get; set; } public int OpenItems { get; set; }
}
public class ClearanceItemRow { public int Id { get; set; } public string Title { get; set; } = ""; public bool Done { get; set; } public string? DoneBy { get; set; } public DateTime? DoneAt { get; set; } public string? Note { get; set; } }
public record ClearanceCheck(string Key, string Title, bool Ok, bool Blocking, string Detail);
public record ClearanceDetail(string EmpCode, string? EmpName, ClearanceRow? Clearance, List<ClearanceItemRow> Items, List<ClearanceCheck> Checks, List<CustodyRow> Assets, bool CanClear, List<string> Blockers);
public class AssetWaiting
{
    public string Kind { get; set; } = ""; public int Id { get; set; } public string? DocNo { get; set; } public int AssetId { get; set; } public string AssetCode { get; set; } = ""; public string AssetName { get; set; } = ""; public string SerialNo { get; set; } = "";
    public string EmpCode { get; set; } = ""; public string? EmpName { get; set; } public string? ToEmpCode { get; set; } public string? ToEmpName { get; set; } public DateTime Date { get; set; }
    public string? Condition { get; set; } public string? Note { get; set; } public string PreparedBy { get; set; } = ""; public bool CanDecide { get; set; } public bool CanAccept { get; set; }
}
public class AssetTransferRow
{
    public int Id { get; set; } public string? DocNo { get; set; } public int AssetId { get; set; } public string AssetCode { get; set; } = ""; public string AssetName { get; set; } = ""; public string SerialNo { get; set; } = "";
    public string FromEmp { get; set; } = ""; public string? FromName { get; set; } public string ToEmp { get; set; } = ""; public string? ToName { get; set; } public DateTime TransferDate { get; set; }
    public string? Condition { get; set; } public string? Note { get; set; } public string PreparedBy { get; set; } = ""; public string Status { get; set; } = ""; public string? DecisionNote { get; set; } public DateTime? AcceptedAt { get; set; }
}
public class AssetFileRow { public int Id { get; set; } public int AssetId { get; set; } public int? CustodyId { get; set; } public string Kind { get; set; } = ""; public string FileName { get; set; } = ""; public string ContentType { get; set; } = ""; public int SizeBytes { get; set; } public string UploadedBy { get; set; } = ""; public DateTime UploadedAt { get; set; } }
public record ImportLine(string? AssetCode, string? Category, string? Name, string? SerialNo, decimal? Value, DateTime? PurchaseDate, string? Notes, string? EmpCode, DateTime? IssueDate);
public record ImportRowResult(int Row, bool Ok, string? Error);
public record ImportResponse(bool Ok, bool DryRun, int Imported, int Assigned, List<ImportRowResult> Results, string? Error);
public record LeavingRow(string EmpCode, string Name, DateTime TermDate, int AssetsOut);
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
    public Task<List<RequestRow>?> MyRequests(DateTime from, DateTime to) => http.GetFromJsonAsync<List<RequestRow>>($"api/me/requests?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
    public Task<CompOffBalance?> CompOff() => http.GetFromJsonAsync<CompOffBalance>("api/me/compoff");
    public Task<WriteResult> CreateRequest(object body) => Send(HttpMethod.Post, "api/me/requests", body);
    public Task<WriteResult> CancelRequest(int id) => Send(HttpMethod.Delete, $"api/me/requests/{id}");
    public Task<LeaveTimeline?> RequestTimeline(int id) => http.GetFromJsonAsync<LeaveTimeline>($"api/me/requests/{id}/timeline");
    public Task<List<RequestRow>?> PendingRequests() => http.GetFromJsonAsync<List<RequestRow>>("api/requests/pending");
    public Task<RequestOptions?> ReqOptions(int id) => http.GetFromJsonAsync<RequestOptions>($"api/requests/{id}/options");
    public Task<WriteResult> ApproveRequest(int id, string? to, string? note) => Send(HttpMethod.Post, $"api/requests/{id}/approve", new { to, note });
    public Task<WriteResult> RejectRequest(int id, string note) => Send(HttpMethod.Post, $"api/requests/{id}/reject", new { note });
    public Task<List<AttachmentRow>?> Attachments(string ownerType, string ownerKey) => http.GetFromJsonAsync<List<AttachmentRow>>($"api/attachments?ownerType={Q(ownerType)}&ownerKey={Q(ownerKey)}");
    public async Task<byte[]?> AttachmentBytes(int id) { using var res = await http.GetAsync($"api/attachments/{id}"); return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync() : null; }
    public Task<WriteResult> DeleteAttachment(int id) => Send(HttpMethod.Delete, $"api/me/attachments/{id}");
    public async Task<WriteResult> UploadAttachment(string ownerType, string ownerKey, string fileName, string contentType, Stream data)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var part = new StreamContent(data);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(part, "file", fileName);
            using var res = await http.PostAsync($"api/me/attachments?ownerType={Q(ownerType)}&ownerKey={Q(ownerKey)}", form);
            var text = await res.Content.ReadAsStringAsync();
            string? error = null;
            try { using var doc = JsonDocument.Parse(text); if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString(); } catch (JsonException) { }
            return res.IsSuccessStatusCode ? new WriteResult(true, null, null) : new WriteResult(false, error ?? $"The service returned {(int)res.StatusCode}.", null);
        }
        catch (Exception ex) { return new WriteResult(false, "Couldn't upload: " + ex.Message, null); }
    }
    public Task<OrgView?> MyOrg() => http.GetFromJsonAsync<OrgView>("api/me/org");
    public Task<OrgCompany?> CompanyOrg() => http.GetFromJsonAsync<OrgCompany>("api/admin/org");
    public Task<LeaveTimeline?> LeaveTimeline(long id) => http.GetFromJsonAsync<LeaveTimeline>($"api/me/leave/{id}/timeline");
    public Task<WriteResult> CancelLeave(long id) => Send(HttpMethod.Delete, $"api/me/leave/{id}");
    public Task<List<PayslipRow>?> MyPayslips() => http.GetFromJsonAsync<List<PayslipRow>>("api/me/payslips");
    public Task<ReportResult?> MyPayslip(string month) => http.GetFromJsonAsync<ReportResult>($"api/me/payslip?month={Q(month)}");

    // ----- admin -----
    public Task<LeaveBalance?> MyBalance() => http.GetFromJsonAsync<LeaveBalance>("api/me/leave/balance");
    public Task<List<LeaveBalance>?> Balances() => http.GetFromJsonAsync<List<LeaveBalance>>("api/leave/balances");
    public Task<NotificationList?> Notifications() => http.GetFromJsonAsync<NotificationList>("api/notifications");
    public Task<WriteResult> MarkRead(long id) => Send(HttpMethod.Post, $"api/notifications/{id}/read");
    public Task<WriteResult> DismissNotification(long id) => Send(HttpMethod.Post, $"api/notifications/{id}/dismiss");
    public Task<WriteResult> ClearNotifications() => Send(HttpMethod.Post, "api/notifications/clear");
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

    public Task<DepartmentOverview?> Departments() => http.GetFromJsonAsync<DepartmentOverview>("api/admin/departments");
    public Task<List<DeptMember>?> DepartmentMembers() => http.GetFromJsonAsync<List<DeptMember>>("api/admin/departments/members");
    public Task<WriteResult> SaveDepartment(int? id, string name, string? approver, bool active) =>
        id is null ? Send(HttpMethod.Post, "api/admin/departments", new { name, approverUserId = approver }) : Send(HttpMethod.Put, $"api/admin/departments/{id}", new { name, approverUserId = approver, active });
    public Task<WriteResult> DeleteDepartment(int id) => Send(HttpMethod.Delete, $"api/admin/departments/{id}");
    public Task<WriteResult> AssignDepartment(int? deptId, List<string> empCodes) => Send(HttpMethod.Post, "api/admin/departments/assign", new { deptId, empCodes });
    public Task<WriteResult> SaveLeaveRule(LeaveRule r) => Send(HttpMethod.Put, "api/admin/departments/rule", r);
    public Task<PendingAll?> AllPending() => http.GetFromJsonAsync<PendingAll>("api/admin/approvals/all");
    public Task<WriteResult> Reassign(long id, string to, string? note) => Send(HttpMethod.Post, $"api/admin/approvals/{id}/reassign", new { to, note });
    public Task<List<ApprovalRow>?> Approvals() => http.GetFromJsonAsync<List<ApprovalRow>>("api/admin/approvals");
    public Task<ApprovalOptions?> ApprovalOptions(long id) => http.GetFromJsonAsync<ApprovalOptions>($"api/admin/approvals/{id}/options");
    public Task<WriteResult> Approve(long id, string? to, string? note) => Send(HttpMethod.Post, $"api/admin/approvals/{id}/approve", new { to, note });
    public Task<WriteResult> SendBack(long id, string to, string note) => Send(HttpMethod.Post, $"api/admin/approvals/{id}/return", new { to, note });
    public Task<WriteResult> Reject(long id, string note) => Send(HttpMethod.Post, $"api/admin/approvals/{id}/reject", new { note });
    public Task<CheckResult?> Checks(int payChange, int late, int absent, int missing) => http.GetFromJsonAsync<CheckResult>($"api/admin/checks?payChange={payChange}&late={late}&absent={absent}&missing={missing}");
    // ----- assets and clearance -----
    public Task<List<string>?> AssetCategories() => http.GetFromJsonAsync<List<string>>("api/assets/categories");
    public Task<List<AssetRow>?> Assets(string? status, string? category, string? q) => http.GetFromJsonAsync<List<AssetRow>>($"api/assets?status={Q(status)}&category={Q(category)}&q={Q(q)}");
    public Task<WriteResult> AddAsset(object body) => Send(HttpMethod.Post, "api/assets", body);
    public Task<WriteResult> UpdateAsset(int id, object body) => Send(HttpMethod.Put, $"api/assets/{id}", body);
    public Task<WriteResult> DisposeAsset(int id) => Send(HttpMethod.Delete, $"api/assets/{id}");
    public Task<List<CustodyRow>?> Custody(string scope, string? emp = null) => http.GetFromJsonAsync<List<CustodyRow>>($"api/assets/custody?scope={Q(scope)}&emp={Q(emp)}");
    public Task<WriteResult> IssueAsset(int asset, object body) => Send(HttpMethod.Post, $"api/assets/{asset}/issue", body);
    public Task<WriteResult> ReturnAsset(int custody, object body) => Send(HttpMethod.Post, $"api/assets/custody/{custody}/return", body);
    public Task<WriteResult> ApproveAsset(int custody, string? note) => Send(HttpMethod.Post, $"api/assets/custody/{custody}/approve", new { note });
    public Task<WriteResult> RejectAsset(int custody, string note) => Send(HttpMethod.Post, $"api/assets/custody/{custody}/reject", new { note });
    public Task<List<CustodyRow>?> MyAssets() => http.GetFromJsonAsync<List<CustodyRow>>("api/me/assets");
    public Task<WriteResult> AcceptAsset(int custody) => Send(HttpMethod.Post, $"api/me/assets/{custody}/accept");
    public Task<List<AssetWaiting>?> AssetWaitingList() => http.GetFromJsonAsync<List<AssetWaiting>>("api/assets/waiting");
    public Task<List<AssetWaiting>?> MyIncomingAssets() => http.GetFromJsonAsync<List<AssetWaiting>>("api/me/assets/incoming");
    public Task<CustodyRow?> CustodyOne(int id) => http.GetFromJsonAsync<CustodyRow>($"api/assets/custody/{id}");
    public Task<AssetTransferRow?> TransferOne(int id) => http.GetFromJsonAsync<AssetTransferRow>($"api/assets/transfers/{id}");
    public Task<List<AssetTransferRow>?> AssetTransfers(string scope) => http.GetFromJsonAsync<List<AssetTransferRow>>($"api/assets/transfers?scope={Q(scope)}");
    public Task<WriteResult> TransferAsset(int custody, object body) => Send(HttpMethod.Post, $"api/assets/custody/{custody}/transfer", body);
    public Task<WriteResult> WithdrawAsset(int custody) => Send(HttpMethod.Post, $"api/assets/custody/{custody}/withdraw");
    public Task<WriteResult> WithdrawTransfer(int id) => Send(HttpMethod.Post, $"api/assets/transfers/{id}/withdraw");
    public Task<WriteResult> ApproveTransfer(int id, string? note) => Send(HttpMethod.Post, $"api/assets/transfers/{id}/approve", new { note });
    public Task<WriteResult> RejectTransfer(int id, string note) => Send(HttpMethod.Post, $"api/assets/transfers/{id}/reject", new { note });
    public Task<WriteResult> AcceptTransferForEmployee(int id) => Send(HttpMethod.Post, $"api/assets/transfers/{id}/accept");
    public Task<WriteResult> AcceptTransferMine(int id) => Send(HttpMethod.Post, $"api/me/assets/transfers/{id}/accept");
    public Task<List<AssetFileRow>?> AssetFiles(int asset) => http.GetFromJsonAsync<List<AssetFileRow>>($"api/assets/{asset}/files");
    public async Task<byte[]?> AssetFileBytes(int file) { using var res = await http.GetAsync($"api/assets/files/{file}"); return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync() : null; }
    public Task<WriteResult> DeleteAssetFile(int file) => Send(HttpMethod.Delete, $"api/assets/files/{file}");
    public async Task<WriteResult> UploadAssetFile(int asset, string kind, string fileName, string contentType, Stream data)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var part = new StreamContent(data);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(part, "file", fileName);
            using var res = await http.PostAsync($"api/assets/{asset}/files?kind={Q(kind)}", form);
            var text = await res.Content.ReadAsStringAsync();
            string? error = null;
            try { using var doc = JsonDocument.Parse(text); if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString(); } catch (JsonException) { }
            return res.IsSuccessStatusCode ? new WriteResult(true, null, null) : new WriteResult(false, error ?? $"The service returned {(int)res.StatusCode}.", null);
        }
        catch (Exception ex) { return new WriteResult(false, "Couldn't upload: " + ex.Message, null); }
    }
    public async Task<ImportResponse> ImportAssets(List<ImportLine> rows, bool dryRun)
    {
        try
        {
            using var res = await http.PostAsJsonAsync($"api/assets/import?dryRun={(dryRun ? "true" : "false")}", new { rows });
            var text = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out _))
                return JsonSerializer.Deserialize<ImportResponse>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return new ImportResponse(false, dryRun, 0, 0, [], root.TryGetProperty("error", out var e) ? e.GetString() : $"The service returned {(int)res.StatusCode}.");
        }
        catch (Exception ex) { return new ImportResponse(false, dryRun, 0, 0, [], "Couldn't reach the HRMS service: " + ex.Message); }
    }
    public Task<List<LeavingRow>?> Leaving() => http.GetFromJsonAsync<List<LeavingRow>>("api/clearance/leaving");

    public Task<List<ClearanceRow>?> Clearances(string? status) => http.GetFromJsonAsync<List<ClearanceRow>>($"api/clearance?status={Q(status)}");
    public Task<ClearanceDetail?> ClearanceFor(string emp) => http.GetFromJsonAsync<ClearanceDetail>($"api/clearance/employee/{Q(emp)}");
    public Task<WriteResult> StartClearance(object body) => Send(HttpMethod.Post, "api/clearance", body);
    public Task<WriteResult> ToggleClearanceItem(int item, bool done, string? note) => Send(HttpMethod.Post, $"api/clearance/items/{item}/toggle", new { done, note });
    public Task<WriteResult> AddClearanceItem(int clearance, string title) => Send(HttpMethod.Post, $"api/clearance/{clearance}/items", new { title });
    public Task<WriteResult> ClearEmployee(int clearance) => Send(HttpMethod.Post, $"api/clearance/{clearance}/clear");
    public Task<WriteResult> CancelClearance(int clearance) => Send(HttpMethod.Post, $"api/clearance/{clearance}/cancel");

    public Task<List<string>?> AskSuggestions() => http.GetFromJsonAsync<List<string>>("api/me/ask/suggestions");
    public async Task<AskAnswer?> Ask(string q)
    {
        using var res = await http.GetAsync($"api/me/ask?q={Q(q)}");
        var text = await res.Content.ReadAsStringAsync();
        if (res.IsSuccessStatusCode) return JsonSerializer.Deserialize<AskAnswer>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string? error = null;
        try { using var doc = JsonDocument.Parse(text); if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString(); }
        catch (JsonException) { }
        return new AskAnswer("error", "Not available", error ?? $"The service returned {(int)res.StatusCode}.", [], null, []);
    }
    public Task<List<Approver>?> Approvers() => http.GetFromJsonAsync<List<Approver>>("api/me/approvers");
}
