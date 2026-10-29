using Dapper;
using Hrms.Api;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<HrmsSession>();
builder.Services.AddSingleton<AuthStore>();
builder.Services.AddHostedService<EmailSender>();
builder.Services.AddSingleton(new Db(builder.Configuration.GetConnectionString("Main")
    ?? throw new InvalidOperationException("ConnectionStrings:Main is not configured. Use dotnet user-secrets.")));

// HTTPS: when a certificate is configured the API serves HTTPS on 5443 for the app and keeps HTTP on 5080 only for punching machines.
// It listens on this computer only unless Hrms:ListenOn is "any".
var certPath = builder.Configuration["Hrms:CertPath"];
if (!string.IsNullOrEmpty(certPath))
{
    var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certPath, builder.Configuration["Hrms:CertPassword"]);
    var any = string.Equals(builder.Configuration["Hrms:ListenOn"], "any", StringComparison.OrdinalIgnoreCase);
    builder.WebHost.ConfigureKestrel(k =>
    {
        if (any) { k.ListenAnyIP(5080); k.ListenAnyIP(5443, o => o.UseHttps(cert)); }
        else { k.ListenLocalhost(5080); k.ListenLocalhost(5443, o => o.UseHttps(cert)); }
    });
}

var app = builder.Build();
await Passwords.MigrateLegacy(app.Services.GetRequiredService<Db>());

if (!string.IsNullOrEmpty(certPath))
    app.Use(async (ctx, next) =>
    {
        // plain HTTP is refused for the API unless the caller is this same computer (that traffic never leaves it)
        if (!ctx.Request.IsHttps && !(ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)) && (ctx.Request.Path.Value ?? "").StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 426;
            await ctx.Response.WriteAsJsonAsync(new { error = "This service must be used over HTTPS." });
            return;
        }
        await next();
    });
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuth();
app.MapAuth();


app.MapGet("/api/dashboard", async (Db db) =>
{
    await using var c = db.Open();

    var kpi = await c.QuerySingleAsync<Kpis>(Sql.Kpis);
    var week = (await c.QueryAsync<LeaveDay>(Sql.LeaveWeek)).ToList();
    var designations = (await c.QueryAsync<NameCount>(Sql.Designations)).ToList();
    var birthdays = (await c.QueryAsync<Occasion>(Sql.Birthdays)).ToList();
    var anniversaries = (await c.QueryAsync<Occasion>(Sql.Anniversaries)).ToList();
    var recentLeave = (await c.QueryAsync<LeaveRow>(Sql.LeaveRecent)).ToList();
    var joiners = (await c.QueryAsync<Person>(Sql.NewJoiners)).ToList();
    var payroll = (await c.QueryAsync<PayrollMonth>(Sql.PayrollMonths)).ToList();

    foreach (var l in recentLeave) l.LeaveName = LeaveTypes.Name(l.LeaveType);
    return new Dashboard(kpi, week, designations, birthdays, anniversaries, recentLeave, joiners, payroll);
});

