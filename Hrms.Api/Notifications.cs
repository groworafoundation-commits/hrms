using System.Net;
using System.Net.Mail;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Notifications: every event that needs someone's attention writes an in-app notification (the bell) and, when that person has an email
// address, a row in the email outbox. Both are written inside the same transaction as the change itself, so a rolled-back change never
// notifies anyone. A background sender delivers the outbox through SMTP when SMTP is configured.

public class NotificationRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}
public record NotificationList(int Unread, List<NotificationRow> Items);
public class OutboxRow
{
    public long Id { get; set; }
    public string? ToUser { get; set; }
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? Error { get; set; }
}
public record TestEmailInput(string To);

public static class Notifier
{
    public static async Task Send(SqlConnection c, SqlTransaction? tx, string userId, string kind, string title, string? body, string? link)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        await c.ExecuteAsync("INSERT hrms.Notification (UserId, Kind, Title, Body, Link) VALUES (@userId, @kind, @title, @body, @link)",
            new { userId, kind, title = Trim(title, 200), body = Trim(body, 500), link }, tx);
        var email = await c.ExecuteScalarAsync<string?>("SELECT TOP 1 emailid FROM HRMSINTER.interface.Userdef WHERE userid = @userId AND ISNULL([disable],0) = 0", new { userId }, tx);
        if (!string.IsNullOrWhiteSpace(email) && email.Contains('@'))
            await c.ExecuteAsync("INSERT hrms.EmailOutbox (ToUser, ToAddress, Subject, Body) VALUES (@userId, @email, @subject, @body)",
                new { userId, email = email.Trim(), subject = Trim(title, 200), body = (body ?? "") + (link is null ? "" : "\n\nOpen HRMS: " + link) }, tx);
    }

    static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";

    // Everyone who holds a menu option (for example attendance corrections) and can see this employee.
    public static async Task<List<string>> HoldersOf(SqlConnection c, SqlTransaction? tx, string optionId, string employee) =>
        (await c.QueryAsync<string>(@"SELECT DISTINCT u.userid FROM HRMSINTER.interface.Userdef u
            JOIN HRMSINTER.interface.accessright a ON a.roleid = u.roleid AND a.optionid = @optionId AND CAST(a.access AS int) = 1
            JOIN inter01.AccessUser s ON s.UserId = u.userid AND s.AccessUser = @employee
            WHERE ISNULL(u.[disable],0) = 0 AND u.userid <> @employee", new { optionId, employee }, tx)).ToList();
}

// Delivers the outbox. Without SMTP settings (Hrms:Smtp:Host) mail is marked Skipped so the outbox shows what would have been sent.
public sealed class EmailSender(Db db, IConfiguration cfg, ILogger<EmailSender> log) : BackgroundService
{
    public static bool Configured(IConfiguration cfg) => !string.IsNullOrWhiteSpace(cfg["Hrms:Smtp:Host"]);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Pump(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Email sender cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(20), stop).ConfigureAwait(false);
        }
    }

    public async Task Pump(CancellationToken stop)
    {
        await using var c = db.OpenWrite();
        var batch = (await c.QueryAsync<OutboxRow>("SELECT TOP 20 Id, ToUser, ToAddress, Subject, Status, Attempts, CreatedAt FROM hrms.EmailOutbox WHERE Status = 'Pending' AND Attempts < 5 ORDER BY Id")).ToList();
        if (batch.Count == 0) return;
        if (!Configured(cfg))
        {
            await c.ExecuteAsync("UPDATE hrms.EmailOutbox SET Status='Skipped', Error='Email is not set up (no SMTP server configured)' WHERE Id IN @ids", new { ids = batch.Select(b => b.Id).ToArray() });
            return;
        }
        using var smtp = new SmtpClient(cfg["Hrms:Smtp:Host"], int.TryParse(cfg["Hrms:Smtp:Port"], out var port) ? port : 587)
        {
            EnableSsl = !string.Equals(cfg["Hrms:Smtp:UseSsl"], "false", StringComparison.OrdinalIgnoreCase),
            Credentials = string.IsNullOrEmpty(cfg["Hrms:Smtp:User"]) ? null : new NetworkCredential(cfg["Hrms:Smtp:User"], cfg["Hrms:Smtp:Password"]),
        };
        foreach (var m in batch)
        {
            var body = await c.ExecuteScalarAsync<string>("SELECT Body FROM hrms.EmailOutbox WHERE Id=@id", new { id = m.Id });
            try
            {
                using var msg = new MailMessage(cfg["Hrms:Smtp:From"] ?? cfg["Hrms:Smtp:User"] ?? "hrms@localhost", m.ToAddress, m.Subject, body);
                await smtp.SendMailAsync(msg, stop);
                await c.ExecuteAsync("UPDATE hrms.EmailOutbox SET Status='Sent', SentAt=GETDATE(), Attempts=Attempts+1, Error=NULL WHERE Id=@id", new { id = m.Id });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await c.ExecuteAsync("UPDATE hrms.EmailOutbox SET Attempts=Attempts+1, Status = CASE WHEN Attempts+1 >= 5 THEN 'Failed' ELSE 'Pending' END, Error=@err WHERE Id=@id",
                    new { id = m.Id, err = ex.Message.Length > 380 ? ex.Message[..380] : ex.Message });
            }
        }
    }
}

