-- ============================================================================
-- 2026-10-05_BookingOrderLink_Rollback.sql   (ROLLBACK ONLY -- normally never run)
--
-- WHY: undoes 2026-10-05_BookingOrderLink_Schema.sql (and with it the
-- backfill and every order link made since), for the case where the booking
-- ORDER release has to be withdrawn. Deploy the previous application build
-- FIRST (it does not use OrderBookingId); the order-aware build REQUIRES the
-- column and must not be running when this script commits.
--
-- WHAT THIS DOES: drops IX_Bookings_OrderBookingId, CK_Bookings_OrderBookingId,
-- FK_Bookings_OrderBooking and the dbo.Bookings.OrderBookingId column.
-- Every booking row stays, with all its other values: multi-variety orders
-- simply show as separate bookings again, exactly as before the release.
-- The removed links are listed first (keep that output) so they could be
-- re-applied.
--
-- NOT changed: any Booking Id or other column, ReadyStock, allocations,
-- dispatches, revisions, transactions, any other table.
--
-- SAFETY: one transaction; idempotent (re-run = no-op).
--   * database must be PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy, AND
--   * -v TARGET_SERVER=<@@SERVERNAME> must match.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -W -s"|" -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
--       then MODE=COMMIT
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52201, 'BookingOrderLink_Rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52202, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52203, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @RowsBefore INT = (SELECT COUNT(*) FROM dbo.Bookings);

BEGIN TRANSACTION BookingOrderLinkRollback;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF COL_LENGTH('dbo.Bookings', 'OrderBookingId') IS NULL
        PRINT 'OrderBookingId column does not exist -- nothing to roll back.';
    ELSE
    BEGIN
        PRINT '=== Order links that will be removed (keep this output) ===';
        EXEC (N'SELECT OrderBookingId, COUNT(*) AS Lines, STRING_AGG(CAST(Id AS VARCHAR(20)), '','') WITHIN GROUP (ORDER BY Id) AS BookingIds
                FROM dbo.Bookings WHERE OrderBookingId IS NOT NULL GROUP BY OrderBookingId HAVING COUNT(*) > 1 ORDER BY OrderBookingId;');
        EXEC (N'SELECT COUNT(*) AS LinkedRows, COUNT(DISTINCT OrderBookingId) AS Orders FROM dbo.Bookings WHERE OrderBookingId IS NOT NULL;');
    END

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Bookings') AND name = N'IX_Bookings_OrderBookingId')
    BEGIN EXEC (N'DROP INDEX IX_Bookings_OrderBookingId ON dbo.Bookings;'); PRINT 'IX_Bookings_OrderBookingId dropped.'; END
    IF OBJECT_ID('dbo.CK_Bookings_OrderBookingId', 'C') IS NOT NULL
    BEGIN EXEC (N'ALTER TABLE dbo.Bookings DROP CONSTRAINT CK_Bookings_OrderBookingId;'); PRINT 'CK_Bookings_OrderBookingId dropped.'; END
    IF OBJECT_ID('dbo.FK_Bookings_OrderBooking', 'F') IS NOT NULL
    BEGIN EXEC (N'ALTER TABLE dbo.Bookings DROP CONSTRAINT FK_Bookings_OrderBooking;'); PRINT 'FK_Bookings_OrderBooking dropped.'; END
    IF COL_LENGTH('dbo.Bookings', 'OrderBookingId') IS NOT NULL
    BEGIN EXEC (N'ALTER TABLE dbo.Bookings DROP COLUMN OrderBookingId;'); PRINT 'OrderBookingId column dropped.'; END

    IF (SELECT COUNT(*) FROM dbo.Bookings) <> @RowsBefore
        THROW 52204, 'Verification failed: dbo.Bookings rows changed.', 1;
    IF COL_LENGTH('dbo.Bookings', 'OrderBookingId') IS NOT NULL
        THROW 52205, 'Verification failed: OrderBookingId still exists.', 1;
    PRINT '=== VERIFIED: ' + CAST(@RowsBefore AS VARCHAR(10)) + ' booking rows unchanged; order link removed ===';

    IF N'$(MODE)' = N'COMMIT'
    BEGIN
        COMMIT TRANSACTION BookingOrderLinkRollback;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION BookingOrderLinkRollback;
        PRINT '=== DRYRUN -- rolled back, nothing changed ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
