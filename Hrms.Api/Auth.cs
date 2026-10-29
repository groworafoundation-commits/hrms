using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dapper;

namespace Hrms.Api;

// Login and access. Users, roles and rights come from the ERP (HRMSINTER.interface.Userdef / SELECTMENUOPTION), so an
// account that works in the ERP works here with the same menu rights. Which employees a user may see comes from
// LaunchMain inter01.accessuser, exactly as the ERP HR screens filter them.

public static class CurrentUser
{
    static readonly AsyncLocal<string?> id = new();
    public static string? Id { get => id.Value; set => id.Value = value; }

    // Set for self-service calls: the data layer then exposes only this employee, whatever the user's access list says.
    static readonly AsyncLocal<string?> self = new();
    public static string? SelfCode { get => self.Value; set => self.Value = value; }
}

public record OptionRight(string OptionId, bool View, bool Add, bool Edit, bool Delete, bool Print, int Level);
public record LoginInput(string User, string Password);
public record ChangePasswordInput(string Current, string NewPassword);
public record MenuItem(string Id, string Name, string Route, string Icon, int Sequence);
public record AuthUser(string UserId, string UserName, string FullName, string RoleId, string RoleName, string Company, string FinYear, List<string> Companies,
    Dictionary<string, OptionRight> Rights, int EmployeeScope, bool IsAdmin, string? EmployeeCode, List<MenuItem> Menu, bool MustChange = false);
public record LoginResult(string Token, AuthUser User);

sealed class UserRow
{
    public string UserId { get; set; } = "";
    public string? UserName { get; set; }
    public string? Name { get; set; }
    public string? Password { get; set; }
    public string? PasswordHash { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    public bool MustChange { get; set; }
    public string? RoleId { get; set; }
    public string? CompanyCodes { get; set; }
    public string? FinancialYearCode { get; set; }
    public string? DefaultCompany { get; set; }
}

sealed class RightRow
{
    public string OptionId { get; set; } = "";
    public string? Access { get; set; }
    public bool Addd { get; set; }
    public bool Modifyy { get; set; }
    public bool Delete1 { get; set; }
    public bool Prints { get; set; }
    public string? Level { get; set; }
}

public sealed class AuthStore
{
    readonly ConcurrentDictionary<string, (AuthUser User, DateTime Expires)> tokens = new();

    public string Issue(AuthUser u)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tokens[token] = (u, DateTime.UtcNow.AddHours(12));
        return token;
    }

    public AuthUser? Find(string? token)
    {
        if (string.IsNullOrEmpty(token) || !tokens.TryGetValue(token, out var t)) return null;
        if (t.Expires < DateTime.UtcNow) { tokens.TryRemove(token, out _); return null; }
        return t.User;
    }

    public void Replace(string token, AuthUser user) { if (tokens.TryGetValue(token, out var t)) tokens[token] = (user, t.Expires); }

    public void Revoke(string? token) { if (token is not null) tokens.TryRemove(token, out _); }

    // Sign out everyone matching, except the token that made the change.
    public void RevokeWhere(Func<AuthUser, bool> match, string? except = null)
    {
        foreach (var kv in tokens)
            if (kv.Key != except && match(kv.Value.User)) tokens.TryRemove(kv.Key, out _);
    }
}

public static class AuthEndpoints
{
    // ERP option ids for the HR screens this app surfaces, and the API route prefix each one guards.
    public static readonly (string Prefix, string Option)[] Guards =
    [
        ("/api/employees/", "207"),       // extra details, structure and transfers are refined below
        ("/api/employees", "137"),
        ("/api/leave", "210"),
        ("/api/payroll/loans", "1765"),
        ("/api/payroll/accruals", "1765"),
        ("/api/payroll/variable", "209"),
        ("/api/payroll/structure", "208"),
        ("/api/payroll/transfers", "1012"),
    ];

