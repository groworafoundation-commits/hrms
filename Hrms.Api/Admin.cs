using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Administration: users, roles and their rights, menus, and leave approvals. All of it is stored in the same ERP-shaped tables
// (Userdef, Roledef, accessright, interface, AccessUser), so a role edited here behaves the same everywhere.

public record UserDto(string UserId, string UserName, string? FullName, string? RoleId, string? RoleName, string? Email, string? Mobile, bool Disabled, string? EmployeeCode, string? EmployeeName, int Scope, bool Locked = false, bool MustChange = false);
public record UserInput(string UserId, string UserName, string? FullName, string RoleId, string? Password, string? Email, string? Mobile, bool Disabled, string Scope, string? CopyFrom);
public record ScopeInput(string Mode, string? CopyFrom);
public record RoleDto(string RoleId, string RoleName, int AuthLevel, int Users);
public record RoleInput(string RoleId, string RoleName, int AuthLevel, string? CopyFrom);
public record RightDto(string OptionId, string Name, string Group, bool View, bool Add, bool Edit, bool Delete, bool Print, int Level);
public record RightInput(string OptionId, bool View, bool Add, bool Edit, bool Delete, bool Print, int Level);
public record MenuDto(string Id, string Name, string Route, string Icon, int Sequence, bool Active, bool Custom);
public record MenuInput(string Name, string Route, string Icon, int Sequence, bool Active);
public record Plain(string Id, string Name);
public record DecisionInput(string? To, string? Note);
public class ApprovalRow : LeaveRow { public int FromLevel { get; set; } public string? FromUser { get; set; } public string? Note { get; set; } }

public static class Admin
{
    // ERP options an HRMS administrator may grant, next to the HRMS menu options themselves.
    const string ManagedErp = "'137','207','208','209','210','1012','1765','162','164'";
    static readonly Regex RouteOk = new(@"^(https?://\S+|[a-z0-9][a-z0-9/_\-]*)$", RegexOptions.Compiled);

    static AuthUser Caller(HttpContext ctx) => (AuthUser)ctx.Items["user"]!;
    static string? Token(HttpContext ctx) { var h = ctx.Request.Headers.Authorization.ToString(); return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null; }
    static string? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    static async Task SetScope(SqlConnection c, SqlTransaction tx, string userId, string mode, string? copyFrom)
    {
        if (mode == "keep") return;
        await c.ExecuteAsync("DELETE FROM inter01.AccessUser WHERE UserId=@u", new { u = userId }, tx);
        switch (mode)
        {
            case "self":
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmployeeMaster WHERE EmpCode=@u", new { u = userId }, tx) == 0)
                    throw new ApiException("Only their own record is possible when the user ID is an employee code. Choose another access option.");
                await c.ExecuteAsync("INSERT inter01.AccessUser (UserId, AccessUser) VALUES (@u, @u)", new { u = userId }, tx);
                break;
            case "all":
                await c.ExecuteAsync("INSERT inter01.AccessUser (UserId, AccessUser) SELECT @u, EmpCode FROM inter01.EmployeeMaster", new { u = userId }, tx);
                break;
            case "copy":
                if (string.IsNullOrWhiteSpace(copyFrom)) throw new ApiException("Choose the user whose employee access to copy.");
                await c.ExecuteAsync("INSERT inter01.AccessUser (UserId, AccessUser) SELECT @u, AccessUser FROM inter01.AccessUser WHERE UserId=@from", new { u = userId, from = copyFrom }, tx);
                break;
            default: throw new ApiException("Unknown employee access option.");
        }
    }

