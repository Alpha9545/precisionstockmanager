-- ============================================================================
-- 2026-10-16_MotherPlantSupervisorTrayEnter_Rollback.sql
--
-- Undoes 2026-10-16_MotherPlantSupervisorTrayEnter.sql: removes TrayStock.Enter from the Mother Plant Supervisor role,
-- logged in dbo.RolePermissionChangeLog -- only when that script added it (its 'Added' log row exists). Trays already
-- added stay (ledger rows are never removed). No other grant changes.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52491, '2026-10-16_MotherPlantSupervisorTrayEnter_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52492, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52493, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MpsTrayEnter1016Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    DECLARE @Role INT = (SELECT Id FROM dbo.Roles WHERE Name = N'Mother Plant Supervisor');
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = N'TrayStock.Enter');
    IF NOT EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm)
        PRINT '=== Not granted -- no-op ===';
    ELSE IF NOT EXISTS (SELECT 1 FROM dbo.RolePermissionChangeLog WHERE RoleId = @Role AND PermissionCode = N'TrayStock.Enter'
                        AND Change = N'Added' AND Script = N'2026-10-16_MotherPlantSupervisorTrayEnter.sql')
        THROW 52494, 'The grant was not added by 2026-10-16_MotherPlantSupervisorTrayEnter.sql -- nothing changed.', 1;
    ELSE
    BEGIN
        INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
        VALUES (@Role, N'Mother Plant Supervisor', N'TrayStock.Enter', N'Removed', N'2026-10-16_MotherPlantSupervisorTrayEnter_Rollback.sql',
                N'Rollback of 2026-10-16_MotherPlantSupervisorTrayEnter.sql');
        DELETE FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm;
        PRINT '=== TrayStock.Enter removed from Mother Plant Supervisor ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpsTrayEnter1016Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpsTrayEnter1016Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpsTrayEnter1016Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
