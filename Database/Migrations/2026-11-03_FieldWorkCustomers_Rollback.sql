-- ============================================================================
-- 2026-11-03_FieldWorkCustomers_Rollback.sql          *** PREPARED FOR REVIEW ***
--
-- Undoes 2026-11-03_FieldWorkCustomers.sql, ONLY while nothing depends on it:
--   * REFUSED when any Phase 2 table holds a row, or dbo.FieldPhotos holds a 'CustomerPhoto' / 'VisitPhoto' row
--     (customer and visit records are work records -- archive / delete them deliberately first; photo FILES are untouched)
-- Then: drops the six triggers and seven tables, drops CK_FieldPhotos_ConsentReminder, restores CK_FieldPhotos_Purpose to
--   EXACTLY the 2026-11-03_FieldWorkDays.sql definition (verified by SHA-256 before committing) and drops the column
--   dbo.FieldPhotos.ConsentReminderVersion. Phase 1 (dbo.FieldDays etc.) is not touched.
--
-- Same target safeguards and operator checklist as the migration (MODE=DRYRUN first; COMMIT only with the owner's
-- explicit approval for that database).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54221, '2026-11-03_FieldWorkCustomers_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54222, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54223, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54224, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54225, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54226, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54227, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
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

BEGIN TRANSACTION FieldWorkCustomers1103Rollback;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    DECLARE @Rows INT = 0;
    IF OBJECT_ID('dbo.FieldCustomerEvents', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldCustomerEvents WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldFollowUps', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldFollowUps WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldCustomerNotes', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldCustomerNotes WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldVisitPhotos', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldVisitPhotos WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldVisitItems', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldVisitItems WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldVisits', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldVisits WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldCustomers', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldCustomers WITH (TABLOCKX, HOLDLOCK));
    SET @Rows += (SELECT COUNT(*) FROM dbo.FieldPhotos WITH (TABLOCKX, HOLDLOCK) WHERE Purpose IN ('CustomerPhoto', 'VisitPhoto'));
    IF @Rows > 0
        THROW 54228, 'Field Work customer / visit / follow-up data exists (or customer / visit photos) -- refusing to drop it. Nothing changed.', 1;

    IF OBJECT_ID('dbo.TR_FieldCustomerEvents_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldCustomerEvents_Guard;
    IF OBJECT_ID('dbo.TR_FieldFollowUps_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldFollowUps_Guard;
    IF OBJECT_ID('dbo.TR_FieldCustomerNotes_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldCustomerNotes_Guard;
    IF OBJECT_ID('dbo.TR_FieldCustomers_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldCustomers_Guard;
    IF OBJECT_ID('dbo.TR_FieldVisitPhotos_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldVisitPhotos_Guard;
    IF OBJECT_ID('dbo.TR_FieldVisits_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldVisits_Guard;
    IF OBJECT_ID('dbo.FieldCustomerEvents', 'U') IS NOT NULL DROP TABLE dbo.FieldCustomerEvents;
    IF OBJECT_ID('dbo.FieldFollowUps', 'U') IS NOT NULL DROP TABLE dbo.FieldFollowUps;
    IF OBJECT_ID('dbo.FieldCustomerNotes', 'U') IS NOT NULL DROP TABLE dbo.FieldCustomerNotes;
    IF OBJECT_ID('dbo.FieldVisitPhotos', 'U') IS NOT NULL DROP TABLE dbo.FieldVisitPhotos;
    IF OBJECT_ID('dbo.FieldVisitItems', 'U') IS NOT NULL DROP TABLE dbo.FieldVisitItems;
    IF OBJECT_ID('dbo.FieldVisits', 'U') IS NOT NULL DROP TABLE dbo.FieldVisits;
    IF OBJECT_ID('dbo.FieldCustomers', 'U') IS NOT NULL DROP TABLE dbo.FieldCustomers;
    PRINT '=== Phase 2 triggers and tables dropped ===';

    IF OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder', 'C') IS NOT NULL
        ALTER TABLE dbo.FieldPhotos DROP CONSTRAINT CK_FieldPhotos_ConsentReminder;
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose')) LIKE N'%CustomerPhoto%'
    BEGIN
        ALTER TABLE dbo.FieldPhotos DROP CONSTRAINT CK_FieldPhotos_Purpose;
        ALTER TABLE dbo.FieldPhotos WITH CHECK ADD CONSTRAINT CK_FieldPhotos_Purpose CHECK (Purpose IN ('DayStart', 'DayEnd'));
    END
    IF CONVERT(CHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose'))), 2)
       <> '851B7E5A5E96ACC963F2B5C6E403BFA2D04A5B1509C6D4526DC899AD59C41ECE'
        THROW 54229, 'CK_FieldPhotos_Purpose could not be restored to the exact 2026-11-03_FieldWorkDays.sql version -- nothing changed.', 1;
    IF COL_LENGTH('dbo.FieldPhotos', 'ConsentReminderVersion') IS NOT NULL
        ALTER TABLE dbo.FieldPhotos DROP COLUMN ConsentReminderVersion;
    PRINT '=== dbo.FieldPhotos restored to the Phase 1 shape ===';

    SELECT name, type_desc FROM sys.objects
    WHERE name IN (N'FieldCustomers', N'FieldVisits', N'FieldVisitItems', N'FieldVisitPhotos', N'FieldCustomerNotes', N'FieldFollowUps',
                   N'FieldCustomerEvents', N'CK_FieldPhotos_ConsentReminder');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION FieldWorkCustomers1103Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION FieldWorkCustomers1103Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION FieldWorkCustomers1103Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
