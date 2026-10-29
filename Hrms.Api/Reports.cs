using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// The ERP's Payroll Reports menu (Reports > Payroll Reports). In the ERP each entry opens a Crystal Reports file
// (EmployeeMaster.rpt, ProcessedSalarybreakupLaunch.rpt, ...). Those files are compiled, so each report here is rebuilt
// from the same ERP tables and views, with the same filters (employee, designation, location, month).

public record ReportColumn(string Key, string Label, string Type, bool Total = false);
public record ReportDef(string Id, string Name, string Group, string Description, string[] Filters, string ErpMenu);
public record ReportResult(string Id, string Title, string Subtitle, List<ReportColumn> Columns, List<Dictionary<string, object?>> Rows,
    Dictionary<string, decimal> Totals, string? Note)
{
    // Extra per-employee header data (used by the pay slip layout).
    public List<Dictionary<string, object?>> Extra { get; init; } = [];
}

public record CompanyInfo(string Name, List<string> AddressLines, string? Phone, string? Fax, string? Email, string? VatNo, string Currency, string? Logo);

public record ReportQuery(string? Month, string? Employee, string? Designation, string? Location, string Status, DateTime? AsOf, bool IncludeZero);

public static class Reports
{
    // ---------- catalogue ----------

    public static readonly ReportDef[] Catalogue =
    [
        new("employee-master", "Employee master", "Employees", "Every employee with personal and employment details.", ["employee", "designation", "status"], "797 Employee Master"),
        new("employee-extra-details", "Employee extra details", "Employees", "Latest extra details: bank, payment mode, documents, contract dates.", ["employee", "designation", "status"], "798 Employee Extra Details"),
        new("employee-grade-details", "Employee grade details", "Salary", "Fixed salary structure by effective date.", ["employee", "designation", "status"], "799 Employee Grade Details"),
        new("employee-advances-loans", "Employee advances and loans", "Salary", "Loans and advances issued, recovered and outstanding.", ["employee", "designation", "status", "includeZero"], "7990 Employee Advances and Loans"),
        new("provision", "Provision report", "Accruals", "Monthly leave salary, end of service, leave days and air ticket provisions.", ["month", "employee", "designation"], "160 Provision Report"),
        new("salary-breakup", "Salary breakup", "Salary", "Fixed salary components per employee as of a date.", ["asOf", "employee", "designation", "status"], "123 Salary Breakup Report"),
        new("processed-salary-breakup", "Processed salary breakup", "Salary", "Processed payroll for a month with every addition and deduction.", ["month", "employee", "designation", "location"], "162 Processed Salary Breakup Report"),
        new("payslip", "Pay slip", "Salary", "Pay slip lines for a month: earnings, deductions and net pay.", ["month", "employee", "designation"], "163 Pay Slip"),
        new("salary-summary", "Salary summary", "Salary", "Payroll totals by salary head for a month.", ["month", "designation", "location"], "164 Salary Summary"),
        new("wps", "WPS", "Salary", "Wages Protection System file: net salary paid through the bank.", ["month", "employee", "designation"], "795 WPS"),
        new("wps-balance", "WPS balance", "Salary", "Net salary not paid through WPS (cash balance) for a month.", ["month", "employee", "designation", "includeZero"], "7950 WPS Balance"),
        new("asset-in-hand", "Asset in hand", "Employees", "Assets issued to employees and not yet returned.", ["employee"], "167 Asset In Hand"),
        new("document-expiry", "Document expiry", "Employees", "Documents held by employees with their expiry dates.", ["employee", "asOf"], "169 Document Expiary Report"),
        new("all-payment-details", "All payment details", "Salary", "Every salary and loan head ever set for an employee, by effective date.", ["employee", "designation"], "205 All Payment Details"),
        new("accrual", "Accrual report", "Accruals", "Accrued leave salary, end of service and air ticket balances at a month end.", ["month", "employee", "designation"], "206 Accrual Report"),
        new("final-settlement", "Employee final settlement", "Accruals", "Final settlement amounts for employees who have left.", ["employee"], "1150 Employee Final Settlement Report"),
    ];