public static class NotificationEndpoints
{
    public static void MapNotifications(this WebApplication app)
    {
        app.MapGet("/api/notifications", async (Db db, HttpContext ctx) =>
        {
            var me = ((AuthUser)ctx.Items["user"]!).UserId;
            await using var c = db.Open();
            var items = (await c.QueryAsync<NotificationRow>("SELECT TOP 30 Id, Kind, Title, Body, Link, CreatedAt, ReadAt FROM hrms.Notification WHERE UserId=@me ORDER BY Id DESC", new { me })).ToList();
            var unread = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Notification WHERE UserId=@me AND ReadAt IS NULL", new { me });
            return new NotificationList(unread, items);
        });

        app.MapPost("/api/notifications/{id:long}/read", async (Db db, HttpContext ctx, long id) =>
        {
            await using var c = db.OpenWrite();
            await c.ExecuteAsync("UPDATE hrms.Notification SET ReadAt = GETDATE() WHERE Id=@id AND UserId=@me AND ReadAt IS NULL", new { id, me = ((AuthUser)ctx.Items["user"]!).UserId });
            return Results.Ok();
        });

        app.MapPost("/api/notifications/read-all", async (Db db, HttpContext ctx) =>
        {
            await using var c = db.OpenWrite();
            await c.ExecuteAsync("UPDATE hrms.Notification SET ReadAt = GETDATE() WHERE UserId=@me AND ReadAt IS NULL", new { me = ((AuthUser)ctx.Items["user"]!).UserId });
            return Results.Ok();
        });

        // admin: the outbox, and a test email
        app.MapGet("/api/admin/email", async (Db db, IConfiguration cfg) =>
        {
            await using var c = db.Open();
            var rows = (await c.QueryAsync<OutboxRow>("SELECT TOP 100 Id, ToUser, ToAddress, Subject, Status, Attempts, CreatedAt, SentAt, Error FROM hrms.EmailOutbox ORDER BY Id DESC")).ToList();
            return Results.Ok(new { Configured = EmailSender.Configured(cfg), From = cfg["Hrms:Smtp:From"], Host = cfg["Hrms:Smtp:Host"], Rows = rows });
        });

        app.MapPost("/api/admin/email/test", async (Db db, TestEmailInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (string.IsNullOrWhiteSpace(x.To) || !x.To.Contains('@')) throw new ApiException("Enter a valid email address.");
            await c.ExecuteAsync("INSERT hrms.EmailOutbox (ToAddress, Subject, Body) VALUES (@to, 'HRMS test email', 'This is a test email from HRMS. If you can read it, email alerts are working.')", new { to = x.To.Trim() }, tx);
            return new { Queued = true };
        }));
    }
}