    static string? OptionFor(string path)
    {
        if (path.StartsWith("/api/me/attendance", StringComparison.OrdinalIgnoreCase)) return "H106";
        if (path.StartsWith("/api/admin/email", StringComparison.OrdinalIgnoreCase)) return "H209";
        if (path.StartsWith("/api/admin/attendance/devices", StringComparison.OrdinalIgnoreCase)) return "H208";
        if (path.StartsWith("/api/admin/attendance/requests", StringComparison.OrdinalIgnoreCase)) return "H206";
        if (path.StartsWith("/api/admin/attendance/setup", StringComparison.OrdinalIgnoreCase)) return "H207";
        if (path.StartsWith("/api/admin/attendance", StringComparison.OrdinalIgnoreCase)) return "H205";
        if (path.StartsWith("/api/me/leave", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/me/approvers", StringComparison.OrdinalIgnoreCase)) return "H102";
        if (path.StartsWith("/api/me/calendar", StringComparison.OrdinalIgnoreCase)) return "H103";
        if (path.StartsWith("/api/me/payslip", StringComparison.OrdinalIgnoreCase)) return "H104";
        if (path.StartsWith("/api/me", StringComparison.OrdinalIgnoreCase)) return "H105";
        if (path.StartsWith("/api/admin/approvals", StringComparison.OrdinalIgnoreCase)) return "H201";
        if (path.StartsWith("/api/admin/users", StringComparison.OrdinalIgnoreCase)) return "H202";
        if (path.StartsWith("/api/admin/roles", StringComparison.OrdinalIgnoreCase)) return "H203";
        if (path.StartsWith("/api/admin/menus", StringComparison.OrdinalIgnoreCase)) return "H204";
        if (path.Contains("/structure", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/employees/", StringComparison.OrdinalIgnoreCase)) return "208";
        if (path.Contains("/transfers", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/employees/", StringComparison.OrdinalIgnoreCase)) return "1012";
        foreach (var (prefix, option) in Guards)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return option;
        return null;
    }

    public static void UseAuth(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<AuthStore>();
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase) || HttpMethods.IsOptions(ctx.Request.Method))
            {
                await next();
                return;
            }

            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
            var user = store.Find(token);
            if (user is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Please sign in." });
                return;
            }

            if (user.MustChange && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "Please change your password first.", code = "password_change_required" });
                return;
            }

            var option = OptionFor(path);
            if (option is not null && !user.IsAdmin)
            {
                user.Rights.TryGetValue(option, out var r);
                var write = !HttpMethods.IsGet(ctx.Request.Method);
                var allowed = r is not null && r.View && (!write || (HttpMethods.IsPost(ctx.Request.Method) ? r.Add : HttpMethods.IsDelete(ctx.Request.Method) ? r.Delete : r.Edit));
                if (!allowed)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Your role does not have access to this." });
                    return;
                }
            }

            if (path.StartsWith("/api/me", StringComparison.OrdinalIgnoreCase))
            {
                if (user.EmployeeCode is null)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Your login is not linked to an employee record." });
                    return;
                }
                CurrentUser.SelfCode = user.EmployeeCode;
            }

            var session = ctx.RequestServices.GetRequiredService<HrmsSession>();
            session.UserId = user.UserId;
            if (option is not null && user.Rights.TryGetValue(option, out var right) && right.Level > 0) session.UserLevel = right.Level;
            ctx.Items["user"] = user;
            CurrentUser.Id = user.UserId;
            await next();
        });
    }

    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (Db db, AuthStore store, LoginInput x) =>
        {
            var login = (x.User ?? "").Trim();
            if (login.Length == 0 || string.IsNullOrEmpty(x.Password)) return Results.BadRequest(new { error = "Enter your user name and password." });

            await using var c = db.Open();
            var u = await c.QueryFirstOrDefaultAsync<UserRow>(@"
SELECT TOP 1 userid AS UserId, username AS UserName, name AS Name, password AS Password, PasswordHash, ISNULL(FailedLogins,0) AS FailedLogins, LockedUntil, CAST(ISNULL(MustChange,0) AS bit) AS MustChange,
       roleid AS RoleId, CompanyCodes, financialyearcode AS FinancialYearCode, Defaultcompany AS DefaultCompany
FROM HRMSINTER.interface.Userdef WHERE (username = @login OR userid = @login) AND ISNULL([disable],0) = 0", new { login });

            if (u?.LockedUntil is { } until && until > DateTime.Now)
                return Results.Json(new { error = $"Too many failed attempts. Try again in {Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes))} minutes, or ask an administrator to unlock you." }, statusCode: 423);

            // One message for every failure so the form never reveals which part was wrong; the same work is done either way.
            if (!Passwords.VerifyAny(x.Password, u?.PasswordHash, u?.Password, u is not null))
            {
                if (u is not null)
                {
                    var fails = u.FailedLogins + 1;
                    var locks = fails >= Passwords.MaxFailed;
                    await c.ExecuteAsync("UPDATE HRMSINTER.interface.Userdef SET FailedLogins=@fails, LockedUntil=@until WHERE userid=@id",
                        new { id = u.UserId, fails = locks ? 0 : fails, until = locks ? DateTime.Now.AddMinutes(Passwords.LockMinutes) : (DateTime?)null });
                    if (locks) return Results.Json(new { error = $"Too many failed attempts. This account is locked for {Passwords.LockMinutes} minutes." }, statusCode: 423);
                }
                return Results.Json(new { error = "Incorrect user name or password." }, statusCode: 401);
            }
            // success: clear the counters, and move a not-yet-migrated plain password to a hash
            await c.ExecuteAsync(u.PasswordHash is null
                ? "UPDATE HRMSINTER.interface.Userdef SET FailedLogins=0, LockedUntil=NULL, LastLogin=GETDATE(), PasswordHash=@hash, password=NULL, PasswordChangedAt=GETDATE() WHERE userid=@id"
                : "UPDATE HRMSINTER.interface.Userdef SET FailedLogins=0, LockedUntil=NULL, LastLogin=GETDATE() WHERE userid=@id",
                new { id = u.UserId, hash = u.PasswordHash is null ? Passwords.Hash(x.Password) : null });

            var rows = (await c.QueryAsync<RightRow>(@"
SELECT r.optionid AS OptionId, CAST(r.access AS int) AS Access, CAST(ISNULL(r.addd,0) AS bit) AS Addd, CAST(ISNULL(r.modifyy,0) AS bit) AS Modifyy, CAST(ISNULL(r.delete1,0) AS bit) AS Delete1,
       CAST(ISNULL(r.Prints,0) AS bit) AS Prints, CAST(r.[level] AS varchar(10)) AS Level
FROM HRMSINTER.interface.accessright r WHERE r.roleid = @role AND (r.optionid IN ('137','207','208','209','210','1012','1765','162','164') OR r.optionid LIKE 'H%')", new { role = u.RoleId })).ToList();

            var rights = rows.ToDictionary(r => r.OptionId.Trim(), r => new OptionRight(r.OptionId.Trim(), r.Access == "1", r.Addd, r.Modifyy, r.Delete1, r.Prints, int.TryParse(r.Level, out var l) ? l : 0));

            // Any report option the role holds opens the Reports hub (the ERP keeps each payroll report as its own option).
            var reportAccess = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM HRMSINTER.interface.accessright r JOIN HRMSINTER.interface.interface i ON i.OPTIONID = r.optionid
WHERE r.roleid = @role AND CAST(r.access AS int) = 1 AND i.LEVELCODE = '603'", new { role = u.RoleId });
            if (reportAccess > 0 && !rights.ContainsKey("162")) rights["162"] = new OptionRight("162", true, false, false, false, true, 10);

            var menu = (await c.QueryAsync<MenuItem>(@"
SELECT i.OPTIONID AS Id, i.OPTIONNAME AS Name, i.PRGNAME AS Route, ISNULL(i.PICTURE1,'grid') AS Icon, ISNULL(i.SEQUENCE,0) AS Sequence
FROM HRMSINTER.interface.interface i JOIN HRMSINTER.interface.accessright r ON r.optionid = i.OPTIONID
WHERE r.roleid = @role AND CAST(r.access AS int) = 1 AND i.LEVELCODE = 'HRMS' AND ISNULL(i.ACTIVESTAT,'1') = '1' ORDER BY i.SEQUENCE", new { role = u.RoleId })).ToList();
            var employeeCode = await c.ExecuteScalarAsync<string?>("SELECT TOP 1 EmpCode FROM inter01.EmployeeMaster WHERE EmpCode = @id", new { id = u.UserId });
            // The "My ..." pages are about the signed-in person's own employee record; a login without one has nothing to show there.
            if (employeeCode is null) menu = menu.Where(m => !m.Route.StartsWith("my", StringComparison.OrdinalIgnoreCase)).ToList();

            var scope = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.accessuser WHERE userid = @id", new { id = u.UserId });
            var roleName = await c.ExecuteScalarAsync<string>("SELECT TOP 1 rolename FROM HRMSINTER.interface.Roledef WHERE roleid = @role", new { role = u.RoleId });
            var companies = (u.CompanyCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            const bool isAdmin = false; // every role is held to its accessright rows
            var user = new AuthUser(u.UserId, u.UserName ?? u.UserId, u.Name ?? u.UserName ?? u.UserId, u.RoleId ?? "", roleName ?? u.RoleId ?? "", u.DefaultCompany ?? companies.FirstOrDefault() ?? "",
                u.FinancialYearCode ?? DateTime.Today.Year.ToString(), companies, rights, scope, isAdmin, employeeCode, menu, u.MustChange);
            return Results.Ok(new LoginResult(store.Issue(user), user));
        });

        app.MapPost("/api/auth/change-password", async (Db db, AuthStore store, HttpContext ctx, ChangePasswordInput x) =>
        {
            var me = (AuthUser)ctx.Items["user"]!;
            if (string.IsNullOrEmpty(x.Current)) return Results.BadRequest(new { error = "Enter your current password." });
            if (Passwords.Policy(x.NewPassword) is { } problem) return Results.BadRequest(new { error = problem });
            if (x.NewPassword == x.Current) return Results.BadRequest(new { error = "Choose a password you have not used just now." });
            await using var c = db.OpenWrite();
            var row = await c.QueryFirstOrDefaultAsync<UserRow>("SELECT PasswordHash, password AS Password FROM HRMSINTER.interface.Userdef WHERE userid=@id", new { id = me.UserId });
            if (row is null || !Passwords.VerifyAny(x.Current, row.PasswordHash, row.Password, true)) return Results.Json(new { error = "Your current password is not correct." }, statusCode: 400);
            await c.ExecuteAsync("UPDATE HRMSINTER.interface.Userdef SET PasswordHash=@hash, password=NULL, MustChange=0, FailedLogins=0, LockedUntil=NULL, PasswordChangedAt=GETDATE() WHERE userid=@id", new { id = me.UserId, hash = Passwords.Hash(x.NewPassword) });
            var h = ctx.Request.Headers.Authorization.ToString();
            var token = h[7..].Trim();
            store.Replace(token, me with { MustChange = false });
            store.RevokeWhere(u => string.Equals(u.UserId, me.UserId, StringComparison.OrdinalIgnoreCase), token);   // any other open sessions end
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/auth/me", (HttpContext ctx) => ctx.Items["user"] is AuthUser u ? Results.Ok(u) : Results.Unauthorized());

        app.MapPost("/api/auth/logout", (HttpContext ctx, AuthStore store) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            store.Revoke(h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null);
            return Results.Ok();
        });
    }
}
