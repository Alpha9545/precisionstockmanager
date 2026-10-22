-- ============================================================================
-- 2026-10-16_MotherPlantSupervisorTrayEnter.sql
--
-- WHY (owner approval 2026-10-12): the Mother Plant Supervisor adds empty trays to his OWN Mother Plant Area(s)
--   (Production > Mother Plant / Cutting > Add Empty Trays). Until now only TrayStock.EnterMotherPlant (Office
--   Coordinator, Booking Executive), TrayStock.Enter holders of a Mother Plant Area and administrators could.
--
-- WHAT: grants the EXISTING permission TrayStock.Enter to the role "Mother Plant Supervisor" (one dbo.RolePermissions
--   row), logged in dbo.RolePermissionChangeLog. No code rule changes: TrayStock.Enter already means "trays for an
--   active, non-Outlet Area the user is ASSIGNED to" (Authorization/TrayAreaScope.cs), checked on every page, on every
--   POST and again inside TrayStockRepository.AddStockAsync's transaction.
--
-- AREA SCOPE GUARD: refuses (nothing changed) when any user holding the Mother Plant Supervisor role also has a
--   role assignment to an Area that is NOT an active Mother Plant Area, or to no Area -- for such a user the grant
--   would reach beyond his own Mother Plant Areas. Re-check before re-running after role changes.
--
-- NOT changed: no other role, permission, user, Area or stock row.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-16_MotherPlantSupervisorTrayEnter_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52481, '2026-10-16_MotherPlantSupervisorTrayEnter.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52482, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52483, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MpsTrayEnter1016;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 52484, 'dbo.RolePermissionChangeLog is missing -- run 2026-10-11_TrayAccessByLocation.sql first.', 1;
    IF (SELECT COUNT(*) FROM dbo.Roles WHERE Name = N'Mother Plant Supervisor') <> 1
        THROW 52485, 'Exactly one role named Mother Plant Supervisor is expected -- nothing changed.', 1;
    IF (SELECT COUNT(*) FROM dbo.Permissions WHERE Code = N'TrayStock.Enter') <> 1
        THROW 52486, 'Exactly one permission TrayStock.Enter is expected -- nothing changed.', 1;

    DECLARE @Role INT = (SELECT Id FROM dbo.Roles WHERE Name = N'Mother Plant Supervisor');
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = N'TrayStock.Enter');

    PRINT '=== Holders of the role and every Area they are assigned to (any role) ===';
    SELECT u.Id, u.Name, r.Name AS Role, a.Name AS Area, a.AreaType, a.IsActive
    FROM dbo.UserRoles ur JOIN dbo.IMSUsers u ON u.Id = ur.UserId JOIN dbo.Roles r ON r.Id = ur.RoleId LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
    WHERE ur.UserId IN (SELECT UserId FROM dbo.UserRoles WHERE RoleId = @Role)
    ORDER BY u.Id, r.Name;

    IF EXISTS (SELECT 1 FROM dbo.UserRoles ur LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
               WHERE ur.UserId IN (SELECT UserId FROM dbo.UserRoles WHERE RoleId = @Role)
                 AND (a.Id IS NULL OR a.AreaType <> N'MotherPlant' OR a.IsActive = 0))
        THROW 52487, 'A Mother Plant Supervisor is also assigned to an Area that is not an active Mother Plant Area (or to no Area) -- the grant would reach beyond his own Mother Plant Areas. Nothing changed.', 1;

    IF EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm)
        PRINT '=== Already granted -- no-op ===';
    ELSE
    BEGIN
        INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
        VALUES (@Role, N'Mother Plant Supervisor', N'TrayStock.Enter', N'Added', N'2026-10-16_MotherPlantSupervisorTrayEnter.sql',
                N'Mother Plant Supervisor adds empty trays to his own Mother Plant Area(s) (owner approval 2026-10-12)');
        INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Perm);
        PRINT '=== TrayStock.Enter granted to Mother Plant Supervisor ===';
    END

    SELECT r.Name AS Role, p.Code FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE rp.RoleId = @Role ORDER BY p.Code;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpsTrayEnter1016;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpsTrayEnter1016;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpsTrayEnter1016;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
