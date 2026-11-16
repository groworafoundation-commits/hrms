using HrmsApp.Models;

namespace HrmsApp.Services;

// Small helpers shared by the asset screens and the approvals inbox.
public static class AssetUi
{
    public static Task<WriteResult> Approve(PortalClient p, AssetWaiting w, string? note) => w.Kind == "Transfer" ? p.ApproveTransfer(w.Id, note) : p.ApproveAsset(w.Id, note);
    public static Task<WriteResult> Reject(PortalClient p, AssetWaiting w, string note) => w.Kind == "Transfer" ? p.RejectTransfer(w.Id, note) : p.RejectAsset(w.Id, note);

    public static string KindLabel(AssetWaiting w) => w.Kind switch { "Issue" => "Issue", "Return" => "Return", "Transfer" => "Transfer", "Acceptance" => "Receiver to accept", _ => w.Kind };

    public static string Summary(AssetWaiting w) => w.Kind switch
    {
        "Transfer" or "Acceptance" => $"{w.AssetName} ({w.AssetCode}): from {Fmt.Title(w.EmpName ?? w.EmpCode)} to {Fmt.Title(w.ToEmpName ?? w.ToEmpCode ?? "")}",
        _ => $"{w.AssetName} ({w.AssetCode}) {(w.Kind == "Return" ? "returned by" : "for")} {Fmt.Title(w.EmpName ?? w.EmpCode)}",
    };

    public static string StateClass(string state) => state switch { "With employee" => "st-ok", "Returned" => "st-info", "Rejected" => "st-bad", _ => "st-warn" };

    public static string Icon(string category) => category switch { "Laptop" or "Desktop" or "Tablet" => "laptop", "Access card" => "shield", _ => "briefcase" };
}
