using Microsoft.Data.SqlClient;

namespace Hrms.Api;

public sealed class Db(string connectionString)
{
    // Connection for writes: normal isolation (reads that guard a write must see committed data).
    public SqlConnection OpenWrite()
    {
        var c = new SqlConnection(connectionString);
        c.Open();
        return c;
    }

    public SqlConnection Open()
    {
        var c = new SqlConnection(connectionString);
        c.Open();
        // Reporting reads must never wait on (or block) the live ERP's transactions.
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 10000; EXEC sp_set_session_context N'uid', @uid; EXEC sp_set_session_context N'self', @self;";
        cmd.Parameters.AddWithValue("@uid", (object?)CurrentUser.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@self", (object?)CurrentUser.SelfCode ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return c;
    }
}

public static class LeaveTypes
{
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UPL"] = "Unpaid leave",
        ["ALVESAL"] = "Annual leave with salary",
        ["LVE"] = "Annual leave",
        ["EMRLVE"] = "Emergency leave",
        ["MTRLVE"] = "Maternity leave",
    };

    public static string Name(string? code) => code is not null && Names.TryGetValue(code, out var n) ? n : code ?? "";
}

public static class HeadNames
{
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BSC"] = "Basic salary", ["ACCAllow"] = "Accommodation allowance", ["MobileAllow"] = "Mobile allowance", ["PositionAllow"] = "Position allowance",
        ["SewaAllow"] = "Sewa allowance", ["TA1"] = "Travel allowance 1", ["TA2"] = "Travel allowance 2", ["TA3"] = "Travel allowance 3", ["FixedWPS"] = "Fixed WPS",
        ["ADVAMT"] = "Advance", ["LOAN"] = "Loan", ["LOREC"] = "Loan return", ["ADVREC"] = "Advance return", ["ADVAMTEID"] = "Advance (EID)",
        ["AWD"] = "Award", ["BNS"] = "Bonus", ["COMISSION"] = "Commission", ["DeductLifBill"] = "Elife bill", ["DeductMobBill"] = "Mobile bill",
        ["DeductRTAFines"] = "RTA fines", ["DeductSewaBill"] = "Sewa bill", ["EOSAmt"] = "End of service amount", ["FIN-MEMO"] = "Fines (memo)",
        ["HWA"] = "Holiday work allowance", ["ITA"] = "International travel allowance", ["LVSALAMT"] = "Leave salary", ["ONA"] = "Overnight allowance",
        ["PENALTY"] = "Penalty", ["TKT"] = "Air ticket", ["TKTEncash"] = "Ticket encashment", ["VarWPS"] = "Variable WPS", ["AirTic"] = "Air ticket accrual",
        ["ALVESAL"] = "Annual leave salary", ["EOS"] = "End of service accrual", ["LVE"] = "Annual leave", ["nooftickets"] = "Tickets per year",
        ["UPL"] = "Unpaid leave", ["MTRLVE"] = "Maternity leave", ["EMRLVE"] = "Emergency leave",
    };

    public static string Name(string? code) => string.IsNullOrEmpty(code) ? "Other" : Names.TryGetValue(code, out var n) ? n : code;
}

public record Kpis(int Active, int Inactive, int OnLeaveToday, int StartingTomorrow, int JoinedLast30, int LeaveNext7, decimal LastNet, int LastPaid, string? LastMonth);
public record LeaveDay(DateTime Day, int Annual, int Unpaid, int Other);
public record NameCount(string Name, int Count);
public record Occasion(string EmpCode, string Name, string? Designation, DateTime Date, int Years);
public record Person(string EmpCode, string Name, string? Designation, DateTime? Doj);
public record PayrollMonth(string Month, int Headcount, decimal Net, decimal Addition, decimal Deduction);

public class LeaveRow
{
    public long Id { get; set; }
    public DateTime? Rejoining { get; set; }
    public bool SalaryWithoutLeave { get; set; }
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string LeaveType { get; set; } = "";
    public string LeaveName { get; set; } = "";
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public decimal Days { get; set; }
    public DateTime? Applied { get; set; }
    public string Status { get; set; } = "";
    public string? TransNo { get; set; }
    public string? HeldBy { get; set; }
    public string? HeldByName { get; set; }
}

