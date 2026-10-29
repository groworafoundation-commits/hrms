using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Xunit;

namespace Hrms.Tests;

[Collection("api")]
public class LoginTests(ApiFixture f)
{
    async Task<HttpResponseMessage> TryLogin(string user, string password) => await f.Anonymous().PostAsJsonAsync("/api/auth/login", new { user, password });

    [Fact]
    public async Task Correct_password_signs_in_with_the_menu_and_levels_for_the_role()
    {
        var emp = await f.LoginAs(f.Employee);
        var menu = emp.User.GetProperty("menu").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("My Leave", menu);
        Assert.Contains("My Attendance", menu);
        Assert.DoesNotContain("Users", menu);                                            // an employee never gets the admin menus
        Assert.Equal(8, emp.User.GetProperty("rights").GetProperty("H102").GetProperty("level").GetInt32());
        Assert.Equal(f.Employee, emp.User.GetProperty("employeeCode").GetString());

        var adm = await f.LoginAs(ApiFixture.Admin);
        Assert.Equal(9, adm.User.GetProperty("rights").GetProperty("H201").GetProperty("level").GetInt32());
        var mgr = await f.LoginAs(ApiFixture.Manager);
        Assert.Equal(10, mgr.User.GetProperty("rights").GetProperty("H201").GetProperty("level").GetInt32());
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_answer()
    {
        var wrong = await TryLogin(ApiFixture.Manager, "wrong-password-1");
        var unknown = await TryLogin("nobody-by-this-name", "wrong-password-1");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());   // nothing reveals which part was wrong
    }

    [Fact]
    public async Task Calls_without_a_token_or_with_a_made_up_token_are_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous().GetAsync("/api/dashboard")).StatusCode);
        var http = f.Anonymous();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "0000000000000000");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/employees")).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_until_an_administrator_unlocks_it()
    {
        await using (var c = f.Open()) await f.CreateUser(c, "TSTLOCK", "HRM-EMP", "self", null);
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin("TSTLOCK", "wrong-pass-1")).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await TryLogin("TSTLOCK", "wrong-pass-1")).StatusCode);                 // the fifth attempt locks it
        Assert.Equal((HttpStatusCode)423, (await TryLogin("TSTLOCK", ApiFixture.Password)).StatusCode);            // even the right password is refused now

        var adm = await f.LoginAs(ApiFixture.Admin);
        var list = await adm.Get("/api/admin/users?q=TSTLOCK");
        Assert.True(list.Body[0].GetProperty("locked").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await adm.Post("/api/admin/users/TSTLOCK/unlock")).Status);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("TSTLOCK", ApiFixture.Password)).StatusCode);
    }

    [Fact]
    public async Task A_success_clears_the_failed_attempt_counter()
    {
        await using var c = f.Open();
        await f.CreateUser(c, "TSTOFF", "HRM-EMP", "self", null);
        for (var i = 0; i < 3; i++) await TryLogin("TSTOFF", "wrong-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("TSTOFF", ApiFixture.Password)).StatusCode);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT FailedLogins FROM HRMSINTER.interface.Userdef WHERE userid = 'TSTOFF'"));
    }

    [Fact]
    public async Task A_temporary_password_must_be_changed_before_anything_else_works()
    {
        var adm = await f.LoginAs(ApiFixture.Admin);
        var weak = await adm.Post("/api/admin/users", new { userId = "TSTMUST", userName = "TSTMUST", fullName = "Test Must", roleId = "HRM-ADM", password = "short", scope = "all", disabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, weak.Status);                                                    // the password policy applies to administrators too
        var created = await adm.Post("/api/admin/users", new { userId = "TSTMUST", userName = "TSTMUST", fullName = "Test Must", roleId = "HRM-ADM", password = "Temp1234", scope = "all", disabled = false });
        Assert.Equal(HttpStatusCode.OK, created.Status);

        await using (var c = f.Open())
            Assert.Equal("hash", await c.ExecuteScalarAsync<string>("SELECT CASE WHEN password IS NULL AND PasswordHash LIKE 'PBKDF2%' THEN 'hash' ELSE 'plain' END FROM HRMSINTER.interface.Userdef WHERE userid = 'TSTMUST'"));

        var s = await f.LoginAs("TSTMUST", "Temp1234");
        Assert.True(s.User.GetProperty("mustChange").GetBoolean());
        var blocked = await s.Get("/api/notifications");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.Status);
        Assert.Equal("password_change_required", blocked.Body.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/auth/change-password", new { current = "not-my-password", newPassword = "Better5678" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/auth/change-password", new { current = "Temp1234", newPassword = "weak" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await s.Post("/api/auth/change-password", new { current = "Temp1234", newPassword = "Better5678" })).Status);

        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/notifications")).Status);                              // unblocked on the same session
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin("TSTMUST", "Temp1234")).StatusCode);          // the old password is dead
        var again = await f.LoginAs("TSTMUST", "Better5678");
        Assert.False(again.User.GetProperty("mustChange").GetBoolean());
    }

    [Fact]
    public async Task A_password_still_stored_as_plain_text_is_hashed_on_first_sign_in()
    {
        await using var c = f.Open();
        await f.CreateUser(c, "TSTPLAIN", "HRM-EMP", "self", null, "Legacy1234", plainOnly: true);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("TSTPLAIN", "Legacy1234")).StatusCode);
        var row = await c.QuerySingleAsync("SELECT password AS Plain, PasswordHash AS Hash FROM HRMSINTER.interface.Userdef WHERE userid = 'TSTPLAIN'");
        Assert.Null((string?)row.Plain);
        Assert.StartsWith("PBKDF2$", (string)row.Hash);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin("TSTPLAIN", "Legacy1234")).StatusCode);                  // and it still works afterwards
    }

    [Fact]
    public async Task No_plain_text_passwords_remain_in_the_user_table()
    {
        await using var c = f.Open();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM HRMSINTER.interface.Userdef WHERE userid NOT LIKE 'TST%' AND password IS NOT NULL AND LEN(password) > 0"));
    }

    [Fact]
    public async Task Roles_hold_people_to_their_rights()
    {
        var emp = await f.LoginAs(f.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/employees")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/admin/approvals")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/admin/attendance/today")).Status);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Get("/api/admin/users")).Status);                       // a manager has no user administration
        Assert.Equal(HttpStatusCode.OK, (await mgr.Get("/api/admin/approvals")).Status);
    }
}
