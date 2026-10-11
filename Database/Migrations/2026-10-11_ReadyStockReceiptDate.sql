-- ============================================================================
-- 2026-10-11_ReadyStockReceiptDate.sql
--
-- WHY: a Ready Stock transfer into a Mother Plant Area was always dated when the receipt was
-- RECORDED. Owner decision 2026-10-11: the receiver enters the BUSINESS date the plants arrived
-- (default today in India; never in the future; never before the transfer was sent; an earlier
-- date needs a reason). The recorded time stays in ConfirmedDate (audit).
-- dbo.InternalTransfers:
--   ReceivedDate       DATE NULL           business receipt date (Mother Plant transfers received from now on)
--   ReceiptDateReason  NVARCHAR(500) NULL  why an earlier date was recorded late
--   CK_InternalTransfers_ReceivedDate      only on a Completed transfer, never before TransferDate
-- Existing rows are NOT changed: no receipt date is invented for historical receipts (they stay
-- NULL and keep their recorded-time ledger dates -- docs/STOCK_DEFINITIONS.md).
-- Requires 2026-10-09_ReadyStockTransplanting.sql (Mother Plant transfers on InternalTransfers).
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op).
-- Undo: 2026-10-11_ReadyStockReceiptDate_Rollback.sql (refuses once a receipt date is stored).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52121, '2026-10-11_ReadyStockReceiptDate.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52122, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52123, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION ReceiptDate1011;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.InternalTransfers', 'SourceReadyStockId') IS NULL OR COL_LENGTH('dbo.InternalTransfers', 'TransferDate') IS NULL
        THROW 52124, 'Run 2026-10-09_ReadyStockTransplanting.sql first.', 1;

    PRINT '=== Before: rows that must not change ===';
    SELECT COUNT(*) AS Transfers, SUM(CASE WHEN StockType = N'MotherPlantTransfer' THEN 1 ELSE 0 END) AS MotherPlantTransfers,
           CHECKSUM_AGG(CHECKSUM(Id, Status, Quantity, ConfirmedQuantity, ConfirmedDate, TransferDate)) AS Fingerprint
    FROM dbo.InternalTransfers;

    IF COL_LENGTH('dbo.InternalTransfers', 'ReceivedDate') IS NULL
    BEGIN
        ALTER TABLE dbo.InternalTransfers ADD ReceivedDate DATE NULL;
        PRINT '=== InternalTransfers.ReceivedDate added ===';
    END
    ELSE PRINT '=== InternalTransfers.ReceivedDate already exists -- no-op ===';

    IF COL_LENGTH('dbo.InternalTransfers', 'ReceiptDateReason') IS NULL
    BEGIN
        ALTER TABLE dbo.InternalTransfers ADD ReceiptDateReason NVARCHAR(500) NULL;
        PRINT '=== InternalTransfers.ReceiptDateReason added ===';
    END
    ELSE PRINT '=== InternalTransfers.ReceiptDateReason already exists -- no-op ===';

    IF OBJECT_ID('dbo.CK_InternalTransfers_ReceivedDate', 'C') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.InternalTransfers WITH CHECK ADD CONSTRAINT CK_InternalTransfers_ReceivedDate CHECK (
            ReceivedDate IS NULL
            OR (Status = N''Completed'' AND (TransferDate IS NULL OR ReceivedDate >= TransferDate)));');
        PRINT '=== CK_InternalTransfers_ReceivedDate added ===';
    END
    ELSE PRINT '=== CK_InternalTransfers_ReceivedDate already exists -- no-op ===';

    PRINT '=== After (Transfers / Fingerprint must equal Before; no receipt date invented) ===';
    EXEC (N'SELECT COUNT(*) AS Transfers, SUM(CASE WHEN StockType = N''MotherPlantTransfer'' THEN 1 ELSE 0 END) AS MotherPlantTransfers,
           CHECKSUM_AGG(CHECKSUM(Id, Status, Quantity, ConfirmedQuantity, ConfirmedDate, TransferDate)) AS Fingerprint,
           SUM(CASE WHEN ReceivedDate IS NOT NULL THEN 1 ELSE 0 END) AS WithReceiptDate
    FROM dbo.InternalTransfers;');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION ReceiptDate1011;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION ReceiptDate1011;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION ReceiptDate1011;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