    // Lines that make up a pay slip. Amounts use the "C" columns (calculated for the days worked), which add up to
    // EmployeePaymentProcess.Addition and .Deduction.
    const string PayLines = @"
CROSS APPLY (VALUES
 ('Earnings', 'Basic salary', p.CBASIC, 1),
 ('Earnings', 'Accommodation allowance', p.[CAccomodation Allowance], 2),
 ('Earnings', 'Mobile allowance', p.[CMobile Allowance], 3),
 ('Earnings', 'Position allowance', p.[CPosition Allowance], 4),
 ('Earnings', 'Sewa allowance', p.[CSewa Allowance], 5),
 ('Earnings', 'Travel allowance 1', p.[CTravel Allowance1], 6),
 ('Earnings', 'Travel allowance 2', p.[CTravel Allowance2], 7),
 ('Earnings', 'Travel allowance 3', p.[CTravel Allowance3], 8),
 ('Earnings', 'Commission', p.COMISSION, 9),
 ('Earnings', 'Award', p.Award, 10),
 ('Earnings', 'Bonus', p.Bonus, 11),
 ('Earnings', 'Overnight allowance', p.COvernightAllowance, 12),
 ('Earnings', 'International travel allowance', p.CInternationTravelAllowance, 13),
 ('Earnings', 'Holiday work allowance', p.CHolidayWorkAllowance, 14),
 ('Deductions', 'Loan recovery', p.[Loan Deducted], 21),
 ('Deductions', 'Advance recovery', p.[Advance Deducted], 22),
 ('Deductions', 'Mobile bill', p.DeductMobileBill, 23),
 ('Deductions', 'Sewa bill', p.DeductSewaBill, 24),
 ('Deductions', 'Elife bill', p.DeductElifeBill, 25),
 ('Deductions', 'Fines (memo)', p.FinesOverMemo, 26),
 ('Deductions', 'Penalty', p.PENALTY, 27),
 ('Deductions', 'RTA fines', p.DeductRTAFines, 28)
) v(sec, item, amt, ord)";

    const string Prelude = Sql.EmpCte + @",
dsg AS (SELECT Code, Name FROM inter01.dg_masters WHERE typecode='DSG'),
loc AS (SELECT EmpCode, ToLocation FROM (SELECT EmpCode, ToLocation, ROW_NUMBER() OVER (PARTITION BY EmpCode ORDER BY Date DESC, Id DESC) rn FROM inter01.EmployeeTransfer) t WHERE rn = 1),
b AS (SELECT e.*, d.Name AS DesignationName, l.ToLocation AS Location FROM emp e LEFT JOIN dsg d ON d.Code = e.Designation LEFT JOIN loc l ON l.EmpCode = e.EmpCode)";

    const string Who = "(@emp IS NULL OR b.EmpCode = @emp) AND (@dsg IS NULL OR b.Designation = @dsg) AND (@loc IS NULL OR b.Location = @loc)";
    const string InMonth = "p.ProcessedDate >= @from AND p.ProcessedDate < @to";

    static ReportColumn T(string k, string l) => new(k, l, "text");
    static ReportColumn M(string k, string l, bool total = true) => new(k, l, "money", total);
    static ReportColumn N(string k, string l, bool total = false) => new(k, l, "number", total);
    static ReportColumn Dt(string k, string l) => new(k, l, "date");

    // ---------- entry point ----------

    public static async Task<ReportResult> Run(SqlConnection c, string id, ReportQuery q)
    {
        var def = Catalogue.FirstOrDefault(r => r.Id == id) ?? throw new ApiException($"Unknown report '{id}'.", 404);
        var month = q.Month;
        if (def.Filters.Contains("month") && string.IsNullOrEmpty(month))
            month = await c.ExecuteScalarAsync<string>("SELECT TOP 1 CONVERT(varchar(7),ProcessedDate,120) FROM inter01.EmployeePaymentProcess ORDER BY ProcessedDate DESC");
        var first = DateTime.TryParse((month ?? "") + "-01", out var m) ? m : DateTime.Today;
        var p = new
        {
            emp = Nz(q.Employee), dsg = Nz(q.Designation), loc = Nz(q.Location), status = q.Status, from = first, to = first.AddMonths(1),
            asOf = (q.AsOf ?? DateTime.Today).Date, zero = q.IncludeZero ? 1 : 0
        };
        var subtitle = new List<string>();
        if (def.Filters.Contains("month") && month is not null) subtitle.Add(first.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        if (def.Filters.Contains("asOf")) subtitle.Add("as of " + p.asOf.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        if (p.emp is not null) subtitle.Add("employee " + p.emp);
        if (p.dsg is not null) subtitle.Add("designation " + p.dsg);
        if (p.loc is not null) subtitle.Add("location " + p.loc);

        var (cols, sql, note) = Build(id);
        if (sql is null)
            return new ReportResult(id, def.Name, string.Join(" | ", subtitle), cols, [], [], note);

        // Asset and document reports read the large transaction tables. Skip the heavy query when the company has none of that type.
        if (id is "asset-in-hand" or "document-expiry")
        {
            var type = id == "asset-in-hand" ? "800" : "713";
            if (await c.ExecuteScalarAsync<int?>("SELECT TOP 1 1 FROM inter01.inv_head WHERE transactiontype = @type", new { type = new DbString { Value = type, IsAnsi = true, Length = 10 } }, commandTimeout: 120) is null)
                return new ReportResult(id, def.Name, string.Join(" | ", subtitle), cols, [], [], note);
        }

        var rows = new List<Dictionary<string, object?>>();
        foreach (var r in await c.QueryAsync(sql, p, commandTimeout: 120))
        {
            var d = (IDictionary<string, object>)r;
            rows.Add(d.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value is DBNull ? null : kv.Value));
        }
        PostProcess(id, rows);
        var extra = new List<Dictionary<string, object?>>();
        if (id == "payslip")
        {
            foreach (var r in await c.QueryAsync(Prelude + @"
SELECT p.Empcode AS code, b.Name AS name, ISNULL(b.DesignationName, p.Designation) AS designation, p.Doj AS doj, b.Bank AS bank, b.BankAccount AS account, b.PaymentMode AS mop,
       ISNULL(p.WorkedDays,0) AS workedDays, ISNULL(p.LeaveDays,0) AS leaveDays, p.ProcessedDate AS processed
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + " ORDER BY b.Name", p, commandTimeout: 120))
                extra.Add(((IDictionary<string, object>)r).ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value is DBNull ? null : kv.Value));
        }
        if (rows.Count > 0 && id is "asset-in-hand" or "document-expiry") note = null;
        var totals = new Dictionary<string, decimal>();
        foreach (var col in cols.Where(x => x.Total))
            totals[col.Key] = rows.Sum(x => x.TryGetValue(col.Key.ToLowerInvariant(), out var v) && v is not null ? Convert.ToDecimal(v) : 0m);
        return new ReportResult(id, def.Name, string.Join(" | ", subtitle), cols, rows, totals, note) { Extra = extra };
    }

