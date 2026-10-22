-- ============================================================================
-- 2026-10-15_PotProductionFromReadyStock.sql
--
-- WHY (owner decision 2026-10-11): Ready Stock sent to a Mother Plant Area for the purpose "Pot Production"
-- (2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql) is potted there: the received seedlings start a pot
-- production batch exactly like Cutting -> Pots (pots from the Area's own empty-pot stock, daily production
-- entries, READY confirmation into Potted Plant Stock), only the SOURCE is the received Ready Stock batch.
-- The existing Cutting -> Pots production is unchanged.
--
-- WHAT:
--   1. dbo.PotProductionBatches.SourceReadyStockId INT NULL + FK_PotBatches_ReadyStock (-> dbo.ReadyStock);
--      SourceCuttingStockId becomes NULLable; CK_PotBatches_OneSource: exactly ONE source per batch.
--      Existing batches (all from cuttings) keep their SourceCuttingStockId; nothing is rewritten.
--   2. CK_ReadyStockTx_Type gains 'PotProductionOut' (seedlings allocated to a pot batch leave the Ready Stock
--      batch; unused / cancelled ones come back as the existing 'ReversalReturn').
--   3. TR_PotBatches_Update: the batch's source never changes (either source; NULL-safe) -- otherwise the
--      exact 2026 trigger, re-created. Replaced only if it is byte-for-byte the expected version (SHA-256).
--   4. TR_PotBatches_ReadyStockSource (new): a Ready Stock source is of the same variety, in the same Area,
--      and that Area is a Mother Plant Area.
--
-- NOT changed: no batch, ledger, stock, transfer or permission row.
-- Requires 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql (the transfer purpose).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-15_PotProductionFromReadyStock_Rollback.sql
-- (refuses once a pot batch from Ready Stock exists).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52351, '2026-10-15_PotProductionFromReadyStock.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52352, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52353, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION PotsFromReadyStock1015;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NULL
        THROW 52354, 'Run 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql first.', 1;

    DECLARE @Applied BIT = CASE WHEN COL_LENGTH('dbo.PotProductionBatches', 'SourceReadyStockId') IS NULL THEN 0 ELSE 1 END;
    IF @Applied = 0
    BEGIN
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_PotBatches_Update'))), 2) <> 'B452C77910A68EC8666B75548D9F5D404EC1C385180D13D25C663D67C284DF92'
            THROW 52355, 'TR_PotBatches_Update is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', (SELECT definition FROM sys.check_constraints WHERE name = N'CK_ReadyStockTx_Type')), 2) <> 'E5CB5FE907F756D7F9D457CF1DBB09F95B53209B84AE0DBA861184BC44D108AA'
            THROW 52356, 'CK_ReadyStockTx_Type is not the expected definition -- schema drift, nothing changed.', 1;
    END

    PRINT '=== Before: rows that must not change ===';
    SELECT 'PotProductionBatches' AS T, COUNT(*) AS Rows FROM dbo.PotProductionBatches
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock
    UNION ALL SELECT 'ReadyStockTransactions', COUNT(*) FROM dbo.ReadyStockTransactions
    UNION ALL SELECT 'EmptyPotInventory', COUNT(*) FROM dbo.EmptyPotInventory;

    IF @Applied = 0
    BEGIN
        -- 1. the second source
        EXEC (N'ALTER TABLE dbo.PotProductionBatches ALTER COLUMN SourceCuttingStockId INT NULL;');
        EXEC (N'ALTER TABLE dbo.PotProductionBatches ADD SourceReadyStockId INT NULL CONSTRAINT FK_PotBatches_ReadyStock REFERENCES dbo.ReadyStock (Id);');
        EXEC (N'ALTER TABLE dbo.PotProductionBatches WITH CHECK ADD CONSTRAINT CK_PotBatches_OneSource CHECK (
               (SourceCuttingStockId IS NOT NULL AND SourceReadyStockId IS NULL)
            OR (SourceCuttingStockId IS NULL AND SourceReadyStockId IS NOT NULL));');
        EXEC (N'CREATE INDEX IX_PotBatches_SourceReadyStock ON dbo.PotProductionBatches (SourceReadyStockId) WHERE SourceReadyStockId IS NOT NULL;');
        PRINT '=== 1. PotProductionBatches.SourceReadyStockId (exactly one source per batch) ===';

        -- 2. the Ready Stock ledger type
        ALTER TABLE dbo.ReadyStockTransactions DROP CONSTRAINT CK_ReadyStockTx_Type;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransactions WITH CHECK ADD CONSTRAINT CK_ReadyStockTx_Type CHECK ([TransactionType]=N''ReversalReturn'' OR [TransactionType]=N''TransplantIn'' OR [TransactionType]=N''TransplantLoss'' OR [TransactionType]=N''TransplantOut'' OR [TransactionType]=N''TransferIn'' OR [TransactionType]=N''TransferLoss'' OR [TransactionType]=N''TransferOut'' OR [TransactionType]=N''TransferHoldRelease'' OR [TransactionType]=N''TransferHold'' OR [TransactionType]=N''Wastage'' OR [TransactionType]=N''Dispatch'' OR [TransactionType]=N''ReservationRelease'' OR [TransactionType]=N''Reservation'' OR [TransactionType]=N''Transfer'' OR [TransactionType]=N''ReversalRemoval'' OR [TransactionType]=N''Confirmed'' OR [TransactionType]=N''PotProductionOut'');');
        PRINT '=== 2. CK_ReadyStockTx_Type + PotProductionOut ===';

        -- 3. the source never changes (either source)
        DROP TRIGGER dbo.TR_PotBatches_Update;
        EXEC (N'
CREATE   TRIGGER dbo.TR_PotBatches_Update
ON dbo.PotProductionBatches
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- identity of a batch never changes
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.BatchCode <> d.BatchCode OR ISNULL(i.SourceCuttingStockId, -1) <> ISNULL(d.SourceCuttingStockId, -1)
                  OR ISNULL(i.SourceReadyStockId, -1) <> ISNULL(d.SourceReadyStockId, -1) OR i.SpeciesId <> d.SpeciesId
                  OR i.AreaId <> d.AreaId OR i.PotSize <> d.PotSize OR i.EmptyPotInventoryId <> d.EmptyPotInventoryId
                  OR i.CuttingAllocated <> d.CuttingAllocated OR i.ProductionStartDate <> d.ProductionStartDate
                  OR i.SupervisorId <> d.SupervisorId OR i.CreatedById <> d.CreatedById)
        THROW 50211, ''The source (cuttings or Ready Stock), Area, pot size, supervisor and creator of a pot batch cannot be changed.'', 1;
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
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_PotBatches_Update'))), 2) <> 'A33DC5AF54F54D61C081C9E861B8480B595C20A0C009D0DB5438425143C48E78'
            THROW 52357, 'TR_PotBatches_Update was not written as expected.', 1;
        PRINT '=== 3. TR_PotBatches_Update: source of either kind is fixed ===';

        -- 4. a Ready Stock source: same variety, same Mother Plant Area
        EXEC (N'
CREATE TRIGGER dbo.TR_PotBatches_ReadyStockSource
ON dbo.PotProductionBatches
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- 2026-10-15: a pot batch made from received Ready Stock (seedlings) potted in the SAME Mother Plant Area,
    -- of the same variety. (The transfer purpose, Pot Production, is checked by PotBatchRepository.)
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN dbo.ReadyStock rs ON rs.Id = i.SourceReadyStockId
               LEFT JOIN dbo.Area a ON a.Id = rs.AreaId
               WHERE i.SourceReadyStockId IS NOT NULL
                 AND (rs.Id IS NULL OR rs.AreaId <> i.AreaId OR rs.SpeciesId <> i.SpeciesId OR ISNULL(a.AreaType, N'''') <> N''MotherPlant''))
        THROW 52360, ''A pot batch from Ready Stock must use a Ready Stock batch of the same variety in the same Mother Plant Area.'', 1;
END');
        PRINT '=== 4. TR_PotBatches_ReadyStockSource ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';

    PRINT '=== After ===';
    SELECT 'PotProductionBatches' AS T, COUNT(*) AS Rows FROM dbo.PotProductionBatches
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock
    UNION ALL SELECT 'ReadyStockTransactions', COUNT(*) FROM dbo.ReadyStockTransactions
    UNION ALL SELECT 'EmptyPotInventory', COUNT(*) FROM dbo.EmptyPotInventory;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION PotsFromReadyStock1015;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION PotsFromReadyStock1015;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION PotsFromReadyStock1015;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
