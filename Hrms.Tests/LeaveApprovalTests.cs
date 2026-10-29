using System.Net;
using System.Text.Json;
using Dapper;
using Xunit;

namespace Hrms.Tests;

[Collection("api")]
public class LeaveApprovalTests(ApiFixture f)
{
    // Far-future dates so a test never touches real leave; each test uses its own slot.
    static DateTime Slot(int n) => DateTime.Today.AddDays(1000 + n * 10);

    record Leave(long Id, string TransNo);

    async Task<(HttpStatusCode Status, Leave? Leave, JsonElement Body)> Request(Session emp, string? pushTo, int slot, int days = 2, string type = "LVE")
    {
        var from = Slot(slot);
        var r = await emp.Post("/api/me/leave", new { leaveType = type, fromDate = from, toDate = from.AddDays(days - 1), salaryWithoutLeave = false, pushTo });
        if (r.Status != HttpStatusCode.OK) return (r.Status, null, r.Body);
        var rows = await emp.Get($"/api/me/leave?from={from.AddDays(-1):yyyy-MM-dd}&to={from.AddDays(days):yyyy-MM-dd}");
        var row = rows.Body.EnumerateArray().First(x => x.GetProperty("fromDate").GetDateTime().Date == from);
        return (r.Status, new Leave(row.GetProperty("id").GetInt64(), r.Body.GetProperty("result").GetProperty("transNo").GetString()!), r.Body);
    }

    async Task<string> StatusOf(Session emp, int slot)
    {
        var from = Slot(slot);
        var rows = await emp.Get($"/api/me/leave?from={from.AddDays(-1):yyyy-MM-dd}&to={from.AddDays(5):yyyy-MM-dd}");
        return rows.Body.EnumerateArray().First(x => x.GetProperty("fromDate").GetDateTime().Date == from).GetProperty("status").GetString()!;
    }

    async Task<int> AccrualRows(string transNo)
    {
        await using var c = f.Open();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmpAccuralPayment WHERE LTransno = @transNo", new { transNo });
    }

    static bool InInbox(JsonElement inbox, long id) => inbox.EnumerateArray().Any(x => x.GetProperty("id").GetInt64() == id);

