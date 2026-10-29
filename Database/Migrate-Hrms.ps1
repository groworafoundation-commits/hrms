# One-time copy of the HR-related part of the ERP into two new databases:
#   LaunchInter -> HRMSINTER  (users, roles, rights, menu, company)
#   LaunchMain  -> HRMSMAIN   (employees, leave, payroll, loans, lookups, numbering)
# Source databases are only read (READ UNCOMMITTED). Nothing is written to them.
# Usage: powershell -File Migrate-Hrms.ps1 -Password <sa password>
param(
    [Parameter(Mandatory)][string]$Password,
    [string]$Server = 'np:\\.\pipe\MSSQL$MSSQLSERVER01\sql\query',
    [string]$Log = "$PSScriptRoot\migrate.log"
)
$ErrorActionPreference = 'Stop'
function Cs($db) { "Data Source=$Server;Initial Catalog=$db;User ID=sa;Password=$Password;TrustServerCertificate=True;Connect Timeout=30" }
function Say($m) { $l = "{0:HH:mm:ss} {1}" -f (Get-Date), $m; Write-Host $l; Add-Content -Path $Log -Value $l }
function Open($db) { $c = New-Object System.Data.SqlClient.SqlConnection (Cs $db); $c.Open(); $c }
function Exec($c, $sql, $t = 600) { $m = $c.CreateCommand(); $m.CommandTimeout = $t; $m.CommandText = $sql; [void]$m.ExecuteNonQuery() }
function Table($c, $sql) { $m = $c.CreateCommand(); $m.CommandTimeout = 300; $m.CommandText = $sql; $da = New-Object System.Data.SqlClient.SqlDataAdapter $m; $dt = New-Object System.Data.DataTable; [void]$da.Fill($dt); , $dt }
function Br($n) { '[' + $n.Replace(']', ']]') + ']' }

$InterTables = @('interface.Userdef', 'interface.Roledef', 'interface.accessright', 'interface.interface', 'interface.CompanySetUp', 'interface.Application')
$MainTables = @('EmployeeMaster', 'EmployeeExtraDetails', 'EmployeePaymentDetails', 'EmployeePaymentProcess', 'EmployeeProcessPaymentDetails', 'EmployeeTransfer',
    'EMPLOYEELEAVEDETAILS', 'EMPLOYEELEAVE', 'EMPVARIABLESALARY', 'EmpAccuralPayment', 'AccessUser', 'PaymentMaster', 'dg_masters', 'analysismaster', 'tb_ReferenceMaster',
    'ledgertype', 'NextNumber', 'transtype', 'UserwiseTransaction', 'itemmaster', 'inv_head', 'inv_footer') | ForEach-Object { "inter01.$_" }
$Procs = @('SPINSERTVARIABLESAL1', 'SPINSERTVARIABLESALNew', 'SPUPDATEVARIABLESAL', 'SPDELETEVARIABLESAL', 'SPDELETEVARIABLESALNew', 'SPDELETEVARIABLESAL_ForAdvandLoan',
    'SPDELETELeaveDetails', 'SPINSERTLeaveMASTER', 'SP_UserwiseTransactionINSERT', 'SP_PayRoll_NextNumberGeneration', 'SP_NextNumberGenerationForPayRoll',
    'EmployeeTransferDetailsInsert', 'EmployeePaymentDetailsInsert', 'EmployeeMasterInsert', 'EmployeeLeaveupdate', 'EmployeeLeaveInsertNEW', 'EmployeeExtraDetailsInsert', 'sp_deleteempdetails')
# ERP-wide sync triggers (company list, item pricing/mapping) are left out; the HR triggers are kept.
$Triggers = @('trg_EmpAccuralPayment', 'trg_EmpAccuralPaymentUpdate', 'trg_EmployeeProcessPayment', 'trg_EmployeeProcessPaymentUpdate',
    'trg_EmployeeTypeInsert', 'trg_EmployeeTypeUpdate', 'trig_Ins_Employee_Ref', 'trig_DEL_Employee_Ref')

