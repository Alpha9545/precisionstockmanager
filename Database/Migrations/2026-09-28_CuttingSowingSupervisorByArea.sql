-- ============================================================================
-- 2026-09-28_CuttingSowingSupervisorByArea.sql
--
-- Cutting Tray Sowing: the Sowing Supervisor is chosen from the ACTIVE users
-- assigned to the sowing's Area who can approve sowings -- not only from users
-- whose role is named exactly "Sowing Supervisor" (the old rule, which also
-- ignored the Area entirely).
--
-- WHY A DATABASE CHANGE IS NEEDED: the insert trigger dbo.TR_SeedSowings_SupervisorRole
-- rejects any new sowing whose supervisor does not hold the role
-- "Sowing Supervisor", so an application-only change would be refused by the
-- database for every other role.
--
-- WHY A PERMISSION CHANGE IS NEEDED: the assigned supervisor is the only person
-- who can approve a sowing, and approving requires the permission
-- 'ReadyStock.Confirm' (page /Production/ReadyConfirmation/Confirm). Today only
-- the role "Sowing Supervisor" carries it, so an Area's Mother Plant Supervisor
-- could neither be offered nor approve. This script therefore grants the role
-- "Mother Plant Supervisor" exactly two permissions (nothing else, no other role):
--     ReadyStock.Confirm  -- lets them approve a sowing assigned to them
--     ReadyStock.View     -- lets them open the pending-approvals list, where the
--                            Approve button is (the Confirm page is linked from there)
-- The grant is by role name + permission code, idempotent (NOT EXISTS), the same
-- pattern PhaseD_ProductionRestructure.sql uses for other role grants. It applies
-- to the ROLE (every Mother Plant Supervisor) but changes nothing about Areas:
-- a Mother Plant Supervisor is only ever offered, and can only approve, sowings
-- of an Area they are assigned to, and only sowings assigned to them. Seed
-- sowing is unaffected (its supervisor list and trigger branch stay role-based
-- on "Sowing Supervisor"). Logged-in users pick the new permissions up at the
-- next claims revalidation (SecurityOptions.PrincipalRevalidationMinutes).
--
-- WHAT CHANGES:
--   1. dbo.RolePermissions: +2 rows (only when missing) for the role
--      'Mother Plant Supervisor'.
--   2. dbo.TR_SeedSowings_SupervisorRole is replaced by a two-branch trigger:
--   * SourceType <> 'Cutting' (direct SEED sowing): the ORIGINAL rule, verbatim
--       (active user holding the role 'Sowing Supervisor', not the recorder).
--   * SourceType =  'Cutting' (CUTTING TRAY sowing): the supervisor must be
--       - an ACTIVE user assigned to the sowing's Area (a UserRoles row with
--         AreaId = the sowing's AreaId), any role;
--       - holding the approval permission 'ReadyStock.Confirm' (from any of
--         their roles) -- the permission that gates the Supervisor Approval
--         page, so the assigned supervisor can actually approve the sowing;
--       - not the person recording the sowing (unchanged rule; the approval
--         trigger TR_ReadyConfirmations_AssignedSupervisor also forbids it).
--   The Area is the boundary: a user assigned only to another Area is rejected.
--
-- NOT changed: no table, column, constraint, index or other trigger; no other
-- role's permissions; no user, Area or UserRoles assignment.
-- TR_SeedSowings_ImmutableTrayData still fixes SupervisorId after the sowing
-- is saved, and TR_ReadyConfirmations_AssignedSupervisor is untouched.
-- Existing sowings are not re-validated (a trigger fires on new inserts only);
-- the verification below reports, for information, historical cutting sowings
-- whose supervisor is not assigned to the sowing's Area.
--
-- SAFETY: guarded to PlantsIMS2_Test only; one transaction; ends in ROLLBACK
-- (was a ROLLBACK dry run first; switched to COMMIT with explicit approval). Idempotent
-- (CREATE OR ALTER). Deploy together with the matching application build.
--
-- NOT executed against any database by this script's author.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50320, '2026-09-28_CuttingSowingSupervisorByArea.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingSowingSupervisor;

