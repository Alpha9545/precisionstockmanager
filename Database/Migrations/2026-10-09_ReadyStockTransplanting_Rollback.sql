-- ============================================================================
-- 2026-10-09_ReadyStockTransplanting_Rollback.sql -- undoes 2026-10-09_ReadyStockTransplanting.sql.
-- REFUSES while any Mother Plant transfer, transplanting, received / transplanted batch, in-transit
-- quantity or new ledger type exists: stock history is never discarded silently.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51941, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51942, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51943, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION RsTransplantUndo2026_10_09;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    DECLARE @n INT;
    IF OBJECT_ID('dbo.ReadyStockTransplants', 'U') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.ReadyStockTransplants', N'@n INT OUTPUT', @n = @n OUTPUT;
        IF @n > 0 THROW 51944, 'Transplantings exist -- refusing to roll back.', 1;
    END
    IF COL_LENGTH('dbo.ReadyStock', 'OriginType') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.ReadyStock WHERE OriginType <> N''Sowing'' OR InTransitQuantity <> 0', N'@n INT OUTPUT', @n = @n OUTPUT;
        IF @n > 0 THROW 51945, 'Received / transplanted batches or in-transit stock exist -- refusing to roll back.', 1;
    END
    SELECT @n = COUNT(*) FROM dbo.InternalTransfers WHERE StockType = N'MotherPlantTransfer';
    IF @n > 0 THROW 51946, 'Mother Plant transfers exist -- refusing to roll back.', 1;
    SELECT @n = COUNT(*) FROM dbo.ReadyStockTransactions WHERE TransactionType IN (N'TransferHold', N'TransferHoldRelease', N'TransferOut', N'TransferLoss',
        N'TransferIn', N'TransplantOut', N'TransplantLoss', N'TransplantIn', N'ReversalReturn');
    IF @n > 0 THROW 51947, 'Transfer / transplant ledger rows exist -- refusing to roll back.', 1;
    SELECT @n = COUNT(*) FROM dbo.TrayStockTransactions WHERE TransactionType = N'Transplant';
    IF @n > 0 THROW 51948, 'Tray Stock transplant rows exist -- refusing to roll back.', 1;

    -- Permissions
    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code IN (N'ReadyStock.TransferToMotherPlant', N'ReadyStock.ReceiveTransfer', N'Transplant.Enter', N'Transplant.View', N'Transplant.Reverse', N'TrayStock.EnterMotherPlant');
    DELETE FROM dbo.Permissions WHERE Code IN (N'ReadyStock.TransferToMotherPlant', N'ReadyStock.ReceiveTransfer', N'Transplant.Enter', N'Transplant.View', N'Transplant.Reverse', N'TrayStock.EnterMotherPlant');

    IF OBJECT_ID('dbo.ReadyStockTransplants', 'U') IS NOT NULL DROP TABLE dbo.ReadyStockTransplants;

    -- Ledger types back to the previous lists
    EXEC (N'ALTER TABLE dbo.TrayStockTransactions DROP CONSTRAINT CK_TrayStockTx_Type;');
    EXEC (N'ALTER TABLE dbo.TrayStockTransactions ADD CONSTRAINT CK_TrayStockTx_Type CHECK (TransactionType IN (N''Allocation'', N''Sowing'', N''ReversalReturn'', N''Adjustment''));');
    EXEC (N'ALTER TABLE dbo.ReadyStockTransactions DROP CONSTRAINT CK_ReadyStockTx_Type;');
    EXEC (N'ALTER TABLE dbo.ReadyStockTransactions ADD CONSTRAINT CK_ReadyStockTx_Type CHECK (TransactionType IN (
        N''Confirmed'', N''ReversalRemoval'', N''Transfer'', N''Reservation'', N''ReservationRelease'', N''Dispatch'', N''Wastage''));');

    -- InternalTransfers
    IF OBJECT_ID('dbo.CK_InternalTransfers_MotherPlantTransfer', 'C') IS NOT NULL
        EXEC (N'ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_MotherPlantTransfer;');
    EXEC (N'ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_SourceMatchesStockType;');
    EXEC (N'ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_SourceMatchesStockType CHECK (
           StockType = N''EmptyPot'' AND SourceEmptyPotInventoryId IS NOT NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
        OR StockType = N''PottedPlant'' AND SourcePottedPlantStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
        OR StockType = N''Cutting'' AND SourceCuttingStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL AND SourceReadyStockId IS NULL
        OR StockType = N''MainOfficeIssue'' AND SourcePottedPlantStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
        OR StockType = N''GrowingPartnerToOutlet'' AND SourcePottedPlantStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
        OR StockType = N''ReadyStock'' AND SourceReadyStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL);');
    EXEC (N'ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_StockType;');
    EXEC (N'ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_StockType CHECK (StockType IN (
        N''EmptyPot'', N''PottedPlant'', N''Cutting'', N''MainOfficeIssue'', N''GrowingPartnerToOutlet'', N''ReadyStock''));');
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'UX_InternalTransfers_SubmissionToken')
        EXEC (N'DROP INDEX UX_InternalTransfers_SubmissionToken ON dbo.InternalTransfers;');
    IF COL_LENGTH('dbo.InternalTransfers', 'DestinationPolyhouseId') IS NOT NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT FK_InternalTransfers_DestinationPolyhouse, FK_InternalTransfers_CancelledBy;');
        EXEC (N'ALTER TABLE dbo.InternalTransfers DROP COLUMN DestinationPolyhouseId, TransferDate, SubmissionToken, CancelledById, CancelledDate, CancelReason;');
    END

    -- ReadyStock back to one batch per sowing, cavity = sowing cavity, whole trays everywhere
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyStock_WholeTrays
ON dbo.ReadyStock
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT UPDATE(Quantity) RETURN;
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE (d.Id IS NULL OR d.Quantity <> i.Quantity)
                 AND i.Quantity % CASE i.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                    WHEN N''42 Cavity'' THEN 42 WHEN N''102 Cavity'' THEN 102
                                                    WHEN N''150 Cavity'' THEN 150 END <> 0)
        THROW 50125, ''Ready Stock quantity must be a whole number of trays of the sowing cavity.'', 1;
