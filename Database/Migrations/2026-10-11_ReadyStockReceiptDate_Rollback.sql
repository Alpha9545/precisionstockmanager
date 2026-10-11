-- ============================================================================
-- 2026-10-11_ReadyStockReceiptDate_Rollback.sql -- undoes 2026-10-11_ReadyStockReceiptDate.sql.
-- REFUSES once any receipt date or reason is stored: that business history is never discarded.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52131, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52132, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52133, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION ReceiptDateUndo1011;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.InternalTransfers', 'ReceivedDate') IS NOT NULL
    BEGIN
        DECLARE @n INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.InternalTransfers WHERE ReceivedDate IS NOT NULL OR ReceiptDateReason IS NOT NULL',
            N'@n INT OUTPUT', @n = @n OUTPUT;
        IF @n > 0 THROW 52134, 'Receipt dates are stored -- refusing to roll back.', 1;
    END
    IF OBJECT_ID('dbo.CK_InternalTransfers_ReceivedDate', 'C') IS NOT NULL
        ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_ReceivedDate;
    IF COL_LENGTH('dbo.InternalTransfers', 'ReceiptDateReason') IS NOT NULL
        ALTER TABLE dbo.InternalTransfers DROP COLUMN ReceiptDateReason;
    IF COL_LENGTH('dbo.InternalTransfers', 'ReceivedDate') IS NOT NULL
        ALTER TABLE dbo.InternalTransfers DROP COLUMN ReceivedDate;
    PRINT '=== Rolled back to the 2026-10-10 state ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION ReceiptDateUndo1011;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION ReceiptDateUndo1011;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION ReceiptDateUndo1011;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
