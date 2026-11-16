using System.Net;
using Xunit;

namespace Hrms.Tests;

// The rules-based "Ask HR": questions are matched to fixed intents, and every answer is pinned to the signed-in employee.
[Collection("api")]
public class AskHrTests(ApiFixture f)
{
    static async Task<(string Intent, string Title, string Text)> Ask(Session s, string q)
    {
        var (status, body) = await s.Get("/api/me/ask?q=" + Uri.EscapeDataString(q));
        Assert.Equal(HttpStatusCode.OK, status);
        return (body.GetProperty("intent").GetString()!, body.GetProperty("title").GetString()!, body.GetProperty("text").GetString()!);
    }

    [Theory]
    [InlineData("How many leave days do I have?", "balance")]
    [InlineData("what is my leave balance", "balance")]
    [InlineData("status of my leave requests", "leave")]
    [InlineData("when is my next leave", "nextleave")]
    [InlineData("What was my last net pay?", "payslip")]
    [InlineData("show my payslip for october", "payslip")]
    [InlineData("Did I punch in today?", "punchtoday")]
    [InlineData("how many days was I late this month", "attmonth")]
    [InlineData("when is the next holiday", "holiday")]
    [InlineData("who approves my leave", "approver")]
    [InlineData("hello", "hello")]
    public async Task A_question_is_matched_to_the_right_answer(string question, string intent)
    {
        var emp = await f.LoginAs(f.Employee);
        var a = await Ask(emp, question);
        Assert.Equal(intent, a.Intent);
        Assert.False(string.IsNullOrWhiteSpace(a.Text));
    }

    [Fact]
    public async Task A_question_it_does_not_understand_gets_help_with_examples()
    {
        var emp = await f.LoginAs(f.Employee);
        var (status, body) = await emp.Get("/api/me/ask?q=" + Uri.EscapeDataString("what is the meaning of life"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("help", body.GetProperty("intent").GetString());
        Assert.True(body.GetProperty("suggestions").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task An_empty_question_is_answered_with_help_not_an_error()
    {
        var emp = await f.LoginAs(f.Employee);
        var a = await Ask(emp, "   ");
        Assert.Equal("help", a.Intent);
    }

    [Fact]
    public async Task Suggestions_only_list_what_the_person_can_get_answered()
    {
        var emp = await f.LoginAs(f.Employee);
        var (status, list) = await emp.Get("/api/me/ask/suggestions");
        Assert.Equal(HttpStatusCode.OK, status);
        var questions = list.EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("How many leave days do I have?", questions);
        Assert.DoesNotContain("What is waiting for my approval?", questions);   // an employee has no approvals right

        // the administrator has no employee record, so the whole "my" area is closed to that login
        var adm = await f.LoginAs(ApiFixture.Admin);
        var (admStatus, _) = await adm.Get("/api/me/ask/suggestions");
        Assert.Equal(HttpStatusCode.Forbidden, admStatus);
    }

    [Fact]
    public async Task A_login_without_an_employee_record_is_refused_instead_of_seeing_anyones_data()
    {
        var adm = await f.LoginAs(ApiFixture.Admin);
        var (status, body) = await adm.Get("/api/me/ask?q=" + Uri.EscapeDataString("how many leave days do I have"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("not linked to an employee", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Asking_needs_a_signed_in_user()
    {
        var res = await f.Anonymous().GetAsync("/api/me/ask?q=hello");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
