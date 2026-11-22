using Dapper;

namespace Hrms.Api;

// The organisation chart is read from what is already set up: the department each person is in, the department's first approver, and the people who hold the
// leave-approvals right at the HR and Director levels. Nothing here is stored; it is worked out each time.

public record OrgNode(string Id, string Name, string? Title, string? RoleName, int Level, string? Dept);
public record OrgView(OrgNode Me, List<OrgNode> Chain, string? Dept, List<OrgNode> Team, List<OrgNode> Reports, string? Note);
public record OrgDept(int Id, string Name, OrgNode? Head, List<OrgNode> Members);
public record OrgCompany(List<OrgNode> Leaders, List<OrgNode> Hr, List<OrgDept> Departments, List<OrgNode> Unassigned);

public static class OrgChartEndpoints
{
    sealed class P { public string EmpCode { get; set; } = ""; public string Name { get; set; } = ""; public string? Designation { get; set; } }
    sealed class U { public string UserId { get; set; } = ""; public string Name { get; set; } = ""; public string? RoleName { get; set; } public int Level { get; set; } }

    static string Pretty(string s) => s == s.ToUpperInvariant() || s == s.ToLowerInvariant() ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()) : s;

    static async Task<Dictionary<string, U>> Approvers(Microsoft.Data.SqlClient.SqlConnection c) =>
        (await c.QueryAsync<U>(@"SELECT u.userid AS UserId, ISNULL(NULLIF(u.name,''), u.username) AS Name, r.rolename AS RoleName, CAST(a.[level] AS int) AS Level
            FROM interface.Userdef u JOIN interface.Roledef r ON r.roleid = u.roleid
            JOIN interface.accessright a ON a.roleid = u.roleid AND a.optionid = @opt AND CAST(a.access AS int) = 1
            WHERE ISNULL(u.[disable],0) = 0", new { opt = Workflow.ApprovalOption })).ToDictionary(x => x.UserId, StringComparer.OrdinalIgnoreCase);

    public static void MapOrgChart(this WebApplication app)
    {
        app.MapGet("/api/me/org", async (Db db, HttpContext ctx) =>
        {
            var me = (AuthUser)ctx.Items["user"]!;
            var code = me.EmployeeCode ?? me.UserId;
            await using var c = db.Open();
            var rule = await LeaveRouting.Rule(c, null);
            var approvers = await Approvers(c);
            var myNode = new OrgNode(code, Pretty(me.FullName), null, me.RoleName, approvers.TryGetValue(me.UserId, out var mu) ? mu.Level : 0, null);
            string? dept = null; string? headId = null; int deptId = 0;
            try
            {
                var d = await c.QueryFirstOrDefaultAsync<(int Id, string Name, string? Head)?>("SELECT d.Id, d.Name, d.ApproverUserId FROM hrms.EmployeeDepartment e JOIN hrms.Department d ON d.Id = e.DeptId WHERE e.EmpCode = @code", new { code });
                if (d is { } dd) { dept = dd.Name; headId = dd.Head; deptId = dd.Id; }
            }
            catch { }
            OrgNode N(U u) => new(u.UserId, Pretty(u.Name), null, u.RoleName, u.Level, null);
            var chain = new List<OrgNode>();
            var isManager = approvers.ContainsKey(me.UserId);
            var directors = approvers.Values.Where(u => u.Level >= rule.ManagerFinal && !string.Equals(u.UserId, me.UserId, StringComparison.OrdinalIgnoreCase)).OrderBy(u => u.Name).Select(N).ToList();
            var hr = approvers.Values.Where(u => u.Level >= rule.EmployeeFinal && u.Level < rule.ManagerFinal && !string.Equals(u.UserId, me.UserId, StringComparison.OrdinalIgnoreCase)).OrderBy(u => u.Name).Select(N).ToList();
            var myLevel = myNode.Level;
            if (myLevel < rule.ManagerFinal) chain.AddRange(directors);
            if (myLevel < rule.EmployeeFinal) chain.AddRange(hr);
            if (headId is not null && !string.Equals(headId, me.UserId, StringComparison.OrdinalIgnoreCase) && approvers.TryGetValue(headId, out var head)) chain.Add(N(head) with { Dept = dept });

            var team = new List<OrgNode>(); var reports = new List<OrgNode>();
            try
            {
                var heads = (await c.QueryAsync<int>("SELECT Id FROM hrms.Department WHERE ApproverUserId = @u AND Active = 1", new { u = me.UserId })).ToList();
                var ids = heads.Append(deptId).Where(i => i > 0).Distinct().ToArray();
                if (ids.Length > 0)
                {
                    var people = (await c.QueryAsync<(string EmpCode, string Name, string? Designation, int DeptId, string DeptName)>(Sql.EmpCte + @"
SELECT e.EmpCode, e.Name, e.Designation, d.Id AS DeptId, d.Name AS DeptName FROM emp e JOIN hrms.EmployeeDepartment ed ON ed.EmpCode = e.EmpCode JOIN hrms.Department d ON d.Id = ed.DeptId
WHERE e.Status = 'active' AND d.Id IN @ids ORDER BY e.Name", new { ids })).ToList();
                    foreach (var p in people.Where(p => !string.Equals(p.EmpCode, code, StringComparison.OrdinalIgnoreCase)))
                    {
                        var node = new OrgNode(p.EmpCode, Pretty(p.Name), p.Designation, null, 0, p.DeptName);
                        if (heads.Contains(p.DeptId)) reports.Add(node); else if (p.DeptId == deptId) team.Add(node);
                    }
                }
            }
            catch { }
            return Results.Ok(new OrgView(myNode, chain, dept, team.Take(60).ToList(), reports.Take(120).ToList(), dept is null && !isManager ? "You have not been placed in a department yet. Ask HR." : null));
        });

        // the whole company for HR: leaders, HR, then each department with its head and people
        app.MapGet("/api/admin/org", async (Db db) =>
        {
            await using var c = db.Open();
            var rule = await LeaveRouting.Rule(c, null);
            var approvers = await Approvers(c);
            OrgNode N(U u) => new(u.UserId, Pretty(u.Name), null, u.RoleName, u.Level, null);
            var leaders = approvers.Values.Where(u => u.Level >= rule.ManagerFinal).OrderBy(u => u.Name).Select(N).ToList();
            var hr = approvers.Values.Where(u => u.Level >= rule.EmployeeFinal && u.Level < rule.ManagerFinal).OrderBy(u => u.Name).Select(N).ToList();
            var depts = (await c.QueryAsync<(int Id, string Name, string? Head)>("SELECT Id, Name, ApproverUserId FROM hrms.Department WHERE Active = 1 ORDER BY Name")).ToList();
            var people = (await c.QueryAsync<(string EmpCode, string Name, string? Designation, int? DeptId)>(Sql.EmpCte + @"
SELECT e.EmpCode, e.Name, e.Designation, ed.DeptId FROM emp e LEFT JOIN hrms.EmployeeDepartment ed ON ed.EmpCode = e.EmpCode WHERE e.Status = 'active' ORDER BY e.Name")).ToList();
            var outDepts = depts.Select(d => new OrgDept(d.Id, d.Name, d.Head is not null && approvers.TryGetValue(d.Head, out var h) ? N(h) : null,
                people.Where(p => p.DeptId == d.Id).Select(p => new OrgNode(p.EmpCode, Pretty(p.Name), p.Designation, null, 0, d.Name)).ToList())).ToList();
            var loose = people.Where(p => p.DeptId is null).Take(200).Select(p => new OrgNode(p.EmpCode, Pretty(p.Name), p.Designation, null, 0, null)).ToList();
            return Results.Ok(new OrgCompany(leaders, hr, outDepts, loose));
        });
    }
}
