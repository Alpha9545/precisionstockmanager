-- ============================================================================
-- 2026-10-01_CuttingSowingConfirmerAnyApprover.sql
--
-- Follow-up to 2026-10-01_CuttingSowingDestinationAtConfirmation.sql.
--
-- User decision (2026-10-01): a pending Cutting Tray Sowing may be confirmed
-- by ANY user who may approve sowings -- e.g. the Main Office Sowing
-- Supervisors (Maya, Akshay, Somnath, assigned only to Main Office) as well as
-- the Mother Plant Supervisors. The confirmer chooses the destination growing
-- Polyhouse (its Area becomes the sowing's AreaId) and the Sowing Supervisor
-- (who must be eligible for that Area -- unchanged, enforced by
-- TR_SeedSowings_SupervisorRole).
--
-- The previous version of the approval trigger required a cutting confirmer
-- who is not the selected supervisor to be ASSIGNED TO THE DESTINATION AREA,
-- which refuses every Main Office Sowing Supervisor. This script changes
-- exactly ONE trigger:
--
--   dbo.TR_ReadyConfirmations_AssignedSupervisor
--     SEED:    unchanged (approver = assigned supervisor, never the recorder).
--     CUTTING: approver = the assigned supervisor, OR an ACTIVE user who holds
--              ReadyStock.Confirm through any role, OR an active holder of the
--              full-access role "System Administrator" (who passes every
--              permission check in the application without RolePermissions rows).
--              No Area requirement for the confirmer any more.
--
-- NOT changed: any table, column, constraint, index, data row, permission, role,
-- or any other trigger. Existing approvals are not re-validated (INSERT trigger).
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Idempotent (CREATE OR ALTER). Deploy together with the matching app build.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 50342, '2026-10-01_CuttingSowingConfirmerAnyApprover.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 50343, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingSowingConfirmer;

BEGIN TRY

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED: only the assigned supervisor, never the recorder.
    -- CUTTING: the assigned supervisor, or any active user who may approve sowings
    -- (ReadyStock.Confirm through any role, or the full-access System Administrator role).
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE i.ApprovedById IS NULL
                  OR sw.SupervisorId IS NULL
                  OR (sw.SourceType <> N''Cutting''
                      AND (i.ApprovedById <> sw.SupervisorId
                           OR (sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById)))
                  OR (sw.SourceType = N''Cutting''
                      AND i.ApprovedById <> sw.SupervisorId
                      AND NOT EXISTS (SELECT 1
                                      FROM dbo.IMSUsers u
                                      INNER JOIN dbo.UserRoles ur ON ur.UserId = u.Id
                                      INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                      WHERE u.Id = i.ApprovedById
                                        AND ISNULL(u.IsActive, 0) = 1
                                        AND (COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''System Administrator''
                                             OR EXISTS (SELECT 1
                                                        FROM dbo.RolePermissions rp
                                                        INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                                        WHERE rp.RoleId = ur.RoleId
                                                          AND p.Code = N''ReadyStock.Confirm'')))))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, a user who may approve sowings) can approve it; a seed sowing never by the user who recorded it.'', 1;
END');

    PRINT '=== Approval trigger exists and is enabled (expect 1 row, is_disabled = 0) ===';
    SELECT name, is_disabled FROM sys.triggers WHERE name = N'TR_ReadyConfirmations_AssignedSupervisor';

    PRINT '=== Cutting confirmer no longer needs the destination Area (expect 0) ===';
    SELECT COUNT(*) AS AreaClausePresent FROM sys.triggers
    WHERE name = N'TR_ReadyConfirmations_AssignedSupervisor' AND OBJECT_DEFINITION(object_id) LIKE N'%ur.AreaId = sw.AreaId%';

    PRINT '=== Seed rule unchanged (expect 1) ===';
    SELECT COUNT(*) AS SeedRulePresent FROM sys.triggers
    WHERE name = N'TR_ReadyConfirmations_AssignedSupervisor'
      AND OBJECT_DEFINITION(object_id) LIKE N'%sw.SourceType <> N''Cutting''%i.ApprovedById <> sw.SupervisorId%i.ApprovedById = sw.CreatedById%';

    PRINT '=== Users who may now confirm a cutting sowing (FYI) ===';
    SELECT DISTINCT u.Id, u.Name
    FROM dbo.IMSUsers u
    INNER JOIN dbo.UserRoles ur ON ur.UserId = u.Id
    INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
    WHERE ISNULL(u.IsActive, 0) = 1
      AND (r.Name = N'System Administrator'
           OR EXISTS (SELECT 1 FROM dbo.RolePermissions rp INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                      WHERE rp.RoleId = ur.RoleId AND p.Code = N'ReadyStock.Confirm'))
    ORDER BY u.Id;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingSowingConfirmer;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION CuttingSowingConfirmer;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION CuttingSowingConfirmer;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