public class EmployeeRow
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Gender { get; set; }
    public DateTime? Dob { get; set; }
    public string? Phone { get; set; }
    public string? Designation { get; set; }
    public string? Country { get; set; }
    public string? MaritalStatus { get; set; }
    public string? OfficePhone { get; set; }
    public string? OfficeNo { get; set; }
    public string? MasterStatus { get; set; }
    public string? Description { get; set; }
    public string? Address { get; set; }
    public string? Remarks { get; set; }
    public string? Bank { get; set; }
    public string? BankAccount { get; set; }
    public string? PaymentMode { get; set; }
    public string? DocNo { get; set; }
    public double? TicketPeriod { get; set; }
    public int? EntitledYear { get; set; }
    public DateTime? ConfirmDate { get; set; }
    public DateTime? ContractDate { get; set; }
    public string? BloodGroup { get; set; }
    public DateTime? Doj { get; set; }
    public DateTime? TermDate { get; set; }
    public string Status { get; set; } = "";
    public int Total { get; set; }
}

public class PayslipRow
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Month { get; set; } = "";
    public decimal Basic { get; set; }
    public decimal Addition { get; set; }
    public decimal Deduction { get; set; }
    public decimal Net { get; set; }
    public decimal WorkedDays { get; set; }
    public decimal LeaveDays { get; set; }
}

public class StructureRow
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Designation { get; set; }
    public decimal Basic { get; set; }
    public decimal Accommodation { get; set; }
    public decimal Mobile { get; set; }
    public decimal Position { get; set; }
    public decimal Sewa { get; set; }
    public decimal Travel { get; set; }
    public decimal Wps { get; set; }
    public decimal Total => Basic + Accommodation + Mobile + Position + Sewa + Travel;
}

