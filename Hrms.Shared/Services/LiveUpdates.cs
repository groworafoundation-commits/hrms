namespace HrmsApp.Services;

// A tiny bridge so a page can react the moment the bell polls a new notification (for example the leave list refreshing when the request is approved).
public static class LiveUpdates
{
    public static event Action<NotificationItem>? Arrived;
    public static void Raise(NotificationItem n) => Arrived?.Invoke(n);

    // The outcome of a leave decision, read from the notification title the API writes, so it can be shown as a coloured badge.
    public static (string Label, string Css)? Outcome(NotificationItem n)
    {
        if (n.Kind != "leave") return null;
        var t = n.Title.ToLowerInvariant();
        if (t.Contains("rejected")) return ("Rejected", "badge-rejected");
        if (t.Contains("approved")) return ("Approved", "badge-approved");
        if (t.Contains("moved on")) return ("In progress", "badge-pending");
        return null;
    }
}
