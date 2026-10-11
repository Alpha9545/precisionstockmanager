-- ============================================================================
-- 2026-10-11_RolePermissionAlignment.sql
--
-- WHY (owner decision 2026-10-10): the Dispatch Executive role enters dispatch transactions.
--   Booking Executive: Dispatch.Enter is REMOVED. Dispatch.View is REMOVED too, because the booking
--   workflow does not need it: every booking page already shows dispatch status with Booking.View
--   (Booking Details: ordered / reserved / dispatched / remaining per line; Booking Fulfilment;
--   Dispatch Register; Potted Plant Booking pages). Dispatch.View only added the potted-plant Dispatch
--   list / details and a read-only view of Seedling Dispatch, whose every action needs Dispatch.Enter.
--   Both grants were made through the Roles admin page between 2026-10-01 and 2026-10-04 -- no script
--   ever granted them (PhaseA_RoleBasedAccess.sql seeds Dashboard.View, Booking.View, Booking.Enter,
--   ReadyStock.View; 2026-10-09 adds TrayStock.EnterMotherPlant).
-- This script CHANGES EXISTING PERMISSION DATA: run it on PlantsIMS2_Test (or production) only with
-- the owner's explicit approval. Every grant it removes is first written to dbo.RolePermissionChangeLog
-- (created here if missing) -- nothing disappears silently, and the rollback restores exactly it.
-- No other role, grant, stock row or ledger row changes.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op).
-- Undo: 2026-10-11_RolePermissionAlignment_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52101, '2026-10-11_RolePermissionAlignment.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52102, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52103, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION RolePermAlign1011;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF NOT EXISTS (SELECT 1 FROM dbo.Roles x WHERE COALESCE(NULLIF(LTRIM(RTRIM(x.Name)), N''), x.RoleName) = N'Booking Executive')
        THROW 52106, 'Role Booking Executive is missing.', 1;
    DECLARE @BookingExecutive INT = (SELECT TOP 1 Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Booking Executive');

    PRINT '=== Before: Booking Executive ===';
    SELECT p.Code FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE rp.RoleId = @BookingExecutive ORDER BY p.Code;

    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.RolePermissionChangeLog (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RolePermissionChangeLog PRIMARY KEY,
            ChangedAt   DATETIME2     NOT NULL CONSTRAINT DF_RolePermissionChangeLog_ChangedAt DEFAULT SYSUTCDATETIME(),
            RoleId      INT           NOT NULL,
            RoleName    NVARCHAR(100) NOT NULL,
            PermissionCode NVARCHAR(200) NOT NULL,
            Change      NVARCHAR(10)  NOT NULL CONSTRAINT CK_RolePermissionChangeLog_Change CHECK (Change IN (N''Added'', N''Removed'')),
            Script      NVARCHAR(200) NOT NULL,
            Reason      NVARCHAR(500) NOT NULL);');
        PRINT '=== dbo.RolePermissionChangeLog created ===';
    END

    DECLARE @Changes TABLE (RoleId INT, RoleName NVARCHAR(100), Code NVARCHAR(200), Reason NVARCHAR(500));
    INSERT INTO @Changes VALUES
        (@BookingExecutive, N'Booking Executive', N'Dispatch.Enter', N'Dispatch entry belongs to the Dispatch Executive role (owner decision 2026-10-10)'),
        (@BookingExecutive, N'Booking Executive', N'Dispatch.View',  N'Not needed by the booking workflow: booking pages show dispatch status with Booking.View (owner decision 2026-10-10)');

    INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
    SELECT c.RoleId, c.RoleName, c.Code, N'Removed', N'2026-10-11_RolePermissionAlignment.sql', c.Reason
    FROM @Changes c JOIN dbo.Permissions p ON p.Code = c.Code
    WHERE EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = c.RoleId AND rp.PermissionId = p.Id);
    DELETE rp
    FROM dbo.RolePermissions rp
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    JOIN @Changes c ON c.RoleId = rp.RoleId AND c.Code = p.Code;
    PRINT CONCAT('Grants removed: ', @@ROWCOUNT, ' (Booking Executive Dispatch.Enter / Dispatch.View where granted; 0 on re-run)');

    PRINT '=== After: Booking Executive ===';
    SELECT p.Code FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE rp.RoleId = @BookingExecutive ORDER BY p.Code;
    SELECT RoleName, PermissionCode, Change, ChangedAt FROM dbo.RolePermissionChangeLog
    WHERE Script = N'2026-10-11_RolePermissionAlignment.sql' ORDER BY Id;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RolePermAlign1011;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RolePermAlign1011;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RolePermAlign1011;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
