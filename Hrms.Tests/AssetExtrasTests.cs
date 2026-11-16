using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Hrms.Api;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hrms.Tests;

// Transfer with acceptance, the waiting list, attachments, bulk import and the leaving-soon check. Everything created is tagged TST- and removed afterwards.
[Collection("api")]
public class AssetExtrasTests(ApiFixture f) : IAsyncLifetime
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

    async Task<int> Add(Session adm, string code, string serial)
    {
        var r = await adm.Post("/api/assets", new { assetCode = code, category = "Laptop", name = "Test " + code, serialNo = serial });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("result").GetProperty("id").GetInt32();
    }

    static int Result((HttpStatusCode Status, JsonElement Body) r) { Assert.Equal(HttpStatusCode.OK, r.Status); return r.Body.GetProperty("result").GetProperty("id").GetInt32(); }

    // another active employee, so an asset can move from them to the test employee
    async Task<string> OtherEmployee()
    {
        await using var c = f.Open();
        return await c.QueryFirstAsync<string>(Sql.EmpCte + "SELECT TOP 1 EmpCode FROM emp WHERE Status = 'active' AND EmpCode <> @e AND EmpCode NOT LIKE 'TST%' ORDER BY EmpCode", new { e = f.Employee });
    }

    [Fact]
    public async Task Everything_waiting_is_in_one_list_and_the_approvers_are_told()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var asset = await Add(adm, "TST-W1", "TST-SN-W1");
        var custody = Result(await adm.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today }));

        var forAdmin = (await adm.Get("/api/assets/waiting")).Body.EnumerateArray().First(x => x.GetProperty("id").GetInt32() == custody && x.GetProperty("kind").GetString() == "Issue");
        Assert.False(forAdmin.GetProperty("canDecide").GetBoolean());                                    // they prepared it, and are below the document level
        var forMgr = (await mgr.Get("/api/assets/waiting")).Body.EnumerateArray().First(x => x.GetProperty("id").GetInt32() == custody && x.GetProperty("kind").GetString() == "Issue");
        Assert.True(forMgr.GetProperty("canDecide").GetBoolean());

        var bell = (await mgr.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString());
        Assert.Contains("An asset issue needs your approval", bell);
    }

    [Fact]
    public async Task A_transfer_is_authorised_then_accepted_by_the_receiver_and_the_asset_changes_hands()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var emp = await f.LoginAs(f.Employee);
        var other = await OtherEmployee();
        var asset = await Add(adm, "TST-T1", "TST-SN-T1");
        var first = Result(await mgr.Post($"/api/assets/{asset}/issue", new { empCode = other, date = Today }));        // with someone else, approved

        Assert.Equal(HttpStatusCode.BadRequest, (await adm.Post($"/api/assets/custody/{first}/transfer", new { toEmp = other, date = Today })).Status);          // already theirs
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Post($"/api/assets/custody/{first}/transfer", new { toEmp = "NO-SUCH-EMPLOYEE", date = Today })).Status);

        var transfer = Result(await adm.Post($"/api/assets/custody/{first}/transfer", new { toEmp = f.Employee, date = Today, condition = "Good", note = "new desk" }));
        var held = (await adm.Get("/api/assets?q=TST-T1")).Body.EnumerateArray().First();
        Assert.Equal("Transfer waiting approval", held.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Post($"/api/assets/custody/{first}/return", new { date = Today })).Status);                  // not while it is moving
        Assert.Equal(HttpStatusCode.Conflict, (await adm.Post($"/api/assets/custody/{first}/transfer", new { toEmp = f.Employee, date = Today })).Status);   // one at a time
        Assert.Equal(HttpStatusCode.Forbidden, (await adm.Post($"/api/assets/transfers/{transfer}/approve", new { })).Status);                         // below the level

        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/assets/transfers/{transfer}/approve", new { note = "ok" })).Status);
        Assert.Equal("Transfer waiting acceptance", (await adm.Get("/api/assets?q=TST-T1")).Body.EnumerateArray().First().GetProperty("state").GetString());

        // the receiver sees it and accepts; only the receiver can accept through the employee route
        var incoming = (await emp.Get("/api/me/assets/incoming")).Body.EnumerateArray().Select(x => x.GetProperty("id").GetInt32());
        Assert.Contains(transfer, incoming);
        Assert.Equal(HttpStatusCode.OK, (await emp.Post($"/api/me/assets/transfers/{transfer}/accept")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await emp.Post($"/api/me/assets/transfers/{transfer}/accept")).Status);                                 // already done

        var mine = (await emp.Get("/api/me/assets")).Body.EnumerateArray().First(x => x.GetProperty("assetCode").GetString() == "TST-T1");
        Assert.Equal("With employee", mine.GetProperty("state").GetString());
        var before = (await adm.Get($"/api/assets/custody?scope=held&emp={other}")).Body.EnumerateArray().Select(x => x.GetProperty("assetCode").GetString());
        Assert.DoesNotContain("TST-T1", before);                                                                                                        // the first holder no longer has it
        Assert.Equal("Issued", (await adm.Get("/api/assets?q=TST-T1")).Body.EnumerateArray().First().GetProperty("status").GetString());              // still issued, never through stock

        // the signature slips read one handover and one transfer
        var slip = await adm.Get($"/api/assets/custody/{first}");
        Assert.Equal(HttpStatusCode.OK, slip.Status);
        Assert.Equal("Returned", slip.Body.GetProperty("state").GetString());                                                                          // the first handover was closed by the transfer
        var tr = await adm.Get($"/api/assets/transfers/{transfer}");
        Assert.Equal("Completed", tr.Body.GetProperty("status").GetString());
        Assert.StartsWith("ATRF", tr.Body.GetProperty("docNo").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await adm.Get("/api/assets/transfers/999999")).Status);
    }

    [Fact]
    public async Task A_rejected_transfer_leaves_the_asset_where_it_was()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var asset = await Add(adm, "TST-T2", "TST-SN-T2");
        var first = Result(await mgr.Post($"/api/assets/{asset}/issue", new { empCode = f.Employee, date = Today }));
        var other = await OtherEmployee();
        var transfer = Result(await adm.Post($"/api/assets/custody/{first}/transfer", new { toEmp = other, date = Today }));
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post($"/api/assets/transfers/{transfer}/reject", new { })).Status);   // a reason is required
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/assets/transfers/{transfer}/reject", new { note = "Keep it" })).Status);
        Assert.Equal("With employee", (await adm.Get("/api/assets?q=TST-T2")).Body.EnumerateArray().First().GetProperty("state").GetString());
    }

    static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    static async Task<HttpResponseMessage> Upload(Session s, int asset, byte[] bytes, string contentType, string name, string kind = "Photo")
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", name);
        return await s.Http.PostAsync($"/api/assets/{asset}/files?kind={Uri.EscapeDataString(kind)}", form);
    }

    [Fact]
    public async Task Photos_and_pdfs_can_be_attached_and_only_real_ones()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var asset = await Add(adm, "TST-P1", "TST-SN-P1");

        var ok = await Upload(adm, asset, TinyPng, "image/png", "laptop.png");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var list = (await adm.Get($"/api/assets/{asset}/files")).Body.EnumerateArray().ToList();
        Assert.Single(list);
        var id = list[0].GetProperty("id").GetInt32();
        var back = await adm.Http.GetAsync($"/api/assets/files/{id}");
        Assert.Equal("image/png", back.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TinyPng, await back.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(adm, asset, System.Text.Encoding.UTF8.GetBytes("not a picture"), "image/png", "fake.png")).StatusCode);      // not really a PNG
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(adm, asset, TinyPng, "text/plain", "note.txt")).StatusCode);                                               // type not allowed
        var big = new byte[6 * 1024 * 1024]; Array.Copy(TinyPng, big, TinyPng.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(adm, asset, big, "image/png", "big.png")).StatusCode);                                                     // too large
        Assert.Equal(HttpStatusCode.OK, (await Upload(adm, asset, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 test"), "application/pdf", "form.pdf", "Handover form")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await adm.Delete($"/api/assets/files/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await adm.Http.GetAsync($"/api/assets/files/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_employee_cannot_open_the_asset_files()
    {
        var emp = await f.LoginAs(f.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Get("/api/assets/1/files")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Http.GetAsync("/api/assets/files/1")).StatusCode);
    }

    [Fact]
    public async Task Bulk_import_saves_nothing_unless_every_row_is_good()
    {
        var adm = await f.LoginAs(ApiFixture.HrNine);
        var good = new object[]
        {
            new { assetCode = "TST-IMP-1", category = "laptop", name = "Dell 5430", serialNo = "TST-IMP-SN-1", value = 3200 },
            new { assetCode = "TST-IMP-2", category = "phone", name = "iPhone", serialNo = "TST-IMP-SN-2" },        // "phone" is understood as Mobile phone
        };
        var trial = await adm.Post("/api/assets/import?dryRun=true", new { rows = good });
        Assert.True(trial.Body.GetProperty("ok").GetBoolean());
        Assert.Equal(0, trial.Body.GetProperty("imported").GetInt32());
        Assert.Empty((await adm.Get("/api/assets?q=TST-IMP")).Body.EnumerateArray());                                  // a trial run saved nothing

        var bad = new object[]
        {
            new { assetCode = "TST-IMP-1", category = "Laptop", name = "A", serialNo = "TST-IMP-SN-1" },
            new { assetCode = "TST-IMP-1", category = "Laptop", name = "B", serialNo = "TST-IMP-SN-9" },              // tag twice
            new { assetCode = "TST-IMP-3", category = "Spaceship", name = "C", serialNo = "TST-IMP-SN-3" },           // unknown category
            new { assetCode = "TST-IMP-4", category = "Laptop", name = "D", serialNo = "" },                          // serial missing
            new { assetCode = "TST-IMP-5", category = "Laptop", name = "E", serialNo = "TST-IMP-SN-5", empCode = f.Employee },   // assigning needs the document level
        };
        var refused = await adm.Post("/api/assets/import", new { rows = bad });
        Assert.False(refused.Body.GetProperty("ok").GetBoolean());
        var results = refused.Body.GetProperty("results").EnumerateArray().ToList();
        Assert.True(results[0].GetProperty("ok").GetBoolean());
        Assert.False(results[1].GetProperty("ok").GetBoolean());
        Assert.False(results[2].GetProperty("ok").GetBoolean());
        Assert.False(results[3].GetProperty("ok").GetBoolean());
        Assert.False(results[4].GetProperty("ok").GetBoolean());
        Assert.Empty((await adm.Get("/api/assets?q=TST-IMP")).Body.EnumerateArray());                                  // and the good first row was not kept either

        var real = await adm.Post("/api/assets/import", new { rows = good });
        Assert.Equal(2, real.Body.GetProperty("imported").GetInt32());
        var saved = (await adm.Get("/api/assets?q=TST-IMP")).Body.EnumerateArray().ToList();
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, x => x.GetProperty("category").GetString() == "Mobile phone");
    }

    [Fact]
    public async Task A_manager_can_import_assets_that_are_already_with_employees()
    {
        var mgr = await f.LoginAs(ApiFixture.Manager);
        var r = await mgr.Post("/api/assets/import", new { rows = new object[] { new { assetCode = "TST-IMP-6", category = "Laptop", name = "Old laptop", serialNo = "TST-IMP-SN-6", empCode = f.Employee, issueDate = DateTime.Today.AddYears(-1) } } });
        Assert.True(r.Body.GetProperty("ok").GetBoolean());
        Assert.Equal(1, r.Body.GetProperty("assigned").GetInt32());
        var held = (await mgr.Get($"/api/assets/custody?scope=held&emp={f.Employee}")).Body.EnumerateArray().Select(x => x.GetProperty("assetCode").GetString());
        Assert.Contains("TST-IMP-6", held);
    }

    [Fact]
    public async Task The_leaving_soon_check_and_the_daily_watch_run_cleanly()
    {
        await using var c = f.Open();
        var leaving = await AssetExtras.Leaving(c, null, includeWithoutAssets: true);
        Assert.NotNull(leaving);
        var watch = new ClearanceWatch(f.Factory.Services.GetRequiredService<Db>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ClearanceWatch>.Instance);
        Assert.True(await watch.Run() >= 0);
    }
}
