-- ============================================================================
-- 2026-09-28_PotBatchReadyConfirmByAreaUser.sql
--
-- Pot Production READY confirmation: ANY authorized user assigned to the
-- batch's Area may confirm it READY -- not only the designated Mother Plant
-- Supervisor. The batch creator may confirm too.
--
-- The old rule was enforced in the database in four places; this script
-- replaces them so the application change can actually work:
--
--   DROPPED  CK_PotBatches_ConfirmedBySupervisor   ReadyConfirmedById must = SupervisorId
--   DROPPED  CK_PotBatches_SupervisorNotCreator     SupervisorId <> CreatedById
--   CHANGED  TR_PotBatches_Insert   the "Ready Confirmation By" user (SupervisorId) must be
--                                   an ACTIVE user assigned to the production Area
--                                   (was: an active "Mother Plant Supervisor" of the Area)
--   CHANGED  TR_PotBatches_Update   the user who confirms READY (ReadyConfirmedById) must be
--                                   an ACTIVE user assigned to the batch Area
--                                   (was: an active "Mother Plant Supervisor" of the Area)
--
-- The Area stays the authority boundary: a user assigned only to Area A is
-- still rejected for a batch of Area B, at the database level. The Pot
-- Production PERMISSION is checked by the application (it already gates every
-- POST on the batch page); the database enforces active + Area assignment,
-- the same test the app re-runs inside the READY transaction.
--
-- NO column is added, renamed or changed: SupervisorId keeps its name and now
-- means "Ready Confirmation By" (the expected confirmer, not a restriction);
-- ReadyConfirmedById / ReadyDate / ModifiedBy / ModifiedDate already record
-- the ACTUAL confirmer. Every other rule of TR_PotBatches_Update is kept
-- verbatim (identity of a batch never changes, closed batches stay closed,
-- READY quantity / reason / unused-cuttings rules, no cancel after production).
-- No data is changed.
--
-- SAFETY:
--   * Guarded to PlantsIMS2_Test only.
--   * One transaction; ends in ROLLBACK (dry run). Change the last statement to
--     COMMIT TRANSACTION only after review and explicit approval, and take a
--     verified COPY_ONLY backup first.
--   * Idempotent: constraints are dropped only if present; triggers are
--     CREATE OR ALTER.
--   * DEPLOY TOGETHER with the matching application build: the OLD app cannot
--     use this schema change (it still refuses everyone but the designated
--     supervisor), and the NEW app fails at the OLD database constraints.
--
-- NOT executed against any database by this script's author.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50300, '2026-09-28_PotBatchReadyConfirmByAreaUser.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION PotBatchReadyConfirm;

BEGIN TRY

    ----------------------------------------------------------------------
    -- 1. The two CHECK constraints that hard-wired the old rule
    ----------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PotBatches_ConfirmedBySupervisor' AND parent_object_id = OBJECT_ID('dbo.PotProductionBatches'))
        ALTER TABLE dbo.PotProductionBatches DROP CONSTRAINT CK_PotBatches_ConfirmedBySupervisor;
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PotBatches_SupervisorNotCreator' AND parent_object_id = OBJECT_ID('dbo.PotProductionBatches'))
        ALTER TABLE dbo.PotProductionBatches DROP CONSTRAINT CK_PotBatches_SupervisorNotCreator;

    ----------------------------------------------------------------------
    -- 2. Insert trigger: "Ready Confirmation By" = active user assigned to the Area
    ----------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_PotBatches_Insert
ON dbo.PotProductionBatches
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted WHERE Status <> N''InProduction'')
        THROW 50209, ''A pot production batch starts In Production.'', 1;
    -- the "Ready Confirmation By" user (SupervisorId) is an active user
    -- assigned to the production Area (any role; the creator may be chosen)
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1))
        THROW 50210, ''The batch Ready Confirmation By user must be an active user assigned to the production Area.'', 1;
END');

    ----------------------------------------------------------------------
    -- 3. Update trigger: whoever confirms READY is an active user assigned to
    --    the batch Area. Everything else is unchanged from PhaseD.
    ----------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_PotBatches_Update
