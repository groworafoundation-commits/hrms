namespace HrmsApp;

/// <summary>How each payroll report is laid out on paper.</summary>
/// <param name="Kind">"table" (grouped columns), "payslip" (one page per employee) or "statement" (one page per row).</param>
/// <param name="GroupBy">Row key to group by, or null for a flat list.</param>
/// <param name="GroupLabel">Caption shown on each group heading.</param>
/// <param name="SubtotalKeys">Columns to subtotal; null means every column the report totals.</param>
/// <param name="GrandTotal">Whether to print a grand total line.</param>
/// <param name="Signatures">Captions for the signature boxes at the foot of the last page.</param>
public record ReportLayout(string Kind, bool Landscape, string? GroupBy = null, string GroupLabel = "", string[]? SubtotalKeys = null, bool GrandTotal = true, string[]? Signatures = null);

public static class ReportLayouts
{
    static readonly string[] Payroll = ["Prepared by", "Checked by", "Approved by"];
    static readonly string[] Hr = ["Prepared by", "HR manager"];

    public static ReportLayout For(string id) => id switch
    {
        "payslip" => new("payslip", false),
        "final-settlement" => new("statement", false),
        "employee-master" => new("table", true, "designation", "Designation", Signatures: Hr),
        "employee-extra-details" => new("table", true, "designation", "Designation", Signatures: Hr),
        "employee-grade-details" => new("table", true, "designation", "Designation", Signatures: Payroll),
        "employee-advances-loans" => new("table", true, Signatures: Payroll),
        "provision" => new("table", true, "designation", "Designation", Signatures: Payroll),
        "salary-breakup" => new("table", true, "designation", "Designation", Signatures: Payroll),
        "processed-salary-breakup" => new("table", true, "designation", "Designation", Signatures: Payroll),
        "salary-summary" => new("table", false, "section", "", ["amount"], false, Payroll),
        "wps" => new("table", true, "designation", "Designation", Signatures: Payroll),
        "wps-balance" => new("table", false, "designation", "Designation", Signatures: Payroll),
        "all-payment-details" => new("table", false, "name", "Employee", Signatures: Hr),
        "accrual" => new("table", true, "designation", "Designation", Signatures: Payroll),
        "asset-in-hand" => new("table", false, "name", "Employee", Signatures: Hr),
        "document-expiry" => new("table", false, "name", "Employee", Signatures: Hr),
        _ => new("table", true),
    };
}
