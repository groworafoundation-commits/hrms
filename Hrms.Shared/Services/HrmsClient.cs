using HrmsApp.Services;
using System.Net.Http.Json;
using System.Text.Json;
using HrmsApp.Models;

namespace HrmsApp;

public sealed class HrmsClient(HttpClient http)
{
    public Task<Dashboard?> Dashboard() => http.GetFromJsonAsync<Dashboard>("api/dashboard");

    public Task<Paged<EmployeeRow>?> Employees(string? q, string status, int page, int size = 20) =>
        http.GetFromJsonAsync<Paged<EmployeeRow>>($"api/employees?q={Uri.EscapeDataString(q ?? "")}&status={status}&page={page}&size={size}");

    public async Task<EmployeeProfile?> Employee(string code)
    {
        try { return await http.GetFromJsonAsync<EmployeeProfile>($"api/employees/{Uri.EscapeDataString(code)}"); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public Task<List<LeaveRow>?> Leave(string? type, DateTime from, DateTime to, string? q) =>
        http.GetFromJsonAsync<List<LeaveRow>>($"api/leave?type={type}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Uri.EscapeDataString(q ?? "")}");

    public Task<List<PayrollMonth>?> PayrollMonths() => http.GetFromJsonAsync<List<PayrollMonth>>("api/payroll/months");

    public Task<PayrollRun?> Payroll(string? month) => http.GetFromJsonAsync<PayrollRun>($"api/payroll?month={month}");

    static string Q(string? s) => Uri.EscapeDataString(s ?? "");

    public Task<List<StructureRow>?> Structure(string? q, string status = "active") =>
        http.GetFromJsonAsync<List<StructureRow>>($"api/payroll/structure?q={Q(q)}&status={status}");

    public Task<EntryReport?> Entries(string group, string? type, string? code, string? q, DateTime from, DateTime to) =>
        http.GetFromJsonAsync<EntryReport>($"api/payroll/entries?group={group}&type={Q(type)}&code={Q(code)}&q={Q(q)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LoanBalance>?> Loans(string? q, string status = "all") =>
        http.GetFromJsonAsync<List<LoanBalance>>($"api/payroll/loans?q={Q(q)}&status={status}");

    public Task<AccrualReport?> Accruals(string? type, string? code, string? q) =>
        http.GetFromJsonAsync<AccrualReport>($"api/payroll/accruals?type={Q(type)}&code={Q(code)}&q={Q(q)}");

    public Task<List<TransferRow>?> Transfers(string? q, string? code = null) =>
        http.GetFromJsonAsync<List<TransferRow>>($"api/payroll/transfers?q={Q(q)}&code={Q(code)}");

    public Task<List<HeadRow>?> Heads() => http.GetFromJsonAsync<List<HeadRow>>("api/payroll/heads");

    public Task<List<LeaveSummaryRow>?> LeaveSummary(int year) => http.GetFromJsonAsync<List<LeaveSummaryRow>>($"api/leave/summary?year={year}");

    // ---------- reports ----------
    public Task<List<ReportDef>?> ReportList() => http.GetFromJsonAsync<List<ReportDef>>("api/reports");
    public Task<CompanyInfo?> Company() => http.GetFromJsonAsync<CompanyInfo>("api/company");
    public Task<ReportLookups?> ReportLookups() => http.GetFromJsonAsync<ReportLookups>("api/reports/lookups");

    public Task<ReportResult?> Report(string id, ReportFilter f) =>
        http.GetFromJsonAsync<ReportResult>($"api/reports/{id}?month={Q(f.Month)}&employee={Q(f.Employee)}&designation={Q(f.Designation)}&location={Q(f.Location)}&status={f.Status}&asOf={f.AsOf:yyyy-MM-dd}&includeZero={(f.IncludeZero ? "true" : "false")}");

    // ---------- lookups ----------
    List<EmployeeRow>? employeeOptions;
    public async Task<List<EmployeeRow>> EmployeeOptions(bool refresh = false)
    {
        if (refresh || employeeOptions is null)
            employeeOptions = (await http.GetFromJsonAsync<Paged<EmployeeRow>>("api/employees?status=active&size=500"))?.Items ?? [];
        return employeeOptions;
    }
    public void ForgetEmployees() => employeeOptions = null;

    public Task<FormLookups?> FormLookups() => http.GetFromJsonAsync<FormLookups>("api/lookups/form");
    public Task<List<VariableType>?> VariableTypes() => http.GetFromJsonAsync<List<VariableType>>("api/payroll/variable/types");
    public Task<List<ExtraVersion>?> ExtraVersions(string code) => http.GetFromJsonAsync<List<ExtraVersion>>($"api/employees/{Q(code)}/extra");
    public Task<StructureHistory?> StructureOf(string code) => http.GetFromJsonAsync<StructureHistory>($"api/employees/{Q(code)}/structure");
    public Task<List<EntryRow>?> Installments(long id) => http.GetFromJsonAsync<List<EntryRow>>($"api/payroll/loans/{id}/installments");

    // ---------- sign in ----------
    public async Task<(LoginResponse? Result, string? Error)> Login(string user, string password)
    {
        try
        {
            using var res = await http.PostAsJsonAsync("api/auth/login", new { user, password });
            if (res.IsSuccessStatusCode) return (await res.Content.ReadFromJsonAsync<LoginResponse>(), null);
            string? error = null;
            try { using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()); if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString(); }
            catch (JsonException) { }
            return (null, error ?? $"The service returned {(int)res.StatusCode}.");
        }
        catch (Exception ex) { return (null, "Couldn't reach the HRMS service: " + ex.Message); }
    }

    public async Task Logout() { try { using var _ = await http.PostAsync("api/auth/logout", null); } catch { } }

    // ---------- writes ----------
    async Task<WriteResult> Send(HttpMethod method, string url, object? body = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            string? error = null, message = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
                if (doc.RootElement.TryGetProperty("result", out var r)) message = r.ToString();
            }
            catch (JsonException) { }
            return res.IsSuccessStatusCode ? new WriteResult(true, null, message) : new WriteResult(false, error ?? $"The service returned {(int)res.StatusCode}.", null);
        }
        catch (Exception ex) { return new WriteResult(false, "Couldn't reach the HRMS service: " + ex.Message, null); }
    }