    [Fact]
    public async Task A_request_must_name_an_approver_who_is_more_senior()
    {
        var emp = await f.LoginAs(f.Employee);
        var approvers = (await emp.Get("/api/me/approvers")).Body.EnumerateArray().Select(a => (Id: a.GetProperty("id").GetString(), Level: a.GetProperty("level").GetInt32())).ToList();
        Assert.Contains((ApiFixture.Admin, 9), approvers);
        Assert.Contains((ApiFixture.Manager, 10), approvers);
        Assert.DoesNotContain(approvers, a => a.Id == f.Employee);                                          // you cannot approve your own request

        Assert.Equal(HttpStatusCode.BadRequest, (await Request(emp, null, 1)).Status);                      // no approver chosen
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(emp, f.Employee, 1)).Status);               // the employee himself
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(emp, "NOBODY", 1)).Status);                  // not an approver
    }

    [Fact]
    public async Task A_leave_goes_employee_then_administrator_then_manager_and_is_final_at_the_document_level()
    {
        var emp = await f.LoginAs(f.Employee); var adm = await f.LoginAs(ApiFixture.Admin); var mgr = await f.LoginAs(ApiFixture.Manager);
        var (status, leave, body) = await Request(emp, ApiFixture.Admin, 2);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("result").GetProperty("pending").GetBoolean());                        // level 8 is below the document level 10
        Assert.Equal("Pending", await StatusOf(emp, 2));

        Assert.True(InInbox((await adm.Get("/api/admin/approvals")).Body, leave!.Id));                       // it sits with the administrator
        Assert.False(InInbox((await mgr.Get("/api/admin/approvals")).Body, leave.Id));                       // and not yet with the manager
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Post($"/api/admin/approvals/{leave.Id}/approve", new { })).Status);

        var options = (await adm.Get($"/api/admin/approvals/{leave.Id}/options")).Body;
        Assert.False(options.GetProperty("final").GetBoolean());                                             // level 9 cannot finish it
        Assert.Contains(options.GetProperty("push").EnumerateArray(), p => p.GetProperty("id").GetString() == ApiFixture.Manager);
        Assert.Equal(HttpStatusCode.BadRequest, (await adm.Post($"/api/admin/approvals/{leave.Id}/approve", new { })).Status);   // must choose who it goes to
        Assert.Equal(HttpStatusCode.OK, (await adm.Post($"/api/admin/approvals/{leave.Id}/approve", new { to = ApiFixture.Manager, note = "Looks fine" })).Status);
        Assert.Equal("Pending", await StatusOf(emp, 2));                                                     // still pending until level 10

        Assert.True(InInbox((await mgr.Get("/api/admin/approvals")).Body, leave.Id));
        Assert.False(InInbox((await adm.Get("/api/admin/approvals")).Body, leave.Id));
        Assert.True((await mgr.Get($"/api/admin/approvals/{leave.Id}/options")).Body.GetProperty("final").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/admin/approvals/{leave.Id}/approve", new { })).Status);

        Assert.Equal("Approved", await StatusOf(emp, 2));
        await using var c = f.Open();
        var row = await c.QuerySingleAsync("SELECT CAST(Userlevel AS int) AS Level, CAST(documentlevel AS int) AS Doc FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id = @id", new { id = leave.Id });
        Assert.Equal(10, (int)row.Level);
        Assert.Equal((int)row.Doc, (int)row.Level);                                                          // approved exactly as the ERP defines it
        var trail = (await c.QueryAsync<(string Userid, int Auth)>("SELECT Userid, CAST(Auth AS int) AS Auth FROM inter01.UserwiseTransaction WHERE TransNo = @no ORDER BY id", new { no = leave.TransNo })).ToList();
        Assert.Equal([f.Employee, ApiFixture.Admin, ApiFixture.Admin, ApiFixture.Manager, ApiFixture.Manager], trail.Select(t => t.Userid));
        Assert.Equal([8, 9, 9, 10, 10], trail.Select(t => t.Auth));

        var cancel = await emp.Delete($"/api/me/leave/{leave.Id}");
        Assert.True((int)cancel.Status >= 400);                                                              // approved leave cannot be cancelled by the employee
        Assert.Equal("Approved", await StatusOf(emp, 2));
    }

    [Fact]
    public async Task The_employee_can_cancel_a_pending_request()
    {
        var emp = await f.LoginAs(f.Employee);
        var (_, leave, _) = await Request(emp, ApiFixture.Manager, 3);
        Assert.True(await AccrualRows(leave!.TransNo) > 0);                                                  // the ERP trigger creates accrual rows with the request
        Assert.Equal(HttpStatusCode.OK, (await emp.Delete($"/api/me/leave/{leave.Id}")).Status);
        var rows = await emp.Get($"/api/me/leave?from={Slot(3).AddDays(-1):yyyy-MM-dd}&to={Slot(3).AddDays(5):yyyy-MM-dd}");
        Assert.Empty(rows.Body.EnumerateArray());
        Assert.Equal(0, await AccrualRows(leave.TransNo));                                                   // and cancelling removes them again
    }

    [Fact]
    public async Task A_manager_can_send_a_request_back_with_a_note_and_reject_with_a_reason()
    {
        var emp = await f.LoginAs(f.Employee); var adm = await f.LoginAs(ApiFixture.Admin); var mgr = await f.LoginAs(ApiFixture.Manager);
        var (_, leave, _) = await Request(emp, ApiFixture.Admin, 4);
        await adm.Post($"/api/admin/approvals/{leave!.Id}/approve", new { to = ApiFixture.Manager });

        var back = (await mgr.Get($"/api/admin/approvals/{leave.Id}/options")).Body.GetProperty("back").EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(ApiFixture.Admin, back);
        Assert.DoesNotContain(f.Employee, back);                                                             // sent back to approvers, not the requester
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Post($"/api/admin/approvals/{leave.Id}/return", new { to = ApiFixture.Admin })).Status);   // a note is required
        Assert.Equal(HttpStatusCode.OK, (await mgr.Post($"/api/admin/approvals/{leave.Id}/return", new { to = ApiFixture.Admin, note = "Check team cover first" })).Status);

        var inbox = (await adm.Get("/api/admin/approvals")).Body.EnumerateArray().First(x => x.GetProperty("id").GetInt64() == leave.Id);
        Assert.Contains("Check team cover first", inbox.GetProperty("note").GetString());                    // the note travels with it

        Assert.Equal(HttpStatusCode.BadRequest, (await adm.Post($"/api/admin/approvals/{leave.Id}/reject", new { })).Status);            // a reason is required
        Assert.Equal(HttpStatusCode.OK, (await adm.Post($"/api/admin/approvals/{leave.Id}/reject", new { note = "Peak season" })).Status);
        Assert.Equal(0, await AccrualRows(leave.TransNo));                                                   // a rejected request leaves no accrual rows behind
        var rows = await emp.Get($"/api/me/leave?from={Slot(4).AddDays(-1):yyyy-MM-dd}&to={Slot(4).AddDays(5):yyyy-MM-dd}");
        Assert.Empty(rows.Body.EnumerateArray());                                                            // a rejected request is gone

        await using var c = f.Open();
        Assert.Contains("Rejected: Peak season", await c.QueryAsync<string>("SELECT CAST(Descr AS varchar(200)) FROM inter01.UserwiseTransaction WHERE TransNo = @no", new { no = leave.TransNo }));   // but the reason is kept
    }

    [Fact]
    public async Task Nobody_can_decide_someone_elses_request_or_their_own()
    {
        var emp = await f.LoginAs(f.Employee); var mgr = await f.LoginAs(ApiFixture.Manager);
        var (_, leave, _) = await Request(emp, ApiFixture.Admin, 5);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.Post($"/api/admin/approvals/{leave!.Id}/approve", new { })).Status);   // an employee has no approvals right
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Post($"/api/admin/approvals/{leave.Id}/reject", new { note = "x" })).Status);   // not with him
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Post("/api/admin/approvals/999999999/approve", new { })).Status);
    }

    [Fact]
    public async Task Overlapping_leave_for_the_same_person_is_refused()
    {
        var emp = await f.LoginAs(f.Employee);
        Assert.Equal(HttpStatusCode.OK, (await Request(emp, ApiFixture.Manager, 6, days: 3)).Status);
        var overlap = await emp.Post("/api/me/leave", new { leaveType = "LVE", fromDate = Slot(6).AddDays(2), toDate = Slot(6).AddDays(4), salaryWithoutLeave = false, pushTo = ApiFixture.Manager });
        Assert.Equal(HttpStatusCode.Conflict, overlap.Status);
    }

    [Fact]
    public async Task Each_hand_over_notifies_the_next_person_and_the_outcome_notifies_the_employee()
    {
        var emp = await f.LoginAs(f.Employee); var adm = await f.LoginAs(ApiFixture.Admin); var mgr = await f.LoginAs(ApiFixture.Manager);
        await using var c = f.Open();
        await c.ExecuteAsync("UPDATE HRMSINTER.interface.Userdef SET emailid = @e WHERE userid = @u", new { e = ApiFixture.AdminEmail, u = ApiFixture.Admin });
        var (_, leave, _) = await Request(emp, ApiFixture.Admin, 7);

        var first = (await adm.Get("/api/notifications")).Body;
        Assert.True(first.GetProperty("unread").GetInt32() >= 1);
        var item = first.GetProperty("items").EnumerateArray().First(n => n.GetProperty("title").GetString() == "A leave request is waiting for you");
        Assert.Equal("admin/approvals", item.GetProperty("link").GetString());
        Assert.Contains("Annual leave", item.GetProperty("body").GetString());
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.EmailOutbox WHERE ToUser = @u AND ToAddress = @e AND Subject = 'A leave request is waiting for you'", new { u = ApiFixture.Admin, e = ApiFixture.AdminEmail }) >= 1);   // and an email is queued

        await adm.Post($"/api/admin/approvals/{leave!.Id}/approve", new { to = ApiFixture.Manager });
        Assert.Contains((await mgr.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray(), n => n.GetProperty("title").GetString() == "A leave request is waiting for you");
        await mgr.Post($"/api/admin/approvals/{leave.Id}/approve", new { });
        Assert.Contains((await emp.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray(), n => n.GetProperty("title").GetString() == "Your leave was approved");

        var (_, second, _) = await Request(emp, ApiFixture.Manager, 8);
        await mgr.Post($"/api/admin/approvals/{second!.Id}/reject", new { note = "Peak season" });
        var rejected = (await emp.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray().First(n => n.GetProperty("title").GetString() == "Your leave request was rejected");
        Assert.Contains("Peak season", rejected.GetProperty("body").GetString());                             // the employee sees the reason

        // reading
        var unread = (await emp.Get("/api/notifications")).Body.GetProperty("unread").GetInt32();
        var firstId = rejected.GetProperty("id").GetInt64();
        await emp.Post($"/api/notifications/{firstId}/read");
        Assert.Equal(unread - 1, (await emp.Get("/api/notifications")).Body.GetProperty("unread").GetInt32());
        await emp.Post("/api/notifications/read-all");
        Assert.Equal(0, (await emp.Get("/api/notifications")).Body.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Notifications_are_private_to_each_person()
    {
        var emp = await f.LoginAs(f.Employee); var adm = await f.LoginAs(ApiFixture.Admin);
        await Request(emp, ApiFixture.Admin, 9);
        var mine = (await emp.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        Assert.DoesNotContain("A leave request is waiting for you", mine);                                    // that one was for the approver
        var id = (await adm.Get("/api/notifications")).Body.GetProperty("items")[0].GetProperty("id").GetInt64();
        await emp.Post($"/api/notifications/{id}/read");                                                      // an employee cannot mark someone else's as read
        Assert.Contains((await adm.Get("/api/notifications")).Body.GetProperty("items").EnumerateArray(), n => n.GetProperty("id").GetInt64() == id && n.GetProperty("readAt").ValueKind == JsonValueKind.Null);
    }
}
