-- ============================================================================
-- 2026-10-15_PotProductionFromReadyStock_Rollback.sql -- undoes 2026-10-15_PotProductionFromReadyStock.sql.
-- Refuses once a pot batch from Ready Stock or a 'PotProductionOut' ledger row exists. Restores
-- TR_PotBatches_Update byte-for-byte (verified by SHA-256), CK_ReadyStockTx_Type, and SourceCuttingStockId NOT NULL.
-- SAFETY: as the migration. One transaction. Run with -v MODE=DRYRUN first.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52361, '2026-10-15_PotProductionFromReadyStock_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52362, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52363, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION PotsFromReadyStock1015Rb;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.PotProductionBatches', 'SourceReadyStockId') IS NULL
        PRINT '=== Not applied -- nothing to roll back ===';
    ELSE
    BEGIN
        DECLARE @n INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.PotProductionBatches WHERE SourceReadyStockId IS NOT NULL', N'@n INT OUTPUT', @n = @n OUTPUT;
        IF @n > 0 THROW 52364, 'Pot batches made from Ready Stock exist -- rollback refused.', 1;
        IF EXISTS (SELECT 1 FROM dbo.ReadyStockTransactions WHERE TransactionType = N'PotProductionOut')
            THROW 52365, 'Ready Stock ledger rows of type PotProductionOut exist -- rollback refused.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_PotBatches_Update'))), 2) <> 'A33DC5AF54F54D61C081C9E861B8480B595C20A0C009D0DB5438425143C48E78'
            THROW 52366, 'TR_PotBatches_Update is not the 2026-10-15 version -- rollback refused.', 1;

        DROP TRIGGER IF EXISTS dbo.TR_PotBatches_ReadyStockSource;
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
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_PotBatches_Update'))), 2) <> 'B452C77910A68EC8666B75548D9F5D404EC1C385180D13D25C663D67C284DF92'
            THROW 52367, 'The restored TR_PotBatches_Update does not match the original -- rollback refused.', 1;
        PRINT '=== TR_PotBatches_Update restored byte-for-byte ===';

        ALTER TABLE dbo.ReadyStockTransactions DROP CONSTRAINT CK_ReadyStockTx_Type;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransactions WITH CHECK ADD CONSTRAINT CK_ReadyStockTx_Type CHECK ([TransactionType]=N''ReversalReturn'' OR [TransactionType]=N''TransplantIn'' OR [TransactionType]=N''TransplantLoss'' OR [TransactionType]=N''TransplantOut'' OR [TransactionType]=N''TransferIn'' OR [TransactionType]=N''TransferLoss'' OR [TransactionType]=N''TransferOut'' OR [TransactionType]=N''TransferHoldRelease'' OR [TransactionType]=N''TransferHold'' OR [TransactionType]=N''Wastage'' OR [TransactionType]=N''Dispatch'' OR [TransactionType]=N''ReservationRelease'' OR [TransactionType]=N''Reservation'' OR [TransactionType]=N''Transfer'' OR [TransactionType]=N''ReversalRemoval'' OR [TransactionType]=N''Confirmed'');');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', (SELECT definition FROM sys.check_constraints WHERE name = N'CK_ReadyStockTx_Type')), 2) <> 'E5CB5FE907F756D7F9D457CF1DBB09F95B53209B84AE0DBA861184BC44D108AA'
            THROW 52368, 'The restored CK_ReadyStockTx_Type does not match the original -- rollback refused.', 1;

        ALTER TABLE dbo.PotProductionBatches DROP CONSTRAINT CK_PotBatches_OneSource;
        DROP INDEX IX_PotBatches_SourceReadyStock ON dbo.PotProductionBatches;
        ALTER TABLE dbo.PotProductionBatches DROP CONSTRAINT FK_PotBatches_ReadyStock;
        EXEC (N'ALTER TABLE dbo.PotProductionBatches DROP COLUMN SourceReadyStockId;');
        EXEC (N'ALTER TABLE dbo.PotProductionBatches ALTER COLUMN SourceCuttingStockId INT NOT NULL;');
        PRINT '=== 2026-10-15 shapes removed ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION PotsFromReadyStock1015Rb;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION PotsFromReadyStock1015Rb;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION PotsFromReadyStock1015Rb;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