function ColType($r) {
    $t = $r.tname
    switch -Regex ($t) {
        '^(char|varchar|binary|varbinary)$' { return "$t(" + $(if ($r.max_length -eq -1) { 'MAX' } else { $r.max_length }) + ')' }
        '^(nchar|nvarchar)$' { return "$t(" + $(if ($r.max_length -eq -1) { 'MAX' } else { [int]($r.max_length / 2) }) + ')' }
        '^(decimal|numeric)$' { return "$t($($r.precision),$($r.scale))" }
        '^(datetime2|time|datetimeoffset)$' { return "$t($($r.scale))" }
        '^float$' { return "float($($r.precision))" }
        default { return $t }
    }
}

function CopyTable($src, $dst, $full) {
    $sch, $name = $full.Split('.')
    $obj = "[$sch].[$name]"
    $id = (Table $src "SELECT OBJECT_ID(N'$obj') id").Rows[0].id
    if ($id -is [DBNull]) { throw "Source table $obj not found" }

    $cols = Table $src "SELECT c.column_id, c.name, TYPE_NAME(c.user_type_id) tname, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
        ic.seed_value, ic.increment_value, dc.name dname, dc.definition ddef, cc.definition cdef
        FROM sys.columns c LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.parent_object_id=c.object_id AND dc.parent_column_id=c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
        WHERE c.object_id=$id ORDER BY c.column_id"
    $defs = foreach ($r in $cols.Rows) {
        if ($r.is_computed) { "$(Br $r.name) AS $($r.cdef)"; continue }
        $d = "$(Br $r.name) $(ColType $r)"
        if ($r.is_identity) { $d += " IDENTITY($($r.seed_value),$($r.increment_value))" }
        $d += $(if ($r.is_nullable) { ' NULL' } else { ' NOT NULL' })
        if ($r.ddef -isnot [DBNull]) { $d += " CONSTRAINT $(Br $r.dname) DEFAULT $($r.ddef)" }
        $d
    }
    Exec $dst "IF SCHEMA_ID(N'$sch') IS NULL EXEC('CREATE SCHEMA [$sch]');"
    Exec $dst "CREATE TABLE $obj (`n  $($defs -join ",`n  ")`n);"

    # data (rowversion and computed columns are not copied)
    $copyCols = $cols.Rows | Where-Object { -not $_.is_computed -and $_.tname -ne 'timestamp' } | ForEach-Object { $_.name }
    $sel = ($copyCols | ForEach-Object { Br $_ }) -join ', '
    $m = $src.CreateCommand(); $m.CommandTimeout = 1800; $m.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SELECT $sel FROM $obj"
    $rd = $m.ExecuteReader()
    $bc = New-Object System.Data.SqlClient.SqlBulkCopy($dst, ([System.Data.SqlClient.SqlBulkCopyOptions]::KeepIdentity -bor [System.Data.SqlClient.SqlBulkCopyOptions]::KeepNulls -bor [System.Data.SqlClient.SqlBulkCopyOptions]::TableLock), $null)
    $bc.DestinationTableName = $obj; $bc.BulkCopyTimeout = 1800; $bc.BatchSize = 20000
    foreach ($n in $copyCols) { [void]$bc.ColumnMappings.Add($n, $n) }
    $bc.WriteToServer($rd); $rd.Close(); $bc.Close()

    # indexes and keys (foreign keys point at tables that are not part of the copy, so they are left out)
    $ix = Table $src "SELECT i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.filter_definition FROM sys.indexes i
        WHERE i.object_id=$id AND i.type IN (1,2) AND i.is_hypothetical=0 ORDER BY i.index_id"
    foreach ($i in $ix.Rows) {
        $kc = Table $src "SELECT c.name, ic.is_descending_key d, ic.is_included_column inc FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE ic.object_id=$id AND ic.index_id=$($i.index_id) AND ic.key_ordinal>=0 ORDER BY ic.is_included_column, ic.key_ordinal"
        $keys = ($kc.Rows | Where-Object { -not $_.inc } | ForEach-Object { (Br $_.name) + $(if ($_.d) { ' DESC' } else { '' }) }) -join ', '
        $inc = ($kc.Rows | Where-Object { $_.inc } | ForEach-Object { Br $_.name }) -join ', '
        $cl = $(if ($i.type -eq 1) { 'CLUSTERED' } else { 'NONCLUSTERED' })
        try {
            if ($i.is_primary_key) { Exec $dst "ALTER TABLE $obj ADD CONSTRAINT $(Br $i.name) PRIMARY KEY $cl ($keys)" 1800 }
            elseif ($i.is_unique_constraint) { Exec $dst "ALTER TABLE $obj ADD CONSTRAINT $(Br $i.name) UNIQUE $cl ($keys)" 1800 }
            else {
                $sql = "CREATE $(if ($i.is_unique) { 'UNIQUE ' })$cl INDEX $(Br $i.name) ON $obj ($keys)"
                if ($inc) { $sql += " INCLUDE ($inc)" }
                if ($i.filter_definition -isnot [DBNull]) { $sql += " WHERE $($i.filter_definition)" }
                Exec $dst $sql 1800
            }
        }
        catch { Say "  index $($i.name) on $obj skipped: $($_.Exception.Message)" }
    }
    $n = (Table $dst "SELECT COUNT(*) n FROM $obj").Rows[0].n
    Say "  $obj copied: $n rows"
    return $n
}

