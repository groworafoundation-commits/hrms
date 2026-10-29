using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Hrms.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hrms.Tests;

// Starts the real API in memory against the HRMS databases, creates its own test people (an administrator at level 9, a manager at level 10 and
// an employee at level 8), and removes everything it created when the run ends. Real employees and their data are never changed.
public sealed class ApiFixture : IAsyncLifetime
{
    public const string Password = "Test1234x";
    public const string Admin = "TSTADM", Manager = "TSTMGR";
    public const string AdminEmail = "tstadm@example.test";

    public WebApplicationFactory<Program> Factory = null!;
    public string Employee = "";                  // the employee code, also the employee's login
    string connection = "";
    long maxLeaveId, maxAccrualId;
    DateTime? startedAt;
    readonly List<string> extraUsers = [];

    public async Task InitializeAsync()
    {
        // an empty certificate setting keeps the test server on plain HTTP; real saves go to the HRMS copies, not the ERP
        Environment.SetEnvironmentVariable("Hrms__CertPath", "");
        Environment.SetEnvironmentVariable("Hrms__DryRunAll", "false");
        Factory = new WebApplicationFactory<Program>();
        connection = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Main")!;

        await Cleanup(reportErrors: false);
        await using var c = Open();
        maxLeaveId = await c.ExecuteScalarAsync<long>("SELECT ISNULL(MAX(Id),0) FROM inter01.EMPLOYEELEAVEDETAILS");
        maxAccrualId = await c.ExecuteScalarAsync<long>("SELECT ISNULL(MAX(Id),0) FROM inter01.EmpAccuralPayment");
        startedAt = await c.ExecuteScalarAsync<DateTime>("SELECT GETDATE()");
        // an active employee who has no login yet
        Employee = (await c.ExecuteScalarAsync<string>(@"SELECT TOP 1 e.EmpCode FROM inter01.EmployeeMaster e
            OUTER APPLY (SELECT TOP 1 termdate FROM inter01.EmployeeExtraDetails WHERE EmpCode = e.EmpCode AND ISNULL(deleteflag,0)=0 ORDER BY Id DESC) x
            WHERE x.termdate IS NULL AND NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.Userdef u WHERE u.userid = e.EmpCode) AND e.EmpCode NOT IN (SELECT EmpCode FROM inter01.EMPLOYEELEAVEDETAILS WHERE ISNULL(Deleteflag,0)=0)
            ORDER BY e.EmpCode DESC"))!;
        await CreateUser(c, Admin, "HRM-ADM", "all", AdminEmail);
        await CreateUser(c, Manager, "HRM-MGR", "all", null);
        await CreateUser(c, Employee, "HRM-EMP", "self", null);
    }

    public SqlConnection Open() { var c = new SqlConnection(connection); c.Open(); return c; }

    public async Task CreateUser(SqlConnection c, string id, string role, string scope, string? email, string? password = Password, bool plainOnly = false)
    {
        extraUsers.Add(id);
        await c.ExecuteAsync(@"INSERT HRMSINTER.interface.Userdef (userid, username, roleid, password, PasswordHash, MustChange, name, emailid, [disable], CompanyCodes, financialyearcode, Defaultcompany)
            VALUES (@id, @id, @role, @plain, @hash, 0, @name, @email, 0, 'Launch', '2026', 'Launch')",
            new { id, role, plain = plainOnly ? password : null, hash = plainOnly ? null : Passwords.Hash(password!), name = "Test " + id, email });
        await c.ExecuteAsync("DELETE inter01.AccessUser WHERE UserId = @id", new { id });
        if (scope == "all") await c.ExecuteAsync("INSERT inter01.AccessUser (UserId, AccessUser) SELECT @id, EmpCode FROM inter01.EmployeeMaster", new { id });
        else await c.ExecuteAsync("INSERT inter01.AccessUser (UserId, AccessUser) VALUES (@id, @id)", new { id });
    }

    public async Task<Session> LoginAs(string user, string password = Password)
    {
        var http = Factory.CreateClient();
        var res = await http.PostAsJsonAsync("/api/auth/login", new { user, password });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("token").GetString());
        return new Session(http, json.GetProperty("user"));
    }

    // gives a test a clean slate for the test employee's punches and corrections
    public async Task ClearAttendance()
    {
        await using var c = Open();
        await c.ExecuteAsync("DELETE hrms.AttPunch WHERE EmpCode = @Employee; DELETE hrms.AttRegularization WHERE EmpCode = @Employee; DELETE hrms.AttShiftAssign WHERE EmpCode = @Employee; DELETE hrms.AttDeviceLog WHERE DeviceSerial LIKE 'TST-%'", new { Employee });
    }

    public HttpClient Anonymous() => Factory.CreateClient();

