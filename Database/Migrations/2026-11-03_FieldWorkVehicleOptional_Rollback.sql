-- ============================================================================
-- 2026-11-03_FieldWorkVehicleOptional_Rollback.sql          *** PREPARED FOR REVIEW ***
--
-- Undoes 2026-11-03_FieldWorkVehicleOptional.sql ONLY while every Field Day still has a vehicle type (it cannot invent one):
--   REFUSED when any dbo.FieldDays.VehicleType IS NULL. Then: VehicleType NOT NULL again (CHECK dropped / re-added unchanged)
--   and TR_FieldDays_Guard restored to EXACTLY the 2026-11-03_FieldWorkDays.sql text -- both verified by SHA-256 before
--   committing. No data row changes.
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
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54321, '2026-11-03_FieldWorkVehicleOptional_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54322, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54323, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54324, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54325, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54326, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54327, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
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

BEGIN TRANSACTION FieldWorkVehicleOptional1103Rb;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF EXISTS (SELECT 1 FROM dbo.FieldDays WITH (TABLOCKX, HOLDLOCK) WHERE VehicleType IS NULL)
        THROW 54328, 'Field Days without a vehicle type exist -- the column cannot be made NOT NULL again. Nothing changed.', 1;

    IF COLUMNPROPERTY(OBJECT_ID('dbo.FieldDays'), 'VehicleType', 'AllowsNull') = 1
    BEGIN
        ALTER TABLE dbo.FieldDays DROP CONSTRAINT CK_FieldDays_VehicleType;
        ALTER TABLE dbo.FieldDays ALTER COLUMN VehicleType VARCHAR(20) NOT NULL;
        ALTER TABLE dbo.FieldDays WITH CHECK ADD CONSTRAINT CK_FieldDays_VehicleType CHECK (VehicleType IN ('OwnBike', 'OwnCar', 'CompanyVehicle'));
        PRINT '=== dbo.FieldDays.VehicleType is NOT NULL again ===';
    END
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_FieldDays_Guard')) LIKE N'%2026-11-03[_]FieldWorkVehicleOptional.sql%'
    BEGIN
        DROP TRIGGER dbo.TR_FieldDays_Guard;
        EXEC (N'
CREATE TRIGGER dbo.TR_FieldDays_Guard ON dbo.FieldDays
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    -- 2026-11-03_FieldWorkDays.sql: the owner, date, vehicle and start reading of a day never change.
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.UserId <> d.UserId OR i.WorkDate <> d.WorkDate OR i.VehicleType <> d.VehicleType
                  OR i.StartedAtUtc <> d.StartedAtUtc OR i.StartKm <> d.StartKm
                  OR ISNULL(i.StartPhotoId, -1) <> ISNULL(d.StartPhotoId, -1)
                  OR ISNULL(i.StartLatitude, 999) <> ISNULL(d.StartLatitude, 999)
                  OR ISNULL(i.StartLongitude, 999) <> ISNULL(d.StartLongitude, 999)
                  OR i.StartSubmissionToken <> d.StartSubmissionToken)
        THROW 54151, ''The start reading of a field day cannot be changed.'', 1;

    -- A photo used by a day belongs to that day''s employee and was taken for that step.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.FieldPhotos p ON p.Id = i.StartPhotoId
               WHERE p.OwnerUserId <> i.UserId OR p.Purpose <> ''DayStart'')
       OR EXISTS (SELECT 1 FROM inserted i JOIN dbo.FieldPhotos p ON p.Id = i.EndPhotoId
               WHERE p.OwnerUserId <> i.UserId OR p.Purpose <> ''DayEnd'')
        THROW 54152, ''The photo does not belong to this field day.'', 1;
END');
        PRINT '=== TR_FieldDays_Guard restored to the Phase 1 text ===';
    END
    IF CONVERT(CHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_FieldDays_Guard'))), 2) <> '099CA29CA9924A7E721311BE369F6B2F9980200663C951A3CFD908869D5A6A44'
        THROW 54329, 'TR_FieldDays_Guard could not be restored to the exact Phase 1 version -- nothing changed.', 1;
    IF CONVERT(CHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldDays_VehicleType'))), 2) <> 'D16B3578A234B7D0D7B72CF9A25A6ADC576056034856305642FFEC68E8EBD771'
        THROW 54330, 'CK_FieldDays_VehicleType could not be restored to the exact Phase 1 version -- nothing changed.', 1;
    SELECT COLUMNPROPERTY(OBJECT_ID('dbo.FieldDays'), 'VehicleType', 'AllowsNull') AS VehicleTypeAllowsNull;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION FieldWorkVehicleOptional1103Rb;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION FieldWorkVehicleOptional1103Rb;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION FieldWorkVehicleOptional1103Rb;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