    static object? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    // Reports 12 and 13 read the ERP's asset and document transactions; they are filled in by BuildTransactional.
    static (List<ReportColumn> Cols, string? Sql, string? Note) Build(string id) => id switch
    {
        "employee-master" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), T("gender", "Gender"), Dt("dob", "Date of birth"), T("nationality", "Nationality"),
            T("marital", "Marital status"), T("phone", "Phone"), T("officeNo", "Office number"), Dt("doj", "Joined"), Dt("termdate", "Terminated"), T("status", "Status"), T("remarks", "Remarks")],
            Prelude + @"
SELECT b.EmpCode AS code, b.Name AS name, ISNULL(b.DesignationName, b.Designation) AS designation, b.Gender AS gender, b.Dob AS dob, b.Country AS nationality, b.MaritalStatus AS marital,
       b.Phone AS phone, b.OfficeNo AS officeNo, b.Doj AS doj, b.TermDate AS termdate, b.Status AS status, b.Remarks AS remarks
FROM b WHERE " + Who + " AND (@status = 'all' OR b.Status = @status) ORDER BY b.Name", null),

        "employee-extra-details" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), T("company", "Company"), T("bank", "Bank"), T("ebank", "Employee bank"), T("account", "Account"),
            T("mop", "Payment mode"), T("docno", "Document no."), N("ticket", "Ticket period (yrs)"), T("entitled", "Entitled days"), Dt("doj", "Joined"), Dt("confdate", "Confirmed"),
            Dt("contdate", "Contract"), Dt("termdate", "Terminated"), T("route", "Route"), T("quals", "Qualifications"), Dt("effective", "Effective"), T("transno", "Reference")],
            Prelude + @",
x2 AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY EmpCode ORDER BY effectivedate DESC, Id DESC) rn FROM inter01.EmployeeExtraDetails WHERE ISNULL(deleteflag,0)=0)
SELECT b.EmpCode AS code, b.Name AS name, ISNULL(b.DesignationName, b.Designation) AS designation, x2.Company AS company, x2.bank AS bank, x2.ebank AS ebank, x2.accode AS account,
       x2.mop AS mop, x2.docno AS docno, x2.tktperiod AS ticket, x2.txt7 AS entitled, x2.doj AS doj, x2.confdate AS confdate, x2.contdate AS contdate, x2.termdate AS termdate, x2.route AS route,
       LTRIM(RTRIM(ISNULL(x2.bqual,'') + ' ' + ISNULL(x2.oqual,'') + ' ' + ISNULL(x2.pqual,''))) AS quals, x2.effectivedate AS effective, x2.transno AS transno
FROM b JOIN x2 ON x2.EmpCode = b.EmpCode AND x2.rn = 1 WHERE " + Who + " AND (@status = 'all' OR b.Status = @status) ORDER BY b.Name", null),

        "employee-grade-details" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), Dt("effective", "Effective"), M("basic", "Basic"), M("accommodation", "Accommodation"), M("mobile", "Mobile"),
            M("position", "Position"), M("sewa", "Sewa"), M("travel", "Travel"), M("total", "Total fixed"), M("wps", "WPS part")],
            Prelude + @"
