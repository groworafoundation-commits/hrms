using System.Net;
using Hrms.Api;
using Xunit;

namespace Hrms.Tests;

// The rule-based payroll and attendance checks for HR.
[Collection("api")]
public class ChecksTests(ApiFixture f)
{
    [Fact]
    public async Task The_checks_run_on_the_real_data_and_return_ordered_findings()
    {
        await using var c = f.Open();
        var r = await Checks.Run(c, null, 20, 5, 3, 3);
        Assert.True(r.Employees > 0);
        Assert.NotNull(r.PayrollMonth);
        Assert.All(r.Findings, x => Assert.Contains(x.Severity, new[] { "High", "Medium", "Low" }));
        Assert.All(r.Findings, x => Assert.False(string.IsNullOrWhiteSpace(x.Detail)));
        // most serious first
        var rank = r.Findings.Select(x => x.Severity == "High" ? 0 : x.Severity == "Medium" ? 1 : 2).ToList();
        Assert.Equal(rank.OrderBy(x => x), rank);
    }

    [Fact]
    public async Task A_very_strict_threshold_finds_at_least_as_much_as_a_loose_one()
    {
        await using var c = f.Open();
        var loose = await Checks.Run(c, null, 500, 31, 31, 31);
        var strict = await Checks.Run(c, null, 5, 1, 1, 1);
        Assert.True(strict.Findings.Count >= loose.Findings.Count);
    }

    [Fact]
    public async Task An_ordinary_employee_cannot_open_the_checks()
    {
        var emp = await f.LoginAs(f.Employee);
        var (status, _) = await emp.Get("/api/admin/checks");
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Asking_for_the_checks_needs_a_signed_in_user()
    {
        var res = await f.Anonymous().GetAsync("/api/admin/checks");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
