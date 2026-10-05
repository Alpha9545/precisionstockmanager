-- ============================================================================
-- 2026-10-05_BookingOrderLink_Schema.sql
--
-- WHY: a seedling customer order with several varieties ("Add Another
-- Species") is stored as one dbo.Bookings row per variety, with nothing
-- linking the rows. This adds the link -- and nothing else -- so the
-- application can show ONE booking/order with its variety lines while every
-- line keeps its own quantities, status, reservations, allocations and
-- dispatches.
--
-- WHAT THIS DOES (schema only, no data is changed):
--   dbo.Bookings.OrderBookingId      INT NULL (only if missing)
--                                    = the order's first line Id (the Booking
--                                      Number). NULL = a single-line order whose
--                                      order number is its own Id
--                                      (EffectiveOrderId = ISNULL(OrderBookingId, Id)).
--   FK_Bookings_OrderBooking          OrderBookingId -> dbo.Bookings(Id)
--   CK_Bookings_OrderBookingId        OrderBookingId IS NULL OR OrderBookingId <= Id
--                                    (an order is numbered by its FIRST line)
--   IX_Bookings_OrderBookingId        on OrderBookingId (a plain index: a filtered one would
--                                    make every INSERT/UPDATE on dbo.Bookings require
--                                    SET QUOTED_IDENTIFIER ON, e.g. from sqlcmd)
--
-- NOT changed: any Booking Id, any existing row or value, ReadyStock,
-- allocations, dispatches, revisions, transactions, any other table.
-- Historical orders are linked separately and only after review:
-- 2026-10-05_BookingOrderLink_Backfill.sql.
--
-- SAFETY: one transaction; idempotent (re-run = no-op).
--   * database must be PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy, AND
--   * the server must be named explicitly with -v TARGET_SERVER=<@@SERVERNAME>
--     (the live database is ALSO named PlantsIMS2_Test, so the database name
--     alone cannot tell local from live).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
--       then MODE=COMMIT
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52001, 'BookingOrderLink_Schema may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52002, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52003, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF OBJECT_ID('dbo.Bookings', 'U') IS NULL
    THROW 52004, 'dbo.Bookings does not exist.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @RowsBefore INT = (SELECT COUNT(*) FROM dbo.Bookings);
DECLARE @MaxIdBefore INT = (SELECT MAX(Id) FROM dbo.Bookings);

BEGIN TRANSACTION BookingOrderLinkSchema;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF COL_LENGTH('dbo.Bookings', 'OrderBookingId') IS NULL
    BEGIN
        ALTER TABLE dbo.Bookings ADD OrderBookingId INT NULL;
        PRINT 'OrderBookingId column added.';
    END
    ELSE PRINT 'OrderBookingId column already exists -- no-op.';

    -- (dynamic SQL: the column may have been added in this same batch)
    IF OBJECT_ID('dbo.FK_Bookings_OrderBooking', 'F') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.Bookings WITH CHECK ADD CONSTRAINT FK_Bookings_OrderBooking FOREIGN KEY (OrderBookingId) REFERENCES dbo.Bookings (Id);');
        PRINT 'FK_Bookings_OrderBooking added.';
    END
    ELSE PRINT 'FK_Bookings_OrderBooking already exists -- no-op.';

    IF OBJECT_ID('dbo.CK_Bookings_OrderBookingId', 'C') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.Bookings WITH CHECK ADD CONSTRAINT CK_Bookings_OrderBookingId CHECK (OrderBookingId IS NULL OR OrderBookingId <= Id);');
        PRINT 'CK_Bookings_OrderBookingId added.';
    END
    ELSE PRINT 'CK_Bookings_OrderBookingId already exists -- no-op.';

    -- (an earlier draft created it filtered; replace that with the plain index)
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Bookings') AND name = N'IX_Bookings_OrderBookingId' AND has_filter = 1)
    BEGIN
        EXEC (N'DROP INDEX IX_Bookings_OrderBookingId ON dbo.Bookings;');
        PRINT 'Filtered IX_Bookings_OrderBookingId dropped (replaced below).';
    END
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Bookings') AND name = N'IX_Bookings_OrderBookingId')
    BEGIN
        EXEC (N'CREATE NONCLUSTERED INDEX IX_Bookings_OrderBookingId ON dbo.Bookings (OrderBookingId);');
        PRINT 'IX_Bookings_OrderBookingId added.';
    END
    ELSE PRINT 'IX_Bookings_OrderBookingId already exists -- no-op.';

    -- Verification: nothing but the schema changed.
    IF (SELECT COUNT(*) FROM dbo.Bookings) <> @RowsBefore OR (SELECT MAX(Id) FROM dbo.Bookings) <> @MaxIdBefore
        THROW 52005, 'Verification failed: dbo.Bookings rows changed.', 1;
    EXEC (N'IF EXISTS (SELECT 1 FROM dbo.Bookings b WHERE b.OrderBookingId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.Bookings r WHERE r.Id = b.OrderBookingId))
                THROW 52006, ''Verification failed: OrderBookingId points to a missing booking.'', 1;');

    PRINT '=== After ===';
    SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType, c.is_nullable AS IsNullable
    FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.Bookings') AND c.name = N'OrderBookingId';
    SELECT name AS ConstraintOrIndex FROM sys.objects WHERE name IN (N'FK_Bookings_OrderBooking', N'CK_Bookings_OrderBookingId')
    UNION ALL SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Bookings') AND name = N'IX_Bookings_OrderBookingId';
    SELECT @RowsBefore AS BookingRows, @MaxIdBefore AS MaxBookingId;

    IF N'$(MODE)' = N'COMMIT'
    BEGIN
        COMMIT TRANSACTION BookingOrderLinkSchema;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION BookingOrderLinkSchema;
        PRINT '=== DRYRUN -- rolled back, nothing changed ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