SELECT b.EmpCode AS code, b.Name AS name, ISNULL(b.DesignationName, b.Designation) AS designation, d.EffectiveDate AS effective,
  SUM(CASE WHEN d.SalaryType='BSC' THEN d.Amt ELSE 0 END) AS basic, SUM(CASE WHEN d.SalaryType='ACCAllow' THEN d.Amt ELSE 0 END) AS accommodation,
  SUM(CASE WHEN d.SalaryType='MobileAllow' THEN d.Amt ELSE 0 END) AS mobile, SUM(CASE WHEN d.SalaryType='PositionAllow' THEN d.Amt ELSE 0 END) AS position,
  SUM(CASE WHEN d.SalaryType='SewaAllow' THEN d.Amt ELSE 0 END) AS sewa, SUM(CASE WHEN d.SalaryType IN ('TA1','TA2','TA3') THEN d.Amt ELSE 0 END) AS travel,
  SUM(CASE WHEN d.SalaryType IN ('BSC','ACCAllow','MobileAllow','PositionAllow','SewaAllow','TA1','TA2','TA3') THEN d.Amt ELSE 0 END) AS total,
  SUM(CASE WHEN d.SalaryType='FixedWPS' THEN d.Amt ELSE 0 END) AS wps
FROM inter01.EmployeePaymentDetails d JOIN b ON b.EmpCode = d.EmpCode
WHERE ISNULL(d.Deleteflag,0)=0 AND d.PaymentType='FIX' AND " + Who + @" AND (@status = 'all' OR b.Status = @status)
GROUP BY b.EmpCode, b.Name, b.DesignationName, b.Designation, d.EffectiveDate ORDER BY b.Name, d.EffectiveDate DESC", null),

        "employee-advances-loans" => ([T("code", "Code"), T("name", "Name"), T("status", "Status"), M("loans", "Loans"), M("advances", "Advances"), M("deducted", "Recovered via payroll"), M("returned", "Returned"),
            M("outstanding", "Outstanding"), Dt("last", "Last entry")],
            Prelude + @",
iss AS (SELECT Empcode, SUM(CASE WHEN Type='LOAN' THEN Amount ELSE 0 END) Loans, SUM(CASE WHEN Type='ADVAMT' THEN Amount ELSE 0 END) Advances,
        SUM(CASE WHEN Type IN ('LOREC','ADVREC') THEN Amount ELSE 0 END) Returned, MAX(Date) LastDate
        FROM inter01.EMPVARIABLESALARY WHERE ISNULL(Deleteflag,0)=0 AND ISNULL(Variabledelete,0)=0 AND Type IN ('LOAN','ADVAMT','LOREC','ADVREC') GROUP BY Empcode),
ded AS (SELECT Empcode, SUM(ISNULL([Loan Deducted],0) + ISNULL([Advance Deducted],0)) Deducted FROM inter01.EmployeePaymentProcess GROUP BY Empcode),
r AS (SELECT i.Empcode, i.Loans, i.Advances, ISNULL(d.Deducted,0) Deducted, i.Returned, i.LastDate,
      CASE WHEN i.Loans + i.Advances - ISNULL(d.Deducted,0) - i.Returned < 0 THEN 0 ELSE i.Loans + i.Advances - ISNULL(d.Deducted,0) - i.Returned END Outstanding
      FROM iss i LEFT JOIN ded d ON d.Empcode = i.Empcode WHERE i.Loans + i.Advances > 0)
SELECT b.EmpCode AS code, b.Name AS name, b.Status AS status, r.Loans AS loans, r.Advances AS advances, r.Deducted AS deducted, r.Returned AS returned, r.Outstanding AS outstanding, r.LastDate AS last
FROM r JOIN b ON b.EmpCode = r.Empcode WHERE " + Who + @" AND (@status = 'all' OR b.Status = @status) AND (@zero = 1 OR r.Outstanding > 0)
ORDER BY r.Outstanding DESC, b.Name", null),

        "provision" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), N("workedDays", "Days worked"), M("alsMonth", "Leave salary (month)"), M("alsTotal", "Leave salary (accrued)"),
            M("eosMonth", "End of service (month)"), M("eosTotal", "End of service (accrued)"), N("leaveMonth", "Leave days (month)", true), N("leaveTotal", "Leave days (accrued)", true),
            M("ticketMonth", "Air ticket (month)"), M("ticketTotal", "Air ticket (accrued)")],
            Prelude + @"