ON dbo.PotProductionBatches
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- identity of a batch never changes
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.BatchCode <> d.BatchCode OR i.SourceCuttingStockId <> d.SourceCuttingStockId OR i.SpeciesId <> d.SpeciesId
                  OR i.AreaId <> d.AreaId OR i.PotSize <> d.PotSize OR i.EmptyPotInventoryId <> d.EmptyPotInventoryId
                  OR i.CuttingAllocated <> d.CuttingAllocated OR i.ProductionStartDate <> d.ProductionStartDate
                  OR i.SupervisorId <> d.SupervisorId OR i.CreatedById <> d.CreatedById)
        THROW 50211, ''The cuttings, Area, pot size, supervisor and creator of a pot batch cannot be changed.'', 1;
    -- a Ready, Lost or Cancelled batch is closed
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE d.Status <> N''InProduction''
                 AND (i.Status <> d.Status OR ISNULL(i.ReadyQuantity, -1) <> ISNULL(d.ReadyQuantity, -1)
                      OR ISNULL(i.ExpectedReadyDate, ''19000101'') <> ISNULL(d.ExpectedReadyDate, ''19000101'')))
        THROW 50212, ''This pot batch is closed.'', 1;
    -- READY: ready pots <= pots produced; pots lost need a reason; cuttings
    -- not potted are either returned to stock or recorded as wastage
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN deleted d ON d.Id = i.Id
               CROSS APPLY (SELECT ISNULL(SUM(e.Quantity), 0) AS Potted FROM dbo.PotProductionEntries e WHERE e.BatchId = i.Id) p
               WHERE i.Status IN (N''Ready'', N''Lost'') AND d.Status = N''InProduction''
                 AND (i.ReadyQuantity > p.Potted
                      OR (i.ReadyQuantity < p.Potted AND i.WastageReason IS NULL)
                      OR (p.Potted < i.CuttingAllocated AND i.UnusedCuttingAction IS NULL)
                      OR (p.Potted = i.CuttingAllocated AND i.UnusedCuttingAction IS NOT NULL)))
        THROW 50213, ''READY quantity cannot exceed the pots produced; lost pots need a reason; unused cuttings need an action.'', 1;
    -- READY / complete loss is confirmed by an active user assigned to the
    -- batch Area (any role; the batch Area is the boundary)
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.Status IN (N''Ready'', N''Lost'') AND d.Status = N''InProduction''
                 AND NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.ReadyConfirmedById
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1))
        THROW 50218, ''READY must be confirmed by an active user assigned to the batch Area.'', 1;
    -- a batch with production entries is finished with READY, not cancelled
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.Status = N''Cancelled'' AND d.Status = N''InProduction''
                 AND EXISTS (SELECT 1 FROM dbo.PotProductionEntries e WHERE e.BatchId = i.Id))
        THROW 50214, ''A batch that already has production cannot be cancelled.'', 1;
END');

    ----------------------------------------------------------------------
    -- VERIFICATION (before the ROLLBACK, so it shows what WOULD happen)
    ----------------------------------------------------------------------
    PRINT '=== Old supervisor CHECK constraints gone (expect 0 rows) ===';
    SELECT name FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.PotProductionBatches')
      AND name IN (N'CK_PotBatches_ConfirmedBySupervisor', N'CK_PotBatches_SupervisorNotCreator');

    PRINT '=== Other PotProductionBatches CHECK constraints untouched (expect 7) ===';
    SELECT COUNT(*) AS RemainingChecks FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PotProductionBatches');

    PRINT '=== Triggers no longer name the Mother Plant Supervisor role (expect 0) ===';
    SELECT COUNT(*) AS TriggersStillRoleBound FROM sys.triggers t
    WHERE t.parent_id = OBJECT_ID('dbo.PotProductionBatches')
      AND OBJECT_DEFINITION(t.object_id) LIKE N'%Mother Plant Supervisor%';

    PRINT '=== Triggers still enforce the Area boundary (expect 2) ===';
    SELECT COUNT(*) AS TriggersEnforcingArea FROM sys.triggers t
    WHERE t.parent_id = OBJECT_ID('dbo.PotProductionBatches')
      AND OBJECT_DEFINITION(t.object_id) LIKE N'%ur.AreaId = i.AreaId%';

    PRINT '=== Both triggers exist and are enabled (expect 2 rows, is_disabled = 0) ===';
    SELECT name, is_disabled FROM sys.triggers WHERE parent_id = OBJECT_ID('dbo.PotProductionBatches') ORDER BY name;

    PRINT '=== No existing batch has a confirmer / designated user outside its Area (must be 0) ===';
    SELECT COUNT(*) AS ExistingViolations
    FROM dbo.PotProductionBatches b
    WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = b.SupervisorId AND ur.AreaId = b.AreaId)
       OR (b.ReadyConfirmedById IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = b.ReadyConfirmedById AND ur.AreaId = b.AreaId));

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION PotBatchReadyConfirm;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-28: user reviewed the dry run (22/22 scenarios, before/after
-- snapshots identical apart from the four intended objects) and explicitly
-- approved applying it. Verified backup:
-- PlantsIMS2_Test_PrePotReadyConfirmMigrationCommit_20260928_122820.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION PotBatchReadyConfirm;
GO
