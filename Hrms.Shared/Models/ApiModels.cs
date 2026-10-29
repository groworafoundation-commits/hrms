using System.Text.Json;
namespace HrmsApp.Models;

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

public record Paged<T>(List<T> Items, int Total, int Page, int Size);
public record EmployeeProfile(EmployeeRow Employee, List<LeaveRow> Leave, List<PayslipRow> Payslips);
public record PayrollRun(string Month, List<PayslipRow> Rows, decimal Basic, decimal Addition, decimal Deduction, decimal Net);
public record Dashboard(Kpis Kpis, List<LeaveDay> LeaveWeek, List<NameCount> Designations, List<Occasion> Birthdays, List<Occasion> Anniversaries,
    List<LeaveRow> RecentLeave, List<Person> NewJoiners, List<PayrollMonth> Payroll);

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
    public decimal Total { get; set; }
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

public class EmployeeInput
{
    public string EmpCode { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? Dob { get; set; }
    public string? Gender { get; set; }
    public string? Status { get; set; }
    public string? Phone { get; set; }
    public string? OfficeNo { get; set; }
    public string? Description { get; set; }
    public string? Remarks { get; set; }
}

public class ExtraVersion
{
    public long Id { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public string? Company { get; set; }
    public string? Birthplace { get; set; }
    public string? Country { get; set; }
    public string? Bank { get; set; }
    public string? Ebank { get; set; }
    public string? BankAccount { get; set; }
    public string? Designation { get; set; }
    public string? MaritalStatus { get; set; }
    public string? BloodGroup { get; set; }
    public DateTime? Doj { get; set; }
    public DateTime? TermDate { get; set; }
    public DateTime? ConfirmDate { get; set; }
    public DateTime? ContractDate { get; set; }
    public string? PaymentMode { get; set; }
    public string? DocNo { get; set; }
    public double? TicketPeriod { get; set; }
    public string? EntitledYear { get; set; }
    public string? Route { get; set; }
    public bool Discontinue { get; set; }
    public string? TransNo { get; set; }
}

public class HeadValue
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime? EffectiveDate { get; set; }
}

public record StructureHistory(List<HeadValue> Current, List<DateTime> EffectiveDates);
public record FormLookups(string NextEmpCode, List<string> Genders, List<string> Statuses, List<string> Designations, List<string> Countries, List<string> Banks,
    List<string> PaymentModes, List<string> MaritalStatuses, List<string> BloodGroups, List<string> Locations, List<string> Companies);
public record VariableType(string Code, string Name);
public record ScheduleLine(DateTime Date, decimal Amount, string TransNo);
public record WriteResult(bool Ok, string? Error, string? Message);

public record ReportColumn(string Key, string Label, string Type, bool Total);
public record ReportDef(string Id, string Name, string Group, string Description, string[] Filters, string ErpMenu);
public record ReportOption(string Code, string Name);
public record ReportLookups(List<ReportOption> Designations, List<string> Locations, List<string> Months);

public class ReportResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<ReportColumn> Columns { get; set; } = [];
    public List<Dictionary<string, JsonElement>> Rows { get; set; } = [];
    public Dictionary<string, decimal> Totals { get; set; } = [];
    public string? Note { get; set; }
    public List<Dictionary<string, JsonElement>> Extra { get; set; } = [];
}

public class ReportFilter
{
    public string? Month { get; set; }
    public string? Employee { get; set; }
    public string? Designation { get; set; }
    public string? Location { get; set; }
    public string Status { get; set; } = "active";
    public DateTime AsOf { get; set; } = DateTime.Today;
    public bool IncludeZero { get; set; }
}

public record CompanyInfo(string Name, List<string> AddressLines, string? Phone, string? Fax, string? Email, string? VatNo, string Currency, string? Logo);
