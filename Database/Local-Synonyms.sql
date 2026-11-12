-- Local development only: the API uses plain interface.* names (the hosted version keeps everything in ONE database).
-- Locally the users, roles and rights live in HRMSINTER and the API connects to HRMSMAIN, so point the interface.* names
-- in HRMSMAIN at the HRMSINTER tables. Run against HRMSMAIN; safe to run again. Adds objects only, no data is changed.
IF SCHEMA_ID(N'interface') IS NULL EXEC('CREATE SCHEMA [interface]');
GO
DECLARE @t sysname, @sql nvarchar(max);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.name FROM HRMSINTER.sys.tables t JOIN HRMSINTER.sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'interface' AND t.is_ms_shipped = 0;
OPEN c; FETCH NEXT FROM c INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'interface.' + QUOTENAME(@t)) IS NULL
    BEGIN
        SET @sql = N'CREATE SYNONYM [interface].' + QUOTENAME(@t) + N' FOR HRMSINTER.[interface].' + QUOTENAME(@t);
        EXEC (@sql);
    END
    FETCH NEXT FROM c INTO @t;
END
CLOSE c; DEALLOCATE c;
GO
SELECT name AS synonym, base_object_name FROM sys.synonyms WHERE schema_id = SCHEMA_ID(N'interface') ORDER BY name;
