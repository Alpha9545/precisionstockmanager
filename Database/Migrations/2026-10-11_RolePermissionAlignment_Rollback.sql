-- ============================================================================
-- 2026-10-11_RolePermissionAlignment_Rollback.sql -- undoes 2026-10-11_RolePermissionAlignment.sql
-- using ONLY what that script logged in dbo.RolePermissionChangeLog:
--   grants it REMOVED are restored (Booking Executive Dispatch.Enter / Dispatch.View);
--   grants it ADDED (none in the current version) are removed.
-- The log rows of that script are kept and a matching 'rollback' row is written for every
-- change undone (nothing disappears silently either way).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52111, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52112, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52113, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION RolePermAlignUndo1011;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
    BEGIN
        PRINT '=== Nothing to undo (no change log) ===';
    END
    ELSE
    BEGIN
        DECLARE @Script NVARCHAR(200) = N'2026-10-11_RolePermissionAlignment.sql';
        DECLARE @Undo NVARCHAR(200) = N'2026-10-11_RolePermissionAlignment_Rollback.sql';

        -- the forward script's changes not yet undone: per (role, permission), only when the LAST
        -- logged change came from the forward script (a second rollback run is a no-op)
        DECLARE @Net TABLE (RoleId INT, RoleName NVARCHAR(100), Code NVARCHAR(200), Change NVARCHAR(10));
        INSERT INTO @Net
        SELECT l.RoleId, l.RoleName, l.PermissionCode, l.Change
        FROM dbo.RolePermissionChangeLog l
        WHERE l.Script = @Script
          AND l.Id = (SELECT MAX(x.Id) FROM dbo.RolePermissionChangeLog x
                      WHERE x.Script IN (@Script, @Undo) AND x.RoleId = l.RoleId AND x.PermissionCode = l.PermissionCode);

        -- restore what the script removed
        INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
        SELECT n.RoleId, n.RoleName, n.Code, N'Added', @Undo, N'Rollback of ' + @Script
        FROM @Net n JOIN dbo.Permissions p ON p.Code = n.Code
        WHERE n.Change = N'Removed' AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = n.RoleId AND rp.PermissionId = p.Id);
        INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
        SELECT n.RoleId, p.Id FROM @Net n JOIN dbo.Permissions p ON p.Code = n.Code
        WHERE n.Change = N'Removed' AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = n.RoleId AND rp.PermissionId = p.Id);
        PRINT CONCAT('Grants restored: ', @@ROWCOUNT);

        -- remove what the script added
        INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
        SELECT n.RoleId, n.RoleName, n.Code, N'Removed', @Undo, N'Rollback of ' + @Script
        FROM @Net n JOIN dbo.Permissions p ON p.Code = n.Code
        WHERE n.Change = N'Added' AND EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = n.RoleId AND rp.PermissionId = p.Id);
        DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
        JOIN @Net n ON n.RoleId = rp.RoleId AND n.Code = p.Code AND n.Change = N'Added';
        PRINT CONCAT('Grants removed: ', @@ROWCOUNT);

    END
    PRINT '=== Rolled back to the 2026-10-10 state ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RolePermAlignUndo1011;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RolePermAlignUndo1011;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RolePermAlignUndo1011;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