END');
    IF OBJECT_ID('dbo.CK_ReadyStock_TransplantCavity', 'C') IS NOT NULL
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_TransplantCavity;');
    IF COL_LENGTH('dbo.ReadyStock', 'SowingCavityKey') IS NOT NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT FK_ReadyStock_SowingCavity;');
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP COLUMN SowingCavityKey;');
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT FK_ReadyStock_SowingCavity FOREIGN KEY (SeedSowingId, CavityType) REFERENCES dbo.SeedSowings (Id, CavityType);');
    END
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'IX_ReadyStock_Parent')
        EXEC (N'DROP INDEX IX_ReadyStock_Parent ON dbo.ReadyStock;');
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'UX_ReadyStock_Origin')
        EXEC (N'DROP INDEX UX_ReadyStock_Origin ON dbo.ReadyStock;');
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'UX_ReadyStock_SowingBatch')
        EXEC (N'DROP INDEX UX_ReadyStock_SowingBatch ON dbo.ReadyStock;');
    IF OBJECT_ID('dbo.UQ_ReadyStock_SeedSowing', 'UQ') IS NULL
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT UQ_ReadyStock_SeedSowing UNIQUE (SeedSowingId);');

    EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_WholeQuantities;');
    EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_WholeQuantities CHECK (
        Quantity = FLOOR(Quantity) AND ReservedQuantity = FLOOR(ReservedQuantity) AND DispatchedQuantity = FLOOR(DispatchedQuantity));');
    EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_ReservedDispatched;');
    EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_ReservedDispatched CHECK (
        ReservedQuantity >= 0 AND DispatchedQuantity >= 0 AND ReservedQuantity + DispatchedQuantity <= Quantity);');
    EXEC (N'ALTER TABLE dbo.ReadyStock DROP COLUMN AvailableQuantity;');
    EXEC (N'ALTER TABLE dbo.ReadyStock DROP COLUMN PhysicalQuantity;');
    EXEC (N'ALTER TABLE dbo.ReadyStock ADD
        PhysicalQuantity AS (Quantity - DispatchedQuantity) PERSISTED,
        AvailableQuantity AS (Quantity - ReservedQuantity - DispatchedQuantity) PERSISTED;');
    IF OBJECT_ID('dbo.CK_ReadyStock_Origin', 'C') IS NOT NULL
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_Origin, CK_ReadyStock_OriginType;');
    IF COL_LENGTH('dbo.ReadyStock', 'OriginType') IS NOT NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT FK_ReadyStock_Parent, DF_ReadyStock_OriginType, DF_ReadyStock_InTransit;');
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP COLUMN OriginType, ParentReadyStockId, OriginReferenceId, InTransitQuantity;');
    END
    PRINT '=== Rolled back to the pre-2026-10-09 schema ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RsTransplantUndo2026_10_09;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RsTransplantUndo2026_10_09;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RsTransplantUndo2026_10_09;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
