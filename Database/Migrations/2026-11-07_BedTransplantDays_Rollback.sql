-- ============================================================================
-- 2026-11-07_BedTransplantDays_Rollback.sql          *** PREPARED FOR REVIEW ***
--
-- Undoes 2026-11-07_BedTransplantDays.sql: drops CK_PlantSpecies_BedTransplantDays and dbo.PlantSpecies.BedTransplantDays.
--   REFUSED (error 54529) while any variety still has Bed Transplant Days (dropping the column would silently lose the owner's schedule).
--   To roll back after the schedule was loaded, first clear it on purpose with a separate, owner-approved
--     UPDATE dbo.PlantSpecies SET BedTransplantDays = NULL (after a backup; the data-entry script never clears values).
--   Rehearsed 2026-11-07 on PlantsIMS2_Scratch_BedAlertsRb_20261107: nothing-to-undo, apply, DRYRUN, COMMIT, re-apply.
--   No other object or data row changes. Nothing else depends on the column in the database (no trigger, view or index).
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
-- The application keeps working after the rollback (Data/BedTransplantScheduleSchema.cs re-probes within 2 minutes; a
-- positive probe is cached for the process lifetime, so restart the application after a rollback).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54521, '2026-11-07_BedTransplantDays_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54522, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54523, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54524, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54525, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54526, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54527, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
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

BEGIN TRANSACTION BedTransplantDaysRollback1107;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    IF COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') IS NULL
    BEGIN
        IF OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') IS NOT NULL
            THROW 54528, 'CK_PlantSpecies_BedTransplantDays exists without its column -- schema drift, nothing changed.', 1;
        PRINT '=== dbo.PlantSpecies.BedTransplantDays not present -- nothing to undo ===';
    END
    ELSE
    BEGIN
        DECLARE @WithDays INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.PlantSpecies WITH (UPDLOCK, HOLDLOCK) WHERE BedTransplantDays IS NOT NULL',
            N'@n INT OUTPUT', @n = @WithDays OUTPUT;
        PRINT '=== Varieties with Bed Transplant Days: ' + CAST(@WithDays AS VARCHAR(10)) + ' ===';
        IF @WithDays > 0
            THROW 54529, 'Refused: varieties still have Bed Transplant Days -- dropping the column would lose the owner''s schedule. Clear it on purpose first (see header).', 1;

        IF OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') IS NOT NULL
            ALTER TABLE dbo.PlantSpecies DROP CONSTRAINT CK_PlantSpecies_BedTransplantDays;
        ALTER TABLE dbo.PlantSpecies DROP COLUMN BedTransplantDays;
        PRINT '=== CK_PlantSpecies_BedTransplantDays and dbo.PlantSpecies.BedTransplantDays dropped ===';
    END

    SELECT COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') AS ColumnBytesNowNull,
           OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') AS CheckIdNowNull,
           (SELECT COUNT(*) FROM dbo.PlantSpecies) AS Varieties;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedTransplantDaysRollback1107;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedTransplantDaysRollback1107;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedTransplantDaysRollback1107;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