SELECT p.Empcode AS code, b.Name AS name, ISNULL(b.DesignationName, p.Designation) AS designation, ISNULL(p.WorkedDays,0) AS workedDays,
  ISNULL(p.CAnnualLeaveSalary,0) AS alsMonth, ISNULL(p.CTMAnnualLeaveSalary,0) AS alsTotal, ISNULL(p.CEndofService,0) AS eosMonth, ISNULL(p.CTMEndofService,0) AS eosTotal,
  ISNULL(p.[CAnnual Leave],0) AS leaveMonth, ISNULL(p.[CTMAnnual Leave],0) AS leaveTotal, ISNULL(p.CAirTicket,0) AS ticketMonth, ISNULL(p.CTMAirTicket,0) AS ticketTotal
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + " ORDER BY b.Name", null),

        "salary-breakup" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), M("basic", "Basic"), M("accommodation", "Accommodation"), M("mobile", "Mobile"), M("position", "Position"),
            M("sewa", "Sewa"), M("travel", "Travel"), M("total", "Total fixed"), M("wps", "WPS part"), M("cash", "Balance (cash)")],
            Prelude + @",
pay AS (SELECT EmpCode, SalaryType, Amt, ROW_NUMBER() OVER (PARTITION BY EmpCode, SalaryType ORDER BY EffectiveDate DESC, Id DESC) rn
        FROM inter01.EmployeePaymentDetails WHERE ISNULL(Deleteflag,0)=0 AND PaymentType='FIX' AND EffectiveDate <= @asOf)
SELECT b.EmpCode AS code, b.Name AS name, ISNULL(b.DesignationName, b.Designation) AS designation,
  SUM(CASE WHEN p.SalaryType='BSC' THEN p.Amt ELSE 0 END) AS basic, SUM(CASE WHEN p.SalaryType='ACCAllow' THEN p.Amt ELSE 0 END) AS accommodation,
  SUM(CASE WHEN p.SalaryType='MobileAllow' THEN p.Amt ELSE 0 END) AS mobile, SUM(CASE WHEN p.SalaryType='PositionAllow' THEN p.Amt ELSE 0 END) AS position,
  SUM(CASE WHEN p.SalaryType='SewaAllow' THEN p.Amt ELSE 0 END) AS sewa, SUM(CASE WHEN p.SalaryType IN ('TA1','TA2','TA3') THEN p.Amt ELSE 0 END) AS travel,
  SUM(CASE WHEN p.SalaryType IN ('BSC','ACCAllow','MobileAllow','PositionAllow','SewaAllow','TA1','TA2','TA3') THEN p.Amt ELSE 0 END) AS total,
  SUM(CASE WHEN p.SalaryType='FixedWPS' THEN p.Amt ELSE 0 END) AS wps,
  SUM(CASE WHEN p.SalaryType IN ('BSC','ACCAllow','MobileAllow','PositionAllow','SewaAllow','TA1','TA2','TA3') THEN p.Amt ELSE 0 END) - SUM(CASE WHEN p.SalaryType='FixedWPS' THEN p.Amt ELSE 0 END) AS cash
FROM b JOIN pay p ON p.EmpCode = b.EmpCode AND p.rn = 1 WHERE " + Who + @" AND (@status = 'all' OR b.Status = @status)
GROUP BY b.EmpCode, b.Name, b.DesignationName, b.Designation ORDER BY b.Name", null),

        "processed-salary-breakup" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), N("workedDays", "Days worked"), N("leaveDays", "Leave days"), M("basic", "Basic"),
            M("accommodation", "Accommodation"), M("mobile", "Mobile"), M("position", "Position"), M("sewa", "Sewa"), M("travel", "Travel"), M("commission", "Commission"), M("award", "Award"),
            M("bonus", "Bonus"), M("otherAllowances", "Other allowances"), M("additions", "Total additions"), M("loan", "Loan recovery"), M("advance", "Advance recovery"),
            M("otherDeductions", "Other deductions"), M("deductions", "Total deductions"), M("net", "Net salary")],
            Prelude + @"
