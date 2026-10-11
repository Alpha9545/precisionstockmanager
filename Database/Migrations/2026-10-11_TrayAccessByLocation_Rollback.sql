-- ============================================================================
-- 2026-10-11_TrayAccessByLocation_Rollback.sql -- undoes 2026-10-11_TrayAccessByLocation.sql.
-- REFUSES while a PENDING tray transfer targets a Main Office Area: removing
-- TrayStock.ReceiveTransferMainArea would leave it without a non-admin receiver. Removes only the
-- grants that script logged as Added, then the two permissions when no grant uses them; logs every
-- removal in dbo.RolePermissionChangeLog. Stock and ledger rows are never touched.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52151, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52152, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52153, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION TrayAccessUndo1011;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.TrayTransfers', 'U') IS NOT NULL
    BEGIN
        DECLARE @n INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.TrayTransfers t JOIN dbo.Area a ON a.Id = t.DestinationAreaId
            WHERE t.Status = N''Pending'' AND a.AreaType = N''MainOffice''', N'@n INT OUTPUT', @n = @n OUTPUT;
        IF @n > 0 THROW 52154, 'Pending tray transfers to Main Office Areas exist -- refusing to roll back.', 1;
    END

    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NOT NULL
    BEGIN
        DECLARE @Script NVARCHAR(200) = N'2026-10-11_TrayAccessByLocation.sql';
        DECLARE @Undo NVARCHAR(200) = N'2026-10-11_TrayAccessByLocation_Rollback.sql';
        DECLARE @Net TABLE (RoleId INT, RoleName NVARCHAR(100), Code NVARCHAR(200));
        INSERT INTO @Net
        SELECT l.RoleId, l.RoleName, l.PermissionCode
        FROM dbo.RolePermissionChangeLog l
        WHERE l.Script = @Script AND l.Change = N'Added'
          AND l.Id = (SELECT MAX(x.Id) FROM dbo.RolePermissionChangeLog x
                      WHERE x.Script IN (@Script, @Undo) AND x.RoleId = l.RoleId AND x.PermissionCode = l.PermissionCode);

        INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
        SELECT n.RoleId, n.RoleName, n.Code, N'Removed', @Undo, N'Rollback of ' + @Script
        FROM @Net n JOIN dbo.Permissions p ON p.Code = n.Code
        WHERE EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = n.RoleId AND rp.PermissionId = p.Id);
        DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
        JOIN @Net n ON n.RoleId = rp.RoleId AND n.Code = p.Code;
        PRINT CONCAT('Grants removed: ', @@ROWCOUNT);
    END

    DELETE p FROM dbo.Permissions p
    WHERE p.Code IN (N'TrayStock.ViewMotherPlant', N'TrayStock.ReceiveTransferMainArea')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.PermissionId = p.Id);
    PRINT '=== Rolled back to the 2026-10-10 state ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayAccessUndo1011;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayAccessUndo1011;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayAccessUndo1011;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