app.MapGet("/api/employees", async (Db db, string? q, string? status, int page = 1, int size = 25) =>
{
    await using var c = db.Open();
    var p = new { q = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%", status = status ?? "active", skip = (Math.Max(page, 1) - 1) * size, size };
    var rows = (await c.QueryAsync<EmployeeRow>(Sql.Employees, p)).ToList();
    var total = rows.Count > 0 ? rows[0].Total : 0;
    return new Paged<EmployeeRow>(rows, total, page, size);
});

app.MapGet("/api/employees/{code}", async (Db db, string code) =>
{
    await using var c = db.Open();
    var emp = await c.QuerySingleOrDefaultAsync<EmployeeRow>(Sql.Employee, new { code });
    if (emp is null) return Results.NotFound();
    var leave = (await c.QueryAsync<LeaveRow>(Sql.LeaveForEmployee, new { code })).ToList();
    foreach (var l in leave) l.LeaveName = LeaveTypes.Name(l.LeaveType);
    var pay = (await c.QueryAsync<PayslipRow>(Sql.PayrollForEmployee, new { code })).ToList();
    return Results.Ok(new EmployeeDetail(emp, leave, pay));
});

app.MapGet("/api/leave", async (Db db, string? type, DateTime? from, DateTime? to, string? q) =>
{
    await using var c = db.Open();
    var start = from ?? DateTime.Today.AddDays(-30);
    var end = to ?? DateTime.Today.AddDays(60);
    var rows = (await c.QueryAsync<LeaveRow>(Sql.Leave, new { type = string.IsNullOrWhiteSpace(type) ? null : type, start, end, q = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%" })).ToList();
    foreach (var l in rows) l.LeaveName = LeaveTypes.Name(l.LeaveType);
    return rows;
});

app.MapGet("/api/payroll/months", async (Db db) =>
{
    await using var c = db.Open();
    return (await c.QueryAsync<PayrollMonth>(Sql.PayrollMonths)).ToList();
});

app.MapGet("/api/payroll", async (Db db, string? month) =>
{
    await using var c = db.Open();
    month ??= await c.ExecuteScalarAsync<string>("SELECT TOP 1 CONVERT(varchar(7),ProcessedDate,120) FROM inter01.EmployeePaymentProcess ORDER BY ProcessedDate DESC");
    var rows = (await c.QueryAsync<PayslipRow>(Sql.PayrollForMonth, new { month })).ToList();
    return new PayrollRun(month ?? "", rows, rows.Sum(r => r.Basic), rows.Sum(r => r.Addition), rows.Sum(r => r.Deduction), rows.Sum(r => r.Net));
});

static object? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
static object? Like(string? v) => string.IsNullOrWhiteSpace(v) ? null : $"%{v.Trim()}%";

app.MapGet("/api/payroll/structure", async (Db db, string? q, string? status) =>
{
    await using var c = db.Open();
    return (await c.QueryAsync<StructureRow>(Sql.Structure, new { q = Like(q), status = status ?? "active" })).ToList();
});

app.MapGet("/api/payroll/entries", async (Db db, string group, string? type, string? code, string? q, DateTime? from, DateTime? to) =>
{
    await using var c = db.Open();
    var p = new { loans = group == "loans" ? 1 : 0, type = Nz(type), code = Nz(code), q = Like(q), from = from ?? DateTime.Today.AddMonths(-6), to = to ?? DateTime.Today.AddMonths(6) };
    var rows = (await c.QueryAsync<EntryRow>(Sql.EntryRows, p)).ToList();
    var sum = (await c.QueryAsync<TypeTotal>(Sql.EntrySummary, p)).ToList();
    foreach (var r in rows) r.TypeName = HeadNames.Name(r.Type);
    foreach (var s in sum) s.TypeName = HeadNames.Name(s.Type);
    return new EntryReport(sum, rows);
});

app.MapGet("/api/payroll/loans", async (Db db, string? q, string? status) =>
{
    await using var c = db.Open();
    return (await c.QueryAsync<LoanBalance>(Sql.LoanBalances, new { q = Like(q), status = status ?? "all" })).ToList();
});

app.MapGet("/api/payroll/accruals", async (Db db, string? type, string? code, string? q) =>
{
    await using var c = db.Open();
    var p = new { type = Nz(type), code = Nz(code), q = Like(q) };
    var rows = (await c.QueryAsync<AccrualRow>(Sql.AccrualRows, p)).ToList();
    var sum = (await c.QueryAsync<TypeTotal>(Sql.AccrualSummary, p)).ToList();
    foreach (var r in rows) r.TypeName = HeadNames.Name(r.Type);
    foreach (var s in sum) s.TypeName = HeadNames.Name(s.Type);
    return new AccrualReport(sum, rows);
});

app.MapGet("/api/payroll/transfers", async (Db db, string? q, string? code) =>
{
    await using var c = db.Open();
    return (await c.QueryAsync<TransferRow>(Sql.Transfers, new { q = Like(q), code = Nz(code) })).ToList();
});

app.MapGet("/api/payroll/heads", async (Db db) =>
{
    await using var c = db.Open();
    var rows = (await c.QueryAsync<HeadRow>(Sql.Heads)).ToList();
    foreach (var r in rows) r.Name = HeadNames.Name(r.Code);
    return rows;
});

app.MapGet("/api/leave/summary", async (Db db, int? year) =>
{
    await using var c = db.Open();
    var rows = (await c.QueryAsync<LeaveSummaryRow>(Sql.LeaveSummary, new { year = year ?? DateTime.Today.Year })).ToList();
    return rows.Select(r => r with { TypeName = LeaveTypes.Name(r.Type) }).ToList();
});

Writes.ForceDryRun = builder.Configuration.GetValue<bool>("Hrms:DryRunAll");
app.MapWrites();
app.MapSelf();
app.MapAdmin();
app.MapAttendance();
app.MapDevices();
app.MapNotifications();
app.MapLeaveBalances();
app.MapReports();

// any other address is a page of the web app (API calls are answered by the sign-in check first)
app.MapFallbackToFile("index.html");
app.Run();


// lets the automated tests start the API in memory
public partial class Program { }
