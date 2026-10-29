# HRMS (Blazor Hybrid MAUI) over the Launch ERP

Two projects:

- `Hrms.Api` - ASP.NET Core minimal API, read-only queries over the ERP `HRMSMAIN` database. HRMSMAIN (data) and HRMSINTER (users, roles, rights, menu) are a one-time copy of LaunchMain and LaunchInter made by `Database/Migrate-Hrms.ps1`.
  (`inter01.EmployeeMaster`, `EmployeeExtraDetails`, `EMPLOYEELEAVEDETAILS`, `EmployeePaymentProcess`).
- `Hrms.App` - .NET MAUI Blazor Hybrid client (Windows/Android/iOS) calling the API.

## Run

1. Store the connection string once (kept out of source control):

   ```
   cd Hrms.Api
   dotnet user-secrets set "ConnectionStrings:Main" "Data Source=np:\.\pipe\MSSQL$MSSQLSERVER01\sql\query;Initial Catalog=HRMSMAIN;User ID=<user>;Password=<password>;TrustServerCertificate=True;Connect Timeout=10"
   ```

   The instance has TCP disabled, so the named-pipe form is used (it is also much faster than the
   `localhost\MSSQLSERVER01` browser lookup). Prefer a SELECT-only SQL login over `sa`.

2. `dotnet run --urls http://localhost:5080` in `Hrms.Api`.
3. `dotnet run -f net9.0-windows10.0.19041.0` in `Hrms.App` (Android emulator uses `10.0.2.2:5080`).

## Notes

- Build `Hrms.App` from a short path (for example `C:\src\HRMS`): the WinUI XAML compiler fails on very long paths.
- Leave approval status is derived from `Userlevel >= documentlevel`; all current rows are approved.
- Attendance punches (`AttendenceCalculation`) only contain 2014 data, so there is no live attendance screen yet.
- Designations show ERP codes (TEC, SLM...); there is no designation-name table in `HRMSMAIN`.

## Payroll section

Payroll has nine tabs (Payslips, Employee master, Salary structure, Employee leave, Loans and advances, Variable salary,
Accruals, Transfers, Salary heads), all read-only over the ERP tables:

| Tab | ERP source |
| --- | --- |
| Employee master | `EmployeeMaster` + `EmployeeExtraDetails` (latest row per employee) |
| Salary structure | `EmployeePaymentDetails` (FIX heads effective today) |
| Employee leave | `EMPLOYEELEAVEDETAILS` |
| Loans and advances | `EMPVARIABLESALARY` types LOAN, ADVAMT, LOREC, ADVREC; recoveries from `EmployeePaymentProcess` |
| Variable salary | `EMPVARIABLESALARY`, all other types |
| Accruals | `EmpAccuralPayment` |
| Transfers | `EmployeeTransfer` |
| Salary heads | `PaymentMaster` |

The outstanding loan balance is an estimate: issued minus payroll deductions minus manual returns.

To open a screen directly (useful for demos), set `HRMS_START` before launching, for example `HRMS_START=/payroll/loans`.

## Writing to the ERP (create, edit, delete)

The app can now create, edit and delete the payroll masters. Every write calls the ERP's own stored procedures and number
series, so rows are indistinguishable from rows saved in the ERP:

| Master | ERP procedure | Number series |
| --- | --- | --- |
| Employee master | `EmployeeMasterInsert` | none |
| Extra details (versioned by effective date) | `EmployeeExtraDetailsInsert` | 455 `EED` |
| Salary structure | `EmployeePaymentDetailsInsert` | 457 `EGD` |
| Transfer | `EmployeeTransferDetailsInsert` | 1001 `ET` |
| Leave | `EmployeeLeaveInsertNEW`, `EmployeeLeaveupdate`, `SPDELETELeaveDetails` | 458 `LVE` (`La/26/EMP002/LVE/4026`) |
| Loan or advance + installments | `SPINSERTVARIABLESAL1`, `SPDELETEVARIABLESAL_ForAdvandLoan` | 460 `EAL` |
| Variable salary | `SPINSERTVARIABLESAL1`, `SPDELETEVARIABLESALNew` | 459 `EVS` |

Writes are made as one ERP user, set in `Hrms.Api/appsettings.json` (or user-secrets): `Hrms:CompanyCode` (Launch),
`Hrms:FinYear` (defaults to the current year), `Hrms:UserId` (PAYROLL), `Hrms:UserLevel` and `Hrms:DocumentLevel` (10).
The app has no sign-in yet, so ERP access rights per user are not applied.

Safety rules: edits and deletes are refused for dates payroll has already processed, for loans whose installments are
already in a processed month, for leave with a posted accrual, for the last extra details version, and for employees
with leave, salary or payroll history.

Testing without changing data: add `?dryRun=true` to any write, or start the API with `Hrms__DryRunAll=true`. The write runs
completely and is then rolled back (number series included).

Not covered: loan or advance cash returns (the ERP posts an accounting voucher), accounting posting of loans and variable
salary (done later in the ERP), salary head and leave master set-up.

## Security, notifications, leave balances and tests

- **Passwords** are salted PBKDF2 hashes (`Database/Security.sql` adds the columns; the API hashes every old plain-text password the first time it starts and clears the original). 5 wrong attempts lock an account for 15 minutes; an administrator can unlock it. Temporary passwords must be changed at first sign-in. New passwords need 8+ characters with a letter and a number.
- **HTTPS**: the API serves HTTPS on 5443 using a certificate kept in `~/.hrms` (path and password in user-secrets as `Hrms:CertPath` / `Hrms:CertPassword`). The app trusts only that certificate, pinned by the fingerprint in `~/.hrms/api-cert.sha256`. Plain HTTP on 5080 is only for punching machines (`/iclock`); the API refuses it. Set `Hrms:ListenOn` to `any` to listen on the network.
- **Notifications**: the bell shows in-app notifications. Emails queue in `hrms.EmailOutbox` and are sent when SMTP is configured (`Hrms:Smtp:Host`, `Port`, `User`, `Password`, `From`); otherwise they are listed as Skipped (`Database/Notifications.sql`).
- **Leave balances** come from the ERP payroll ledger (`CTMAnnual Leave`), see `Hrms.Api/LeaveBalances.cs`.
- **Tests**: `dotnet test` in `Hrms.Tests` runs the unit tests and the API tests. The API tests use the real HRMS databases, create their own `TST*` users and remove everything they create.

## Web app

`Hrms.Shared` holds the screens, models and API clients used by both the Windows app (`Hrms.App`, .NET MAUI Blazor Hybrid) and the web app (`Hrms.Web`, Blazor WebAssembly). The few things that differ between them (saving files, making PDFs, remembered preferences) sit behind `IHostServices`.

The API serves the web app, so the site and the API share one address: run `Hrms.Api` and open `http://localhost:5080/` (or `https://localhost:5443/`). The web app keeps the sign-in for the length of the browser tab. Plain HTTP is accepted for the API only from the same computer; any other computer must use HTTPS with a real certificate.

Projects: `Hrms.Api` (API and web host), `Hrms.Shared` (screens), `Hrms.Web` (browser), `Hrms.App` (Windows), `Hrms.Tests` (automated tests), `Database` (SQL and the one-time copy script).