public class EntryRow
{
    public long Id { get; set; }
    public string EmpCode { get; set; } = "";
    public string? Name { get; set; }
    public string Type { get; set; } = "";
    public string TypeName { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
    public decimal? Installments { get; set; }
    public string? TransNo { get; set; }
    public string? ReturnTransNo { get; set; }
    public DateTime? ReturnDate { get; set; }
    public string? Remarks { get; set; }
}

public class TypeTotal
{
    public string Type { get; set; } = "";
    public string TypeName { get; set; } = "";
    public int Count { get; set; }
    public decimal Total { get; set; }
}

public class LoanBalance
{
    public string EmpCode { get; set; } = "";
    public string? Name { get; set; }
    public string? Status { get; set; }
    public decimal Loans { get; set; }
    public decimal Advances { get; set; }
    public decimal Deducted { get; set; }
    public decimal Returned { get; set; }
    public decimal Outstanding { get; set; }
    public int Entries { get; set; }
    public DateTime? LastDate { get; set; }
}

public class AccrualRow
{
    public string EmpCode { get; set; } = "";
    public string? Name { get; set; }
    public string Type { get; set; } = "";
    public string TypeName { get; set; } = "";
    public DateTime? ProcessDate { get; set; }
    public DateTime? PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public decimal LeaveDays { get; set; }
    public decimal PaidDays { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

public class TransferRow
{
    public int Id { get; set; }
    public string EmpCode { get; set; } = "";
    public string? Name { get; set; }
    public DateTime Date { get; set; }
    public string? FromLocation { get; set; }
    public string? ToLocation { get; set; }
}

public class HeadRow
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Formula { get; set; }
    public bool Accrual { get; set; }
}

public record EntryReport(List<TypeTotal> Summary, List<EntryRow> Rows);
public record AccrualReport(List<TypeTotal> Summary, List<AccrualRow> Rows);
public record LeaveSummaryRow(string Type, string TypeName, int Records, int Employees, decimal Days);
public record Paged<T>(List<T> Items, int Total, int Page, int Size);
public record EmployeeDetail(EmployeeRow Employee, List<LeaveRow> Leave, List<PayslipRow> Payslips);
public record PayrollRun(string Month, List<PayslipRow> Rows, decimal Basic, decimal Addition, decimal Deduction, decimal Net);
public record Dashboard(Kpis Kpis, List<LeaveDay> LeaveWeek, List<NameCount> Designations, List<Occasion> Birthdays, List<Occasion> Anniversaries,
    List<LeaveRow> RecentLeave, List<Person> NewJoiners, List<PayrollMonth> Payroll);

public static class Sql
{
    // The employee view is materialised into #emp once per batch; joining the windowed CTE directly made SQL Server
    // re-evaluate it per joined row on the larger leave, loan and payroll tables.
    public const string EmpCte = @"
IF OBJECT_ID('tempdb..#emp') IS NOT NULL DROP TABLE #emp;
WITH x AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY EmpCode ORDER BY Id DESC) rn FROM inter01.EmployeeExtraDetails WHERE ISNULL(deleteflag,0)=0),
e0 AS (
  SELECT e.EmpCode, e.Empname AS Name, e.Gender, e.dob AS Dob, e.PhoneNo AS Phone, e.OfficePhone, e.Addressdetail AS OfficeNo, e.Status AS MasterStatus, e.OtherDetails AS Description, e.Address, e.Remarks, x.designation AS Designation, x.country AS Country,
         x.mstatus AS MaritalStatus, x.doj AS Doj, x.termdate AS TermDate, x.bank AS Bank, x.accode AS BankAccount, x.mop AS PaymentMode, x.docno AS DocNo,
         x.tktperiod AS TicketPeriod, x.entitledyear AS EntitledYear, x.confdate AS ConfirmDate, x.contdate AS ContractDate, x.Bgroup AS BloodGroup,
         CASE WHEN x.termdate IS NULL OR x.termdate > GETDATE() THEN 'active' ELSE 'inactive' END AS Status
  FROM inter01.EmployeeMaster e LEFT JOIN x ON x.EmpCode = e.EmpCode AND x.rn = 1)
SELECT * INTO #emp FROM e0;
DELETE FROM #emp WHERE (SESSION_CONTEXT(N'self') IS NOT NULL AND EmpCode <> CAST(SESSION_CONTEXT(N'self') AS nvarchar(100)))
   OR (SESSION_CONTEXT(N'self') IS NULL AND SESSION_CONTEXT(N'uid') IS NOT NULL AND EmpCode NOT IN (SELECT accessuser FROM inter01.accessuser WHERE userid = CAST(SESSION_CONTEXT(N'uid') AS nvarchar(100))));
CREATE CLUSTERED INDEX ix_emp ON #emp (EmpCode);
WITH emp AS (SELECT * FROM #emp)";

    const string LeaveSelect = @"
SELECT l.Id AS Id, l.Rejoiningdate AS Rejoining, CAST(ISNULL(l.LeaveSalWithoutLeave,0) AS bit) AS SalaryWithoutLeave, l.EmpCode, e.Name, l.LeaveType, l.FromDate, l.ToDate, l.NoofLeavedays AS Days, l.Applieddate AS Applied, l.Transno AS TransNo, l.Userid AS HeldBy,
       (SELECT TOP 1 ISNULL(NULLIF(u.name,''), u.username) FROM HRMSINTER.interface.Userdef u WHERE u.userid = l.Userid) AS HeldByName,
       CASE WHEN l.Userlevel >= l.documentlevel THEN 'Approved' ELSE 'Pending' END AS Status
FROM inter01.EMPLOYEELEAVEDETAILS l JOIN emp e ON e.EmpCode = l.EmpCode
WHERE ISNULL(l.Deleteflag,0)=0";

    public const string Kpis = EmpCte + @"
SELECT
 (SELECT COUNT(*) FROM emp WHERE Status='active') AS Active,
 (SELECT COUNT(*) FROM emp WHERE Status='inactive') AS Inactive,
 (SELECT COUNT(DISTINCT EmpCode) FROM inter01.EMPLOYEELEAVEDETAILS WHERE ISNULL(Deleteflag,0)=0 AND CAST(GETDATE() AS date) BETWEEN FromDate AND ToDate) AS OnLeaveToday,
 (SELECT COUNT(DISTINCT EmpCode) FROM inter01.EMPLOYEELEAVEDETAILS WHERE ISNULL(Deleteflag,0)=0 AND CAST(GETDATE()+1 AS date) = FromDate) AS StartingTomorrow,
 (SELECT COUNT(*) FROM emp WHERE Doj >= DATEADD(day,-30,GETDATE()) AND Status='active') AS JoinedLast30,
 (SELECT COUNT(*) FROM inter01.EMPLOYEELEAVEDETAILS WHERE ISNULL(Deleteflag,0)=0 AND FromDate BETWEEN CAST(GETDATE() AS date) AND DATEADD(day,7,GETDATE())) AS LeaveNext7,
 ISNULL((SELECT SUM(CNet) FROM inter01.EmployeePaymentProcess p WHERE CONVERT(varchar(7),p.ProcessedDate,120) = lm.m),0) AS LastNet,
 (SELECT COUNT(*) FROM inter01.EmployeePaymentProcess p WHERE CONVERT(varchar(7),p.ProcessedDate,120) = lm.m) AS LastPaid,
 lm.m AS LastMonth
FROM (SELECT TOP 1 CONVERT(varchar(7),ProcessedDate,120) AS m FROM inter01.EmployeePaymentProcess ORDER BY ProcessedDate DESC) lm";

    public const string LeaveWeek = @"
SELECT d.M AS Day,
  COUNT(DISTINCT CASE WHEN l.LeaveType IN ('LVE','ALVESAL') THEN l.EmpCode END) AS Annual,
  COUNT(DISTINCT CASE WHEN l.LeaveType='UPL' THEN l.EmpCode END) AS Unpaid,
  COUNT(DISTINCT CASE WHEN l.LeaveType NOT IN ('UPL','LVE','ALVESAL') THEN l.EmpCode END) AS Other
FROM (SELECT DATEFROMPARTS(YEAR(x),MONTH(x),1) AS M FROM (SELECT DATEADD(month,-n,GETDATE()) AS x FROM (VALUES (5),(4),(3),(2),(1),(0)) v(n)) t) d
LEFT JOIN inter01.EMPLOYEELEAVEDETAILS l ON ISNULL(l.Deleteflag,0)=0 AND l.FromDate < DATEADD(month,1,d.M) AND l.ToDate >= d.M
GROUP BY d.M ORDER BY d.M";

    public const string Designations = EmpCte + @"
SELECT TOP 7 ISNULL(NULLIF(Designation,''),'Unassigned') AS Name, COUNT(*) AS Count FROM emp WHERE Status='active' GROUP BY ISNULL(NULLIF(Designation,''),'Unassigned') ORDER BY 2 DESC";

    public const string Birthdays = EmpCte + @"
SELECT TOP 6 EmpCode, Name, Designation, d.Next AS Date, DATEDIFF(year, Dob, d.Next) AS Years
FROM emp CROSS APPLY (SELECT CASE WHEN DATEFROMPARTS(YEAR(GETDATE()), MONTH(Dob), CASE WHEN MONTH(Dob)=2 AND DAY(Dob)=29 THEN 28 ELSE DAY(Dob) END) >= CAST(GETDATE() AS date)
   THEN DATEFROMPARTS(YEAR(GETDATE()), MONTH(Dob), CASE WHEN MONTH(Dob)=2 AND DAY(Dob)=29 THEN 28 ELSE DAY(Dob) END)
   ELSE DATEFROMPARTS(YEAR(GETDATE())+1, MONTH(Dob), CASE WHEN MONTH(Dob)=2 AND DAY(Dob)=29 THEN 28 ELSE DAY(Dob) END) END AS Next) d
WHERE Status='active' AND Dob IS NOT NULL AND d.Next <= DATEADD(day,45,GETDATE()) ORDER BY d.Next";

    public const string Anniversaries = EmpCte + @"
SELECT TOP 6 EmpCode, Name, Designation, d.Next AS Date, DATEDIFF(year, Doj, d.Next) AS Years
FROM emp CROSS APPLY (SELECT CASE WHEN DATEFROMPARTS(YEAR(GETDATE()), MONTH(Doj), CASE WHEN MONTH(Doj)=2 AND DAY(Doj)=29 THEN 28 ELSE DAY(Doj) END) >= CAST(GETDATE() AS date)
   THEN DATEFROMPARTS(YEAR(GETDATE()), MONTH(Doj), CASE WHEN MONTH(Doj)=2 AND DAY(Doj)=29 THEN 28 ELSE DAY(Doj) END)
   ELSE DATEFROMPARTS(YEAR(GETDATE())+1, MONTH(Doj), CASE WHEN MONTH(Doj)=2 AND DAY(Doj)=29 THEN 28 ELSE DAY(Doj) END) END AS Next) d
WHERE Status='active' AND Doj IS NOT NULL AND DATEDIFF(year, Doj, d.Next) > 0 AND d.Next <= DATEADD(day,45,GETDATE()) ORDER BY d.Next";

    public const string LeavePending = EmpCte + LeaveSelect + @" AND ISNULL(l.Userlevel,0) < ISNULL(l.documentlevel,0) ORDER BY l.Applieddate, l.FromDate";

    public const string LeaveRecent = EmpCte + @"
SELECT TOP 5 * FROM (" + LeaveSelect + @") q ORDER BY Applied DESC, FromDate DESC";

    public const string NewJoiners = EmpCte + @"
SELECT TOP 5 EmpCode, Name, Designation, Doj FROM emp WHERE Status='active' AND Doj IS NOT NULL ORDER BY Doj DESC";

    public const string PayrollMonths = @"
SELECT TOP 12 CONVERT(varchar(7),ProcessedDate,120) AS Month, COUNT(*) AS Headcount, SUM(CNet) AS Net, SUM(Addition) AS Addition, SUM(Deduction) AS Deduction
FROM inter01.EmployeePaymentProcess GROUP BY CONVERT(varchar(7),ProcessedDate,120) ORDER BY 1 DESC";

    public const string Employees = EmpCte + @"
SELECT *, COUNT(*) OVER() AS Total FROM emp
WHERE (@status = 'all' OR Status = @status) AND (@q IS NULL OR Name LIKE @q OR EmpCode LIKE @q OR Designation LIKE @q)
ORDER BY Name OFFSET @skip ROWS FETCH NEXT @size ROWS ONLY";

    public const string Employee = EmpCte + "SELECT *, 1 AS Total FROM emp WHERE EmpCode = @code";

    public const string LeaveForEmployee = EmpCte + LeaveSelect + " AND l.EmpCode=@code ORDER BY l.FromDate DESC OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY";

    public const string Leave = EmpCte + LeaveSelect + @"
 AND l.ToDate >= @start AND l.FromDate <= @end AND (@type IS NULL OR l.LeaveType=@type) AND (@q IS NULL OR e.Name LIKE @q OR l.EmpCode LIKE @q)
ORDER BY l.FromDate DESC";

    const string PayrollSelect = @"
SELECT p.Empcode AS EmpCode, e.Name, CONVERT(varchar(7),p.ProcessedDate,120) AS Month,
       ISNULL(p.BASIC,0) AS Basic, ISNULL(p.Addition,0) AS Addition, ISNULL(p.Deduction,0) AS Deduction, ISNULL(p.CNet,0) AS Net,
       ISNULL(p.WorkedDays,0) AS WorkedDays, ISNULL(p.LeaveDays,0) AS LeaveDays
FROM inter01.EmployeePaymentProcess p JOIN emp e ON e.EmpCode = p.Empcode";

    public const string PayrollForMonth = EmpCte + PayrollSelect + " WHERE CONVERT(varchar(7),p.ProcessedDate,120)=@month ORDER BY e.Name";
    public const string PayrollForEmployee = EmpCte + PayrollSelect + " WHERE p.Empcode=@code ORDER BY p.ProcessedDate DESC OFFSET 0 ROWS FETCH NEXT 12 ROWS ONLY";

    const string EntryWhere = @"
FROM inter01.EMPVARIABLESALARY v JOIN emp e ON e.EmpCode = v.Empcode
WHERE ISNULL(v.Deleteflag,0)=0 AND ISNULL(v.Variabledelete,0)=0
  AND ((@loans=1 AND v.Type IN ('LOAN','ADVAMT','LOREC','ADVREC')) OR (@loans=0 AND v.Type NOT IN ('LOAN','ADVAMT','LOREC','ADVREC')))
  AND v.Date BETWEEN @from AND @to AND (@type IS NULL OR v.Type=@type) AND (@code IS NULL OR v.Empcode=@code)
  AND (@q IS NULL OR e.Name LIKE @q OR v.Empcode LIKE @q)";

    public const string EntryRows = EmpCte + @"
SELECT TOP 500 v.ID AS Id, v.Empcode AS EmpCode, e.Name, v.Type, v.Amount, v.Date, v.Noofmonth AS Installments, v.Transno AS TransNo,
  v.ReturnTransnumber AS ReturnTransNo, v.Returndate AS ReturnDate,
  LEFT(REPLACE(REPLACE(ISNULL(NULLIF(v.Remarks,''), v.Description), CHAR(13), ' '), CHAR(10), ' '), 160) AS Remarks" + EntryWhere + " ORDER BY v.Date DESC, v.ID DESC";

    public const string EntrySummary = EmpCte + "SELECT v.Type, COUNT(*) AS Count, SUM(v.Amount) AS Total" + EntryWhere + " GROUP BY v.Type ORDER BY SUM(v.Amount) DESC";

    public const string LoanBalances = EmpCte + @",
iss AS (SELECT Empcode, SUM(CASE WHEN Type='LOAN' THEN Amount ELSE 0 END) Loans, SUM(CASE WHEN Type='ADVAMT' THEN Amount ELSE 0 END) Advances,
               SUM(CASE WHEN Type IN ('LOREC','ADVREC') THEN Amount ELSE 0 END) Returned, COUNT(*) Entries, MAX(Date) LastDate
        FROM inter01.EMPVARIABLESALARY WHERE ISNULL(Deleteflag,0)=0 AND ISNULL(Variabledelete,0)=0 AND Type IN ('LOAN','ADVAMT','LOREC','ADVREC') GROUP BY Empcode),
ded AS (SELECT Empcode, SUM(ISNULL([Loan Deducted],0) + ISNULL([Advance Deducted],0)) Deducted FROM inter01.EmployeePaymentProcess GROUP BY Empcode)
SELECT i.Empcode AS EmpCode, e.Name, e.Status, i.Loans, i.Advances, ISNULL(d.Deducted,0) AS Deducted, i.Returned, i.Entries, i.LastDate,
  CASE WHEN i.Loans + i.Advances - ISNULL(d.Deducted,0) - i.Returned < 0 THEN 0 ELSE i.Loans + i.Advances - ISNULL(d.Deducted,0) - i.Returned END AS Outstanding
FROM iss i JOIN emp e ON e.EmpCode = i.Empcode LEFT JOIN ded d ON d.Empcode = i.Empcode
WHERE (@status = 'all' OR e.Status = @status) AND (@q IS NULL OR e.Name LIKE @q OR i.Empcode LIKE @q) AND i.Loans + i.Advances > 0
ORDER BY Outstanding DESC, e.Name";

    public const string Structure = EmpCte + @",
pay AS (SELECT EmpCode, SalaryType, Amt, ROW_NUMBER() OVER (PARTITION BY EmpCode, SalaryType ORDER BY EffectiveDate DESC, Id DESC) rn
        FROM inter01.EmployeePaymentDetails WHERE ISNULL(Deleteflag,0)=0 AND PaymentType='FIX' AND EffectiveDate <= GETDATE())
SELECT e.EmpCode, e.Name, e.Designation,
  ISNULL(SUM(CASE WHEN p.SalaryType='BSC' THEN p.Amt END),0) AS Basic,
  ISNULL(SUM(CASE WHEN p.SalaryType='ACCAllow' THEN p.Amt END),0) AS Accommodation,
  ISNULL(SUM(CASE WHEN p.SalaryType='MobileAllow' THEN p.Amt END),0) AS Mobile,
  ISNULL(SUM(CASE WHEN p.SalaryType='PositionAllow' THEN p.Amt END),0) AS Position,
  ISNULL(SUM(CASE WHEN p.SalaryType='SewaAllow' THEN p.Amt END),0) AS Sewa,
  ISNULL(SUM(CASE WHEN p.SalaryType IN ('TA1','TA2','TA3') THEN p.Amt END),0) AS Travel,
  ISNULL(SUM(CASE WHEN p.SalaryType='FixedWPS' THEN p.Amt END),0) AS Wps
FROM emp e JOIN pay p ON p.EmpCode = e.EmpCode AND p.rn = 1
WHERE (@status = 'all' OR e.Status = @status) AND (@q IS NULL OR e.Name LIKE @q OR e.EmpCode LIKE @q)
GROUP BY e.EmpCode, e.Name, e.Designation ORDER BY e.Name";

    const string AccrualWhere = @"
FROM inter01.EmpAccuralPayment a JOIN emp e ON e.EmpCode = a.EmpCode
WHERE (@type IS NULL OR a.AccuralType = @type) AND (@code IS NULL OR a.EmpCode = @code) AND (@q IS NULL OR e.Name LIKE @q OR a.EmpCode LIKE @q)";

    public const string AccrualRows = EmpCte + @"
SELECT TOP 400 a.EmpCode, e.Name, ISNULL(a.AccuralType,'') AS Type, a.ProcessDate, a.PaymentDate, ISNULL(a.Amount,0) AS Amount,
  ISNULL(a.numberofleavedays,0) AS LeaveDays, ISNULL(a.NumberofPaidDays,0) AS PaidDays, a.fromdate AS FromDate, a.todate AS ToDate" + AccrualWhere + " ORDER BY ISNULL(a.PaymentDate, a.ProcessDate) DESC, a.Id DESC";

    public const string AccrualSummary = EmpCte + "SELECT ISNULL(a.AccuralType,'') AS Type, COUNT(*) AS Count, SUM(ISNULL(a.Amount,0)) AS Total" + AccrualWhere + " GROUP BY a.AccuralType ORDER BY 3 DESC";

    public const string Transfers = EmpCte + @"
SELECT t.Id AS Id, t.EmpCode, e.Name, t.Date, t.FromLocation, t.ToLocation FROM inter01.EmployeeTransfer t JOIN emp e ON e.EmpCode = t.EmpCode
WHERE (@code IS NULL OR t.EmpCode=@code) AND (@q IS NULL OR e.Name LIKE @q OR t.EmpCode LIKE @q) ORDER BY t.Date DESC";

    public const string Heads = @"
SELECT SalaryType AS Code, PaymentType AS Kind, Formula, CAST(ISNULL(AccrualFlag,0) AS bit) AS Accrual FROM inter01.PaymentMaster
WHERE PMHCode = (SELECT MIN(PMHCode) FROM inter01.PaymentMaster) ORDER BY PaymentType, SalaryType";

    public const string LeaveSummary = @"
SELECT LeaveType AS Type, '' AS TypeName, COUNT(*) AS Records, COUNT(DISTINCT EmpCode) AS Employees, ISNULL(SUM(NoofLeavedays),0) AS Days
FROM inter01.EMPLOYEELEAVEDETAILS WHERE ISNULL(Deleteflag,0)=0 AND YEAR(FromDate)=@year GROUP BY LeaveType ORDER BY 4 DESC";
}
