using System.Net;
using System.Text.Json;
using Dapper;
using Xunit;

namespace Hrms.Tests;

// Asset handover and clearance, following the ERP's asset rules. The test people: an administrator at level 9 (prepares), a manager at
// level 10 (the document level, authorises) and an employee. Everything the tests create is tagged TST- and removed afterwards.
[Collection("api")]
public class AssetTests(ApiFixture f) : IAsyncLifetime
{
    public async Task InitializeAsync() => await Cleanup();
    public async Task DisposeAsync() => await Cleanup();

    async Task Cleanup()
    {
        await using var c = f.Open();
        await c.ExecuteAsync(@"DELETE hrms.ClearanceItem WHERE ClearanceId IN (SELECT Id FROM hrms.Clearance WHERE StartedBy IN ('TSTADM','TSTMGR','TSTHR9'));
                               DELETE hrms.Clearance WHERE StartedBy IN ('TSTADM','TSTMGR','TSTHR9');
                               DELETE hrms.AssetFile WHERE AssetId IN (SELECT Id FROM hrms.Asset WHERE AssetCode LIKE 'TST-%');
                               DELETE hrms.AssetTransfer WHERE AssetId IN (SELECT Id FROM hrms.Asset WHERE AssetCode LIKE 'TST-%');
                               DELETE hrms.AssetCustody WHERE AssetId IN (SELECT Id FROM hrms.Asset WHERE AssetCode LIKE 'TST-%');
                               DELETE hrms.Asset WHERE AssetCode LIKE 'TST-%';
                               DELETE hrms.Notification WHERE Kind IN ('asset','clearance') AND UserId IN (@emp, 'TSTADM', 'TSTMGR', 'TSTHR9')", new { emp = f.Employee });
    }

    static object NewAsset(string code, string serial, string category = "Laptop") => new { assetCode = code, category, name = "Test " + code, serialNo = serial, value = 1000m };
    static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    async Task<int> Add(Session adm, string code, string serial)
    {
        var r = await adm.Post("/api/assets", NewAsset(code, serial));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("result").GetProperty("id").GetInt32();
    }

    static async Task<(HttpStatusCode Status, JsonElement Body)> Issue(Session by, int asset, string emp, string? date = null) =>
        await by.Post($"/api/assets/{asset}/issue", new { empCode = emp, date = date ?? Today, condition = "New", note = "test" });

    static int CustodyId((HttpStatusCode Status, JsonElement Body) r) => r.Body.GetProperty("result").GetProperty("id").GetInt32();

    async Task<string> AssetStatus(Session adm, int asset)
    {
        var list = (await adm.Get("/api/assets?q=TST-")).Body.EnumerateArray();
        return list.First(a => a.GetProperty("id").GetInt32() == asset).GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task The_register_needs_a_tag_a_serial_and_never_repeats_either()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        await Add(adm, "TST-A1", "TST-SN-A1");
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Post("/api/assets", NewAsset("TST-A1", "TST-SN-OTHER"))).Status);     // same tag
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Post("/api/assets", NewAsset("TST-A2", "TST-SN-A1"))).Status);        // same serial
        Assert.Equal(HttpStatusCode.BadRequest, (await adm.Post("/api/assets", NewAsset("TST-A3", ""))).Status);              // serial required
        Assert.Equal(HttpStatusCode.BadRequest, (await adm.Post("/api/assets", NewAsset("TST-A4", "TST-SN-A4", "Spaceship"))).Status);
    }

    [Fact]
    public async Task An_ordinary_employee_cannot_use_the_register_or_clearance()
    {
        var emp = await f.LoginAs(f.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/assets")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Post("/api/assets", NewAsset("TST-B1", "TST-SN-B1"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/clearance")).Status);
    }

    [Fact]
    public async Task An_issue_by_the_administrator_waits_for_the_manager_and_an_asset_cannot_be_issued_twice()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var emp = await f.LoginAs(f.Employee);
        var asset = await Add(adm, "TST-C1", "TST-SN-C1");

        var issued = await Issue(adm, asset, f.Employee);
        Assert.Equal(HttpStatusCode.OK, issued.Status);
        Assert.True(issued.Body.GetProperty("result").GetProperty("pending").GetBoolean());                 // level 9 is below the document level
        var id = CustodyId(issued);
        Assert.Equal("Issued", await AssetStatus(adm, asset));                                                // reserved at once: the stock check

        Assert.Equal(HttpStatusCode.Conflict, (await Issue(mgr, asset, f.Employee)).Status);                  // not in stock any more

        // the employee sees it, but it is not confirmed as held yet
        var mine = (await emp.Get("/api/me/assets")).Body.EnumerateArray().First(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal("Issue waiting approval", mine.GetProperty("state").GetString());

        // the administrator cannot approve; the manager can
        Assert.Equal(HttpStatusCode.Forbidden, (await adm.Post($"/api/assets/custody/{id}/approve", new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/assets/custody/{id}/approve", new { note = "ok" })).Status);
        mine = (await emp.Get("/api/me/assets")).Body.EnumerateArray().First(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal("With employee", mine.GetProperty("state").GetString());

        // the employee confirms receiving it, once
        Assert.Equal(HttpStatusCode.OK, (await emp.Post($"/api/me/assets/{id}/accept")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await emp.Post($"/api/me/assets/{id}/accept")).Status);
    }

    [Fact]
    public async Task A_manager_issue_is_final_at_once_and_nobody_approves_their_own()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var asset = await Add(adm, "TST-D1", "TST-SN-D1");
        var issued = await Issue(mgr, asset, f.Employee);
        Assert.False(issued.Body.GetProperty("result").GetProperty("pending").GetBoolean());
        var held = (await adm.Get($"/api/assets/custody?scope=held&emp={f.Employee}")).Body.EnumerateArray().Select(x => x.GetProperty("id").GetInt32());
        Assert.Contains(CustodyId(issued), held);

        // a pending one prepared by the manager (below the level is not possible here), so test self-approval via a return prepared by the administrator
        var ret = await adm.Post($"/api/assets/custody/{CustodyId(issued)}/return", new { date = Today, condition = "Good", note = "back" });
        Assert.True(ret.Body.GetProperty("result").GetProperty("pending").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await adm.Post($"/api/assets/custody/{CustodyId(issued)}/approve", new { })).Status);
    }

    [Fact]
    public async Task Issue_dates_follow_the_ERP_limits_and_only_active_employees_can_receive_assets()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var asset = await Add(adm, "TST-E1", "TST-SN-E1");
        Assert.Equal(HttpStatusCode.Conflict, (await Issue(adm, asset, f.Employee, DateTime.Today.AddDays(-400).ToString("yyyy-MM-dd"))).Status);   // far in the past (and closed)
        Assert.Equal(HttpStatusCode.Conflict, (await Issue(adm, asset, f.Employee, DateTime.Today.AddDays(60).ToString("yyyy-MM-dd"))).Status);    // far ahead
        Assert.Equal(HttpStatusCode.Conflict, (await Issue(adm, asset, "NO-SUCH-EMPLOYEE")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Issue(adm, asset, f.Employee)).Status);
    }

    [Fact]
    public async Task A_return_is_authorised_the_same_way_and_puts_the_asset_back_in_stock()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var asset = await Add(adm, "TST-F1", "TST-SN-F1");
        var id = CustodyId(await Issue(mgr, asset, f.Employee));

        Assert.Equal(HttpStatusCode.BadRequest, (await adm.Post($"/api/assets/custody/{id}/return", new { date = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd") })).Status);   // not before it was issued
        var ret = await adm.Post($"/api/assets/custody/{id}/return", new { date = Today, condition = "Good" });
        Assert.Equal(HttpStatusCode.OK, ret.Status);
        Assert.Equal("Issued", await AssetStatus(adm, asset));                                                // still out until the return is authorised
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Post($"/api/assets/custody/{id}/return", new { date = Today })).Status);   // one return at a time

        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post($"/api/assets/custody/{id}/reject", new { })).Status);   // a reason is required
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/assets/custody/{id}/approve", new { })).Status);
        Assert.Equal("InStock", await AssetStatus(adm, asset));
        Assert.Equal(HttpStatusCode.OK, (await Issue(mgr, asset, f.Employee)).Status);                         // and it can be issued again
    }

    [Fact]
    public async Task A_rejected_issue_releases_the_stock()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var asset = await Add(adm, "TST-G1", "TST-SN-G1");
        var id = CustodyId(await Issue(adm, asset, f.Employee));
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/assets/custody/{id}/reject", new { note = "Not this one" })).Status);
        Assert.Equal("InStock", await AssetStatus(adm, asset));
    }

    [Fact]
    public async Task An_issued_asset_cannot_be_disposed_but_a_stock_asset_can()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var out_ = await Add(adm, "TST-H1", "TST-SN-H1");
        await Issue(mgr, out_, f.Employee);
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Delete($"/api/assets/{out_}")).Status);
        var spare = await Add(adm, "TST-H2", "TST-SN-H2");
        Assert.Equal(HttpStatusCode.OK, (await adm.Delete($"/api/assets/{spare}")).Status);
        Assert.Equal("Disposed", await AssetStatus(adm, spare));
    }

    [Fact]
    public async Task Clearance_is_blocked_until_every_asset_is_returned_and_the_checklist_is_done()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var asset = await Add(adm, "TST-I1", "TST-SN-I1");
        var custody = CustodyId(await Issue(mgr, asset, f.Employee));

        var start = await adm.Post("/api/clearance", new { empCode = f.Employee, lastWorkingDay = DateTime.Today.AddDays(5).ToString("yyyy-MM-dd"), reason = "Resigned" });
        Assert.Equal(HttpStatusCode.OK, start.Status);
        var clearanceId = start.Body.GetProperty("result").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Post("/api/clearance", new { empCode = f.Employee })).Status);        // one open clearance at a time

        var detail = (await adm.Get($"/api/clearance/employee/{f.Employee}")).Body;
        Assert.False(detail.GetProperty("canClear").GetBoolean());
        Assert.Contains(detail.GetProperty("blockers").EnumerateArray().Select(x => x.GetString()), b => b!.Contains("still out"));
        var items = detail.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
        Assert.True(items.Count >= 4);

        // checklist done, but the laptop is still out: refused
        foreach (var item in items) Assert.Equal(HttpStatusCode.OK, (await adm.Post($"/api/clearance/items/{item}/toggle", new { done = true })).Status);
        var blocked = await adm.Post($"/api/clearance/{clearanceId}/clear");
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("still out", blocked.Body.GetProperty("error").GetString());

        // returned and authorised: now it clears
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/assets/custody/{custody}/return", new { date = Today, condition = "Good" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await adm.Post($"/api/clearance/{clearanceId}/clear")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await adm.Post($"/api/clearance/{clearanceId}/clear")).Status);                // already closed
    }

    [Fact]
    public async Task Clearance_is_also_blocked_by_an_open_checklist_item()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var start = await adm.Post("/api/clearance", new { empCode = f.Employee });
        var clearanceId = start.Body.GetProperty("result").GetProperty("id").GetInt32();
        var blocked = await adm.Post($"/api/clearance/{clearanceId}/clear");
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("checklist", blocked.Body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, (await adm.Post($"/api/clearance/{clearanceId}/cancel")).Status);
    }
}
