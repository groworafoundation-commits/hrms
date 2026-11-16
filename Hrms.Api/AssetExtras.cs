using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// The rest of asset management, all following the ERP's flow:
//  - one list of everything waiting for a decision (issue, return, transfer) or for the receiver (transfer acceptance), with a count for the inbox;
//  - transfer between employees: the ERP's Asset Transfer (613) is authorised like any document and then the receiver accepts it (619);
//    the asset stays with the first person, and counts for their clearance, until the receiver has accepted;
//  - photos and signed forms kept with the asset; bulk import from a spreadsheet; and warnings for people who are leaving with assets.

public class AssetWaiting
{
    public string Kind { get; set; } = "";               // Issue, Return, Transfer (waiting approval), Acceptance (waiting for the receiver)
    public int Id { get; set; }                          // handover id for Issue and Return; transfer id for Transfer and Acceptance
    public string? DocNo { get; set; }
    public int AssetId { get; set; }
    public string AssetCode { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string SerialNo { get; set; } = "";
    public string EmpCode { get; set; } = "";            // the holder (for a transfer, the person giving it up)
    public string? EmpName { get; set; }
    public string? ToEmpCode { get; set; }
    public string? ToEmpName { get; set; }
    public DateTime Date { get; set; }
    public string? Condition { get; set; }
    public string? Note { get; set; }
    public string PreparedBy { get; set; } = "";
    public bool CanDecide { get; set; }
    public bool CanAccept { get; set; }
}

public record AssetTransferInput(string? ToEmp, DateTime Date, string? Condition, string? Note);

public class AssetTransferRow
{
    public int Id { get; set; }
    public string? DocNo { get; set; }
    public int AssetId { get; set; }
    public string AssetCode { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string SerialNo { get; set; } = "";
    public string FromEmp { get; set; } = "";
    public string? FromName { get; set; }
    public string ToEmp { get; set; } = "";
    public string? ToName { get; set; }
    public DateTime TransferDate { get; set; }
    public string? Condition { get; set; }
    public string? Note { get; set; }
    public string PreparedBy { get; set; } = "";
    public string Status { get; set; } = "";
    public string? DecisionNote { get; set; }
    public DateTime? AcceptedAt { get; set; }
}

public class AssetFileRow
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public int? CustodyId { get; set; }
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int SizeBytes { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

public record ImportRow(string? AssetCode, string? Category, string? Name, string? SerialNo, decimal? Value, DateTime? PurchaseDate, string? Notes, string? EmpCode, DateTime? IssueDate);
public record ImportInput(List<ImportRow>? Rows);
public record ImportRowResult(int Row, bool Ok, string? Error);
public record LeavingRow(string EmpCode, string Name, DateTime TermDate, int AssetsOut);

public static class AssetExtras
{
    public const string TransferType = "613";
    const int MaxFileBytes = 5 * 1024 * 1024, MaxFilesPerAsset = 12, MaxImportRows = 500;
    static readonly string[] FileKinds = ["Photo", "Handover form", "Other"];
    static AuthUser Caller(HttpContext ctx) => AssetEndpoints.Caller(ctx);
    static string? Nz(string? s) => AssetEndpoints.Nz(s);

    const string WaitingSql = @"
SELECT 'Issue' AS Kind, c.Id, c.DocNo, c.AssetId, a.AssetCode, a.Name AS AssetName, a.SerialNo, c.EmpCode, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = c.EmpCode) AS EmpName,
       CAST(NULL AS nvarchar(50)) AS ToEmpCode, CAST(NULL AS nvarchar(200)) AS ToEmpName, CAST(c.IssueDate AS datetime) AS Date, c.ConditionOut AS Condition, c.IssueNote AS Note, c.IssuedBy AS PreparedBy
FROM hrms.AssetCustody c JOIN hrms.Asset a ON a.Id = c.AssetId WHERE c.IssueStatus = 'Pending'
UNION ALL
SELECT 'Return', c.Id, c.ReturnDocNo, c.AssetId, a.AssetCode, a.Name, a.SerialNo, c.EmpCode, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = c.EmpCode),
       NULL, NULL, CAST(c.ReturnDate AS datetime), c.ConditionIn, c.ReturnNote, ISNULL(c.ReturnedTo, '')
FROM hrms.AssetCustody c JOIN hrms.Asset a ON a.Id = c.AssetId WHERE c.IssueStatus = 'Approved' AND c.ReturnStatus = 'Pending'
UNION ALL
SELECT CASE t.Status WHEN 'Pending' THEN 'Transfer' ELSE 'Acceptance' END, t.Id, t.DocNo, t.AssetId, a.AssetCode, a.Name, a.SerialNo, t.FromEmp, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = t.FromEmp),
       t.ToEmp, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = t.ToEmp), CAST(t.TransferDate AS datetime), t.Condition, t.Note, t.PreparedBy