    public Task<WriteResult> CreateEmployee(EmployeeInput e) { ForgetEmployees(); return Send(HttpMethod.Post, "api/employees", e); }
    public Task<WriteResult> UpdateEmployee(EmployeeInput e) { ForgetEmployees(); return Send(HttpMethod.Put, $"api/employees/{Q(e.EmpCode)}", e); }
    public Task<WriteResult> DeleteEmployee(string code) { ForgetEmployees(); return Send(HttpMethod.Delete, $"api/employees/{Q(code)}"); }
    public Task<WriteResult> SaveExtra(string code, object body) => Send(HttpMethod.Post, $"api/employees/{Q(code)}/extra", body);
    public Task<WriteResult> DeleteExtra(string code, long id) => Send(HttpMethod.Delete, $"api/employees/{Q(code)}/extra/{id}");
    public Task<WriteResult> SaveStructure(string code, object body) => Send(HttpMethod.Post, $"api/employees/{Q(code)}/structure", body);
    public Task<WriteResult> AddTransfer(string code, object body) => Send(HttpMethod.Post, $"api/employees/{Q(code)}/transfers", body);
    public Task<WriteResult> DeleteTransfer(int id) => Send(HttpMethod.Delete, $"api/payroll/transfers/{id}");
    public Task<WriteResult> AddLeave(object body) => Send(HttpMethod.Post, "api/leave", body);
    public Task<WriteResult> UpdateLeave(long id, object body) => Send(HttpMethod.Put, $"api/leave/{id}", body);
    public Task<WriteResult> DeleteLeave(long id) => Send(HttpMethod.Delete, $"api/leave/{id}");
    public Task<WriteResult> AddLoan(object body) => Send(HttpMethod.Post, "api/payroll/loans", body);
    public Task<WriteResult> CancelLoan(long id) => Send(HttpMethod.Delete, $"api/payroll/loans/{id}");
    public Task<WriteResult> AddVariable(object body) => Send(HttpMethod.Post, "api/payroll/variable", body);
    public Task<WriteResult> UpdateVariable(long id, object body) => Send(HttpMethod.Put, $"api/payroll/variable/{id}", body);
    public Task<WriteResult> DeleteVariable(long id) => Send(HttpMethod.Delete, $"api/payroll/variable/{id}");
}

public static class Fmt
{
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    public static string Money(decimal v) => v.ToString("N0", Inv);
    public static string Money2(decimal v) => v.ToString("N2", Inv);
    public static string Date(DateTime? d) => d?.ToString("dd MMM yyyy", Inv) ?? "-";
    public static string Short(DateTime d) => d.ToString("MMM d", Inv);
    public static string Range(DateTime a, DateTime b) => a.Date == b.Date ? Short(a) : $"{Short(a)} - {Short(b)}";
    public static string Title(string s) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    public static string Cell(System.Text.Json.JsonElement e, string type)
    {
        if (e.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) return "";
        return type switch
        {
            "money" => e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDecimal().ToString("N2", Inv) : e.ToString(),
            "number" => e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDecimal().ToString("0.##", Inv) : e.ToString(),
            "date" => DateTime.TryParse(e.GetString(), Inv, System.Globalization.DateTimeStyles.None, out var d) && d.Year > 1901 ? d.ToString("dd MMM yyyy", Inv) : "",
            _ => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() ?? "" : e.ToString(),
        };
    }

    public static string Csv(string s) => s.Contains('"') || s.Contains(',') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string MonthLabel(string ym) => DateTime.TryParse(ym + "-01", out var d) ? d.ToString("MMM yyyy", Inv) : ym;
}