    // Removes everything the tests create. Each step runs on its own so one failure cannot hide the others, and failures are reported.
    async Task Cleanup(bool reportErrors)
    {
        var errors = new List<string>();
        await using var c = Open();
        async Task Step(string label, Func<Task> action) { try { await action(); } catch (Exception ex) { errors.Add(label + ": " + ex.Message); } }
        var users = extraUsers.Concat(["TSTADM", "TSTMGR", "TSTLOCK", "TSTPLAIN", "TSTMUST", "TSTOFF"]).Distinct().ToArray();
        var emp = Employee;

        if (emp.Length > 0)
        {
            // leave requests made by the test employee: remove them the ERP way, and their authorisation trail
            await Step("leave", async () =>
            {
                var ids = (await c.QueryAsync<long>("SELECT Id FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id > @maxLeaveId AND EmpCode = @emp", new { maxLeaveId, emp })).ToList();
                foreach (var id in ids)
                {
                    var no = await c.ExecuteScalarAsync<string?>("SELECT Transno FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id = @id", new { id });
                    await c.ExecuteAsync("inter01.SPDELETELeaveDetails", new { Id = id }, commandType: System.Data.CommandType.StoredProcedure);
                    if (no is not null) await c.ExecuteAsync("DELETE inter01.UserwiseTransaction WHERE TransNo = @no; DELETE inter01.EmpAccuralPayment WHERE LTransno = @no", new { no });
                }
            });
            await Step("accruals", () => c.ExecuteAsync("DELETE inter01.EmpAccuralPayment WHERE EmpCode = @emp AND Id > @maxAccrualId", new { emp, maxAccrualId }));
            await Step("trail", () => c.ExecuteAsync("DELETE inter01.UserwiseTransaction WHERE TransNo LIKE '%/' + @emp + '/%' AND DateTime >= DATEADD(day, -1, GETDATE())", new { emp }));
            await Step("punches", () => c.ExecuteAsync("DELETE hrms.AttPunch WHERE EmpCode = @emp", new { emp }));
            await Step("corrections", () => c.ExecuteAsync("DELETE hrms.AttRegularization WHERE EmpCode = @emp", new { emp }));
            await Step("shift assignments", () => c.ExecuteAsync("DELETE hrms.AttShiftAssign WHERE EmpCode = @emp", new { emp }));
        }
        await Step("shifts", () => c.ExecuteAsync("DELETE hrms.AttShiftAssign WHERE ShiftId IN (SELECT Id FROM hrms.AttShift WHERE Name LIKE 'TST%'); DELETE hrms.AttShift WHERE Name LIKE 'TST%'"));
        await Step("holidays", () => c.ExecuteAsync("DELETE hrms.AttHoliday WHERE Name LIKE 'TST%'"));
        await Step("machines", () => c.ExecuteAsync("DELETE hrms.AttDeviceLog WHERE DeviceSerial LIKE 'TST-%'; DELETE hrms.AttDevice WHERE Serial LIKE 'TST-%'; DELETE hrms.AttDeviceUser WHERE Pin LIKE 'TST%'"));
        await Step("machine punches", () => c.ExecuteAsync("DELETE hrms.AttPunch WHERE CreatedBy = 'machine' AND Note LIKE 'TST%'"));
        await Step("notifications", () => c.ExecuteAsync("DELETE hrms.Notification WHERE UserId IN @users", new { users }));
        // events about the test employee also notify real approvers (anyone who can see that employee); remove those from this run too
        if (emp.Length > 0)
            await Step("notifications to real people", async () =>
            {
                var name = await c.ExecuteScalarAsync<string?>("SELECT Empname FROM inter01.EmployeeMaster WHERE EmpCode = @emp", new { emp });
                if (!string.IsNullOrWhiteSpace(name) && startedAt is not null)
                    await c.ExecuteAsync("DELETE hrms.Notification WHERE CreatedAt >= @startedAt AND Body LIKE '%' + @name + '%'; DELETE hrms.EmailOutbox WHERE CreatedAt >= @startedAt AND Body LIKE '%' + @name + '%'", new { startedAt, name });
            });
        await Step("email", () => c.ExecuteAsync("DELETE hrms.EmailOutbox WHERE ToUser IN @users OR ToAddress LIKE '%@example.test'", new { users }));
        await Step("access lists", () => c.ExecuteAsync("DELETE inter01.AccessUser WHERE UserId IN @users", new { users }));
        await Step("users", () => c.ExecuteAsync("DELETE HRMSINTER.interface.Userdef WHERE userid IN @users", new { users }));
        if (reportErrors && errors.Count > 0) throw new InvalidOperationException("Test cleanup problems: " + string.Join(" | ", errors));
    }

    public async Task DisposeAsync()
    {
        try { await Cleanup(reportErrors: true); }
        finally { Factory.Dispose(); }
    }
}

public sealed class Session(HttpClient http, JsonElement user)
{
    public HttpClient Http => http;
    public JsonElement User => user;

    public async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (text.Length > 0 && (text[0] is '{' or '[')) json = JsonDocument.Parse(text).RootElement.Clone();
        return (res.StatusCode, json);
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> Get(string path) => Send(HttpMethod.Get, path);
    public Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, object? body = null) => Send(HttpMethod.Post, path, body ?? new { });
    public Task<(HttpStatusCode Status, JsonElement Body)> Put(string path, object body) => Send(HttpMethod.Put, path, body);
    public Task<(HttpStatusCode Status, JsonElement Body)> Delete(string path) => Send(HttpMethod.Delete, path);
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture> { }
