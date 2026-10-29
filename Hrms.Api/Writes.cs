using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Hrms.Api;

// Create, update and delete for the payroll masters. Every write goes through the ERP's own stored procedures
// (EmployeeMasterInsert, EmployeeLeaveInsertNEW, SPINSERTVARIABLESAL1, ...) and its transaction numbering, so rows look
// exactly like rows saved from the ERP screens. Pass ?dryRun=true to run the whole write and roll it back.

public sealed class ApiException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class HrmsSession(IConfiguration cfg)
{
    public string CompanyCode { get; } = cfg["Hrms:CompanyCode"] ?? "Launch";
    public string FinYear { get; } = cfg["Hrms:FinYear"] ?? DateTime.Today.Year.ToString();
    public string UserId { get; set; } = cfg["Hrms:UserId"] ?? "PAYROLL";
    public int UserLevel { get; set; } = int.Parse(cfg["Hrms:UserLevel"] ?? "10");
    public int DocLevel { get; } = int.Parse(cfg["Hrms:DocumentLevel"] ?? "10");
}

public record EmployeeInput(string EmpCode, string Name, DateTime? Dob, string? Gender, string? Status, string? Phone, string? OfficeNo, string? Description, string? Remarks);

public record ExtraInput(DateTime EffectiveDate, string? Company, string? Birthplace, string? Country, string? Bank, string? Ebank, string? BankAccount,
    string? Designation, string? MaritalStatus, string? BloodGroup, DateTime? Doj, DateTime? TermDate, DateTime? ConfirmDate, DateTime? ContractDate,
    string? PaymentMode, string? DocNo, double? TicketPeriod, string? EntitledYear, string? Route, bool Discontinue);

public record HeadAmount(string Code, decimal Amount);
public record StructureInput(DateTime EffectiveDate, List<HeadAmount> Heads);
public record TransferInput(DateTime Date, string? FromLocation, string ToLocation);
public record LeaveInput(string EmpCode, string LeaveType, DateTime FromDate, DateTime ToDate, DateTime? Applied, DateTime? Rejoining, decimal? Days, bool SalaryWithoutLeave);
public record LoanInput(string EmpCode, string Type, DateTime Date, decimal Amount, int Months, DateTime? FirstRecovery, string? Remarks);
public record LeaveHead(string EmpCode, DateTime FromDate, DateTime ToDate);
public record LeaveDates(DateTime FromDate, DateTime ToDate);
public record LoanHead(string Type, int Gone);
public record VarHead(string EmpCode, DateTime Date, string TransNo, string Type);
public record VarDateType(DateTime Date, string Type);
public record VariableInput(string EmpCode, string Type, DateTime Date, decimal Amount, string? Remarks);

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

public static partial class Writes
{
    // Hrms:DryRunAll=true rolls back every write (used for UI checks against live data).
    public static bool ForceDryRun { get; set; }

    static readonly string[] LeaveTypeCodes = ["LVE", "ALVESAL", "UPL", "EMRLVE", "MTRLVE"];
    static readonly string[] FixedHeads = ["BSC", "ACCAllow", "MobileAllow", "PositionAllow", "SewaAllow", "TA1", "TA2", "TA3", "FixedWPS"];
    static readonly string[] ExcludedVariable = ["LOAN", "ADVAMT", "LOREC", "ADVREC", "ADVAMTEID"];

    // ---------- plumbing ----------

