using System.Net;
using System.Text.Json;
using Dapper;
using Hrms.Api;
using Xunit;

namespace Hrms.Tests;

// The administrator role is at the document level (10) for assets, so it approves handovers itself; and whoever prepared a waiting request can withdraw it.
[Collection("api")]
public class AssetLevelTests(ApiFixture f) : IAsyncLifetime
{
    public async Task InitializeAsync() => await Cleanup();
    public async Task DisposeAsync() => await Cleanup();

    async Task Cleanup()
    {
        await using var c = f.Open();
        await c.ExecuteAsync(@"DELETE hrms.AssetFile WHERE AssetId IN (SELECT Id FROM hrms.Asset WHERE AssetCode LIKE 'TST-%');
                               DELETE hrms.AssetTransfer WHERE AssetId IN (SELECT Id FROM hrms.Asset WHERE AssetCode LIKE 'TST-%');
                               DELETE hrms.AssetCustody WHERE AssetId IN (SELECT Id FROM hrms.Asset WHERE AssetCode LIKE 'TST-%');
                               DELETE hrms.Asset WHERE AssetCode LIKE 'TST-%';
                               DELETE hrms.Notification WHERE Kind IN ('asset','clearance') AND UserId IN (@emp, 'TSTADM', 'TSTMGR', 'TSTHR9')", new { emp = f.Employee });
    }

    static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    async Task<int> Add(Session s, string code, string serial)
    {
        var r = await s.Post("/api/assets", new { assetCode = code, category = "Laptop", name = "Test " + code, serialNo = serial });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("result").GetProperty("id").GetInt32();
    }

    static int Result((HttpStatusCode Status, JsonElement Body) r) { Assert.Equal(HttpStatusCode.OK, r.Status); return r.Body.GetProperty("result").GetProperty("id").GetInt32(); }

    async Task<string> StateOf(Session s, string code) =>
        (await s.Get($"/api/assets?q={code}")).Body.EnumerateArray().First().GetProperty("state").GetString() ?? "";

    [Fact]
    public async Task The_administrator_role_is_at_level_10_for_assets_and_clearance()
    {
        var adm = await f.LoginAs(ApiFixture.Admin);
        Assert.Equal(10, adm.User.GetProperty("rights").GetProperty("H301").GetProperty("level").GetInt32());
        Assert.Equal(10, adm.User.GetProperty("rights").GetProperty("H302").GetProperty("level").GetInt32());
        // other levels are untouched
        Assert.Equal(9, adm.User.GetProperty("rights").GetProperty("H201").GetProperty("level").GetInt32());
    }

    [Fact]
    public async Task An_issue_by_the_administrator_is_final_at_once()
    {
        var adm = await f.LoginAs(ApiFixture.Admin);
        var asset = await Add(adm, "TST-L1", "TST-SN-L1");
        var issued = await adm.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today });
        Assert.Equal(HttpStatusCode.OK, issued.Status);
        Assert.False(issued.Body.GetProperty("result").GetProperty("pending").GetBoolean());
        Assert.Equal("With employee", await StateOf(adm, "TST-L1"));
    }

    [Fact]
    public async Task The_administrator_approves_what_a_level_9_preparer_prepared_but_not_their_own()
    {
        var hr = await f.LoginAs(ApiFixture.HrNine);
        var adm = await f.LoginAs(ApiFixture.Admin);
        var asset = await Add(hr, "TST-L2", "TST-SN-L2");
        var custody = Result(await hr.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today }));
        Assert.Equal("Issue waiting approval", await StateOf(hr, "TST-L2"));

        Assert.Equal(HttpStatusCode.Forbidden, (await hr.Post($"/api/assets/custody/{custody}/approve", new { })).Status);   // level 9 cannot
        Assert.Equal(HttpStatusCode.OK, (await adm.Post($"/api/assets/custody/{custody}/approve", new { note = "ok" })).Status);   // the administrator can
        Assert.Equal("With employee", await StateOf(adm, "TST-L2"));
    }

    [Fact]
    public async Task The_preparer_can_withdraw_a_waiting_issue_and_the_stock_is_released()
    {
        var hr = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var asset = await Add(hr, "TST-L3", "TST-SN-L3");
        var custody = Result(await hr.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today }));

        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Post($"/api/assets/custody/{custody}/withdraw")).Status);            // only the preparer
        Assert.Equal(HttpStatusCode.OK, (await hr.Post($"/api/assets/custody/{custody}/withdraw")).Status);
        Assert.Equal("InStock", (await hr.Get("/api/assets?q=TST-L3")).Body.EnumerateArray().First().GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await hr.Post($"/api/assets/custody/{custody}/withdraw")).Status);              // nothing left to withdraw
        Assert.Equal(HttpStatusCode.OK, (await hr.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today })).Status);   // and it can be prepared again
    }

    [Fact]
    public async Task The_preparer_can_withdraw_a_waiting_return_and_the_asset_stays_with_the_employee()
    {
        var hr = await f.LoginAs(ApiFixture.HrNine);
        var adm = await f.LoginAs(ApiFixture.Admin);
        var asset = await Add(adm, "TST-L4", "TST-SN-L4");
        var custody = Result(await adm.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today }));        // final at once
        Assert.Equal(HttpStatusCode.OK, (await hr.Post($"/api/assets/custody/{custody}/return", new { date = Today, condition = "Good" })).Status);
        Assert.Equal("Return waiting approval", await StateOf(hr, "TST-L4"));

        Assert.Equal(HttpStatusCode.Forbidden, (await adm.Post($"/api/assets/custody/{custody}/withdraw")).Status);            // not theirs to withdraw
        Assert.Equal(HttpStatusCode.OK, (await hr.Post($"/api/assets/custody/{custody}/withdraw")).Status);
        Assert.Equal("With employee", await StateOf(hr, "TST-L4"));
        Assert.Equal(HttpStatusCode.OK, (await hr.Post($"/api/assets/custody/{custody}/return", new { date = Today })).Status);   // a new return can be recorded
    }

    [Fact]
    public async Task The_preparer_can_withdraw_a_waiting_transfer()
    {
        var hr = await f.LoginAs(ApiFixture.HrNine);
        var adm = await f.LoginAs(ApiFixture.Admin);
        await using var c = f.Open();
        var other = await c.QueryFirstAsync<string>(Sql.EmpCte + "SELECT TOP 1 EmpCode FROM emp WHERE Status = 'active' AND EmpCode <> @e AND EmpCode NOT LIKE 'TST%' ORDER BY EmpCode", new { e = f.Employee });
        var asset = await Add(adm, "TST-L5", "TST-SN-L5");
        var custody = Result(await adm.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today }));
        var transfer = Result(await hr.Post($"/api/assets/custody/{custody}/transfer", new { toEmp = other, date = Today }));
        Assert.Equal("Transfer waiting approval", await StateOf(hr, "TST-L5"));

        Assert.Equal(HttpStatusCode.Forbidden, (await adm.Post($"/api/assets/transfers/{transfer}/withdraw")).Status);         // only the preparer
        Assert.Equal(HttpStatusCode.OK, (await hr.Post($"/api/assets/transfers/{transfer}/withdraw")).Status);
        Assert.Equal("With employee", await StateOf(hr, "TST-L5"));
        Assert.Equal(HttpStatusCode.Conflict, (await hr.Post($"/api/assets/transfers/{transfer}/withdraw")).Status);           // already withdrawn
    }
}
