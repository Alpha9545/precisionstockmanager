-- ============================================================================
-- 2026-10-01_CuttingSowingDestinationAtConfirmation.sql
--
-- Cutting Tray Sowing is now a TWO-step workflow:
--   1. The Mother Plant Supervisor records the cutting details ONLY (Cutting
--      Stock, quantity, cavity, date). No Polyhouse, no Sowing Supervisor.
--      The row is saved with SupervisorId = NULL, PolyhouseId = NULL and a
--      provisional AreaId = the Cutting Stock pool's own Area. The Used/'Sown'
--      and remainder/'Wastage' Cutting Stock ledger rows are written at this
--      step exactly as before.
--   2. A Sowing Supervisor opens the pending sowing on Supervisor Approval and,
--      in ONE transaction, chooses the destination growing Polyhouse (its Area
--      becomes the sowing's AreaId), the Sowing Supervisor, and the Actual Ready
--      Trays. The confirmer must be an eligible supervisor of that Area (active,
--      assigned to it, holds ReadyStock.Confirm) and may select themselves or
--      another eligible supervisor of that Area (user decision 2026-10-01).
--
-- No schema change is needed: dbo.SeedSowings.AreaId / PolyhouseId / SupervisorId
-- already exist (PolyhouseId and SupervisorId are nullable). Three triggers
-- are changed so the database allows -- and still polices -- that flow:
--
--   1. dbo.TR_SeedSowings_SupervisorRole  (now AFTER INSERT, UPDATE)
--        SEED branch: byte-for-byte the original rule, still INSERT only.
--        CUTTING branch: SupervisorId may be NULL (pending destination). Whenever
--        a cutting sowing GETS a supervisor (on insert, or NULL -> value on
--        update) the supervisor must still be active + assigned to the sowing's
--        Area + hold ReadyStock.Confirm (unchanged rule), AND the destination
--        must be a real growing Polyhouse: PolyhouseId present, belonging to the
--        sowing's Area, Area active, Area not Main Office / Outlet.
--   2. dbo.TR_SeedSowings_ImmutableTrayData
--        The assigned supervisor stays immutable, with ONE exception: a 'Sown'
--        cutting sowing whose SupervisorId is NULL may get one (NULL -> value,
--        once). After that it can never change again.
--   3. dbo.TR_ReadyConfirmations_AssignedSupervisor
--        SEED: unchanged (approver = assigned supervisor, not the recorder).
--        CUTTING: approver = the assigned supervisor, OR an active user assigned
--        to the sowing's Area who holds ReadyStock.Confirm.
--
-- NOT changed: any table, column, constraint, index, data row, permission or
-- role, or any other trigger. Existing saved sowings and approvals are not
-- touched; the UPDATE branches only fire when SupervisorId actually changes.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Idempotent (CREATE OR ALTER). Deploy together with the matching app build.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 50340, '2026-10-01_CuttingSowingDestinationAtConfirmation.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 50341, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingSowingDestination;

BEGIN TRY

    ----------------------------------------------------------------------
    -- 1. Who may be ASSIGNED, and where (insert or NULL -> value update)
    ----------------------------------------------------------------------
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
    -- ...and its destination is a real growing Polyhouse of that Area (never Main Office / Outlet).
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
                                   AND (a.AreaType IS NULL OR a.AreaType NOT IN (N''MainOffice'', N''Outlet''))))
        THROW 50204, ''A cutting tray sowing must be sown at an active growing Polyhouse of its Area (not Main Office or Outlet).'', 1;
END');

    ----------------------------------------------------------------------
    -- 2. Immutability: supervisor may go NULL -> value once, cutting + Sown only
    ----------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_ImmutableTrayData
ON dbo.SeedSowings
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Fixed once sown: the source (seed lot or cutting stock), variety,
    -- cavity, trays, quantities, the ASSIGNED SUPERVISOR (the only person
    -- who may approve) and the recorder. Remarks, the approval totals and
    -- the status may still change. A pending cutting tray sowing (no
    -- supervisor yet) gets its supervisor ONCE, at confirmation.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.CavityType <> d.CavityType
                  OR i.SourceType <> d.SourceType
                  OR ISNULL(i.SourceSeedStockId, -1) <> ISNULL(d.SourceSeedStockId, -1)
                  OR ISNULL(i.SourceCuttingStockId, -1) <> ISNULL(d.SourceCuttingStockId, -1)
                  OR i.SpeciesId <> d.SpeciesId
                  OR i.QuantitySown <> d.QuantitySown
                  OR ISNULL(i.NumberOfTrays, -1) <> ISNULL(d.NumberOfTrays, -1)
                  OR ISNULL(i.SeedQuantity, -1) <> ISNULL(d.SeedQuantity, -1)
                  OR (ISNULL(i.SupervisorId, -1) <> ISNULL(d.SupervisorId, -1)
                      AND NOT (d.SupervisorId IS NULL AND i.SupervisorId IS NOT NULL
                               AND d.SourceType = N''Cutting'' AND d.Status = ''Sown''))
                  OR ISNULL(i.CreatedById, -1) <> ISNULL(d.CreatedById, -1))
        THROW 50123, ''Source, variety, cavity, trays, quantities, assigned supervisor and recorder of a sowing cannot be changed.'', 1;
END');

    ----------------------------------------------------------------------
    -- 3. Who may APPROVE
    ----------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED: only the assigned supervisor, never the recorder.
    -- CUTTING: the assigned supervisor, or an active user assigned to the sowing''s
    -- Area who holds ReadyStock.Confirm (the confirmer who chose the destination).
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
                      AND (NOT EXISTS (SELECT 1
                                       FROM dbo.UserRoles ur
                                       INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                       WHERE ur.UserId = i.ApprovedById
                                         AND ur.AreaId = sw.AreaId
                                         AND ISNULL(u.IsActive, 0) = 1)
                           OR NOT EXISTS (SELECT 1
                                          FROM dbo.UserRoles ur2
                                          INNER JOIN dbo.RolePermissions rp ON rp.RoleId = ur2.RoleId
                                          INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                          WHERE ur2.UserId = i.ApprovedById
                                            AND p.Code = N''ReadyStock.Confirm''))))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, an eligible supervisor of its Area) can approve it; a seed sowing never by the user who recorded it.'', 1;
END');

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== The three triggers exist and are enabled (expect 3 rows, is_disabled = 0) ===';
    SELECT name, is_disabled FROM sys.triggers
    WHERE name IN (N'TR_SeedSowings_SupervisorRole', N'TR_SeedSowings_ImmutableTrayData', N'TR_ReadyConfirmations_AssignedSupervisor') ORDER BY name;

    PRINT '=== Supervisor-role trigger now fires on INSERT and UPDATE (expect 2) ===';
    SELECT COUNT(*) AS Events FROM sys.trigger_events WHERE object_id = OBJECT_ID(N'dbo.TR_SeedSowings_SupervisorRole');

    PRINT '=== Other SeedSowings / ReadyConfirmations triggers untouched (expect 2) ===';
    SELECT COUNT(*) AS OtherTriggersPresent FROM sys.triggers
    WHERE name IN (N'TR_SeedSowings_RequireSeedQuantity', N'TR_ReadyConfirmations_TrayQuantity');

    PRINT '=== FYI: existing cutting sowings without a supervisor (expect 0; nothing is changed) ===';
    SELECT COUNT(*) AS ExistingPendingCutting FROM dbo.SeedSowings WHERE SourceType = N'Cutting' AND SupervisorId IS NULL;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingSowingDestination;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION CuttingSowingDestination;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION CuttingSowingDestination;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