SELECT p.Empcode AS code, b.Name AS name, ISNULL(b.DesignationName, p.Designation) AS designation, ISNULL(p.WorkedDays,0) AS workedDays, ISNULL(p.LeaveDays,0) AS leaveDays,
  ISNULL(p.CBASIC,0) AS basic, ISNULL(p.[CAccomodation Allowance],0) AS accommodation, ISNULL(p.[CMobile Allowance],0) AS mobile, ISNULL(p.[CPosition Allowance],0) AS position,
  ISNULL(p.[CSewa Allowance],0) AS sewa, ISNULL(p.[CTravel Allowance1],0)+ISNULL(p.[CTravel Allowance2],0)+ISNULL(p.[CTravel Allowance3],0) AS travel,
  ISNULL(p.COMISSION,0) AS commission, ISNULL(p.Award,0) AS award, ISNULL(p.Bonus,0) AS bonus,
  ISNULL(p.COvernightAllowance,0)+ISNULL(p.CInternationTravelAllowance,0)+ISNULL(p.CHolidayWorkAllowance,0) AS otherAllowances, ISNULL(p.Addition,0) AS additions,
  ISNULL(p.[Loan Deducted],0) AS loan, ISNULL(p.[Advance Deducted],0) AS advance, ISNULL(p.OtherVariableDeductions,0) AS otherDeductions, ISNULL(p.Deduction,0) AS deductions, ISNULL(p.CNet,0) AS net
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + " ORDER BY b.Name", null),

        "payslip" => ([T("code", "Code"), T("name", "Name"), T("section", "Section"), T("item", "Item"), M("amount", "Amount", false)],
            Prelude + @"
SELECT p.Empcode AS code, b.Name AS name, v.sec AS section, v.item AS item, v.amt AS amount, v.ord AS ord
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode" + PayLines + @"
WHERE " + InMonth + " AND " + Who + @" AND ISNULL(v.amt,0) <> 0
UNION ALL
SELECT p.Empcode, b.Name, 'Net pay', 'Net salary', p.CNet, 99 FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + @"
ORDER BY name, code, ord", null),

        "salary-summary" => ([T("section", "Section"), T("item", "Salary head"), N("employees", "Employees"), M("amount", "Total", false)],
            Prelude + @"
SELECT v.sec AS section, v.item AS item, COUNT(*) AS employees, SUM(v.amt) AS amount, v.ord AS ord
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode" + PayLines + @"
WHERE " + InMonth + " AND " + Who + @" AND ISNULL(v.amt,0) <> 0 GROUP BY v.sec, v.item, v.ord
UNION ALL
SELECT 'Net pay', 'Net salary', COUNT(*), SUM(ISNULL(p.CNet,0)), 99 FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + @"
ORDER BY ord", "Net pay equals total earnings minus total deductions."),

        "wps" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), T("bank", "Bank"), T("account", "Account"), M("net", "Net salary"), M("fixedWps", "Fixed WPS"), M("variableWps", "Variable WPS"),
            M("wps", "Total WPS")],
            Prelude + @"
SELECT p.Empcode AS code, b.Name AS name, ISNULL(b.DesignationName, p.Designation) AS designation, b.Bank AS bank, b.BankAccount AS account, ISNULL(p.CNet,0) AS net,
  ISNULL(p.FixedWPS,0) AS fixedWps, ISNULL(p.VariableWPS,0) AS variableWps, ISNULL(p.FixedWPS,0) + ISNULL(p.VariableWPS,0) AS wps
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + @" AND ISNULL(p.FixedWPS,0) + ISNULL(p.VariableWPS,0) <> 0 ORDER BY b.Name", null),

        "wps-balance" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), M("net", "Net salary"), M("wps", "Paid through WPS"), M("balance", "Balance (cash)")],
            Prelude + @"
