using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Punching machines. Machines that speak the ZKTeco "push" protocol (ADMS) call /iclock/cdata on this server and send each punch as it
// happens. The machine knows people by a PIN; the mapping table turns that into an employee. Every received line is kept in
// hrms.AttDeviceLog (so nothing is lost and a re-sent punch is never stored twice) and matched punches become ordinary attendance punches.

public class DeviceRow
{
    public int Id { get; set; }
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public bool Active { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? LastIp { get; set; }
    public int PunchesToday { get; set; }
    public int Unmatched { get; set; }
    public int? SkewSeconds { get; set; }
}
public record MapRow(int Id, string Pin, string EmpCode, string? Name);
public class DeviceLogRow
{
    public long Id { get; set; }
    public string DeviceSerial { get; set; } = "";
    public string? DeviceName { get; set; }
    public string Pin { get; set; } = "";
    public DateTime PunchTime { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int? SkewSeconds { get; set; }
    public string? EmpCode { get; set; }
    public string? Name { get; set; }
    public string? Verify { get; set; }
    public string Result { get; set; } = "";
}
public record UnmappedPin(string Pin, int Punches, DateTime LastPunch);
public record DeviceInput(string Serial, string Name, string? Location, bool Active);
public record MapInput(string Pin, string EmpCode);

public static class DeviceEndpoints
{
    static string? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    static IResult Text(string body, int status = 200) => Results.Text(body, "text/plain", Encoding.UTF8, status);

    static async Task Touch(SqlConnection c, SqlTransaction? tx, string sn, string ip)
    {
        var n = await c.ExecuteAsync("UPDATE hrms.AttDevice SET LastSeen = GETDATE(), LastIp = @ip WHERE Serial = @sn", new { sn, ip }, tx);
        if (n == 0)
            await c.ExecuteAsync("INSERT hrms.AttDevice (Serial, Name, Active, LastSeen, LastIp) VALUES (@sn, @name, 0, GETDATE(), @ip)", new { sn, name = "New machine " + sn, ip }, tx);
    }

    // Who a machine PIN is: the mapping table first, then a PIN that is itself an employee code.
    static async Task<string?> Resolve(SqlConnection c, SqlTransaction tx, string pin) =>
        await c.ExecuteScalarAsync<string?>(@"SELECT TOP 1 EmpCode FROM (
            SELECT EmpCode, 0 AS rank FROM hrms.AttDeviceUser WHERE Pin = @pin
            UNION ALL SELECT EmpCode, 1 FROM inter01.EmployeeMaster WHERE EmpCode = @pin) x ORDER BY rank", new { pin }, tx);

