-- ============================================================================
-- 2026-09-28_CuttingSowingRecorderAsSupervisor.sql
--
-- Cutting Tray Sowing: the person who RECORDS the sowing may choose themselves
-- as its Sowing Supervisor -- and, as that assigned supervisor, approve it --
-- provided they still meet the normal eligibility rules (active + assigned to
-- the sowing's Area + holds 'ReadyStock.Confirm').
--
-- Direct SEED sowing is NOT changed: its recorder still cannot be its supervisor
-- and still cannot approve it.
--
-- The recorder restriction was enforced in TWO database places (found by
-- scanning every trigger/procedure/function/view and every CHECK constraint):
--
--   1. dbo.TR_SeedSowings_SupervisorRole  (INSERT: who may be assigned)
--        CUTTING branch: the clause "supervisor = recorder is refused" is REMOVED.
--        Still required for a cutting sowing: supervisor present, ACTIVE, assigned
--        to the sowing's Area (UserRoles.AreaId = AreaId), holds ReadyStock.Confirm.
--        SEED branch: byte-for-byte the original rule (recorder still refused).
--
--   2. dbo.TR_ReadyConfirmations_AssignedSupervisor  (INSERT: who may approve)
--        Was: approver must be the assigned supervisor AND not the sowing's recorder.
--        Now: approver must ALWAYS be the assigned supervisor; the "not the recorder"
--        clause applies only to sowings whose SourceType is not 'Cutting' (i.e. seed).
--        Without this, a recorder-supervisor could be assigned but could never approve.
--
-- (The application makes the matching changes: SowingSupervisorRules,
-- DirectSowingRules.CanApprove(sourceType), ReadyConfirmationRepository.ConfirmAsync,
-- the Confirm / Index pages. Seed paths are untouched.)
--
-- NOT changed: any table, column, constraint, index, data row, permission, role,
-- Area.SupervisorId, or any other trigger. Existing saved sowings and approvals are
-- not touched or re-validated (triggers fire on new inserts only).
--
-- SAFETY: guarded to PlantsIMS2_Test only; one transaction (was a ROLLBACK dry run
-- first; switched to COMMIT with explicit approval). Idempotent (CREATE OR ALTER).
-- Deploy together with the matching application build.
--
-- NOT executed against any database by this script's author.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50330, '2026-09-28_CuttingSowingRecorderAsSupervisor.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingSowingRecorderSupervisor;

BEGIN TRY

    ----------------------------------------------------------------------
    -- 1. Who may be ASSIGNED: cutting branch no longer refuses the recorder
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
    -- sowing''s Area (any role) who holds the approval permission. The person
    -- recording the sowing may be that supervisor.
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE i.SourceType = N''Cutting''
                 AND (i.SupervisorId IS NULL
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
        THROW 50203, ''A cutting tray sowing must be assigned to an active user of the sowing''''s Area who can approve sowings.'', 1;
END');

    ----------------------------------------------------------------------
    -- 2. Who may APPROVE: only the assigned supervisor; the recorder is refused
    --    only on SEED sowings
    ----------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE i.ApprovedById IS NULL
                  OR sw.SupervisorId IS NULL
                  OR i.ApprovedById <> sw.SupervisorId
                  OR (sw.SourceType <> N''Cutting'' AND sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById))
        THROW 50111, ''Only the supervisor assigned to the sowing can approve it (and, for a seed sowing, not the user who recorded it).'', 1;
END');

    ----------------------------------------------------------------------
    -- VERIFICATION (before the ROLLBACK, so it shows what WOULD happen)
    ----------------------------------------------------------------------
    PRINT '=== Both triggers exist and are enabled (expect 2 rows, is_disabled = 0) ===';
    SELECT name, is_disabled FROM sys.triggers
    WHERE name IN (N'TR_SeedSowings_SupervisorRole', N'TR_ReadyConfirmations_AssignedSupervisor') ORDER BY name;

    PRINT '=== "supervisor = recorder" clause remains ONLY in the seed branch (expect 1 occurrence) ===';
    SELECT (LEN(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole')))
          - LEN(REPLACE(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole')), N'i.SupervisorId = i.CreatedById', N'')))
          / LEN(N'i.SupervisorId = i.CreatedById') AS RecorderClauseOccurrences;

    PRINT '=== Cutting branch still requires Area assignment + ReadyStock.Confirm + active (expect 1 / 1) ===';
    SELECT (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%ur.AreaId = i.AreaId%') AS AreaRequired,
           (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%p.Code = N''ReadyStock.Confirm''%') AS PermissionRequired;

    PRINT '=== Seed branch still requires the Sowing Supervisor role (expect 1) ===';
    SELECT COUNT(*) AS SeedBranchKeepsRole FROM sys.triggers
    WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%SourceType <> N''Cutting''%''Sowing Supervisor''%';

    PRINT '=== Approval trigger: recorder refused only for non-cutting sowings (expect 1) ===';
    SELECT COUNT(*) AS ApprovalRecorderRuleSeedOnly FROM sys.triggers
    WHERE name = N'TR_ReadyConfirmations_AssignedSupervisor' AND OBJECT_DEFINITION(object_id) LIKE N'%sw.SourceType <> N''Cutting'' AND sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById%';

    PRINT '=== Other SeedSowings / ReadyConfirmations triggers untouched (expect 4 of 4 present) ===';
    SELECT COUNT(*) AS OtherTriggersPresent FROM sys.triggers
    WHERE name IN (N'TR_SeedSowings_ImmutableTrayData', N'TR_SeedSowings_RequireSeedQuantity', N'TR_ReadyConfirmations_TrayQuantity', N'TR_Area_PolyhouseIdRetired');

    PRINT '=== FYI: existing cutting sowings recorded by their own supervisor (expect 0; nothing is changed either way) ===';
    SELECT COUNT(*) AS ExistingSelfSupervisedCutting FROM dbo.SeedSowings WHERE SourceType = N'Cutting' AND SupervisorId = CreatedById;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingSowingRecorderSupervisor;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-28: user reviewed the dry run (30/30 real-user scenarios;
-- before/after snapshots differ only by these two triggers) and explicitly
-- approved applying it, including self-approval for cutting sowings. Verified backup:
-- PlantsIMS2_Test_PreRecorderAsSupervisorCommit_20260928_140647.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION CuttingSowingRecorderSupervisor;
GO