FROM hrms.AssetTransfer t JOIN hrms.Asset a ON a.Id = t.AssetId WHERE t.Status IN ('Pending', 'Accepting')";

    const string TransferSelect = @"
SELECT t.Id, t.DocNo, t.AssetId, a.AssetCode, a.Name AS AssetName, a.SerialNo, t.FromEmp, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = t.FromEmp) AS FromName,
       t.ToEmp, (SELECT TOP 1 e.Empname FROM inter01.EmployeeMaster e WHERE e.EmpCode = t.ToEmp) AS ToName, t.TransferDate, t.Condition, t.Note, t.PreparedBy, t.Status, t.DecisionNote, t.AcceptedAt
FROM hrms.AssetTransfer t JOIN hrms.Asset a ON a.Id = t.AssetId";

    static async Task<AssetTransferRow> LoadTransfer(SqlConnection c, SqlTransaction? tx, int id) =>
        await c.QueryFirstOrDefaultAsync<AssetTransferRow>(TransferSelect + " WHERE t.Id = @id", new { id }, tx) ?? throw new ApiException("That transfer does not exist.", 404);

    // Shared checks for deciding on a transfer: waiting for approval, at the document level, not the person who prepared it, not one of the two employees.
    static async Task<(AssetTransferRow Row, AuthUser User)> DecidableTransfer(SqlConnection c, SqlTransaction tx, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id)
    {
        var user = Caller(ctx);
        var t = await LoadTransfer(c, tx, id);
        if (t.Status != "Pending") throw new ApiException("Nothing is waiting for approval on that transfer.", 409);
        var need = AssetEndpoints.DocLevel(cfg, s);
        if (s.UserLevel < need) throw new ApiException($"Deciding needs approval level {need}. Yours is {s.UserLevel}.", 403);
        if (string.Equals(t.PreparedBy, user.UserId, StringComparison.OrdinalIgnoreCase)) throw new ApiException("You cannot approve a transfer you prepared yourself.", 403);
        if (user.EmployeeCode is { } mine && (string.Equals(t.FromEmp, mine, StringComparison.OrdinalIgnoreCase) || string.Equals(t.ToEmp, mine, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException("You cannot decide a transfer you are part of.", 403);
        return (t, user);
    }

    // The receiver has accepted: the asset moves. The first handover is closed as a return and a new approved handover opens for the receiver.
    static async Task<object> Complete(SqlConnection c, SqlTransaction tx, int id, string acceptedBy)
    {
        var t = await LoadTransfer(c, tx, id);
        if (t.Status != "Accepting") throw new ApiException(t.Status == "Completed" ? "That transfer is already complete." : "That transfer is not ready to be accepted.", 409);
        var approvedBy = await c.ExecuteScalarAsync<string?>("SELECT ApprovedBy FROM hrms.AssetTransfer WHERE Id = @id", new { id }, tx);
        await c.ExecuteAsync(@"UPDATE hrms.AssetCustody SET ReturnDate = @date, ConditionIn = @cond, ReturnNote = @note, ReturnedTo = @prep, ReturnStatus = 'Approved',
            ReturnApprovedBy = @approver, ReturnApprovedAt = GETDATE(), ReturnDocNo = @doc WHERE Id = (SELECT FromCustodyId FROM hrms.AssetTransfer WHERE Id = @id)",
            new { id, date = t.TransferDate.Date, cond = t.Condition, note = "Transferred to " + (t.ToName ?? t.ToEmp), prep = t.PreparedBy, approver = approvedBy, doc = t.DocNo }, tx);
        var custody = await c.ExecuteScalarAsync<int>(@"INSERT hrms.AssetCustody (DocNo, AssetId, EmpCode, IssueDate, ConditionOut, IssueNote, IssuedBy, IssueStatus, IssueApprovedBy, IssueApprovedAt, AcceptedAt)
            OUTPUT INSERTED.Id VALUES (@doc, @asset, @to, @date, @cond, @note, @prep, 'Approved', @approver, GETDATE(), GETDATE())",
            new { doc = t.DocNo, asset = t.AssetId, to = t.ToEmp, date = t.TransferDate.Date, cond = t.Condition, note = "Transferred from " + (t.FromName ?? t.FromEmp), prep = t.PreparedBy, approver = approvedBy }, tx);
        await c.ExecuteAsync("UPDATE hrms.AssetTransfer SET Status = 'Completed', AcceptedBy = @by, AcceptedAt = GETDATE(), NewCustodyId = @custody WHERE Id = @id", new { id, by = acceptedBy, custody }, tx);
        await Notifier.Send(c, tx, t.FromEmp, "asset", "Asset transferred", $"{t.AssetName} ({t.AssetCode}) now belongs to {t.ToName ?? t.ToEmp}.", "my/assets");
        return new { Id = id, NewCustody = custody };
    }

    // ---------- magic bytes: the file must really be what it says ----------
    static bool LooksLike(byte[] head, string type) => type switch
    {
        "image/jpeg" => head.Length > 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
        "image/png" => head.Length > 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47,
        "image/gif" => head.Length > 3 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46,
        "image/webp" => head.Length > 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50,
        "application/pdf" => head.Length > 4 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46,
        _ => false,
    };

    static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["phone"] = "Mobile phone", ["mobile"] = "Mobile phone", ["mobile phone"] = "Mobile phone", ["sim"] = "SIM card", ["sim card"] = "SIM card",
        ["card"] = "Access card", ["id card"] = "Access card", ["access card"] = "Access card", ["tools"] = "Tools and equipment", ["equipment"] = "Tools and equipment",
        ["tools and equipment"] = "Tools and equipment", ["pc"] = "Desktop", ["notebook"] = "Laptop",
    };

    static string? CanonicalCategory(string? raw)
    {
        var v = Nz(raw);
        if (v is null) return null;
        return AssetEndpoints.Categories.FirstOrDefault(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)) ?? (CategoryAliases.TryGetValue(v, out var a) ? a : null);
    }

    public static void MapAssetExtras(this WebApplication app)
    {
        // ----- everything waiting, for the assets screen and the inbox -----
        app.MapGet("/api/assets/waiting", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx) =>
        {
            var user = Caller(ctx);
            var levelOk = s.UserLevel >= AssetEndpoints.DocLevel(cfg, s);
            var canAdd = user.IsAdmin || (user.Rights.TryGetValue("H301", out var r) && r.Add);
            await using var c = db.Open();
            var rows = (await c.QueryAsync<AssetWaiting>(WaitingSql + " ORDER BY 12")).ToList();
            foreach (var w in rows)
            {
                var involved = user.EmployeeCode is { } me && (string.Equals(w.EmpCode, me, StringComparison.OrdinalIgnoreCase) || string.Equals(w.ToEmpCode, me, StringComparison.OrdinalIgnoreCase));   // a login with no employee record is never "involved"
                w.CanDecide = w.Kind != "Acceptance" && levelOk && canAdd && !involved && !string.Equals(w.PreparedBy, user.UserId, StringComparison.OrdinalIgnoreCase);
                w.CanAccept = w.Kind == "Acceptance" && canAdd;
            }
            return Results.Ok(rows);
        });

        // transfers the signed-in employee has to accept
        app.MapGet("/api/me/assets/incoming", async (Db db, HttpContext ctx) =>
        {
            var me = Caller(ctx).EmployeeCode!;
            await using var c = db.Open();
            var rows = (await c.QueryAsync<AssetWaiting>("SELECT * FROM (" + WaitingSql + ") w WHERE w.Kind = 'Acceptance' AND w.ToEmpCode = @me ORDER BY w.Date", new { me })).ToList();
            foreach (var w in rows) w.CanAccept = true;
            return Results.Ok(rows);
        });

        // one handover or transfer, for the signature slips
        app.MapGet("/api/assets/custody/{id:int}", async (Db db, int id) =>
        {
            await using var c = db.Open();
            try { return Results.Ok(await AssetEndpoints.Load(c, null, id)); }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapGet("/api/assets/transfers/{id:int}", async (Db db, int id) =>
        {
            await using var c = db.Open();
            try { return Results.Ok(await LoadTransfer(c, null, id)); }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        });

        // ----- transfers -----
        app.MapGet("/api/assets/transfers", async (Db db, string? scope) =>
        {
            await using var c = db.Open();
            var where = scope == "history" ? "t.Status IN ('Completed','Rejected')" : "1 = 1";
            return Results.Ok((await c.QueryAsync<AssetTransferRow>(TransferSelect + $" WHERE {where} ORDER BY t.Id DESC")).Take(300).ToList());
        });

        app.MapPost("/api/assets/custody/{id:int}/transfer", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id, AssetTransferInput x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var user = Caller(ctx);
            var cu = await AssetEndpoints.Load(c, tx, id);
            if (cu.IssueStatus != "Approved" || cu.ReturnStatus is "Approved" or "Pending") throw new ApiException("Only an asset that is with an employee can be transferred.", 409);
            if (cu.TransferStatus is not null) throw new ApiException("A transfer is already in progress for this asset.", 409);
            var to = Nz(x.ToEmp) ?? throw new ApiException("Choose the employee who receives it.");
            if (string.Equals(to, cu.EmpCode, StringComparison.OrdinalIgnoreCase)) throw new ApiException("The asset is already with that employee.");
            var toName = await AssetEndpoints.ActiveEmployeeName(c, tx, to) ?? throw new ApiException($"Employee {to} is not an active employee.", 409);
            if (x.Date.Date < cu.IssueDate.Date) throw new ApiException("The transfer date cannot be before the asset was issued.");
            await AssetEndpoints.CheckDate(c, tx, TransferType, x.Date, "transfer");

            var need = AssetEndpoints.DocLevel(cfg, s);
            var approved = s.UserLevel >= need;
            var tid = await c.ExecuteScalarAsync<int>(@"INSERT hrms.AssetTransfer (AssetId, FromCustodyId, FromEmp, ToEmp, TransferDate, Condition, Note, PreparedBy, Status, ApprovedBy, ApprovedAt)
                OUTPUT INSERTED.Id VALUES (@asset, @id, @from, @to, @date, @cond, @note, @user, @status, @approver, @at)",
                new { asset = cu.AssetId, id, from = cu.EmpCode, to, date = x.Date.Date, cond = Nz(x.Condition), note = Nz(x.Note), user = user.UserId, status = approved ? "Accepting" : "Pending", approver = approved ? user.UserId : null, at = approved ? (DateTime?)DateTime.Now : null }, tx);
            var doc = $"ATRF{DateTime.Today:yy}-{tid:00000}";
            await c.ExecuteAsync("UPDATE hrms.AssetTransfer SET DocNo = @doc WHERE Id = @tid", new { tid, doc }, tx);
            if (approved) await Notifier.Send(c, tx, to, "asset", "An asset is being transferred to you", $"{cu.AssetName} ({cu.AssetCode}) from {cu.EmpName ?? cu.EmpCode}. Please accept it.", "my/assets");
            else await AssetEndpoints.NotifyApprovers(c, tx, need, user.UserId, "An asset transfer needs your approval", $"{cu.AssetName} ({cu.AssetCode}): {cu.EmpName ?? cu.EmpCode} to {toName}");
            return new { Id = tid, DocNo = doc, Pending = !approved };
        }));

        app.MapPost("/api/assets/transfers/{id:int}/approve", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id, DecideNote x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var (t, user) = await DecidableTransfer(c, tx, s, cfg, ctx, id);
            await c.ExecuteAsync("UPDATE hrms.AssetTransfer SET Status = 'Accepting', ApprovedBy = @user, ApprovedAt = GETDATE(), DecisionNote = @note WHERE Id = @id", new { id, user = user.UserId, note = Nz(x.Note) }, tx);
            await Notifier.Send(c, tx, t.ToEmp, "asset", "An asset is being transferred to you", $"{t.AssetName} ({t.AssetCode}) from {t.FromName ?? t.FromEmp}. Please accept it.", "my/assets");
            return new { Id = id };
        }));

        app.MapPost("/api/assets/transfers/{id:int}/withdraw", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var user = Caller(ctx);
            var t = await LoadTransfer(c, tx, id);
            if (t.Status != "Pending") throw new ApiException("Only a transfer that is still waiting for approval can be withdrawn.", 409);
            if (!string.Equals(t.PreparedBy, user.UserId, StringComparison.OrdinalIgnoreCase)) throw new ApiException("Only the person who prepared it can withdraw it.", 403);
            await c.ExecuteAsync("UPDATE hrms.AssetTransfer SET Status = 'Rejected', DecisionNote = 'Withdrawn by ' + @user WHERE Id = @id", new { id, user = user.UserId }, tx);
            return new { Id = id };
        }));

        app.MapPost("/api/assets/transfers/{id:int}/reject", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, int id, DecideNote x, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (Nz(x.Note) is null) throw new ApiException("Please give a reason for rejecting.");
            var (_, user) = await DecidableTransfer(c, tx, s, cfg, ctx, id);
            await c.ExecuteAsync("UPDATE hrms.AssetTransfer SET Status = 'Rejected', ApprovedBy = @user, ApprovedAt = GETDATE(), DecisionNote = @note WHERE Id = @id", new { id, user = user.UserId, note = Nz(x.Note) }, tx);
            return new { Id = id };
        }));

        // HR receives it on the employee's behalf (the ERP's acceptance document is also a user entry)
        app.MapPost("/api/assets/transfers/{id:int}/accept", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) => await Complete(c, tx, id, Caller(ctx).UserId)));

        app.MapPost("/api/me/assets/transfers/{id:int}/accept", async (Db db, HttpContext ctx, int id, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            var me = Caller(ctx).EmployeeCode!;
            var t = await LoadTransfer(c, tx, id);
            if (!string.Equals(t.ToEmp, me, StringComparison.OrdinalIgnoreCase)) throw new ApiException("That transfer is not for you.", 404);
            return await Complete(c, tx, id, Caller(ctx).UserId);
        }));

        // ----- photos and signed forms -----
        app.MapGet("/api/assets/{id:int}/files", async (Db db, int id) =>
        {
            await using var c = db.Open();
            return Results.Ok((await c.QueryAsync<AssetFileRow>("SELECT Id, AssetId, CustodyId, Kind, FileName, ContentType, SizeBytes, UploadedBy, UploadedAt FROM hrms.AssetFile WHERE AssetId = @id AND Deleted = 0 ORDER BY Id DESC", new { id })).ToList());
        });

        app.MapPost("/api/assets/{id:int}/files", async (Db db, HttpContext ctx, int id, IFormFile file, string? kind, int? custodyId) =>
        {
            try
            {
                var k = FileKinds.FirstOrDefault(x => string.Equals(x, kind, StringComparison.OrdinalIgnoreCase)) ?? "Photo";
                var type = (file.ContentType ?? "").ToLowerInvariant();
                if (file.Length == 0) throw new ApiException("That file is empty.");
                if (file.Length > MaxFileBytes) throw new ApiException($"That file is larger than {MaxFileBytes / 1024 / 1024} MB.");
                if (type is not ("image/jpeg" or "image/png" or "image/gif" or "image/webp" or "application/pdf")) throw new ApiException("Only photos (JPEG, PNG, GIF, WebP) and PDF files can be attached.");
                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var data = ms.ToArray();
                if (!LooksLike(data, type)) throw new ApiException("That file is not really a " + (type == "application/pdf" ? "PDF" : "photo") + ".");
                var name = Path.GetFileName(file.FileName ?? "file");
                await using var c = db.OpenWrite();
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Asset WHERE Id = @id AND Deleted = 0", new { id }) == 0) throw new ApiException("That asset does not exist.", 404);
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AssetFile WHERE AssetId = @id AND Deleted = 0", new { id }) >= MaxFilesPerAsset) throw new ApiException($"An asset can have at most {MaxFilesPerAsset} files. Remove one first.", 409);
                var fid = await c.ExecuteScalarAsync<int>("INSERT hrms.AssetFile (AssetId, CustodyId, Kind, FileName, ContentType, SizeBytes, Data, UploadedBy) OUTPUT INSERTED.Id VALUES (@id, @custodyId, @k, @name, @type, @size, @data, @user)",
                    new { id, custodyId, k, name = name.Length > 200 ? name[..200] : name, type, size = data.Length, data, user = Caller(ctx).UserId });
                return Results.Ok(new { ok = true, result = new { Id = fid } });
            }
            catch (ApiException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status); }
        }).DisableAntiforgery();

        app.MapGet("/api/assets/files/{fileId:int}", async (Db db, int fileId) =>
        {
            await using var c = db.Open();
            var f = await c.QueryFirstOrDefaultAsync<(string FileName, string ContentType, byte[] Data)?>("SELECT FileName, ContentType, Data FROM hrms.AssetFile WHERE Id = @fileId AND Deleted = 0", new { fileId });
            return f is null ? Results.NotFound() : Results.File(f.Value.Data, f.Value.ContentType, f.Value.FileName);
        });

        app.MapDelete("/api/assets/files/{fileId:int}", async (Db db, int fileId, bool dryRun = false) => await Writes.Run(db, dryRun, async (c, tx) =>
        {
            if (await c.ExecuteAsync("UPDATE hrms.AssetFile SET Deleted = 1 WHERE Id = @fileId AND Deleted = 0", new { fileId }, tx) == 0) throw new ApiException("That file does not exist.", 404);
            return new { Id = fileId };
        }));

        // ----- bulk import: nothing is saved unless every row is good -----
        app.MapPost("/api/assets/import", async (Db db, HrmsSession s, IConfiguration cfg, HttpContext ctx, ImportInput x, bool dryRun = false) =>
        {
            var rows = x.Rows ?? [];
            if (rows.Count == 0) return Results.Json(new { ok = false, error = "There are no rows to import." }, statusCode: 400);
            if (rows.Count > MaxImportRows) return Results.Json(new { ok = false, error = $"Import at most {MaxImportRows} rows at a time." }, statusCode: 400);
            var user = Caller(ctx);
            var canAssign = s.UserLevel >= AssetEndpoints.DocLevel(cfg, s);

            await using var c = db.OpenWrite();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
            var results = new List<ImportRowResult>();
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assigned = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; var line = i + 1;
                try
                {
                    var code = Nz(r.AssetCode) ?? throw new ApiException("Tag is missing.");
                    var name = Nz(r.Name) ?? throw new ApiException("Name is missing.");
                    var serial = Nz(r.SerialNo) ?? throw new ApiException("Serial number is missing.");
                    var category = CanonicalCategory(r.Category) ?? throw new ApiException($"Category '{r.Category}' is not one of: {string.Join(", ", AssetEndpoints.Categories)}.");
                    if (r.Value < 0) throw new ApiException("Value cannot be negative.");
                    if (!codes.Add(code)) throw new ApiException($"Tag {code} appears twice in the file.");
                    if (!serials.Add(serial)) throw new ApiException($"Serial number {serial} appears twice in the file.");
                    if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.Asset WHERE Deleted = 0 AND (AssetCode = @code OR SerialNo = @serial)", new { code, serial }, tx) > 0) throw new ApiException("An asset with that tag or serial number already exists.");
                    var emp = Nz(r.EmpCode);
                    string? empName = null;
                    if (emp is not null)
                    {
                        if (!canAssign) throw new ApiException($"Assigning an asset while importing needs approval level {AssetEndpoints.DocLevel(cfg, s)}.");
                        empName = await AssetEndpoints.ActiveEmployeeName(c, tx, emp) ?? throw new ApiException($"Employee {emp} is not an active employee.");
                    }
                    {   // saved inside the transaction even for a trial run, so later rows see earlier ones; a trial run or any error rolls it all back
                        var assetId = await c.ExecuteScalarAsync<int>(@"INSERT hrms.Asset (AssetCode, Category, Name, SerialNo, Value, PurchaseDate, Status, Notes, CreatedBy)
                            OUTPUT INSERTED.Id VALUES (@code, @category, @name, @serial, @value, @pd, @status, @notes, @user)",
                            new { code, category, name, serial, value = r.Value, pd = r.PurchaseDate?.Date, status = emp is null ? "InStock" : "Issued", notes = Nz(r.Notes), user = user.UserId }, tx);
                        if (emp is not null)
                        {
                            // an opening balance: the asset is already with this person, so it is recorded as an approved handover on its own date
                            var cid = await c.ExecuteScalarAsync<int>(@"INSERT hrms.AssetCustody (AssetId, EmpCode, IssueDate, IssueNote, IssuedBy, IssueStatus, IssueApprovedBy, IssueApprovedAt)
                                OUTPUT INSERTED.Id VALUES (@assetId, @emp, @date, 'Imported: already with the employee', @user, 'Approved', @user, GETDATE())",
                                new { assetId, emp, date = (r.IssueDate ?? DateTime.Today).Date, user = user.UserId }, tx);
                            await c.ExecuteAsync("UPDATE hrms.AssetCustody SET DocNo = @doc WHERE Id = @cid", new { cid, doc = $"AEIS{DateTime.Today:yy}-{cid:00000}" }, tx);
                            assigned++;
                        }
                    }
                    results.Add(new ImportRowResult(line, true, null));
                }
                catch (ApiException ex) { results.Add(new ImportRowResult(line, false, ex.Message)); }
                catch (SqlException ex) { results.Add(new ImportRowResult(line, false, "Database error: " + ex.Message)); }
            }
            var allOk = results.All(r => r.Ok);
            if (allOk && !dryRun) await tx.CommitAsync(); else await tx.RollbackAsync();
            return Results.Ok(new { ok = allOk, dryRun, imported = allOk && !dryRun ? rows.Count : 0, assigned = allOk ? assigned : 0, results });
        });

        // ----- people who are leaving -----
        app.MapGet("/api/clearance/leaving", async (Db db) =>
        {
            await using var c = db.Open();
            return Results.Ok(await Leaving(c, null, includeWithoutAssets: true));
        });
    }

    // Employees whose last working day is within the next 60 days or passed in the last 60, with no clearance started or finished, and how many assets they hold.
    public static async Task<List<LeavingRow>> Leaving(SqlConnection c, SqlTransaction? tx, bool includeWithoutAssets)
    {
        var from = DateTime.Today.AddDays(-60); var to = DateTime.Today.AddDays(60);
        var people = (await c.QueryAsync<(string EmpCode, string Name, DateTime TermDate)>(Sql.EmpCte + "SELECT EmpCode, Name, TermDate FROM emp WHERE TermDate IS NOT NULL AND TermDate BETWEEN @from AND @to", new { from, to }, tx))
            .GroupBy(p => p.EmpCode, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (people.Count == 0) return [];
        var codes = people.Select(p => p.EmpCode).ToList();
        var out_ = (await c.QueryAsync<(string EmpCode, int N)>(@"SELECT EmpCode, COUNT(*) FROM hrms.AssetCustody WHERE EmpCode IN @codes AND IssueStatus IN ('Pending','Approved') AND ISNULL(ReturnStatus,'') <> 'Approved' GROUP BY EmpCode", new { codes }, tx))
            .ToDictionary(x => x.EmpCode, x => x.N, StringComparer.OrdinalIgnoreCase);
        var started = (await c.QueryAsync<string>("SELECT DISTINCT EmpCode FROM hrms.Clearance WHERE EmpCode IN @codes AND Status IN ('Open','Cleared')", new { codes }, tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return people.Where(p => !started.Contains(p.EmpCode) && (includeWithoutAssets || out_.GetValueOrDefault(p.EmpCode) > 0))
            .Select(p => new LeavingRow(p.EmpCode, p.Name, p.TermDate, out_.GetValueOrDefault(p.EmpCode))).OrderBy(p => p.TermDate).ToList();
    }
}

// Every couple of hours, tells HR about anyone who is leaving with company assets and has no clearance yet: once per person and last working day.
public sealed class ClearanceWatch(Db db, ILogger<ClearanceWatch> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await Run(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Leaving-with-assets check failed"); }
            try { await Task.Delay(TimeSpan.FromHours(2), stop); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task<int> Run()
    {
        await using var c = db.OpenWrite();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var leaving = await AssetExtras.Leaving(c, tx, includeWithoutAssets: false);
        var told = 0;
        if (leaving.Count > 0)
        {
            var hr = (await c.QueryAsync<string>(@"SELECT DISTINCT u.userid FROM interface.Userdef u JOIN interface.accessright a ON a.roleid = u.roleid AND a.optionid = 'H302' AND CAST(a.access AS int) = 1
                WHERE ISNULL(u.[disable],0) = 0", transaction: tx)).ToList();
            foreach (var p in leaving)
            {
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hrms.AssetAlert WHERE EmpCode = @EmpCode AND TermDate = @TermDate", new { p.EmpCode, TermDate = p.TermDate.Date }, tx) > 0) continue;
                var title = $"{Title(p.Name)} leaves on {p.TermDate:d MMM yyyy} with {p.AssetsOut} company asset{(p.AssetsOut == 1 ? "" : "s")}";
                foreach (var u in hr) await Notifier.Send(c, tx, u, "clearance", title, "Start a clearance so the assets are returned before the last day.", "admin/clearance");
                await c.ExecuteAsync("INSERT hrms.AssetAlert (EmpCode, TermDate) VALUES (@EmpCode, @TermDate)", new { p.EmpCode, TermDate = p.TermDate.Date }, tx);
                told++;
            }
        }
        await tx.CommitAsync();
        return told;
    }

    static string Title(string s) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}
