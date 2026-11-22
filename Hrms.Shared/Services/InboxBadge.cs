using HrmsApp.Services;

namespace HrmsApp.Services;

// How many requests are waiting for the signed-in person to decide (leave plus attendance corrections).
// The layout shows it on the menu; the inbox and the approval pages refresh it after every decision.
public static class InboxBadge
{
    public static int Count { get; private set; }
    public static event Action? Changed;

    public static async Task Refresh(PortalClient portal, AttendanceClient att, AuthState auth)
    {
        var n = 0;
        try { if (auth.Can("H201")) n += (await portal.Approvals())?.Count ?? 0; } catch { }
        try { if (auth.Can("H201")) n += (await portal.PendingRequests())?.Count ?? 0; } catch { }
        try { if (auth.Can("H206")) n += (await att.Requests("Pending"))?.Count ?? 0; } catch { }
        try { if (auth.Can("H301")) n += (await portal.AssetWaitingList())?.Count(w => w.CanDecide) ?? 0; } catch { }
        Set(n);
    }

    public static void Reset() => Set(0);

    static void Set(int n)
    {
        if (n == Count) return;
        Count = n;
        Changed?.Invoke();
    }
}
