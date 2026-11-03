-- ============================================================================
-- 2026-11-03_FieldWorkExpenses_Rollback.sql          *** PREPARED FOR REVIEW ***
--
-- Undoes 2026-11-03_FieldWorkExpenses.sql, ONLY while nothing depends on it:
--   * REFUSED when dbo.FieldExpenses / dbo.FieldExpenseEvents hold a row, or dbo.FieldPhotos holds an 'ExpenseBill' row
--     (expenses are money records -- they are never dropped by a script; photo FILES are untouched)
-- Then: drops the two triggers and two tables, and restores CK_FieldPhotos_Purpose and CK_FieldPhotos_ConsentReminder to
--   EXACTLY the 2026-11-03_FieldWorkCustomers.sql definitions (verified by SHA-256 before committing). Phases 1-3 are not
--   touched.
--
-- Same target safeguards and operator checklist as the migration (MODE=DRYRUN first; COMMIT only with the owner's
-- explicit approval for that database).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54421, '2026-11-03_FieldWorkExpenses_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54422, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54423, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54424, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54425, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54426, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54427, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
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

BEGIN TRANSACTION FieldWorkExpenses1103Rollback;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    DECLARE @Rows INT = 0;
    IF OBJECT_ID('dbo.FieldExpenseEvents', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldExpenseEvents WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldExpenses', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldExpenses WITH (TABLOCKX, HOLDLOCK));
    SET @Rows += (SELECT COUNT(*) FROM dbo.FieldPhotos WITH (TABLOCKX, HOLDLOCK) WHERE Purpose = 'ExpenseBill');
    IF @Rows > 0
        THROW 54428, 'Field Work expenses (or bill photos) exist -- refusing to drop them. Nothing changed.', 1;

    IF OBJECT_ID('dbo.TR_FieldExpenseEvents_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldExpenseEvents_Guard;
    IF OBJECT_ID('dbo.TR_FieldExpenses_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldExpenses_Guard;
    IF OBJECT_ID('dbo.FieldExpenseEvents', 'U') IS NOT NULL DROP TABLE dbo.FieldExpenseEvents;
    IF OBJECT_ID('dbo.FieldExpenses', 'U') IS NOT NULL DROP TABLE dbo.FieldExpenses;
    PRINT '=== Phase 4 triggers and tables dropped ===';

    IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder')) LIKE N'%ExpenseBill%'
    BEGIN
        ALTER TABLE dbo.FieldPhotos DROP CONSTRAINT CK_FieldPhotos_ConsentReminder;
        ALTER TABLE dbo.FieldPhotos WITH CHECK ADD CONSTRAINT CK_FieldPhotos_ConsentReminder
            CHECK (Purpose NOT IN ('CustomerPhoto', 'VisitPhoto') OR ConsentReminderVersion >= 1);
    END
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose')) LIKE N'%ExpenseBill%'
    BEGIN
        ALTER TABLE dbo.FieldPhotos DROP CONSTRAINT CK_FieldPhotos_Purpose;
        ALTER TABLE dbo.FieldPhotos WITH CHECK ADD CONSTRAINT CK_FieldPhotos_Purpose
            CHECK (Purpose IN ('DayStart', 'DayEnd', 'CustomerPhoto', 'VisitPhoto'));
    END
    IF ISNULL(CONVERT(CHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose'))), 2), '')
       <> '7F643662666D8A5DFF56A919054CC3621646811A546D20737DEB0A58674A4B55'
        THROW 54429, 'CK_FieldPhotos_Purpose could not be restored to the exact 2026-11-03_FieldWorkCustomers.sql version -- nothing changed.', 1;
    IF ISNULL(CONVERT(CHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder'))), 2), '')
       <> '04EC4934B24EF54CA562C5EEFB9F7D197216306841CF8F9D778BDF61060C826D'
        THROW 54430, 'CK_FieldPhotos_ConsentReminder could not be restored to the exact 2026-11-03_FieldWorkCustomers.sql version -- nothing changed.', 1;
    PRINT '=== dbo.FieldPhotos checks restored to the Phase 2 versions ===';

    SELECT name, type_desc FROM sys.objects
    WHERE name IN (N'FieldExpenses', N'FieldExpenseEvents', N'TR_FieldExpenses_Guard', N'TR_FieldExpenseEvents_Guard');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION FieldWorkExpenses1103Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION FieldWorkExpenses1103Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION FieldWorkExpenses1103Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