    // Turn one matched machine punch into an attendance punch. In/out is decided here: the machine's own flag is not trusted,
    // so the first punch of the day is In and each next one alternates with the last punch already recorded (from the app or any machine).
    static async Task<(string Result, long? PunchId)> Import(SqlConnection c, SqlTransaction tx, string emp, DateTime when, string deviceName)
    {
        var last = await c.QueryFirstOrDefaultAsync<(DateTime PunchTime, string Direction)?>(
            @"SELECT TOP 1 PunchTime, Direction FROM hrms.AttPunch WHERE EmpCode=@emp AND Deleted=0 AND PunchTime >= @day AND PunchTime <= @when ORDER BY PunchTime DESC, Id DESC",
            new { emp, day = when.Date, when }, tx);
        if (last is { } l && (when - l.PunchTime).TotalSeconds < 60) return ("Ignored", null);     // double touch
        var dir = last is { Direction: "IN" } ? "OUT" : "IN";
        var id = await c.ExecuteScalarAsync<long>(@"INSERT hrms.AttPunch (EmpCode, PunchTime, Direction, Source, WorkMode, Note, CreatedBy)
            OUTPUT INSERTED.Id VALUES (@emp, @when, @dir, 'Machine', 'Office', @note, @by)", new { emp, when, dir, note = deviceName, by = "machine" }, tx);
        return ("Imported", id);
    }

    static async Task<int> Ingest(Db db, string sn, string body, string ip)
    {
        await using var c = db.OpenWrite();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var name = await c.ExecuteScalarAsync<string?>("SELECT Name FROM hrms.AttDevice WHERE Serial=@sn", new { sn }, tx) ?? sn;
        var lines = new List<(string Pin, DateTime Time, string Status, string Verify)>();
        foreach (var raw in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = raw.Split('\t');
            if (f.Length < 2 || !DateTime.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            lines.Add((f[0].Trim(), t, f.Length > 2 ? f[2].Trim() : "", f.Length > 3 ? f[3].Trim() : ""));
        }

        var stored = 0;
        // oldest first, so in/out alternates correctly even when a machine sends a backlog
        foreach (var (pin, time, status, verify) in lines.OrderBy(l => l.Time))
        {
            var exists = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttDeviceLog WHERE DeviceSerial=@sn AND Pin=@pin AND PunchTime=@time", new { sn, pin, time }, tx);
            if (exists > 0) { stored++; continue; }          // already have it: acknowledge so the machine stops resending
            var emp = await Resolve(c, tx, pin);
            var (result, punchId) = emp is null ? ("Unmatched", (long?)null) : await Import(c, tx, emp, time, name);
            await c.ExecuteAsync(@"INSERT hrms.AttDeviceLog (DeviceSerial, Pin, PunchTime, RawStatus, Verify, SkewSeconds, EmpCode, PunchId, Result)
                VALUES (@sn, @pin, @time, @status, @verify, DATEDIFF(second, @time, GETDATE()), @emp, @punchId, @result)", new { sn, pin, time, status, verify, emp, punchId, result }, tx);
            stored++;
        }
        await Touch(c, tx, sn, ip);
        await tx.CommitAsync();
        return stored;
    }

    public static void MapDevices(this WebApplication app)
    {
        // ---------- what the machine calls (no sign-in: machines identify themselves by serial number) ----------
        app.MapGet("/iclock/cdata", async (Db db, HttpContext ctx, string? SN) =>
        {
            if (string.IsNullOrWhiteSpace(SN)) return Text("ERROR: SN missing", 400);
            await using var c = db.OpenWrite();
            await Touch(c, null, SN.Trim(), Ip(ctx));
            // the machine's start-up handshake: how often to report, and to send punches the moment they happen
            return Text($"GET OPTION FROM: {SN}\nErrorDelay=60\nDelay=30\nTransTimes=00:00;14:05\nTransInterval=1\nTransFlag=TransData AttLog\nRealtime=1\nEncrypt=0\n");
        });

        app.MapPost("/iclock/cdata", async (Db db, HttpContext ctx, string? SN, string? table) =>
        {
            if (string.IsNullOrWhiteSpace(SN)) return Text("ERROR: SN missing", 400);
            var sn = SN.Trim();
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            bool active;
            await using (var c = db.OpenWrite())
            {
                await Touch(c, null, sn, Ip(ctx));
                active = await c.ExecuteScalarAsync<bool>("SELECT Active FROM hrms.AttDevice WHERE Serial=@sn", new { sn });
            }
            if (!active) return Text("ERROR: device not approved", 403);          // a non-OK answer makes the machine keep the punches and retry
            if (!string.Equals(table, "ATTLOG", StringComparison.OrdinalIgnoreCase)) return Text("OK");
            return Text("OK:" + await Ingest(db, sn, body, Ip(ctx)));
        });

        app.MapGet("/iclock/getrequest", async (Db db, HttpContext ctx, string? SN) =>
        {
            if (!string.IsNullOrWhiteSpace(SN)) { await using var c = db.OpenWrite(); await Touch(c, null, SN.Trim(), Ip(ctx)); }
            return Text("OK");
        });
        app.MapPost("/iclock/devicecmd", () => Text("OK"));

        // ---------- administration ----------
        app.MapGet("/api/admin/attendance/devices", async (Db db) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<DeviceRow>(@"
SELECT d.Id, d.Serial, d.Name, d.Location, d.Active, d.FirstSeen, d.LastSeen, d.LastIp,
  (SELECT COUNT(*) FROM hrms.AttDeviceLog l WHERE l.DeviceSerial = d.Serial AND l.ReceivedAt >= CAST(GETDATE() AS date)) AS PunchesToday,
  (SELECT COUNT(*) FROM hrms.AttDeviceLog l WHERE l.DeviceSerial = d.Serial AND l.Result = 'Unmatched') AS Unmatched,
  (SELECT TOP 1 l.SkewSeconds FROM hrms.AttDeviceLog l WHERE l.DeviceSerial = d.Serial ORDER BY l.Id DESC) AS SkewSeconds
FROM hrms.AttDevice d ORDER BY d.Active DESC, d.Name")).ToList();
        });

        app.MapPost("/api/admin/attendance/devices", async (Db db, DeviceInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (string.IsNullOrWhiteSpace(x.Serial) || string.IsNullOrWhiteSpace(x.Name)) throw new ApiException("Serial number and name are required.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttDevice WHERE Serial=@s", new { s = x.Serial.Trim() }, tx) > 0) throw new ApiException("That serial number is already registered.", 409);
            var id = await c.ExecuteScalarAsync<int>("INSERT hrms.AttDevice (Serial, Name, Location, Active) OUTPUT INSERTED.Id VALUES (@s, @n, @l, @a)", new { s = x.Serial.Trim(), n = x.Name.Trim(), l = Nz(x.Location), a = x.Active }, tx);
            return new { Id = id };
        }));

        app.MapPut("/api/admin/attendance/devices/{id:int}", async (Db db, int id, DeviceInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (string.IsNullOrWhiteSpace(x.Name)) throw new ApiException("Name is required.");
            if (await c.ExecuteAsync("UPDATE hrms.AttDevice SET Name=@n, Location=@l, Active=@a WHERE Id=@id", new { id, n = x.Name.Trim(), l = Nz(x.Location), a = x.Active }, tx) == 0) throw new ApiException("Machine not found.", 404);
            return new { Id = id };
        }));

        app.MapGet("/api/admin/attendance/devices/mappings", async (Db db) =>
        {
            await using var c = db.Open();
            var maps = (await c.QueryAsync<MapRow>(@"SELECT m.Id, m.Pin, m.EmpCode, e.Empname AS Name FROM hrms.AttDeviceUser m LEFT JOIN inter01.EmployeeMaster e ON e.EmpCode = m.EmpCode ORDER BY TRY_CAST(m.Pin AS int), m.Pin")).ToList();
            var unmapped = (await c.QueryAsync<UnmappedPin>(@"SELECT Pin, COUNT(*) AS Punches, MAX(PunchTime) AS LastPunch FROM hrms.AttDeviceLog WHERE Result='Unmatched' GROUP BY Pin ORDER BY MAX(PunchTime) DESC")).ToList();
            return Results.Ok(new { Maps = maps, Unmapped = unmapped });
        });

        app.MapPost("/api/admin/attendance/devices/mappings", async (Db db, MapInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var pin = x.Pin?.Trim() ?? ""; var emp = x.EmpCode?.Trim() ?? "";
            if (pin.Length == 0 || emp.Length == 0) throw new ApiException("Enter the machine number and choose an employee.");
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmployeeMaster WHERE EmpCode=@emp", new { emp }, tx) == 0) throw new ApiException("That employee does not exist.", 404);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttDeviceUser WHERE Pin=@pin", new { pin }, tx) > 0) throw new ApiException("That machine number is already linked to someone.", 409);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AttDeviceUser WHERE EmpCode=@emp", new { emp }, tx) > 0) throw new ApiException("That employee already has a machine number.", 409);
            await c.ExecuteAsync("INSERT hrms.AttDeviceUser (Pin, EmpCode) VALUES (@pin, @emp)", new { pin, emp }, tx);
            return new { Pin = pin };
        }));