    public static void MapAdmin(this WebApplication app)
    {
        // ----- users -----
        app.MapGet("/api/admin/users", async (Db db, string? q, string? role) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<UserDto>(@"
SELECT u.userid AS UserId, u.username AS UserName, u.name AS FullName, u.roleid AS RoleId, r.rolename AS RoleName, u.emailid AS Email, u.mobno AS Mobile,
       CAST(ISNULL(u.[disable],0) AS bit) AS Disabled, e.EmpCode AS EmployeeCode, e.Empname AS EmployeeName,
       (SELECT COUNT(*) FROM inter01.AccessUser a WHERE a.UserId = u.userid) AS Scope,
       CAST(CASE WHEN u.LockedUntil > GETDATE() THEN 1 ELSE 0 END AS bit) AS Locked, CAST(ISNULL(u.MustChange,0) AS bit) AS MustChange
FROM HRMSINTER.interface.Userdef u LEFT JOIN HRMSINTER.interface.Roledef r ON r.roleid = u.roleid LEFT JOIN inter01.EmployeeMaster e ON e.EmpCode = u.userid
WHERE (@q IS NULL OR u.userid LIKE @q OR u.username LIKE @q OR u.name LIKE @q OR e.Empname LIKE @q) AND (@role IS NULL OR u.roleid = @role)
ORDER BY u.userid", new { q = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%", role = Nz(role) })).ToList();
        });

        app.MapGet("/api/admin/users/employees-without-login", async (Db db) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<Plain>(@"SELECT e.EmpCode AS Id, e.Empname AS Name FROM inter01.EmployeeMaster e
                WHERE NOT EXISTS (SELECT 1 FROM HRMSINTER.interface.Userdef u WHERE u.userid = e.EmpCode) ORDER BY e.Empname")).ToList();
        });

        app.MapPost("/api/admin/users", async (Db db, HttpContext ctx, UserInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx);
            var id = x.UserId?.Trim() ?? "";
            if (id.Length == 0 || id.Length > 20) throw new ApiException("User ID is required (up to 20 characters).");
            if (string.IsNullOrWhiteSpace(x.UserName)) throw new ApiException("Login name is required.");
            if (Passwords.Policy(x.Password) is { } problem) throw new ApiException(problem);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Userdef WHERE userid=@id OR username=@id OR userid=@un OR username=@un", new { id, un = x.UserName.Trim() }, tx) > 0)
                throw new ApiException("That user ID or login name is already in use.", 409);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Roledef WHERE roleid=@r", new { r = x.RoleId }, tx) == 0) throw new ApiException("Choose a role.");
            await c.ExecuteAsync(@"INSERT HRMSINTER.interface.Userdef (userid, username, roleid, password, PasswordHash, MustChange, PasswordChangedAt, emailid, mobno, name, [disable], CompanyCodes, financialyearcode, Defaultcompany)
                VALUES (@id, @un, @role, NULL, @pw, 1, GETDATE(), @email, @mobile, @name, @dis, @companies, @fy, @company)",
                new { id, un = x.UserName.Trim(), role = x.RoleId, pw = Passwords.Hash(x.Password), email = Nz(x.Email), mobile = Nz(x.Mobile), name = Nz(x.FullName) ?? x.UserName.Trim(), dis = x.Disabled,
                      companies = string.Join(",", me.Companies), fy = me.FinYear, company = me.Company }, tx);
            await SetScope(c, tx, id, string.IsNullOrWhiteSpace(x.Scope) ? "self" : x.Scope, x.CopyFrom);
            return new { UserId = id };
        }));

        app.MapPut("/api/admin/users/{id}", async (Db db, AuthStore store, HttpContext ctx, string id, UserInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx);
            if (string.Equals(id, me.UserId, StringComparison.OrdinalIgnoreCase) && x.Disabled) throw new ApiException("You cannot disable your own account.", 409);
            if (string.IsNullOrWhiteSpace(x.UserName)) throw new ApiException("Login name is required.");
            if (!string.IsNullOrEmpty(x.Password) && Passwords.Policy(x.Password) is { } problem) throw new ApiException(problem);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Userdef WHERE userid=@id", new { id }, tx) == 0) throw new ApiException("User not found.", 404);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Userdef WHERE userid<>@id AND (username=@un OR userid=@un)", new { id, un = x.UserName.Trim() }, tx) > 0)
                throw new ApiException("That login name is already in use.", 409);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Roledef WHERE roleid=@r", new { r = x.RoleId }, tx) == 0) throw new ApiException("Choose a role.");
            await c.ExecuteAsync(@"UPDATE HRMSINTER.interface.Userdef SET username=@un, roleid=@role, emailid=@email, mobno=@mobile, name=@name, [disable]=@dis,
                PasswordHash = CASE WHEN @pw IS NULL THEN PasswordHash ELSE @pw END, password = CASE WHEN @pw IS NULL THEN password ELSE NULL END,
                MustChange = CASE WHEN @pw IS NULL THEN MustChange ELSE 1 END, FailedLogins = CASE WHEN @pw IS NULL THEN FailedLogins ELSE 0 END, LockedUntil = CASE WHEN @pw IS NULL THEN LockedUntil ELSE NULL END WHERE userid=@id",
                new { id, un = x.UserName.Trim(), role = x.RoleId, email = Nz(x.Email), mobile = Nz(x.Mobile), name = Nz(x.FullName) ?? x.UserName.Trim(), dis = x.Disabled, pw = string.IsNullOrEmpty(x.Password) ? null : Passwords.Hash(x.Password) }, tx);
            await SetScope(c, tx, id, string.IsNullOrWhiteSpace(x.Scope) ? "keep" : x.Scope, x.CopyFrom);
            // A changed role, password or disabled flag takes effect straight away: their open sessions end.
            store.RevokeWhere(u => string.Equals(u.UserId, id, StringComparison.OrdinalIgnoreCase), Token(ctx));
            return new { UserId = id };
        }));

        app.MapPost("/api/admin/users/{id}/unlock", async (Db db, string id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (await c.ExecuteAsync("UPDATE HRMSINTER.interface.Userdef SET FailedLogins=0, LockedUntil=NULL WHERE userid=@id", new { id }, tx) == 0) throw new ApiException("User not found.", 404);
            return new { UserId = id };
        }));

        // ----- roles and rights -----
        app.MapGet("/api/admin/roles", async (Db db) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<RoleDto>(@"SELECT r.roleid AS RoleId, r.rolename AS RoleName, CAST(ISNULL(r.authlevel,0) AS int) AS AuthLevel,
                (SELECT COUNT(*) FROM HRMSINTER.interface.Userdef u WHERE u.roleid = r.roleid) AS Users FROM HRMSINTER.interface.Roledef r ORDER BY r.rolename")).ToList();
        });

        app.MapPost("/api/admin/roles", async (Db db, RoleInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var id = x.RoleId?.Trim() ?? "";
            if (id.Length == 0 || id.Length > 8) throw new ApiException("Role ID is required (up to 8 characters).");
            if (string.IsNullOrWhiteSpace(x.RoleName)) throw new ApiException("Role name is required.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Roledef WHERE roleid=@id", new { id }, tx) > 0) throw new ApiException("That role ID already exists.", 409);
            await c.ExecuteAsync("INSERT HRMSINTER.interface.Roledef (roleid, rolename, authlevel, Discount, Transactions) VALUES (@id, @name, @lvl, 0, '0')", new { id, name = x.RoleName.Trim(), lvl = x.AuthLevel }, tx);
            if (!string.IsNullOrWhiteSpace(x.CopyFrom))
                await c.ExecuteAsync(@"INSERT HRMSINTER.interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
                    SELECT @id, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush FROM HRMSINTER.interface.accessright WHERE roleid=@from
                    AND (optionid LIKE 'H%' OR optionid IN (" + ManagedErp + "))", new { id, from = x.CopyFrom }, tx);
            return new { RoleId = id };
        }));

        app.MapPut("/api/admin/roles/{id}", async (Db db, string id, RoleInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (string.IsNullOrWhiteSpace(x.RoleName)) throw new ApiException("Role name is required.");
            if (await c.ExecuteAsync("UPDATE HRMSINTER.interface.Roledef SET rolename=@name, authlevel=@lvl WHERE roleid=@id", new { id, name = x.RoleName.Trim(), lvl = x.AuthLevel }, tx) == 0)
                throw new ApiException("Role not found.", 404);
            return new { RoleId = id };
        }));

        app.MapGet("/api/admin/roles/{id}/rights", async (Db db, string id) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<RightDto>(@"
SELECT i.OPTIONID AS OptionId, i.OPTIONNAME AS Name, CASE WHEN i.LEVELCODE = 'HRMS' THEN 'HRMS' ELSE 'ERP payroll' END AS [Group],
       CAST(ISNULL(a.access,0) AS bit) AS [View], CAST(ISNULL(a.addd,0) AS bit) AS [Add], CAST(ISNULL(a.modifyy,0) AS bit) AS [Edit],
       CAST(ISNULL(a.delete1,0) AS bit) AS [Delete], CAST(ISNULL(a.Prints,0) AS bit) AS [Print], CAST(ISNULL(a.[level],0) AS int) AS [Level]
FROM HRMSINTER.interface.interface i LEFT JOIN HRMSINTER.interface.accessright a ON a.roleid = @id AND a.optionid = i.OPTIONID
WHERE i.LEVELCODE = 'HRMS' OR i.OPTIONID IN (" + ManagedErp + @")
ORDER BY CASE WHEN i.LEVELCODE = 'HRMS' THEN 0 ELSE 1 END, i.SEQUENCE", new { id })).ToList();
        });

        app.MapPut("/api/admin/roles/{id}/rights", async (Db db, AuthStore store, HttpContext ctx, string id, List<RightInput> rights, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Roledef WHERE roleid=@id", new { id }, tx) == 0) throw new ApiException("Role not found.", 404);
            var allowed = (await c.QueryAsync<string>("SELECT OPTIONID FROM HRMSINTER.interface.interface WHERE LEVELCODE='HRMS' OR OPTIONID IN (" + ManagedErp + ")", transaction: tx)).ToHashSet();
            foreach (var r in rights)
            {
                if (!allowed.Contains(r.OptionId)) throw new ApiException($"Option {r.OptionId} cannot be edited here.");
                var p = new { id, opt = r.OptionId, v = r.View, a = r.Add && r.View, m = r.Edit && r.View, d = r.Delete && r.View, pr = r.Print && r.View, lvl = Math.Clamp(r.Level, 0, 10) };
                var n = await c.ExecuteAsync("UPDATE HRMSINTER.interface.accessright SET access=@v, addd=@a, modifyy=@m, delete1=@d, Prints=@pr, [level]=@lvl WHERE roleid=@id AND optionid=@opt", p, tx);
                if (n == 0)
                    await c.ExecuteAsync("INSERT HRMSINTER.interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush) VALUES (@id, @opt, @a, @m, @d, @v, @lvl, @pr, 0)", p, tx);
            }
            // Rights are read when someone signs in, so everyone else in this role signs in again to pick up the change.
            store.RevokeWhere(u => u.RoleId == id, Token(ctx));
            return new { RoleId = id, Updated = rights.Count };
        }));

        // ----- menus -----
        app.MapGet("/api/admin/menus", async (Db db) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<MenuDto>(@"SELECT OPTIONID AS Id, OPTIONNAME AS Name, PRGNAME AS Route, ISNULL(PICTURE1,'grid') AS Icon, ISNULL(SEQUENCE,0) AS Sequence,
                CAST(CASE WHEN ISNULL(ACTIVESTAT,'1') = '1' THEN 1 ELSE 0 END AS bit) AS Active,
                CAST(CASE WHEN TRY_CAST(SUBSTRING(OPTIONID,2,10) AS int) >= 300 THEN 1 ELSE 0 END AS bit) AS Custom
                FROM HRMSINTER.interface.interface WHERE LEVELCODE = 'HRMS' ORDER BY SEQUENCE, OPTIONID")).ToList();
        });

        static void CheckMenu(MenuInput x)
        {
            if (string.IsNullOrWhiteSpace(x.Name)) throw new ApiException("Menu name is required.");
            if (!RouteOk.IsMatch((x.Route ?? "").Trim())) throw new ApiException("Route must be a page path such as my/leave, or a full http(s) link.");
        }

        app.MapPost("/api/admin/menus", async (Db db, MenuInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            CheckMenu(x);
            var next = await c.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(OPTIONID,2,10) AS int)),0) FROM HRMSINTER.interface.interface WHERE OPTIONID LIKE 'H[0-9]%' AND TRY_CAST(SUBSTRING(OPTIONID,2,10) AS int) >= 300", transaction: tx);
            var id = "H" + Math.Max(301, next + 1);
            await c.ExecuteAsync(@"INSERT HRMSINTER.interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
                VALUES (@id, @name, @act, @seq, 'HRMS', '1', @route, '0', 'HRMS', 0, @icon, 0, 0, '', 0, 0, 0)",
                new { id, name = x.Name.Trim(), act = x.Active ? "1" : "0", seq = x.Sequence, route = x.Route.Trim(), icon = Nz(x.Icon) ?? "grid" }, tx);
            return new { Id = id };
        }));

        app.MapPut("/api/admin/menus/{id}", async (Db db, string id, MenuInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            CheckMenu(x);
            if (await c.ExecuteAsync(@"UPDATE HRMSINTER.interface.interface SET OPTIONNAME=@name, ACTIVESTAT=@act, SEQUENCE=@seq, PRGNAME=@route, PICTURE1=@icon WHERE OPTIONID=@id AND LEVELCODE='HRMS'",
                new { id, name = x.Name.Trim(), act = x.Active ? "1" : "0", seq = x.Sequence, route = x.Route.Trim(), icon = Nz(x.Icon) ?? "grid" }, tx) == 0)
                throw new ApiException("Menu not found.", 404);
            return new { Id = id };
        }));

        app.MapDelete("/api/admin/menus/{id}", async (Db db, string id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (!(int.TryParse(id.AsSpan(1), out var n) && n >= 300)) throw new ApiException("Built-in menus can be renamed, re-ordered or switched off, but not deleted.", 409);
            await c.ExecuteAsync("DELETE HRMSINTER.interface.accessright WHERE optionid=@id", new { id }, tx);
            if (await c.ExecuteAsync("DELETE HRMSINTER.interface.interface WHERE OPTIONID=@id AND LEVELCODE='HRMS'", new { id }, tx) == 0) throw new ApiException("Menu not found.", 404);
            return new { Id = id };
        }));

        // ----- leave approvals (the ERP authorisation chain) -----
        app.MapGet("/api/admin/approvals", async (Db db, HttpContext ctx) =>
        {
            await using var c = db.Open();
            var rows = (await c.QueryAsync<ApprovalRow>(@"
SELECT l.Id AS Id, l.EmpCode, e.Empname AS Name, l.LeaveType, l.FromDate, l.ToDate, l.NoofLeavedays AS Days, l.Applieddate AS Applied, l.Rejoiningdate AS Rejoining,
       l.Transno AS TransNo, 'Pending' AS Status, l.Userid AS HeldBy, CAST(ISNULL(l.Userlevel,0) AS int) AS FromLevel,
       (SELECT TOP 1 ISNULL(NULLIF(u.name,''), u.username) FROM HRMSINTER.interface.Userdef u WHERE u.userid =
            (SELECT TOP 1 t.Userid FROM inter01.UserwiseTransaction t WHERE t.TransNo = l.Transno AND t.Userid <> l.Userid ORDER BY t.id DESC)) AS FromUser,
       (SELECT TOP 1 CAST(t.Descr AS nvarchar(max)) FROM inter01.UserwiseTransaction t WHERE t.TransNo = l.Transno AND DATALENGTH(t.Descr) > 0 ORDER BY t.id DESC) AS Note
FROM inter01.EMPLOYEELEAVEDETAILS l JOIN inter01.EmployeeMaster e ON e.EmpCode = l.EmpCode
WHERE l.Userid = @me AND ISNULL(l.Deleteflag,0) = 0 AND ISNULL(l.Userlevel,0) < ISNULL(l.documentlevel,0)
ORDER BY l.Applieddate, l.FromDate", new { me = Caller(ctx).UserId })).ToList();
            foreach (var l in rows) l.LeaveName = LeaveTypes.Name(l.LeaveType);
            return rows;
        });

        static int MyLevel(AuthUser u) => u.Rights.TryGetValue(Workflow.ApprovalOption, out var r) ? r.Level : 0;

        // The request as it sits with the caller; throws if it is not theirs to decide.
        static async Task<(string EmpCode, string TransNo, int DocLevel)> Held(SqlConnection c, SqlTransaction? tx, long id, AuthUser me)
        {
            var row = await c.QueryFirstOrDefaultAsync<(string EmpCode, string TransNo, int DocLevel, string? Holder, int Level)>(
                @"SELECT EmpCode, Transno AS TransNo, CAST(ISNULL(documentlevel,0) AS int) AS DocLevel, Userid AS Holder, CAST(ISNULL(Userlevel,0) AS int) AS Level
                  FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id AND ISNULL(Deleteflag,0)=0", new { id }, tx);
            if (row.TransNo is null) throw new ApiException("That request does not exist.", 404);
            if (row.Level >= row.DocLevel) throw new ApiException("That request is already approved.", 409);
            if (!string.Equals(row.Holder, me.UserId, StringComparison.OrdinalIgnoreCase)) throw new ApiException("That request is not with you.", 403);
            if (string.Equals(row.EmpCode, me.EmployeeCode, StringComparison.OrdinalIgnoreCase)) throw new ApiException("You cannot decide your own leave request.", 403);
            return (row.EmpCode, row.TransNo, row.DocLevel);
        }

        // What the caller can do with a request: approve it for good, hand it up to someone, or send it back to someone who handled it.
        app.MapGet("/api/admin/approvals/{id:long}/options", async (Db db, HttpContext ctx, long id) =>
        {
            var me = Caller(ctx);
            await using var c = db.Open();
            try
            {
                var (emp, transNo, docLevel) = await Held(c, null, id, me);
                var level = MyLevel(me);
                var push = level >= docLevel ? [] : await Workflow.Approvers(c, null, level, me.UserId, emp);
                var back = (await c.QueryAsync<Approver>(@"
SELECT t.Userid AS Id, ISNULL(NULLIF(MAX(u.name),''), MAX(u.username)) AS Name, MAX(r.rolename) AS RoleName, CAST(MAX(t.Auth) AS int) AS Level
FROM inter01.UserwiseTransaction t JOIN HRMSINTER.interface.Userdef u ON u.userid = t.Userid LEFT JOIN HRMSINTER.interface.Roledef r ON r.roleid = u.roleid
WHERE t.TransNo = @transNo AND t.Userid <> @me AND t.Userid <> @emp GROUP BY t.Userid ORDER BY MAX(t.Auth)", new { transNo, me = me.UserId, emp })).ToList();
                return Results.Ok(new { final = level >= docLevel, level, docLevel, push, back });
            }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapPost("/api/admin/approvals/{id:long}/approve", async (Db db, HttpContext ctx, long id, DecisionInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx);
            var (emp, transNo, docLevel) = await Held(c, tx, id, me);
            var level = MyLevel(me);
            Approver? next = null;
            if (level < docLevel)
            {
                if (string.IsNullOrWhiteSpace(x.To)) throw new ApiException("Choose who this goes to next.");
                next = (await Workflow.Approvers(c, tx, level, me.UserId, emp)).FirstOrDefault(a => a.Id == x.To) ?? throw new ApiException("That person cannot approve leave requests above you.");
            }
            await c.ExecuteAsync("UPDATE inter01.EMPLOYEELEAVEDETAILS SET Userlevel=@level WHERE Id=@id", new { id, level }, tx);
            await Workflow.Log(c, tx, transNo, me.UserId, level, Nz(x.Note));
            if (next is not null) await Workflow.Push(c, tx, id, transNo, next);
            if (await Workflow.Describe(c, tx, id) is { } info)
                await Notifier.Send(c, tx, emp, "leave", next is null ? "Your leave was approved" : "Your leave request moved on",
                    Workflow.Summary(info) + (next is null ? "" : ". It is now with " + next.Name + "."), "my/leave");
            return new { Id = id, Final = next is null, With = next?.Name };
        }));

        app.MapPost("/api/admin/approvals/{id:long}/return", async (Db db, HttpContext ctx, long id, DecisionInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx);
            var (emp, transNo, _) = await Held(c, tx, id, me);
            if (string.IsNullOrWhiteSpace(x.Note)) throw new ApiException("Write a note for the person you are sending this back to.");
            var back = await c.QueryFirstOrDefaultAsync<Approver>(@"
SELECT t.Userid AS Id, ISNULL(NULLIF(MAX(u.name),''), MAX(u.username)) AS Name, '' AS RoleName, CAST(MAX(t.Auth) AS int) AS Level
FROM inter01.UserwiseTransaction t JOIN HRMSINTER.interface.Userdef u ON u.userid = t.Userid
WHERE t.TransNo = @transNo AND t.Userid = @to AND t.Userid <> @me AND t.Userid <> @emp GROUP BY t.Userid", new { transNo, to = x.To, me = me.UserId, emp }, tx)
                ?? throw new ApiException("Choose someone who handled this request earlier.");
            await Workflow.Push(c, tx, id, transNo, back, "Sent back by " + me.FullName + ": " + x.Note.Trim());
            return new { Id = id, With = back.Name };
        }));

        app.MapPost("/api/admin/approvals/{id:long}/reject", async (Db db, HttpContext ctx, long id, DecisionInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx);
            var (emp, transNo, _) = await Held(c, tx, id, me);
            if (string.IsNullOrWhiteSpace(x.Note)) throw new ApiException("Write the reason for rejecting this request.");
            if (await Workflow.Describe(c, tx, id) is { } info)
                await Notifier.Send(c, tx, emp, "leave", "Your leave request was rejected", Workflow.Summary(info) + ". Reason: " + x.Note.Trim(), "my/leave");
            await Workflow.Log(c, tx, transNo, me.UserId, MyLevel(me), "Rejected: " + x.Note.Trim());
            await c.ExecuteAsync("inter01.SPDELETELeaveDetails", new { Id = id }, tx, commandType: System.Data.CommandType.StoredProcedure);
            await Workflow.RemoveAccruals(c, tx, transNo);
            return new { Id = id };
        }));
    }
}