    internal static async Task<IResult> Run(Db db, bool dryRun, Func<SqlConnection, SqlTransaction, Task<object?>> work)
    {
        await using var c = db.OpenWrite();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        try
        {
            var result = await work(c, tx);
            dryRun |= ForceDryRun;
            if (dryRun) await tx.RollbackAsync(); else await tx.CommitAsync();
            return Results.Ok(new { ok = true, dryRun, result });
        }
        catch (ApiException ex)
        {
            await tx.RollbackAsync();
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: ex.Status);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync();
            return Results.Json(new { ok = false, error = "Database error: " + ex.Message }, statusCode: 500);
        }
    }

    internal static async Task<string> NextNo(SqlConnection c, SqlTransaction tx, HrmsSession s, string? emp, string typeCode, string label)
    {
        string? no;
        if (emp is null)
            no = await c.ExecuteScalarAsync<string?>("inter01.SP_PayRoll_NextNumberGeneration", new { CompanyCode = s.CompanyCode, FinalcialYear = s.FinYear, TransactionType = typeCode }, tx, commandType: CommandType.StoredProcedure);
        else
            no = await c.ExecuteScalarAsync<string?>("inter01.SP_NextNumberGenerationForPayRoll", new { CompanyCode = s.CompanyCode, FinalcialYear = s.FinYear, empcode = emp, TransactionType = typeCode }, tx, commandType: CommandType.StoredProcedure);
        if (string.IsNullOrWhiteSpace(no))
            throw new ApiException($"The ERP has no {label} number series for {s.FinYear}. Create it in the ERP first.", 409);
        return no;
    }

    static Task LogTransaction(SqlConnection c, SqlTransaction tx, HrmsSession s, string transNo, string typeCode) =>
        c.ExecuteAsync("inter01.SP_UserwiseTransactionINSERT", new { TransNo = transNo, Userid = s.UserId, DateTime = DateTime.Now, TransType = typeCode, Auth = s.UserLevel }, tx, commandType: CommandType.StoredProcedure);

    static async Task RequireEmployee(SqlConnection c, SqlTransaction tx, string code)
    {
        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmployeeMaster WHERE EmpCode=@code", new { code }, tx) == 0)
            throw new ApiException($"Employee {code} does not exist.", 404);
    }

    static async Task<DateTime?> LastProcessedMonthEnd(SqlConnection c, SqlTransaction tx)
    {
        var last = await c.ExecuteScalarAsync<DateTime?>("SELECT MAX(ProcessedDate) FROM inter01.EmployeePaymentProcess", transaction: tx);
        return last is null ? null : new DateTime(last.Value.Year, last.Value.Month, DateTime.DaysInMonth(last.Value.Year, last.Value.Month));
    }

    static object? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static string S(string? v) => v?.Trim() ?? "";
    static DateTime D(DateTime? v) => v?.Date ?? DateTime.Today;

    // ---------- endpoints ----------

    public static void MapWrites(this WebApplication app)
    {
        app.MapGet("/api/lookups/form", async (Db db) =>
        {
            await using var c = db.Open();
            async Task<List<string>> Distinct(string col, string table = "inter01.EmployeeExtraDetails") =>
                (await c.QueryAsync<string>($"SELECT DISTINCT {col} FROM {table} WHERE {col} IS NOT NULL AND LTRIM(RTRIM({col})) <> '' AND ISNULL(deleteflag,0)=0 ORDER BY {col}")).ToList();
            var next = await c.ExecuteScalarAsync<int?>("SELECT MAX(TRY_CAST(SUBSTRING(EmpCode,4,10) AS int)) FROM inter01.EmployeeMaster WHERE EmpCode LIKE 'EMP[0-9]%'") ?? 0;
            var genders = (await c.QueryAsync<string>("SELECT analysiscode FROM inter01.analysismaster WHERE mastertypecode='GEN' ORDER BY analysiscode")).ToList();
            var statuses = (await c.QueryAsync<string>("SELECT analysiscode FROM inter01.analysismaster WHERE mastertypecode='STS' ORDER BY analysiscode")).ToList();
            var locations = (await c.QueryAsync<string>("SELECT DISTINCT v FROM (SELECT FromLocation v FROM inter01.EmployeeTransfer UNION SELECT ToLocation FROM inter01.EmployeeTransfer) x WHERE v IS NOT NULL AND v <> '' ORDER BY v")).ToList();
            return new FormLookups($"EMP{next + 1:000}", genders, statuses, await Distinct("designation"), await Distinct("country"), await Distinct("bank"),
                await Distinct("mop"), await Distinct("mstatus"), await Distinct("Bgroup"), locations, await Distinct("Company"));
        });

        // ----- employee master -----
        app.MapPost("/api/employees", async (Db db, EmployeeInput e, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            Validate(e);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmployeeMaster WHERE EmpCode=@EmpCode", new { e.EmpCode }, tx) > 0)
                throw new ApiException("Employee code already exists. Choose another code.", 409);
            await SaveMaster(c, tx, e);
            return new { e.EmpCode };
        }));

        app.MapPut("/api/employees/{code}", async (Db db, string code, EmployeeInput e, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            Validate(e with { EmpCode = code });
            await RequireEmployee(c, tx, code);
            await SaveMaster(c, tx, e with { EmpCode = code });
            return new { EmpCode = code };
        }));

        app.MapDelete("/api/employees/{code}", async (Db db, string code, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            await RequireEmployee(c, tx, code);
            var used = await c.ExecuteScalarAsync<int>(@"SELECT
                (SELECT COUNT(*) FROM inter01.EmployeePaymentProcess WHERE Empcode=@code) + (SELECT COUNT(*) FROM inter01.EMPLOYEELEAVEDETAILS WHERE EmpCode=@code) +
                (SELECT COUNT(*) FROM inter01.EMPVARIABLESALARY WHERE Empcode=@code) + (SELECT COUNT(*) FROM inter01.EmployeePaymentDetails WHERE EmpCode=@code) +
                (SELECT COUNT(*) FROM inter01.EmpAccuralPayment WHERE EmpCode=@code)", new { code }, tx);
            if (used > 0) throw new ApiException("This employee already has leave, salary or payroll records. Set a termination date instead of deleting.", 409);
            await c.ExecuteAsync("DELETE FROM inter01.EmployeeExtraDetails WHERE EmpCode=@code; DELETE FROM inter01.EmployeeTransfer WHERE EmpCode=@code; DELETE FROM inter01.EmployeeMaster WHERE EmpCode=@code", new { code }, tx);
            return new { EmpCode = code };
        }));

        // ----- employee extra details (versioned by effective date) -----
        app.MapGet("/api/employees/{code}/extra", async (Db db, string code) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<ExtraVersion>(@"SELECT Id, effectivedate AS EffectiveDate, Company, birthplace AS Birthplace, country AS Country, bank AS Bank, ebank AS Ebank, accode AS BankAccount,
                designation AS Designation, mstatus AS MaritalStatus, Bgroup AS BloodGroup, doj AS Doj, termdate AS TermDate, confdate AS ConfirmDate, contdate AS ContractDate, mop AS PaymentMode,
                docno AS DocNo, tktperiod AS TicketPeriod, txt7 AS EntitledYear, route AS Route, CAST(ISNULL(discontinue,0) AS bit) AS Discontinue, transno AS TransNo
                FROM inter01.EmployeeExtraDetails WHERE EmpCode=@code AND ISNULL(deleteflag,0)=0 ORDER BY effectivedate DESC, Id DESC", new { code })).ToList();
        });

        app.MapPost("/api/employees/{code}/extra", async (Db db, HrmsSession s, string code, ExtraInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            await RequireEmployee(c, tx, code);
            if (x.Doj is null) throw new ApiException("Date of joining is required.");
            if (x.TermDate is not null && x.TermDate < x.Doj) throw new ApiException("Termination date cannot be before the date of joining.");

            // Carry over the fields this screen does not edit (txt1-10, ddl1-10, dt1-10, sizes, qualifications) from the latest version.
            var prev = await c.QueryFirstOrDefaultAsync("SELECT TOP 1 * FROM inter01.EmployeeExtraDetails WHERE EmpCode=@code AND ISNULL(deleteflag,0)=0 ORDER BY effectivedate DESC, Id DESC", new { code }, tx) as IDictionary<string, object?>;
            object? P(string col, object? fallback = null) => prev is not null && prev.TryGetValue(col, out var v) && v is not DBNull ? v : fallback;

            var transNo = await NextNo(c, tx, s, null, "455", "Employee Extra Details");
            var p = new DynamicParameters();
            p.Add("EmpCode", code);
            p.Add("Company", S(x.Company) is { Length: > 0 } co ? co : P("Company", ""));
            p.Add("birthplace", S(x.Birthplace)); p.Add("country", S(x.Country));
            p.Add("shoesize", P("shoesize", "")); p.Add("dresssize", P("dresssize", ""));
            p.Add("accode", S(x.BankAccount)); p.Add("bank", S(x.Bank)); p.Add("ebank", S(x.Ebank));
            p.Add("designation", string.IsNullOrWhiteSpace(x.Designation) ? "Select" : x.Designation.Trim());
            p.Add("ldesignation", P("ldesignation", "")); p.Add("bqual", P("bqual", "")); p.Add("oqual", P("oqual", "")); p.Add("pqual", P("pqual", ""));
            p.Add("route", S(x.Route)); p.Add("tktperiod", x.TicketPeriod ?? 0);
            p.Add("mstatus", S(x.MaritalStatus)); p.Add("Bgroup", S(x.BloodGroup));
            p.Add("doj", x.Doj.Value.Date, DbType.DateTime); p.Add("termdate", x.TermDate?.Date, DbType.DateTime);
            p.Add("confdate", x.ConfirmDate?.Date, DbType.DateTime); p.Add("contdate", x.ContractDate?.Date, DbType.DateTime);
            p.Add("mop", S(x.PaymentMode)); p.Add("docno", S(x.DocNo)); p.Add("formulatype", P("formulatype", ""));
            for (var i = 1; i <= 10; i++)
            {
                p.Add("txt" + i, i == 7 ? S(x.EntitledYear) : P("txt" + i, ""));
                p.Add("ddl" + i, P("ddl" + i, ""));
                p.Add("dt" + i, P("dt" + i) as DateTime?, DbType.DateTime);
            }
            p.Add("effectivedate", x.EffectiveDate.Date, DbType.DateTime);
            p.Add("Userid", s.UserId); p.Add("Userlevel", s.UserLevel); p.Add("DocumentLevel", s.DocLevel);
            p.Add("transtype", "455"); p.Add("transno", transNo); p.Add("discontinue", x.Discontinue ? 1 : 0);
            await c.ExecuteAsync("inter01.EmployeeExtraDetailsInsert", p, tx, commandType: CommandType.StoredProcedure);
            await LogTransaction(c, tx, s, transNo, "455");

            // Same side effect as the ERP screen: keep the employee's Resign flag in step with the termination date.
            await c.ExecuteAsync("UPDATE inter01.EmployeeMaster SET Resign=@r WHERE EmpCode=@code", new { r = x.TermDate is null ? "0" : "1", code }, tx);
            return new { TransNo = transNo };
        }));

        app.MapDelete("/api/employees/{code}/extra/{id:long}", async (Db db, string code, long id, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var versions = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmployeeExtraDetails WHERE EmpCode=@code AND ISNULL(deleteflag,0)=0", new { code }, tx);
            if (versions <= 1) throw new ApiException("An employee needs at least one extra details record.", 409);
            var n = await c.ExecuteAsync("UPDATE inter01.EmployeeExtraDetails SET deleteflag=1 WHERE Id=@id AND EmpCode=@code", new { id, code }, tx);
            if (n == 0) throw new ApiException("Record not found.", 404);
            return new { Id = id };
        }));

        // ----- salary structure (grade details) -----
        app.MapGet("/api/employees/{code}/structure", async (Db db, string code) =>
        {
            await using var c = db.Open();
            var rows = (await c.QueryAsync<HeadValue>(@"SELECT SalaryType AS Code, Amt AS Amount, EffectiveDate FROM (
                SELECT SalaryType, Amt, EffectiveDate, ROW_NUMBER() OVER (PARTITION BY SalaryType ORDER BY EffectiveDate DESC, Id DESC) rn
                FROM inter01.EmployeePaymentDetails WHERE EmpCode=@code AND ISNULL(Deleteflag,0)=0 AND PaymentType='FIX') x WHERE rn=1", new { code })).ToList();
            var current = FixedHeads.Select(h => rows.FirstOrDefault(r => r.Code == h) ?? new HeadValue { Code = h }).ToList();
            foreach (var h in current) h.Name = HeadNames.Name(h.Code);
            var dates = (await c.QueryAsync<DateTime>("SELECT DISTINCT EffectiveDate FROM inter01.EmployeePaymentDetails WHERE EmpCode=@code AND ISNULL(Deleteflag,0)=0 AND PaymentType='FIX' ORDER BY EffectiveDate DESC", new { code })).ToList();
            return new StructureHistory(current, dates);
        });

        app.MapPost("/api/employees/{code}/structure", async (Db db, HrmsSession s, string code, StructureInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            await RequireEmployee(c, tx, code);
            if (x.Heads.Count == 0) throw new ApiException("Enter at least one salary head.");
            foreach (var h in x.Heads)
            {
                if (!FixedHeads.Contains(h.Code)) throw new ApiException($"{h.Code} is not a fixed salary head.");
                if (h.Amount < 0) throw new ApiException("Amounts cannot be negative.");
            }
            var pmh = await c.ExecuteScalarAsync<string?>("SELECT MIN(PMHCode) FROM inter01.PaymentMaster", transaction: tx) ?? "001";
            var transNo = await NextNo(c, tx, s, null, "457", "Employee Grade Details");
            foreach (var h in x.Heads)
                await c.ExecuteAsync("inter01.EmployeePaymentDetailsInsert", new
                {
                    EffectiveDate = x.EffectiveDate.Date, EmpCode = code, SalaryType = h.Code, PaymentType = "FIX", PMHCode = pmh, Amt = (double)h.Amount,
                    UserID = s.UserId, Userlevel = s.UserLevel, DocumentLevel = s.DocLevel, transactiontype = "457", transno = transNo
                }, tx, commandType: CommandType.StoredProcedure);
            await LogTransaction(c, tx, s, transNo, "457");
            return new { TransNo = transNo, Heads = x.Heads.Count };
        }));

        // ----- transfers -----
        app.MapPost("/api/employees/{code}/transfers", async (Db db, HrmsSession s, string code, TransferInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            await RequireEmployee(c, tx, code);
            if (string.IsNullOrWhiteSpace(x.ToLocation)) throw new ApiException("Enter the location the employee is moving to.");
            var from = Nz(x.FromLocation) ?? await c.ExecuteScalarAsync<string?>("SELECT TOP 1 ToLocation FROM inter01.EmployeeTransfer WHERE EmpCode=@code ORDER BY Date DESC, Id DESC", new { code }, tx) ?? "";
            var transNo = await NextNo(c, tx, s, null, "1001", "Employee Transfer");
            await c.ExecuteAsync("inter01.EmployeeTransferDetailsInsert", new
            {
                EmpCode = code, dot = x.Date.Date, fromlocation = from, tolocation = x.ToLocation.Trim(), Userlevel = s.UserLevel, DocumentLevel = s.DocLevel,
                userid = s.UserId, transtype = "1001", transno = transNo
            }, tx, commandType: CommandType.StoredProcedure);
            await LogTransaction(c, tx, s, transNo, "1001");
            return new { TransNo = transNo };
        }));

        app.MapDelete("/api/payroll/transfers/{id:int}", async (Db db, int id, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var n = await c.ExecuteAsync("DELETE FROM inter01.EmployeeTransfer WHERE Id=@id", new { id }, tx);
            if (n == 0) throw new ApiException("Transfer not found.", 404);
            return new { Id = id };
        }));

        // ----- leave -----
        app.MapPost("/api/leave", async (Db db, HrmsSession s, LeaveInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            await RequireEmployee(c, tx, x.EmpCode);
            await ValidateLeave(c, tx, x, null);
            var transNo = await NextNo(c, tx, s, x.EmpCode, "458", "Employee Leave Apply");
            var days = x.Days ?? (decimal)(x.ToDate.Date - x.FromDate.Date).TotalDays + 1;
            await c.ExecuteAsync("inter01.EmployeeLeaveInsertNEW", new
            {
                EmpCode = x.EmpCode, FromDate = x.FromDate.Date, Todate = x.ToDate.Date, LeaveType = x.LeaveType, Applieddate = D(x.Applied), RejoiningDate = (x.Rejoining ?? x.ToDate.AddDays(1)).Date,
                Userid = s.UserId, Userlevel = s.UserLevel, Documentlevel = s.DocLevel, Transtype = "458", Transno = transNo, Deleteflag = false,
                LeaveSalWithoutLeave = x.SalaryWithoutLeave ? 1 : 0, LeaveSalWithLeave = 1, Lnoofdays = days
            }, tx, commandType: CommandType.StoredProcedure);
            await LogTransaction(c, tx, s, transNo, "458");
            return new { TransNo = transNo, Days = days };
        }));

        app.MapPut("/api/leave/{id:long}", async (Db db, HrmsSession s, long id, LeaveInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var cur = await c.QueryFirstOrDefaultAsync<LeaveHead>("SELECT EmpCode, FromDate, ToDate FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id AND ISNULL(Deleteflag,0)=0", new { id }, tx)
                ?? throw new ApiException("Leave record not found.", 404);
            await GuardLeaveChange(c, tx, id, cur.FromDate, cur.ToDate);
            await ValidateLeave(c, tx, x with { EmpCode = cur.EmpCode }, id);
            var days = x.Days ?? (decimal)(x.ToDate.Date - x.FromDate.Date).TotalDays + 1;
            await c.ExecuteAsync("inter01.EmployeeLeaveupdate", new
            {
                Id = id, transtype = "458", EmpCode = cur.EmpCode, FromDate = x.FromDate.Date, Todate = x.ToDate.Date, LeaveType = x.LeaveType, applieddate = D(x.Applied),
                rejoiningdate = (x.Rejoining ?? x.ToDate.AddDays(1)).Date, deleteflag = false, userlevel = s.UserLevel
            }, tx, commandType: CommandType.StoredProcedure);
            await c.ExecuteAsync("UPDATE inter01.EMPLOYEELEAVEDETAILS SET NoofLeavedays=@days, LeaveSalWithoutLeave=@w WHERE Id=@id", new { days, w = x.SalaryWithoutLeave ? 1 : 0, id }, tx);
            return new { Id = id };
        }));

        app.MapDelete("/api/leave/{id:long}", async (Db db, long id, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var cur = await c.QueryFirstOrDefaultAsync<LeaveDates>("SELECT FromDate, ToDate FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id", new { id }, tx)
                ?? throw new ApiException("Leave record not found.", 404);
            await GuardLeaveChange(c, tx, id, cur.FromDate, cur.ToDate);
            var transNo = await c.ExecuteScalarAsync<string?>("SELECT Transno FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id", new { id }, tx);
            await c.ExecuteAsync("inter01.SPDELETELeaveDetails", new { Id = id }, tx, commandType: CommandType.StoredProcedure);
            await Workflow.RemoveAccruals(c, tx, transNo);
            return new { Id = id };
        }));

        // ----- loans and advances -----
        app.MapGet("/api/payroll/loans/{id:long}/installments", async (Db db, long id) =>
        {
            await using var c = db.Open();
            return (await c.QueryAsync<EntryRow>(@"SELECT ID AS Id, Empcode AS EmpCode, Type, Amount, Date, Noofmonth AS Installments, Transno AS TransNo
                FROM inter01.EMPVARIABLESALARY WHERE RefId=@id AND ISNULL(Variabledelete,0)=0 ORDER BY Date", new { id })).ToList()
                .Select(r => { r.TypeName = HeadNames.Name(r.Type); return r; }).ToList();
        });

        app.MapPost("/api/payroll/loans", async (Db db, HrmsSession s, LoanInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            await RequireEmployee(c, tx, x.EmpCode);
            if (x.Type is not ("LOAN" or "ADVAMT")) throw new ApiException("Choose Loan or Advance.");
            if (x.Amount <= 0) throw new ApiException("Enter an amount greater than zero.");
            if (x.Months is < 1 or > 60) throw new ApiException("Installments must be between 1 and 60.");

            var parentNo = await NextNo(c, tx, s, x.EmpCode, "460", "Employee Advances and Loans");
            var created = D(x.Date).ToString("dd/MMM/yyyy");
            await InsertVariable(c, tx, s, x.EmpCode, D(x.Date), x.Amount, x.Type, 0, x.Months, false, parentNo, "460", created, x.Remarks);
            var parentId = await c.ExecuteScalarAsync<long>("SELECT MAX(ID) FROM inter01.EMPVARIABLESALARY WHERE Transno=@parentNo AND Empcode=@emp", new { parentNo, emp = x.EmpCode }, tx);
            await LogTransaction(c, tx, s, parentNo, "460");

            var recType = x.Type == "LOAN" ? "LOREC" : "ADVREC";
            var each = Math.Round(x.Amount / x.Months, 2);
            var total = 0m;
            var due = D(x.FirstRecovery ?? x.Date);
            for (var i = 0; i < x.Months; i++)
            {
                var amount = i == x.Months - 1 ? x.Amount - total : each;
                total += amount;
                var childNo = await NextNo(c, tx, s, x.EmpCode, "460", "Employee Advances and Loans");
                await InsertVariable(c, tx, s, x.EmpCode, due.AddMonths(i), amount, recType, parentId, x.Months, true, childNo, "460", created, x.Remarks);
            }
            var schedule = (await c.QueryAsync<(DateTime Date, decimal Amount, string TransNo)>("SELECT Date, Amount, Transno FROM inter01.EMPVARIABLESALARY WHERE RefId=@parentId ORDER BY Date", new { parentId }, tx)).ToList();
            return new { TransNo = parentNo, ParentId = parentId, Installments = x.Months, Monthly = each, Schedule = schedule.Select(r => new { r.Date, r.Amount, r.TransNo }) };
        }));

        app.MapDelete("/api/payroll/loans/{id:long}", async (Db db, long id, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var parent = await c.QueryFirstOrDefaultAsync<LoanHead>("SELECT Type, CAST(ISNULL(Variabledelete,0) AS int) AS Gone FROM inter01.EMPVARIABLESALARY WHERE ID=@id AND RefId=0", new { id }, tx)
                ?? throw new ApiException("Loan or advance not found.", 404);
            if (parent.Type is not ("LOAN" or "ADVAMT")) throw new ApiException("Only loans and advances can be cancelled here.");
            if (parent.Gone == 1) throw new ApiException("This entry is already cancelled.", 409);
            var last = await LastProcessedMonthEnd(c, tx);
            if (last is not null && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EMPVARIABLESALARY WHERE RefId=@id AND Date<=@last AND ISNULL(Variabledelete,0)=0", new { id, last }, tx) > 0)
                throw new ApiException("Some installments fall in months that payroll has already processed. Reverse that in the ERP.", 409);
            await c.ExecuteAsync("inter01.SPDELETEVARIABLESAL_ForAdvandLoan", new { Id = id }, tx, commandType: CommandType.StoredProcedure);
            return new { Id = id };
        }));

        // ----- variable salary -----
        app.MapGet("/api/payroll/variable/types", async (Db db) =>
        {
            await using var c = db.Open();
            var codes = (await c.QueryAsync<string>("SELECT DISTINCT SalaryType FROM inter01.PaymentMaster WHERE PaymentType='VARI' ORDER BY SalaryType")).Where(x => !ExcludedVariable.Contains(x));
            return codes.Select(x => new { code = x, name = HeadNames.Name(x) }).OrderBy(x => x.name).ToList();
        });

        app.MapPost("/api/payroll/variable", async (Db db, HrmsSession s, VariableInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            await RequireEmployee(c, tx, x.EmpCode);
            await ValidateVariable(c, tx, x);
            var transNo = await NextNo(c, tx, s, x.EmpCode, "459", "Employee Variable Salary");
            await InsertVariable(c, tx, s, x.EmpCode, D(x.Date), x.Amount, x.Type, 0, 1, false, transNo, "459", D(x.Date).ToString("dd/MMM/yyyy"), x.Remarks);
            await LogTransaction(c, tx, s, transNo, "459");
            return new { TransNo = transNo };
        }));

        app.MapPut("/api/payroll/variable/{id:long}", async (Db db, HrmsSession s, long id, VariableInput x, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var cur = await c.QueryFirstOrDefaultAsync<VarHead>(
                "SELECT Empcode AS EmpCode, Date, Transno AS TransNo, Type FROM inter01.EMPVARIABLESALARY WHERE ID=@id AND ISNULL(Variabledelete,0)=0 AND ISNULL(Deleteflag,0)=0", new { id }, tx)
                ?? throw new ApiException("Entry not found.", 404);
            if (cur.Type is "LOAN" or "ADVAMT" or "LOREC" or "ADVREC") throw new ApiException("Edit loans and advances from the Loans tab.");
            await GuardVariableChange(c, tx, cur.Date);
            await ValidateVariable(c, tx, x with { EmpCode = cur.EmpCode });
            await GuardVariableChange(c, tx, D(x.Date));
            // SPINSERTVARIABLESALNew updates in place when emp + transaction no + date match; a date change is done by direct update.
            await c.ExecuteAsync("UPDATE inter01.EMPVARIABLESALARY SET Amount=@amount, Type=@type, Date=@date, LeaveStartDate=@date, Remarks=@remarks, UserId=@user WHERE ID=@id",
                new { amount = x.Amount, type = x.Type, date = D(x.Date), remarks = Nz(x.Remarks), user = s.UserId, id }, tx);
            return new { Id = id };
        }));

        app.MapDelete("/api/payroll/variable/{id:long}", async (Db db, long id, bool dryRun = false) => await Run(db, dryRun, async (c, tx) =>
        {
            var cur = await c.QueryFirstOrDefaultAsync<VarDateType>("SELECT Date, Type FROM inter01.EMPVARIABLESALARY WHERE ID=@id AND ISNULL(Variabledelete,0)=0", new { id }, tx)
                ?? throw new ApiException("Entry not found.", 404);
            if (cur.Type is "LOAN" or "ADVAMT" or "LOREC" or "ADVREC") throw new ApiException("Cancel loans and advances from the Loans tab.");
            await GuardVariableChange(c, tx, cur.Date);
            await c.ExecuteAsync("inter01.SPDELETEVARIABLESALNew", new { Id = id }, tx, commandType: CommandType.StoredProcedure);
            return new { Id = id };
        }));
    }

    // ---------- helpers ----------

    static void Validate(EmployeeInput e)
    {
        if (string.IsNullOrWhiteSpace(e.EmpCode)) throw new ApiException("Enter the employee code.");
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ApiException("Enter the employee name.");
        if (e.Dob is null) throw new ApiException("Enter the date of birth.");
        if (e.Dob > DateTime.Today.AddYears(-14)) throw new ApiException("Check the date of birth.");
    }

    static Task SaveMaster(SqlConnection c, SqlTransaction tx, EmployeeInput e) =>
        c.ExecuteAsync("inter01.EmployeeMasterInsert", new
        {
            EmpCode = e.EmpCode.Trim(), Empname = e.Name.Trim(), dob = e.Dob!.Value.Date, Gender = S(e.Gender), Status = S(e.Status), PhoneNo = S(e.Phone),
            OfficePhone = S(e.OfficeNo), OtherDetails = S(e.Description), Remarks = S(e.Remarks), ImagePath = ""
        }, tx, commandType: CommandType.StoredProcedure);

    static async Task ValidateLeave(SqlConnection c, SqlTransaction tx, LeaveInput x, long? ignoreId)
    {
        if (!LeaveTypeCodes.Contains(x.LeaveType)) throw new ApiException("Choose a leave type.");
        if (x.ToDate.Date < x.FromDate.Date) throw new ApiException("The leave end date cannot be before the start date.");
        if (x.Days is <= 0) throw new ApiException("Number of days must be greater than zero.");
        var clash = await c.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM inter01.EMPLOYEELEAVEDETAILS WHERE EmpCode=@EmpCode AND ISNULL(Deleteflag,0)=0
            AND (@ignore IS NULL OR Id<>@ignore) AND FromDate <= @to AND ToDate >= @from", new { x.EmpCode, ignore = ignoreId, from = x.FromDate.Date, to = x.ToDate.Date }, tx);
        if (clash > 0) throw new ApiException("There cannot be two leaves with overlapping dates for the same employee.", 409);
    }

    static async Task GuardLeaveChange(SqlConnection c, SqlTransaction tx, long id, DateTime from, DateTime to)
    {
        var last = await LastProcessedMonthEnd(c, tx);
        if (last is not null && from.Date <= last) throw new ApiException("Payroll has already been processed for these dates. Reverse that in the ERP first.", 409);
        var transNo = await c.ExecuteScalarAsync<string?>("SELECT Transno FROM inter01.EMPLOYEELEAVEDETAILS WHERE Id=@id", new { id }, tx);
        if (transNo is not null && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.EmpAccuralPayment WHERE LTransno=@transNo AND ISNULL(Amount,0)<>0", new { transNo }, tx) > 0)
            throw new ApiException("An accrual has already been posted for this leave. Reverse it in the ERP first.", 409);
    }

    static async Task ValidateVariable(SqlConnection c, SqlTransaction tx, VariableInput x)
    {
        if (ExcludedVariable.Contains(x.Type) || string.IsNullOrWhiteSpace(x.Type)) throw new ApiException("Choose a variable salary type.");
        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM inter01.PaymentMaster WHERE SalaryType=@t AND PaymentType='VARI'", new { t = x.Type }, tx) == 0)
            throw new ApiException($"{x.Type} is not a variable salary head in the ERP.");
        if (x.Amount <= 0) throw new ApiException("Enter an amount greater than zero.");
    }

    static async Task GuardVariableChange(SqlConnection c, SqlTransaction tx, DateTime date)
    {
        var last = await LastProcessedMonthEnd(c, tx);
        if (last is not null && date.Date <= last) throw new ApiException("Payroll has already been processed for this date. Reverse that in the ERP first.", 409);
    }

    static Task InsertVariable(SqlConnection c, SqlTransaction tx, HrmsSession s, string emp, DateTime date, decimal amount, string type, long refId, int months,
        bool deleteFlag, string transNo, string transType, string created, string? remarks) =>
        c.ExecuteAsync("inter01.SPINSERTVARIABLESAL1", new
        {
            Id = 0, Empcode = emp, Date = date, Amount = amount, Type = type, RefiD = refId, noofmonth = months, Userid = s.UserId, UserLevel = s.UserLevel.ToString(),
            DocAuthLevel = s.DocLevel.ToString(), Deleteflag = deleteFlag, Visible = 1, Transno = transNo, Transtype = transType, Status = 1, Createddate = created, Remarks = S(remarks)
        }, tx, commandType: CommandType.StoredProcedure);
}
