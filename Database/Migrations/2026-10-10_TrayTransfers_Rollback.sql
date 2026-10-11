-- ============================================================================
-- 2026-10-10_TrayTransfers_Rollback.sql -- undoes 2026-10-10_TrayTransfers.sql.
-- REFUSES while any tray transfer or transfer ledger row exists: stock history is never
-- discarded. Tray ADJUSTMENTS use the pre-existing 'Adjustment' type and are kept as they are.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52011, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52012, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52013, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION TrayTransfersUndo2026_10_10;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    DECLARE @n INT;
    IF OBJECT_ID('dbo.TrayTransfers', 'U') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.TrayTransfers', N'@n INT OUTPUT', @n = @n OUTPUT;
        IF @n > 0 THROW 52014, 'Tray transfers exist -- refusing to roll back.', 1;
    END
    SELECT @n = COUNT(*) FROM dbo.TrayStockTransactions WHERE TransactionType IN (N'TransferOut', N'TransferIn', N'TransferReturn');
    IF @n > 0 THROW 52015, 'Tray transfer ledger rows exist -- refusing to roll back.', 1;

    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code IN (N'TrayStock.Adjust', N'TrayStock.Transfer', N'TrayStock.ReceiveTransfer');
    DELETE FROM dbo.Permissions WHERE Code IN (N'TrayStock.Adjust', N'TrayStock.Transfer', N'TrayStock.ReceiveTransfer');

    IF OBJECT_ID('dbo.TrayTransfers', 'U') IS NOT NULL DROP TABLE dbo.TrayTransfers;

    EXEC (N'ALTER TABLE dbo.TrayStockTransactions DROP CONSTRAINT CK_TrayStockTx_Type;');
    EXEC (N'ALTER TABLE dbo.TrayStockTransactions ADD CONSTRAINT CK_TrayStockTx_Type CHECK (TransactionType IN (
        N''Allocation'', N''Sowing'', N''ReversalReturn'', N''Adjustment'', N''Transplant''));');
    PRINT '=== Rolled back to the 2026-10-09 state ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayTransfersUndo2026_10_10;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayTransfersUndo2026_10_10;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayTransfersUndo2026_10_10;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
