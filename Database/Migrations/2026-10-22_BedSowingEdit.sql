-- ============================================================================
-- 2026-10-22_BedSowingEdit.sql
--
-- WHY (owner requirement 2026-10-13, "Edit option for Bed Sowing"): an authorised user may correct an UNTOUCHED bed
--   sowing's sowing date, variety, seed lot and quantity in place -- same Bed Sowing code, recorder and created time --
--   with the seed ledger reconciled. TR_BedSowings_Guard (2026-10-12_BedSowing.sql) makes exactly those fields immutable
--   (52171, "cancel and record again instead"), so the edit cannot be saved without this change.
--
-- WHAT (one transaction; only this trigger changes -- no table, column, row, ledger or permission):
--   TR_BedSowings_Guard is replaced (only if byte-for-byte the current version, SHA-256) by a version that
--     * keeps fixed: code, Area, Polyhouse, recorder, form token, Ready Date fields; a cancelled bed stays cancelled (52171);
--     * allows variety / seed lot / sowing date / quantity to change ONLY on an untouched bed: pending transplant before
--       and after, Transplanted + Lost = 0, no transplant / loss operation ever, no legacy Ready Date (52801);
--     * then requires the lot to be an active Main Office lot of the bed's variety, with the bed's lot number and seed
--       source copied from it (52802);
--     * and requires the bed's own seed ledger ('BedSowing' rows) to match: net -Quantity on its lot, 0 on every other
--       lot -- i.e. the application reversed the original deduction and recorded the revised one in the same
--       transaction (52803);
--     * keeps the transplant / loss conservation rule unchanged (52172).
--   Remarks were and stay editable. Existing rows are unaffected (the trigger checks only new changes).
-- Requires: 2026-10-12_BedSowing.sql (and dbo.BedSowingAuditLog from 2026-10-16_BedSowingApprover.sql, used by the
--   application to audit each edit).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Idempotent (re-run = no-op). Undo: 2026-10-22_BedSowingEdit_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- Probe used by the application (Data/WorkflowCorrectionsSchema.BedSowingEditProbeSql): OBJECT_DEFINITION(OBJECT_ID('dbo.TR_BedSowings_Guard')) LIKE N'%BedSowingEdit%'
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52811, '2026-10-22_BedSowingEdit.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52812, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52813, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION BedSowingEdit1022;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.BedSowings', 'U') IS NULL OR OBJECT_ID('dbo.TR_BedSowings_Guard', 'TR') IS NULL
        THROW 52814, 'Run 2026-10-12_BedSowing.sql first.', 1;
    IF OBJECT_ID('dbo.BedSowingAuditLog', 'U') IS NULL
        THROW 52815, 'Run 2026-10-16_BedSowingApprover.sql first (dbo.BedSowingAuditLog).', 1;
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_BedSowings_Guard')) LIKE N'%BedSowingEdit%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_BedSowings_Guard'))), 2) <> '1DB1E8BF2B920F334A8F0D38AB3C0073B9429F609EADE53B7FDC654FB0738CD3'
            THROW 52816, 'TR_BedSowings_Guard is not the expected version -- schema drift, nothing changed.', 1;
        DECLARE @sql NVARCHAR(MAX);
        DROP TRIGGER dbo.TR_BedSowings_Guard;
        SET @sql = N'CREATE TRIGGER dbo.TR_BedSowings_Guard ON dbo.BedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- The code, location, recorder, form token and Ready Date are fixed; a cancelled bed stays cancelled.
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.BedSowingCode <> d.BedSowingCode OR i.AreaId <> d.AreaId OR i.PolyhouseId <> d.PolyhouseId
                  OR ISNULL(i.ReadyStockDays, -1) <> ISNULL(d.ReadyStockDays, -1)
                  OR ISNULL(i.ExpectedReadyDate, ''19000101'') <> ISNULL(d.ExpectedReadyDate, ''19000101'')
                  OR ISNULL(i.CreatedById, -1) <> ISNULL(d.CreatedById, -1)
                  OR ISNULL(CONVERT(NCHAR(36), i.SubmissionToken), N'''') <> ISNULL(CONVERT(NCHAR(36), d.SubmissionToken), N'''')
                  OR (d.Status = N''Cancelled'' AND i.Status <> N''Cancelled''))
        THROW 52171, ''The code, location, recorder and Ready Date of a bed sowing cannot be changed, and a cancelled bed sowing stays cancelled.'', 1;
    -- 2026-10-22 (BedSowingEdit): what was sown (variety, seed lot, sowing date, quantity) may be EDITED only on an
    -- untouched bed -- pending transplant before and after, nothing transplanted or lost, no transplant / loss
    -- operation ever recorded, no legacy Ready Date ...
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE (i.SpeciesId <> d.SpeciesId OR i.SeedStockId <> d.SeedStockId OR i.SowingDate <> d.SowingDate OR i.Quantity <> d.Quantity)
                 AND (d.Status <> N''PendingTransplant'' OR i.Status <> N''PendingTransplant''
                      OR d.TransplantedQuantity + d.LostQuantity <> 0
                      OR d.ReadyStockDays IS NOT NULL
                      OR EXISTS (SELECT 1 FROM dbo.BedSowingOperations o WHERE o.BedSowingId = i.Id)))
        THROW 52801, ''Only an untouched bed sowing (pending transplant, never transplanted or lost) can have its variety, seed lot, sowing date or quantity edited.'', 1;
    -- ... its seed lot must be an active Main Office lot of its variety, with the lot number and source copied from it ...
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               LEFT JOIN dbo.SeedStock ss ON ss.Id = i.SeedStockId
               LEFT JOIN dbo.Area a ON a.Id = ss.AreaId
               WHERE (i.SpeciesId <> d.SpeciesId OR i.SeedStockId <> d.SeedStockId)
                 AND (ss.Id IS NULL OR ss.SpeciesId <> i.SpeciesId OR ISNULL(a.AreaType, N'''') <> N''MainOffice'' OR ISNULL(a.IsActive, 0) = 0
                      OR i.SeedLotNo <> ss.BatchNo OR ISNULL(i.SeedSourceId, -1) <> ISNULL(ss.SeedSourceId, -1)))
        THROW 52802, ''An edited bed sowing must use an active Main Office seed lot of its own variety.'', 1;
    -- ... and the bed''s own seed ledger (''BedSowing'' rows) must match it exactly: the net on its lot is minus its quantity,
    -- and nothing is left on any other lot (the original deduction reversed, the revised one recorded).
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE (i.SpeciesId <> d.SpeciesId OR i.SeedStockId <> d.SeedStockId OR i.SowingDate <> d.SowingDate OR i.Quantity <> d.Quantity)
                 AND (ISNULL((SELECT SUM(t.Quantity) FROM dbo.SeedStockTransactions t
                              WHERE t.ReferenceType = N''BedSowing'' AND t.ReferenceId = i.Id AND t.SeedStockId = i.SeedStockId), 0) <> -i.Quantity
                      OR EXISTS (SELECT 1 FROM dbo.SeedStockTransactions t
                                 WHERE t.ReferenceType = N''BedSowing'' AND t.ReferenceId = i.Id AND t.SeedStockId <> i.SeedStockId
                                 GROUP BY t.SeedStockId HAVING SUM(t.Quantity) <> 0)))
        THROW 52803, ''An edited bed sowing must be matched by its seed ledger: the original deduction reversed and the revised deduction recorded.'', 1;
    -- Conservation: Transplanted / Lost are exactly the ACTIVE operations of the bed.
    IF EXISTS (SELECT 1 FROM inserted i
               OUTER APPLY (SELECT ISNULL(SUM(CASE WHEN o.OperationType = N''Transplant'' THEN o.Quantity END), 0) AS T,
                                   ISNULL(SUM(CASE WHEN o.OperationType = N''Loss'' THEN o.Quantity END), 0) AS L
                            FROM dbo.BedSowingOperations o WHERE o.BedSowingId = i.Id AND o.Status = N''Active'') s
               WHERE i.TransplantedQuantity <> s.T OR i.LostQuantity <> s.L)
        THROW 52172, ''Bed Sowing totals must equal its active transplant and loss operations.'', 1;
END';
        SET @sql = REPLACE(@sql, NCHAR(13) + NCHAR(10), NCHAR(10));
        EXEC (@sql);

        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_BedSowings_Guard'))), 2) <> '5214BC02838D9D20AE5C39CD55ABDBF68B610CC6A439EAE00B916D257AA0697F'
            THROW 52817, 'Post-check failed: TR_BedSowings_Guard is not the expected new version.', 1;
        IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.TR_BedSowings_Guard')) LIKE N'%BedSowingEdit%')
            THROW 52818, 'Post-check failed: the application probe would not see the change.', 1;
        PRINT '=== TR_BedSowings_Guard: untouched beds may be edited, with the seed ledger reconciled ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedSowingEdit1022;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedSowingEdit1022;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedSowingEdit1022;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
