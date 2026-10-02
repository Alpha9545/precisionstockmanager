-- ============================================================================
-- 2026-10-02_CuttingSowingMainOfficePolyhousesAllowed.sql
--
-- WHY THIS IS NEEDED: dbo.Area has exactly one row of AreaType='MainOffice'
-- (Id=2, "Main-Office-Areas"), and it has 5 real, individually-named
-- Polyhouses filed under it (Facility-3/4/5, "Facilty- 7 Strawberry
-- Polyhouse", "Facility 1 Main office") -- genuine physical growing sites,
-- not a stand-in for the Area's own Cutting Stock depot concept. The
-- previous trigger (2026-10-01_CuttingSowingDestinationAtConfirmation.sql)
-- blanket-excluded every Polyhouse whose Area is AreaType='MainOffice',
-- which wrongly excluded all 5 of those real facilities along with the
-- Area-level depot concept it was actually meant to exclude. The
-- application layer (PolyhouseRepository.GetGrowingDestinationsAsync,
-- CuttingSowingDestinationRules.ValidateDestination) has already been
-- corrected the same way in this change; this migration brings the
-- database trigger -- the final authority InsertFromCuttingAsync's INSERT
-- goes through -- in line with it, so a user is not rejected at the DB
-- layer for a destination the application now explicitly allows.
--
-- WHAT THIS DOES: changes exactly ONE line of ONE trigger's logic --
-- nothing else.
--   dbo.TR_SeedSowings_SupervisorRole (3rd IF block, the destination check)
--     BEFORE: a.AreaType NOT IN (N'MainOffice', N'Outlet')
--     AFTER:  a.AreaType <> N'Outlet'
--   i.e. Outlet, inactive Areas and unassigned Polyhouses remain refused
--   exactly as before; only the MainOffice exclusion is removed. The other
--   two IF blocks (seed-sowing supervisor rule, cutting-sowing supervisor
--   eligibility) are reproduced byte-for-byte unchanged, since CREATE OR
--   ALTER TRIGGER replaces the whole trigger body.
--
-- NOT changed: any table, column, constraint, index, data row, permission,
-- role, or any other trigger (TR_SeedSowings_ImmutableTrayData,
-- TR_ReadyConfirmations_AssignedSupervisor are untouched).
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Idempotent (CREATE OR ALTER). Deploy together with the matching app build.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51501, '2026-10-02_CuttingSowingMainOfficePolyhousesAllowed.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51502, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingSowingMainOfficePH;

BEGIN TRY

    PRINT '=== Before: does the trigger still exclude MainOffice? (expect 1) ===';
    SELECT COUNT(*) AS MainOfficeClausePresent FROM sys.triggers
    WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%N''MainOffice''%';

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_SupervisorRole
ON dbo.SeedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED sowing (unchanged, INSERT only): assigned to an active Sowing Supervisor
    -- who is not the person recording it (the assigned supervisor is the only approver).
    IF NOT EXISTS (SELECT 1 FROM deleted)
       AND EXISTS (SELECT 1
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
    -- CUTTING TRAY sowing: recorded WITHOUT a supervisor (pending destination) is allowed.
    -- When it GETS one (insert, or NULL -> value at confirmation) the supervisor is an
    -- active user assigned to the sowing''s Area (any role) who holds the approval permission.
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.SourceType = N''Cutting''
                 AND i.SupervisorId IS NOT NULL
                 AND (d.Id IS NULL OR ISNULL(d.SupervisorId, -1) <> i.SupervisorId)
                 AND (NOT EXISTS (SELECT 1
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
    -- ...and its destination is a real, active Polyhouse of that Area (never Outlet).
    -- 2026-10-02: a real Polyhouse filed under a Main Office-type Area IS a
    -- valid destination (only that Area''s own Cutting Stock depot concept
    -- is excluded, never its named physical Polyhouses) -- MainOffice
    -- removed from this exclusion, Outlet unchanged.
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.SourceType = N''Cutting''
                 AND i.SupervisorId IS NOT NULL
                 AND (d.Id IS NULL OR ISNULL(d.SupervisorId, -1) <> i.SupervisorId)
                 AND NOT EXISTS (SELECT 1
                                 FROM dbo.Polyhouses ph
                                 INNER JOIN dbo.Area a ON a.Id = ph.AreaId
                                 WHERE ph.Id = i.PolyhouseId
                                   AND ph.AreaId = i.AreaId
                                   AND a.IsActive = 1
                                   AND (a.AreaType IS NULL OR a.AreaType <> N''Outlet'')))
        THROW 50204, ''A cutting tray sowing must be sown at an active Polyhouse of its Area (not Outlet).'', 1;
END');

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== Trigger exists and is enabled (expect 1 row, is_disabled = 0) ===';
    SELECT name, is_disabled FROM sys.triggers WHERE name = N'TR_SeedSowings_SupervisorRole';

    PRINT '=== After: MainOffice clause removed (expect 0) ===';
    SELECT COUNT(*) AS MainOfficeClausePresent FROM sys.triggers
    WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%N''MainOffice''%';

    PRINT '=== Outlet clause still present (expect 1) ===';
    SELECT COUNT(*) AS OutletClausePresent FROM sys.triggers
    WHERE name = N'TR_SeedSowings_SupervisorRole' AND OBJECT_DEFINITION(object_id) LIKE N'%a.AreaType <> N''Outlet''%';

    PRINT '=== Other SeedSowings / ReadyConfirmations triggers untouched (expect 2) ===';
    SELECT COUNT(*) AS OtherTriggersPresent FROM sys.triggers
    WHERE name IN (N'TR_SeedSowings_ImmutableTrayData', N'TR_ReadyConfirmations_AssignedSupervisor');

    PRINT '=== The 5 real Main Office Area Polyhouses that are now valid destinations (FYI) ===';
    SELECT p.Id, p.Name AS Polyhouse, a.Name AS AreaName, a.AreaType
    FROM dbo.Polyhouses p INNER JOIN dbo.Area a ON a.Id = p.AreaId
    WHERE a.AreaType = N'MainOffice' ORDER BY p.Name;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingSowingMainOfficePH;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-02: user reviewed the dry run (MainOffice clause removed,
-- Outlet clause intact, only TR_SeedSowings_SupervisorRole changed, the
-- other 2 SeedSowings/ReadyConfirmations triggers untouched) and explicitly
-- approved committing.
-- Verified backup: PlantsIMS2_Test_PreCuttingSowingMainOfficePolyhouses_20261002.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION CuttingSowingMainOfficePH;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION CuttingSowingMainOfficePH;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