BEGIN TRY

    ----------------------------------------------------------------------
    -- 1. Permissions: the Mother Plant Supervisor role can approve sowings
    ----------------------------------------------------------------------
    IF (SELECT COUNT(*) FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Mother Plant Supervisor') <> 1
        THROW 50321, 'Expected exactly one role named Mother Plant Supervisor -- aborting rather than guess.', 1;
    IF (SELECT COUNT(*) FROM dbo.Permissions WHERE Code IN (N'ReadyStock.View', N'ReadyStock.Confirm')) <> 2
        THROW 50322, 'Permissions ReadyStock.View / ReadyStock.Confirm not found -- aborting.', 1;

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    CROSS JOIN dbo.Permissions p
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor'
      AND p.Code IN (N'ReadyStock.View', N'ReadyStock.Confirm')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    DECLARE @GrantsAdded INT = @@ROWCOUNT;

    ----------------------------------------------------------------------
    -- 2. Trigger: cutting tray sowing supervisor = active user assigned to the
    --    sowing's Area who can approve; seed sowing keeps the original rule
    ----------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_SupervisorRole
ON dbo.SeedSowings
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED sowing (unchanged): assigned to an active Sowing Supervisor who is
    -- not the person recording it (the assigned supervisor is the only approver).
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE i.SourceType <> N''Cutting''
                 AND (i.SupervisorId IS NULL
                  OR (i.CreatedById IS NOT NULL AND i.SupervisorId = i.CreatedById)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''Sowing Supervisor'')))
        THROW 50203, ''A sowing must be assigned to an active Sowing Supervisor other than the person recording it.'', 1;
    -- CUTTING TRAY sowing: the supervisor is an active user assigned to the
    -- sowing''s Area (any role) who holds the approval permission, and is not
    -- the person recording it.
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE i.SourceType = N''Cutting''
                 AND (i.SupervisorId IS NULL
                  OR (i.CreatedById IS NOT NULL AND i.SupervisorId = i.CreatedById)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur2
                                 INNER JOIN dbo.RolePermissions rp ON rp.RoleId = ur2.RoleId
                                 INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                 WHERE ur2.UserId = i.SupervisorId
                                   AND p.Code = N''ReadyStock.Confirm'')))
        THROW 50203, ''A cutting tray sowing must be assigned to an active user of the sowing''''s Area who can approve sowings, other than the person recording it.'', 1;
END');

    ----------------------------------------------------------------------
    -- VERIFICATION (before the ROLLBACK, so it shows what WOULD happen)
    ----------------------------------------------------------------------
    PRINT '=== Permission rows added by this run (expect 2 the first time, 0 on a re-run) ===';
    SELECT @GrantsAdded AS PermissionRowsAdded;

    PRINT '=== ReadyStock permissions now held by the Mother Plant Supervisor role (expect ReadyStock.Confirm + ReadyStock.View) ===';
    SELECT p.Code FROM dbo.RolePermissions rp
    INNER JOIN dbo.Roles r ON r.Id = rp.RoleId
    INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor' AND p.Code LIKE N'ReadyStock.%'
    ORDER BY p.Code;

    PRINT '=== Roles now holding ReadyStock.Confirm (expect exactly Sowing Supervisor + Mother Plant Supervisor) ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role
    FROM dbo.RolePermissions rp
    INNER JOIN dbo.Roles r ON r.Id = rp.RoleId
    INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code = N'ReadyStock.Confirm' ORDER BY 1;

    PRINT '=== Eligible Sowing Supervisor per Area after this change (active + assigned to the Area + holds ReadyStock.Confirm) ===';
    SELECT a.Id AS AreaId, a.Name AS Area, u.Id AS UserId, u.Name AS [User]
    FROM dbo.Area a
    INNER JOIN dbo.UserRoles ur ON ur.AreaId = a.Id
    INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND ISNULL(u.IsActive, 0) = 1
    WHERE EXISTS (SELECT 1 FROM dbo.UserRoles ur2
                  INNER JOIN dbo.RolePermissions rp ON rp.RoleId = ur2.RoleId
                  INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                  WHERE ur2.UserId = u.Id AND p.Code = N'ReadyStock.Confirm')
    GROUP BY a.Id, a.Name, u.Id, u.Name ORDER BY a.Id, u.Id;

    PRINT '=== Trigger exists and is enabled (expect 1 row, is_disabled = 0) ===';
    SELECT name, is_disabled FROM sys.triggers WHERE parent_id = OBJECT_ID('dbo.SeedSowings') AND name = N'TR_SeedSowings_SupervisorRole';

    PRINT '=== All SeedSowings triggers still present (expect 3) ===';
    SELECT COUNT(*) AS SeedSowingsTriggers FROM sys.triggers WHERE parent_id = OBJECT_ID('dbo.SeedSowings');

    PRINT '=== Seed branch keeps the Sowing Supervisor role rule (expect 1) ===';
    SELECT COUNT(*) AS SeedBranchKeepsRole FROM sys.triggers
    WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%SourceType <> N''Cutting''%''Sowing Supervisor''%';

    PRINT '=== Cutting branch enforces Area assignment (expect 1) ===';
    SELECT COUNT(*) AS CuttingBranchEnforcesArea FROM sys.triggers
    WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%ur.AreaId = i.AreaId%';

    PRINT '=== Approval trigger untouched (expect 1) ===';
    SELECT COUNT(*) AS ApprovalTriggerPresent FROM sys.triggers WHERE name = N'TR_ReadyConfirmations_AssignedSupervisor';

    PRINT '=== FYI: existing cutting sowings whose supervisor is not assigned to the sowing Area (historical, NOT changed) ===';
    SELECT s.Id, s.SowingCode, s.AreaId AS SowingArea, s.SupervisorId
    FROM dbo.SeedSowings s
    WHERE s.SourceType = N'Cutting'
      AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = s.SupervisorId AND ur.AreaId = s.AreaId);

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingSowingSupervisor;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-28: user reviewed the dry run (35/35 real-user scenarios,
-- before/after snapshots differ only by the +2 RolePermissions rows and this
-- one trigger) and explicitly approved applying it. Verified backup:
-- PlantsIMS2_Test_PreMpsSowingApprovalCommit_20260928_134915.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION CuttingSowingSupervisor;
GO
