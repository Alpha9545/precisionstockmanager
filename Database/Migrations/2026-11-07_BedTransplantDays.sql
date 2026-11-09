-- ============================================================================
-- 2026-11-07_BedTransplantDays.sql          *** PREPARED FOR REVIEW -- NOT APPLIED to PlantsIMS2_Test (local or live) ***
--
-- WHY (owner requirement 2026-11-07): Bed Sowing Alerts -- an open bed (Pending / Partially transplanted) is due for
--   transplanting on  bed Sowing Date + the variety's Bed Transplant Days. The owner keeps those days per variety group in
--   BedSowing_ReadyDate.xlsx (sheet "Sheet1", column "Bed Sowing Ready Date" = a number of days); they are loaded into this
--   column by Database/DataEntry/2026-11-07_BedTransplantDays (generated from the workbook, reviewed, PREVIEW then COMMIT).
--   Kept apart from ReadyStockDays (tray batch: transplant date + Ready Days), which is unchanged.
--
-- WHAT (one column + one CHECK; nothing else changes, no data row changes):
--   dbo.PlantSpecies.BedTransplantDays INT NULL, CK_PlantSpecies_BedTransplantDays: NULL or 1..365 (WITH CHECK, trusted).
--   NULL = no transplant schedule for that variety: its open beds are listed under "No transplant schedule", never dated.
--
-- APPLICATION WITHOUT THIS SCRIPT: Data/BedTransplantScheduleSchema.cs reports "not installed": Bed Sowing Alerts shows a
--   notice instead of lists, the dashboard shows no Bed Sowing Alerts figures, Plant Master hides the field. Ready Alerts
--   (Direct Seed Sowing) are not affected either way.
--
-- TARGET SAFEGUARDS (as 2026-11-03_FieldWorkDays.sql): the first batch refuses to continue unless ALL of these hold:
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is DRYRUN or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
-- OPERATOR CHECKLIST: identity query, independent machine check, COPY_ONLY backup, DRYRUN first; COMMIT only with the owner's
--   explicit approval for that database:
--   sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN 'TARGET_SERVER="<ServerName>"' 'TARGET_MACHINE="<MachineName>"'
--             'TARGET_DB="<DatabaseName>"' 'TARGET_DB_CREATED="<DatabaseCreated>"' -i <this file>
--
-- Requires: dbo.PlantSpecies.ReadyStockDays (Phase 24) and 2026-10-12_BedSowing.sql (dbo.BedSowings). One transaction.
--   Idempotent: a re-run reports "already present" and changes nothing. A column / CHECK that exists with a DIFFERENT
--   definition = schema drift: refused, nothing changes.
-- Undo: 2026-11-07_BedTransplantDays_Rollback.sql (refused while any variety still has Bed Transplant Days).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54501, '2026-11-07_BedTransplantDays.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54502, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54503, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54504, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54505, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54506, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54507, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION BedTransplantDays1107;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.PlantSpecies', 'ReadyStockDays') IS NULL
        THROW 54508, 'dbo.PlantSpecies.ReadyStockDays is missing -- this is not a PlantStockManager database at the expected level.', 1;
    IF OBJECT_ID('dbo.BedSowings', 'U') IS NULL
        THROW 54509, 'Run 2026-10-12_BedSowing.sql first.', 1;

    PRINT '=== Before: varieties / open beds (unchanged by this script) ===';
    SELECT (SELECT COUNT(*) FROM dbo.PlantSpecies) AS Varieties,
           (SELECT COUNT(*) FROM dbo.BedSowings WHERE Status IN (N'PendingTransplant', N'PartiallyTransplanted')) AS OpenBeds;

    -- 1. the column -- must be exactly INT NULL when present
    IF COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') IS NULL
    BEGIN
        ALTER TABLE dbo.PlantSpecies ADD BedTransplantDays INT NULL;
        PRINT '=== dbo.PlantSpecies.BedTransplantDays added (INT NULL, every variety NULL) ===';
    END
    ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                        WHERE c.object_id = OBJECT_ID('dbo.PlantSpecies') AND c.name = N'BedTransplantDays'
                          AND t.name = N'int' AND c.is_nullable = 1)
        THROW 54510, 'dbo.PlantSpecies.BedTransplantDays exists but is not INT NULL -- schema drift, nothing changed.', 1;
    ELSE PRINT '=== dbo.PlantSpecies.BedTransplantDays already present -- no-op ===';

    -- 2. the CHECK (dynamic SQL: the column does not exist yet when this batch is compiled)
    IF OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.PlantSpecies WITH CHECK ADD CONSTRAINT CK_PlantSpecies_BedTransplantDays
    CHECK (BedTransplantDays IS NULL OR (BedTransplantDays >= 1 AND BedTransplantDays <= 365));');
        PRINT '=== CK_PlantSpecies_BedTransplantDays added (NULL or 1..365) ===';
    END
    ELSE PRINT '=== CK_PlantSpecies_BedTransplantDays already present ===';

    DECLARE @CkDef NVARCHAR(MAX) = OBJECT_DEFINITION(OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays'));
    IF @CkDef IS NULL OR @CkDef NOT LIKE N'%[[]BedTransplantDays]>=(1)%' OR @CkDef NOT LIKE N'%[[]BedTransplantDays]<=(365)%'
        THROW 54511, 'CK_PlantSpecies_BedTransplantDays does not have the expected rule (NULL or 1..365) -- schema drift, nothing changed.', 1;
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PlantSpecies_BedTransplantDays' AND (is_not_trusted = 1 OR is_disabled = 1))
        THROW 54512, 'CK_PlantSpecies_BedTransplantDays is disabled or not trusted -- nothing changed.', 1;

    SELECT COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') AS ColumnBytes,
           COLUMNPROPERTY(OBJECT_ID('dbo.PlantSpecies'), 'BedTransplantDays', 'AllowsNull') AS AllowsNull,
           (SELECT CAST(is_not_trusted AS INT) FROM sys.check_constraints WHERE name = N'CK_PlantSpecies_BedTransplantDays') AS CheckNotTrusted,
           @CkDef AS CheckDefinition;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedTransplantDays1107;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedTransplantDays1107;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedTransplantDays1107;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
