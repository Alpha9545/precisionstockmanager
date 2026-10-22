-- ============================================================================
-- 2026-10-14_MotherPlantAreaTraysAndTransferPurpose_Rollback.sql
-- Undoes 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql.
--
-- REFUSES once the new shapes are used:
--   * a transfer with a recorded TransferPurpose, or a Mother Plant transfer without a destination Polyhouse;
--   * a transplanting without a Polyhouse;
--   * a Mother Plant Area pool with any ledger row other than the migration's own 'AreaPoolMerge' rows.
-- Otherwise: the migration's merge is reversed row for row (trays back to the exact Polyhouse pools they
-- came from, those pools re-activated, the Area pools retired at 0 -- compensating 'Adjustment' rows,
-- nothing deleted), TR_TrayStock_PoolLocation dropped, CK_TrayStock_ActivePoolHasPolyhouse restored,
-- ReadyStockTransplants.PolyhouseId NOT NULL again, CK_InternalTransfers_MotherPlantTransfer restored
-- (with the destination Polyhouse), TR_InternalTransfers_MotherPlantPurpose / CK_InternalTransfers_TransferPurpose
-- / InternalTransfers.TransferPurpose dropped.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52321, '2026-10-14_MotherPlantAreaTraysAndTransferPurpose_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52322, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52323, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION MpAreaTraysRb1014;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NULL AND OBJECT_ID('dbo.TR_TrayStock_PoolLocation', 'TR') IS NULL
        PRINT '=== Not applied -- nothing to undo ===';
    ELSE
    BEGIN
        DECLARE @n INT;
        IF COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NOT NULL
        BEGIN
            EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.InternalTransfers WHERE TransferPurpose IS NOT NULL', N'@n INT OUTPUT', @n = @n OUTPUT;
            IF @n > 0 THROW 52324, 'Transfers with a recorded purpose exist -- rollback refused.', 1;
        END
        IF EXISTS (SELECT 1 FROM dbo.InternalTransfers WHERE StockType = N'MotherPlantTransfer' AND DestinationPolyhouseId IS NULL)
            THROW 52325, 'Mother Plant transfers without a destination Polyhouse exist -- rollback refused.', 1;
        IF EXISTS (SELECT 1 FROM dbo.ReadyStockTransplants WHERE PolyhouseId IS NULL)
            THROW 52326, 'Transplantings without a Polyhouse exist -- rollback refused.', 1;
        IF EXISTS (SELECT 1 FROM dbo.TrayStockTransactions x JOIN dbo.TrayStock t ON t.Id = x.TrayStockId JOIN dbo.Area a ON a.Id = t.AreaId
                   WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NULL AND t.IsActive = 1
                     AND ISNULL(x.ReferenceType, N'') <> N'AreaPoolMerge'
                     AND x.CreatedAt >= (SELECT MIN(m.CreatedAt) FROM dbo.TrayStockTransactions m WHERE m.ReferenceType = N'AreaPoolMerge'))
            THROW 52327, 'Mother Plant Area tray pools were used after the migration -- rollback refused.', 1;
        IF EXISTS (SELECT 1 FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId
                   WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NULL AND t.IsActive = 1
                     AND NOT EXISTS (SELECT 1 FROM dbo.TrayStockTransactions m WHERE m.TrayStockId = t.Id AND m.ReferenceType = N'AreaPoolMerge')
                     AND EXISTS (SELECT 1 FROM dbo.TrayStockTransactions u WHERE u.TrayStockId = t.Id))
            THROW 52328, 'A Mother Plant Area tray pool created after the migration holds history -- rollback refused.', 1;

        DECLARE @TraysBefore DECIMAL(18, 2) = (SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock);
        IF OBJECT_ID('dbo.TR_TrayStock_PoolLocation', 'TR') IS NOT NULL
            DROP TRIGGER dbo.TR_TrayStock_PoolLocation;

        -- Reverse each merge row: the trays go back to the Polyhouse pool they came from.
        DECLARE @Src INT, @Pool INT, @Qty DECIMAL(18, 2), @SrcBefore DECIMAL(18, 2), @PoolBefore DECIMAL(18, 2);
        DECLARE merges CURSOR LOCAL FAST_FORWARD FOR
            SELECT x.TrayStockId, x.ReferenceId, -x.Quantity
            FROM dbo.TrayStockTransactions x
            WHERE x.ReferenceType = N'AreaPoolMerge' AND x.Quantity < 0
              AND NOT EXISTS (SELECT 1 FROM dbo.TrayStockTransactions r WHERE r.ReferenceType = N'AreaPoolMergeUndo' AND r.ReferenceId = x.Id)
            ORDER BY x.Id;
        OPEN merges;
        FETCH NEXT FROM merges INTO @Src, @Pool, @Qty;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @SrcBefore = (SELECT PhysicalQuantity FROM dbo.TrayStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Src);
            SET @PoolBefore = (SELECT PhysicalQuantity FROM dbo.TrayStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Pool);
            IF @PoolBefore < @Qty THROW 52329, 'An Area pool holds fewer trays than were merged into it -- rollback refused.', 1;
            INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@Pool, SYSUTCDATETIME(), N'Adjustment', N'AreaPoolMergeUndo', @Src, -@Qty, @PoolBefore, NULL, N'2026-10-14 rollback: trays returned to their Polyhouse pool', SYSUTCDATETIME());
            INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@Src, SYSUTCDATETIME(), N'Adjustment', N'AreaPoolMergeUndo', @Pool, @Qty, @SrcBefore, NULL, N'2026-10-14 rollback: trays returned from the Area pool', SYSUTCDATETIME());
            UPDATE dbo.TrayStock SET PhysicalQuantity = PhysicalQuantity - @Qty, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'2026-10-14 rollback' WHERE Id = @Pool;
            UPDATE dbo.TrayStock SET PhysicalQuantity = PhysicalQuantity + @Qty, IsActive = 1, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'2026-10-14 rollback' WHERE Id = @Src;
            FETCH NEXT FROM merges INTO @Src, @Pool, @Qty;
        END
        CLOSE merges;
        DEALLOCATE merges;
        -- Polyhouse pools retired empty by the merge come back as they were (active).
        UPDATE t SET IsActive = 1, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'2026-10-14 rollback'
        FROM dbo.TrayStock t
        WHERE t.IsActive = 0 AND t.PolyhouseId IS NOT NULL AND t.ModifiedBy = N'2026-10-14 migration';
        IF EXISTS (SELECT 1 FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId
                   WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NULL AND t.IsActive = 1 AND t.PhysicalQuantity <> 0)
            THROW 52330, 'A Mother Plant Area pool still holds trays after the reversal -- rollback refused.', 1;
        UPDATE dbo.TrayStock SET IsActive = 0, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'2026-10-14 rollback'
        WHERE PolyhouseId IS NULL AND IsActive = 1;
        IF (SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock) <> @TraysBefore
            THROW 52331, 'Tray total changed during the rollback -- rolled back.', 1;
        IF OBJECT_ID('dbo.CK_TrayStock_ActivePoolHasPolyhouse', 'C') IS NULL
            ALTER TABLE dbo.TrayStock WITH CHECK ADD CONSTRAINT CK_TrayStock_ActivePoolHasPolyhouse CHECK (PolyhouseId IS NOT NULL OR IsActive = 0);

        ALTER TABLE dbo.ReadyStockTransplants ALTER COLUMN PolyhouseId INT NOT NULL;

        ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_MotherPlantTransfer;
        EXEC (N'ALTER TABLE dbo.InternalTransfers WITH CHECK ADD CONSTRAINT CK_InternalTransfers_MotherPlantTransfer CHECK (
            StockType <> N''MotherPlantTransfer''
            OR (DestinationAreaId IS NOT NULL AND DestinationPolyhouseId IS NOT NULL AND TransferDate IS NOT NULL
                AND Status IN (N''PendingConfirmation'', N''Completed'', N''Rejected'', N''Cancelled'')
                AND (Status <> N''Completed'' OR (ConfirmedQuantity IS NOT NULL AND ConfirmedQuantity > 0 AND ConfirmedQuantity <= Quantity
                                                  AND ConfirmedBy IS NOT NULL AND ConfirmedDate IS NOT NULL
                                                  AND (ConfirmedQuantity = Quantity OR DiscrepancyReason IS NOT NULL)))
                AND (Status <> N''Rejected'' OR (ConfirmedBy IS NOT NULL AND ConfirmedDate IS NOT NULL AND DiscrepancyReason IS NOT NULL))
                AND (Status <> N''Cancelled'' OR (CancelledById IS NOT NULL AND CancelledDate IS NOT NULL AND CancelReason IS NOT NULL))));');

        IF OBJECT_ID('dbo.TR_InternalTransfers_MotherPlantPurpose', 'TR') IS NOT NULL
            DROP TRIGGER dbo.TR_InternalTransfers_MotherPlantPurpose;
        IF OBJECT_ID('dbo.CK_InternalTransfers_TransferPurpose', 'C') IS NOT NULL
            ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_TransferPurpose;
        IF COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NOT NULL
            ALTER TABLE dbo.InternalTransfers DROP COLUMN TransferPurpose;
        PRINT '=== Rolled back to the 2026-10-13 shape ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpAreaTraysRb1014;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpAreaTraysRb1014;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpAreaTraysRb1014;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