SELECT p.Empcode AS code, b.Name AS name, ISNULL(b.DesignationName, p.Designation) AS designation, ISNULL(p.CNet,0) AS net, ISNULL(p.FixedWPS,0) + ISNULL(p.VariableWPS,0) AS wps,
  ISNULL(p.WPSBalance,0) AS balance
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + @" AND (@zero = 1 OR ISNULL(p.WPSBalance,0) <> 0) ORDER BY b.Name", null),

        "all-payment-details" => ([T("code", "Code"), T("name", "Name"), Dt("effective", "Effective"), T("head", "Head"), T("kind", "Type"), M("amount", "Amount", false)],
            Prelude + @"
SELECT d.EmpCode AS code, b.Name AS name, d.EffectiveDate AS effective, d.SalaryType AS head, d.PaymentType AS kind, d.Amt AS amount
FROM inter01.EmployeePaymentDetails d JOIN b ON b.EmpCode = d.EmpCode WHERE ISNULL(d.Deleteflag,0)=0 AND " + Who + @" ORDER BY b.Name, d.EffectiveDate DESC, d.SalaryType", null),

        "accrual" => ([T("code", "Code"), T("name", "Name"), T("designation", "Designation"), M("alsOpening", "Leave salary (opening)"), M("alsMonth", "Leave salary (month)"), M("alsClosing", "Leave salary (closing)"),
            M("eosOpening", "End of service (opening)"), M("eosMonth", "End of service (month)"), M("eosClosing", "End of service (closing)"), M("ticketClosing", "Air ticket (closing)"),
            M("alsPosted", "Leave salary posted"), M("eosPosted", "End of service posted"), M("paid", "Accrual payments")],
            Prelude + @"
SELECT p.Empcode AS code, b.Name AS name, ISNULL(b.DesignationName, p.Designation) AS designation,
  ISNULL(p.ACAnnualLeaveSalary,0) AS alsOpening, ISNULL(p.CAnnualLeaveSalary,0) AS alsMonth, ISNULL(p.CTMAnnualLeaveSalary,0) AS alsClosing,
  ISNULL(p.ACEndofService,0) AS eosOpening, ISNULL(p.CEndofService,0) AS eosMonth, ISNULL(p.CTMEndofService,0) AS eosClosing, ISNULL(p.CTMAirTicket,0) AS ticketClosing,
  ISNULL(p.ALVESALPost,0) AS alsPosted, ISNULL(p.EOSPost,0) AS eosPosted, ISNULL(p.AccrualPayments,0) AS paid
FROM inter01.EmployeePaymentProcess p LEFT JOIN b ON b.EmpCode = p.Empcode WHERE " + InMonth + " AND " + Who + " ORDER BY b.Name", null),

        "final-settlement" => ([T("code", "Code"), T("name", "Name"), Dt("joined", "Joined"), Dt("left", "Left on"), N("days", "Days worked (last month)"), M("eos", "End of service"), M("leaveSalary", "Pending leave salary"),
            M("net", "Last month net"), M("pending", "Pending loans and advances"), M("payable", "Total payable")],
            Prelude + @",
last AS (SELECT p.*, ROW_NUMBER() OVER (PARTITION BY p.Empcode ORDER BY p.ProcessedDate DESC, p.Id DESC) rn FROM inter01.EmployeePaymentProcess p),
iss AS (SELECT Empcode, SUM(CASE WHEN Type IN ('LOAN','ADVAMT') THEN Amount ELSE 0 END) - SUM(CASE WHEN Type IN ('LOREC','ADVREC') THEN Amount ELSE 0 END) AS Open1
        FROM inter01.EMPVARIABLESALARY WHERE ISNULL(Deleteflag,0)=0 AND ISNULL(Variabledelete,0)=0 AND Type IN ('LOAN','ADVAMT','LOREC','ADVREC') GROUP BY Empcode),
ded AS (SELECT Empcode, SUM(ISNULL([Loan Deducted],0) + ISNULL([Advance Deducted],0)) AS Deducted FROM inter01.EmployeePaymentProcess GROUP BY Empcode),
r AS (SELECT b.EmpCode, b.Name, b.TermDate, l.Doj, ISNULL(l.WorkedDays,0) AS Days, ISNULL(l.CTMEndofService,0) AS Eos, ISNULL(l.CTMAnnualLeaveSalary,0) AS Als, ISNULL(l.CNet,0) AS Net,
        CASE WHEN ISNULL(i.Open1,0) - ISNULL(d.Deducted,0) < 0 THEN 0 ELSE ISNULL(i.Open1,0) - ISNULL(d.Deducted,0) END AS Pending
      FROM b JOIN last l ON l.Empcode = b.EmpCode AND l.rn = 1 LEFT JOIN iss i ON i.Empcode = b.EmpCode LEFT JOIN ded d ON d.Empcode = b.EmpCode
      WHERE b.TermDate IS NOT NULL AND b.TermDate <= GETDATE() AND (@emp IS NULL OR b.EmpCode = @emp))
SELECT EmpCode AS code, Name AS name, Doj AS joined, TermDate AS [left], Days AS days, Eos AS eos, Als AS leaveSalary, Net AS net, Pending AS pending, Eos + Als + Net - Pending AS payable
FROM r ORDER BY TermDate DESC", "Worked out from each employee's last processed payroll accruals. The ERP applies its settlement formulas when it prints the final settlement, so confirm the figure there before paying."),

        "asset-in-hand" => ([T("code", "Code"), T("name", "Name"), T("asset", "Asset"), T("reference", "Reference"), Dt("issued", "Issued"), N("qty", "Quantity", true)],
            @"
SELECT h.salesmancode AS code, e.Empname AS name, ISNULL(i.itemmastername, f.itemno) AS asset, h.transno AS reference, h.date AS issued, f.qty AS qty
FROM inter01.inv_head h
JOIN inter01.inv_footer f ON f.serno = h.id
JOIN inter01.EmployeeMaster e ON e.EmpCode = h.salesmancode
LEFT JOIN inter01.itemmaster i ON i.itemmastercode = f.itemno
WHERE h.transactiontype = '800' AND h.userlevel = h.documentauthlevel AND f.deleteflag = 0 AND (@emp IS NULL OR h.salesmancode = @emp)
  AND NOT EXISTS (SELECT 1 FROM inter01.inv_head rh JOIN inter01.inv_footer rf ON rf.serno = rh.id
                  WHERE rh.transactiontype IN ('801','726') AND rh.salesmancode = h.salesmancode AND rf.itemno = f.itemno AND rf.deleteflag = 0 AND rh.date >= h.date)
ORDER BY e.Empname, h.date", "No asset issue transactions exist for this company yet."),
        "document-expiry" => ([T("code", "Code"), T("name", "Name"), T("document", "Document"), T("reference", "Reference"), Dt("expiry", "Expiry"), N("days", "Days left")],
            @"
SELECT h.salesmancode AS code, e.Empname AS name, ISNULL(NULLIF(f.Itemdiscription, ''), f.details) AS document, h.transno AS reference, f.resultdate AS expiry,
       DATEDIFF(day, @asOf, f.resultdate) AS days
FROM inter01.inv_head h
JOIN inter01.inv_footer f ON f.serno = h.id
JOIN inter01.EmployeeMaster e ON e.EmpCode = h.salesmancode
WHERE h.transactiontype = '713' AND h.userlevel = h.documentauthlevel AND f.deleteflag = 0 AND f.resultdate > '1901-01-01' AND (@emp IS NULL OR h.salesmancode = @emp)
ORDER BY f.resultdate", "No document expiry transactions exist for this company yet."),
        _ => throw new ApiException($"Unknown report '{id}'.", 404),
    };

    public record Option(string Code, string Name);
    public record ReportLookups(List<Option> Designations, List<string> Locations, List<string> Months);

    public static void MapReports(this WebApplication app)
    {
        app.MapGet("/api/reports", () => Catalogue);

        // Letterhead for printed reports, read from the ERP's company set-up (name, address, TRN and logo).
        app.MapGet("/api/company", async (Db db, HrmsSession session) =>
        {
            await using var c = db.Open();
            var row = await c.QueryFirstOrDefaultAsync(@"SELECT companyname, address1, address2, address3, address4, Phone, Fax, Email, CompanyVatno, BaseCurrency, CompanyLogo
                FROM HRMSINTER.interface.CompanySetUp WHERE CompanyCode = @code", new { code = session.CompanyCode }) as IDictionary<string, object>;
            if (row is null) return Results.Ok(new CompanyInfo(session.CompanyCode, [], null, null, null, null, "AED", null));
            string? Str(string k) => row.TryGetValue(k, out var v) && v is string s2 && !string.IsNullOrWhiteSpace(s2) ? s2.Trim().Trim(',') : null;
            string? logo = null;
            if (row.TryGetValue("CompanyLogo", out var bytes) && bytes is byte[] b && b.Length > 100)
                logo = (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "data:image/jpeg;base64," : "data:image/png;base64,") + Convert.ToBase64String(b);
            var lines = new[] { Str("address1"), Str("address2"), Str("address3"), Str("address4") }.Where(x => x is not null).Select(x => x!).ToList();
            return Results.Ok(new CompanyInfo(Str("companyname") ?? session.CompanyCode, lines, Str("Phone"), Str("Fax"), Str("Email"), Str("CompanyVatno"), Str("BaseCurrency") ?? "AED", logo));
        });

        app.MapGet("/api/reports/lookups", async (Db db) =>
        {
            await using var c = db.Open();
            var dsg = (await c.QueryAsync<Option>(@"SELECT DISTINCT d.Code, d.Name FROM inter01.dg_masters d WHERE d.typecode='DSG' AND d.Code IN (SELECT designation FROM inter01.EmployeeExtraDetails WHERE ISNULL(deleteflag,0)=0) ORDER BY d.Name")).ToList();
            var loc = (await c.QueryAsync<string>("SELECT DISTINCT v FROM (SELECT ToLocation v FROM inter01.EmployeeTransfer) x WHERE v IS NOT NULL AND v <> '' ORDER BY v")).ToList();
            var months = (await c.QueryAsync<string>("SELECT DISTINCT TOP 36 CONVERT(varchar(7),ProcessedDate,120) FROM inter01.EmployeePaymentProcess ORDER BY 1 DESC")).ToList();
            return new ReportLookups(dsg, loc, months);
        });

        app.MapGet("/api/reports/{id}", async (Db db, string id, string? month, string? employee, string? designation, string? location, string? status, DateTime? asOf, bool includeZero = false) =>
        {
            await using var c = db.Open();
            try { return Results.Ok(await Run(c, id, new ReportQuery(month, employee, designation, location, status ?? "active", asOf, includeZero))); }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        });
    }

    static void PostProcess(string id, List<Dictionary<string, object?>> rows)
    {
        if (id == "all-payment-details")
            foreach (var r in rows) r["head"] = HeadNames.Name(r["head"] as string);
    }
}