        app.MapDelete("/api/admin/attendance/devices/mappings/{id:int}", async (Db db, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (await c.ExecuteAsync("DELETE hrms.AttDeviceUser WHERE Id=@id", new { id }, tx) == 0) throw new ApiException("Link not found.", 404);
            return new { Id = id };
        }));

        app.MapGet("/api/admin/attendance/devices/log", async (Db db, string? result, int? limit) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<DeviceLogRow>(@"SELECT TOP (@limit) l.Id, l.DeviceSerial, d.Name AS DeviceName, l.Pin, l.PunchTime, l.ReceivedAt, l.SkewSeconds, l.EmpCode, e.Empname AS Name, l.Verify, l.Result
                FROM hrms.AttDeviceLog l LEFT JOIN hrms.AttDevice d ON d.Serial = l.DeviceSerial LEFT JOIN inter01.EmployeeMaster e ON e.EmpCode = l.EmpCode
                WHERE (@result IS NULL OR l.Result = @result) ORDER BY l.Id DESC", new { limit = Math.Clamp(limit ?? 100, 1, 500), result = Nz(result) == "All" ? null : Nz(result) })).ToList();
        });

        // After linking machine numbers, replay the punches that had no owner, oldest first.
        app.MapPost("/api/admin/attendance/devices/reprocess", async (Db db, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var pending = (await c.QueryAsync<(long Id, string DeviceSerial, string Pin, DateTime PunchTime)>(
                "SELECT Id, DeviceSerial, Pin, PunchTime FROM hrms.AttDeviceLog WHERE Result='Unmatched' ORDER BY PunchTime, Id", transaction: tx)).ToList();
            int matched = 0;
            foreach (var p in pending)
            {
                var emp = await Resolve(c, tx, p.Pin);
                if (emp is null) continue;
                var name = await c.ExecuteScalarAsync<string?>("SELECT Name FROM hrms.AttDevice WHERE Serial=@s", new { s = p.DeviceSerial }, tx) ?? p.DeviceSerial;
                var (result, punchId) = await Import(c, tx, emp, p.PunchTime, name);
                await c.ExecuteAsync("UPDATE hrms.AttDeviceLog SET EmpCode=@emp, PunchId=@punchId, Result=@result WHERE Id=@id", new { id = p.Id, emp, punchId, result }, tx);
                matched++;
            }
            return new { Matched = matched, StillUnmatched = pending.Count - matched };
        }));
    }
}