function Rewrite($text) { $text -replace '(?i)\bLaunchInter\b', 'HRMSINTER' -replace '(?i)\bLaunchMain\b', 'HRMSMAIN' }

function CopyModules($src, $dst, $names, $kind) {
    $pending = New-Object System.Collections.ArrayList
    foreach ($n in $names) {
        $r = Table $src "SELECT s.name sch, o.name, m.definition, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.sql_modules m ON m.object_id=o.object_id WHERE o.name=N'$n' AND $kind"
        if ($r.Rows.Count -eq 0) { Say "  $kind $n not found in source"; continue }
        [void]$pending.Add(@{ Name = $n; Sql = (Rewrite $r.Rows[0].definition) })
    }
    for ($pass = 1; $pass -le 4 -and $pending.Count -gt 0; $pass++) {
        foreach ($p in @($pending)) {
            try { Exec $dst $p.Sql; Say "  created $($p.Name)"; $pending.Remove($p) } catch { $p.Err = $_.Exception.Message }
        }
    }
    foreach ($p in $pending) { Say "  FAILED $($p.Name): $($p.Err)" }
}

Remove-Item $Log -ErrorAction SilentlyContinue
$master = Open 'master'
foreach ($db in 'HRMSINTER', 'HRMSMAIN') {
    if ((Table $master "SELECT DB_ID(N'$db') id").Rows[0].id -isnot [DBNull]) { throw "$db already exists. Drop it first if you want to redo the copy." }
    Exec $master "CREATE DATABASE [$db] COLLATE SQL_Latin1_General_CP1_CI_AS"
    Exec $master "ALTER DATABASE [$db] SET RECOVERY SIMPLE"
    Say "created database $db"
}
$master.Close()

Say '== HRMSINTER'
$src = Open 'LaunchInter'; $dst = Open 'HRMSINTER'
foreach ($t in $InterTables) { [void](CopyTable $src $dst $t) }
CopyModules $src $dst @('SELECTMENUOPTION') "o.type='V'"
$src.Close(); $dst.Close()

Say '== HRMSMAIN'
$src = Open 'LaunchMain'; $dst = Open 'HRMSMAIN'
foreach ($t in $MainTables) { [void](CopyTable $src $dst $t) }
Say '-- procedures'
CopyModules $src $dst $Procs "o.type='P'"
Say '-- triggers'
CopyModules $src $dst $Triggers "o.type='TR'"
$src.Close(); $dst.Close()
Say 'DONE'
