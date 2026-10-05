-- ============================================================================
-- 2026-10-05_BookingOrderLink_Backfill.sql
--
-- WHY: historical "Add Another Species" orders were saved as unlinked
-- dbo.Bookings rows (one per variety, submitted together by the Book page).
-- This links ONLY the confidently identified ones through OrderBookingId,
-- so they display as one booking/order. Requires
-- 2026-10-05_BookingOrderLink_Schema.sql.
--
-- RULE (from the 2026-10-05 audit). Rows that are still unlinked
-- (OrderBookingId IS NULL, ParentBookingId IS NULL) are candidates for the
-- same order when they have the SAME
--     CustomerName, Contact, Address, AddedBy, BookedById
-- and each row was created (BookingDate) within 5 seconds of the previous
-- one of that identity. The order number is the group's lowest Id.
--
-- A candidate group is NOT linked (reported as AMBIGUOUS, left unchanged) if:
--   SPAN            first to last row more than 5 s apart
--   NEAR            a same-identity row was created 5-30 s before/after it
--   MULTI_ADVANCE   more than one row carries an advance (the Book page puts
--                   the advance on one row per submission)
--   MIXED_LOCATION  State / District / BookedByOther differ inside the group
--   INTERLEAVED     its Ids are not contiguous (another booking in between)
--   DUPLICATE_LINE  the same variety + quantity + delivery date appears twice
--                   (looks like a double-submitted form, not one order)
-- Same-identity single rows 5-30 s apart are reported as BORDERLINE and are
-- never linked. A customer name alone never links anything.
--
-- Revise -> "Add variety" split rows (ParentBookingId set) are linked to
-- their parent's order (an explicit link, not a guess).
--
-- WHAT CHANGES (COMMIT only): dbo.Bookings.OrderBookingId on the linked rows.
-- NOTHING ELSE: no Id, quantity, status, delivery date, customer field,
-- reservation, allocation, dispatch, revision or transaction is touched --
-- verified with a checksum of every other column.
--
-- SAFETY: one transaction; idempotent (only rows with OrderBookingId IS NULL
-- are considered; a re-run links nothing new). Both modes apply and verify the
-- links inside the transaction; DRYRUN then rolls back.
--   * database must be PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy, AND
--   * -v TARGET_SERVER=<@@SERVERNAME> must match (the live database is also
--     named PlantsIMS2_Test).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -W -s"|" -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52101, 'BookingOrderLink_Backfill may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52102, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52103, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF COL_LENGTH('dbo.Bookings', 'OrderBookingId') IS NULL
    THROW 52104, 'dbo.Bookings.OrderBookingId does not exist -- run 2026-10-05_BookingOrderLink_Schema.sql first.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @WindowMs BIGINT = 5000, @NearMs BIGINT = 30000;

-- Checksum of every column except OrderBookingId: must be identical afterwards.
DECLARE @ChecksumBefore INT = (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(
    Id, SpeciesId, Quantity, CustomerName, BookingDate, Status, PlantId, DeliveryDate, AddedBy, Address, Contact, ReturnedQuantity,
    ActualDeliveryDate, AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails, BookedById, BookedByOther, StateId, DistrictId,
    ReservedQuantity, DispatchedQuantity, FulfilmentSource, RevisionNo, ParentBookingId, CancelledById, CancelledDate,
    CancellationReason, ModifiedDate, ModifiedBy)) FROM dbo.Bookings);
DECLARE @RowsBefore INT = (SELECT COUNT(*) FROM dbo.Bookings);
DECLARE @LinkedBefore INT = (SELECT COUNT(*) FROM dbo.Bookings WHERE OrderBookingId IS NOT NULL);

BEGIN TRANSACTION BookingOrderLinkBackfill;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    -- 1) Candidate rows, grouped by identity + 5 s chain.
    IF OBJECT_ID('tempdb..#k0') IS NOT NULL DROP TABLE #k0;
    IF OBJECT_ID('tempdb..#k') IS NOT NULL DROP TABLE #k;
    WITH c AS (
        SELECT b.Id, b.BookingDate, b.AdvanceTaken, b.StateId, b.DistrictId, b.BookedByOther, b.SpeciesId, b.Quantity, b.DeliveryDate,
               IdentityKey = CONCAT(ISNULL(b.CustomerName, N''), NCHAR(31), ISNULL(b.Contact, N''), NCHAR(31), ISNULL(b.Address, N''),
                                    NCHAR(31), ISNULL(b.AddedBy, N''), NCHAR(31), ISNULL(CAST(b.BookedById AS NVARCHAR(20)), N''))
        FROM dbo.Bookings b
        WHERE b.OrderBookingId IS NULL AND b.ParentBookingId IS NULL AND b.BookingDate IS NOT NULL),
    l AS (
        SELECT c.*,
               PrevGapMs = ABS(DATEDIFF_BIG(MILLISECOND, LAG(c.BookingDate) OVER (PARTITION BY c.IdentityKey ORDER BY c.Id), c.BookingDate)),
               NextGapMs = ABS(DATEDIFF_BIG(MILLISECOND, c.BookingDate, LEAD(c.BookingDate) OVER (PARTITION BY c.IdentityKey ORDER BY c.Id)))
        FROM c),
    s AS (
        SELECT l.*, NewGroup = CASE WHEN l.PrevGapMs IS NOT NULL AND l.PrevGapMs <= @WindowMs THEN 0 ELSE 1 END FROM l)
    SELECT s.*, GroupId = MAX(CASE WHEN s.NewGroup = 1 THEN s.Id END) OVER (PARTITION BY s.IdentityKey ORDER BY s.Id ROWS UNBOUNDED PRECEDING)
    INTO #k0 FROM s;
    SELECT k0.*, GroupEndId = MAX(k0.Id) OVER (PARTITION BY k0.GroupId) INTO #k FROM #k0 k0;

    -- 2) Group verdicts.
    IF OBJECT_ID('tempdb..#g') IS NOT NULL DROP TABLE #g;
    SELECT g.GroupId, g.Lines, g.MinId, g.MaxId,
           Reasons = CONCAT_WS(N', ',
               CASE WHEN g.SpanMs > @WindowMs THEN N'SPAN' END,
               CASE WHEN (g.StartPrevGapMs > @WindowMs AND g.StartPrevGapMs <= @NearMs) OR (g.EndNextGapMs > @WindowMs AND g.EndNextGapMs <= @NearMs) THEN N'NEAR' END,
               CASE WHEN g.AdvLines > 1 THEN N'MULTI_ADVANCE' END,
               CASE WHEN g.States > 1 OR g.Districts > 1 OR g.Others > 1 THEN N'MIXED_LOCATION' END,
               CASE WHEN g.MaxId - g.MinId + 1 <> g.Lines THEN N'INTERLEAVED' END,
               CASE WHEN EXISTS (SELECT 1 FROM #k d WHERE d.GroupId = g.GroupId
                                 GROUP BY d.SpeciesId, d.Quantity, d.DeliveryDate HAVING COUNT(*) > 1) THEN N'DUPLICATE_LINE' END)
    INTO #g
    FROM (
        SELECT k.GroupId, COUNT(*) AS Lines, MIN(k.Id) AS MinId, MAX(k.Id) AS MaxId,
               DATEDIFF_BIG(MILLISECOND, MIN(k.BookingDate), MAX(k.BookingDate)) AS SpanMs,
               SUM(CASE WHEN k.AdvanceTaken = 1 THEN 1 ELSE 0 END) AS AdvLines,
               COUNT(DISTINCT ISNULL(k.StateId, 0)) AS States, COUNT(DISTINCT ISNULL(k.DistrictId, 0)) AS Districts,
               COUNT(DISTINCT ISNULL(k.BookedByOther, '')) AS Others,
               MAX(CASE WHEN k.Id = k.GroupId THEN k.PrevGapMs END) AS StartPrevGapMs,
               MAX(CASE WHEN k.Id = k.GroupEndId THEN k.NextGapMs END) AS EndNextGapMs
        FROM #k k GROUP BY k.GroupId) g;

    -- Confident multi-line orders (no ambiguity reason).
    IF OBJECT_ID('tempdb..#link') IS NOT NULL DROP TABLE #link;
    SELECT k.Id, k.GroupId AS OrderBookingId, CAST(N'ORDER' AS NVARCHAR(30)) AS LinkKind
    INTO #link
    FROM #k k INNER JOIN #g g ON g.GroupId = k.GroupId
    WHERE g.Lines >= 2 AND g.Reasons = N'';

    -- Revise -> Add variety splits: follow ParentBookingId to the root and use
    -- that root's (new or existing) order number.
    WITH chain AS (
        SELECT b.Id AS SplitId, b.ParentBookingId AS AncestorId, 1 AS Depth
        FROM dbo.Bookings b WHERE b.ParentBookingId IS NOT NULL AND b.OrderBookingId IS NULL
        UNION ALL
        SELECT ch.SplitId, p.ParentBookingId, ch.Depth + 1
        FROM chain ch INNER JOIN dbo.Bookings p ON p.Id = ch.AncestorId
        WHERE p.ParentBookingId IS NOT NULL AND ch.Depth < 20),
    root AS (SELECT SplitId, AncestorId AS RootId FROM (
                 SELECT ch.*, ROW_NUMBER() OVER (PARTITION BY ch.SplitId ORDER BY ch.Depth DESC) rn FROM chain ch) x WHERE rn = 1)
    INSERT INTO #link (Id, OrderBookingId, LinkKind)
    SELECT r.SplitId, COALESCE(rb.OrderBookingId, l.OrderBookingId, r.RootId), N'REVISION_SPLIT'
    FROM root r
    INNER JOIN dbo.Bookings rb ON rb.Id = r.RootId
    LEFT JOIN #link l ON l.Id = r.RootId;

    -- A split's root that is not already part of an order becomes a 2+ line
    -- order numbered by its own Id.
    INSERT INTO #link (Id, OrderBookingId, LinkKind)
    SELECT DISTINCT s.OrderBookingId, s.OrderBookingId, N'REVISION_SPLIT_ROOT'
    FROM #link s INNER JOIN dbo.Bookings b ON b.Id = s.OrderBookingId
    WHERE s.LinkKind = N'REVISION_SPLIT' AND b.OrderBookingId IS NULL
      AND NOT EXISTS (SELECT 1 FROM #link x WHERE x.Id = s.OrderBookingId);

    -- 3) Report. (Line breaks inside names are shown as spaces -- display only.)
    PRINT '=== SUMMARY ===';
    SELECT
        (SELECT COUNT(*) FROM dbo.Bookings) AS TotalBookingRows,
        @LinkedBefore AS AlreadyLinkedRows,
        (SELECT COUNT(*) FROM #g WHERE Lines >= 2) AS CandidateMultiLineGroups,
        (SELECT COUNT(DISTINCT OrderBookingId) FROM #link WHERE LinkKind = N'ORDER') AS ConfidentOrders,
        (SELECT COUNT(*) FROM #link WHERE LinkKind = N'ORDER') AS ConfidentOrderLines,
        (SELECT COUNT(*) FROM #g WHERE Lines >= 2 AND Reasons <> N'') AS AmbiguousGroupsLeftUnlinked,
        (SELECT ISNULL(SUM(Lines), 0) FROM #g WHERE Lines >= 2 AND Reasons <> N'') AS AmbiguousRowsLeftUnlinked,
        (SELECT COUNT(*) FROM #g g WHERE g.Lines = 1 AND EXISTS (SELECT 1 FROM #k k WHERE k.GroupId = g.GroupId
              AND ((k.PrevGapMs > @WindowMs AND k.PrevGapMs <= @NearMs) OR (k.NextGapMs > @WindowMs AND k.NextGapMs <= @NearMs)))) AS BorderlineSingleRowsLeftUnlinked,
        (SELECT COUNT(*) FROM #link WHERE LinkKind LIKE N'REVISION_SPLIT%') AS RevisionSplitRowsLinked,
        (SELECT COUNT(*) FROM #link) AS TotalRowsToLink;

    PRINT '=== AMBIGUOUS GROUPS BY REASON (left unlinked) ===';
    SELECT Reasons, COUNT(*) AS Groups, SUM(Lines) AS Rows_ FROM #g WHERE Lines >= 2 AND Reasons <> N'' GROUP BY Reasons ORDER BY Groups DESC;

    PRINT '=== CONFIDENT ORDERS: lines per order ===';
    SELECT Lines AS VarietyLines, COUNT(*) AS Orders FROM (SELECT OrderBookingId, COUNT(*) Lines FROM #link WHERE LinkKind = N'ORDER' GROUP BY OrderBookingId) x GROUP BY Lines ORDER BY Lines;

    PRINT '=== DETAIL: CONFIDENT ORDERS (one row per variety line) ===';
    SELECT l.OrderBookingId AS ProposedOrderBookingId, b.Id AS BookingId, LTRIM(RTRIM(REPLACE(REPLACE(b.CustomerName, NCHAR(13), N' '), NCHAR(10), N' '))) AS Customer,
           ISNULL(u.Name, ISNULL(b.BookedByOther, N'')) AS BookedBy, LTRIM(RTRIM(REPLACE(REPLACE(CONCAT(pt.Name, N' / ', ps.Name), NCHAR(13), N' '), NCHAR(10), N' '))) AS Variety, b.Quantity,
           ISNULL(b.Status, N'Pending') AS Status, CONVERT(VARCHAR(23), b.BookingDate, 121) AS BookingDateTime,
           CONVERT(VARCHAR(10), b.DeliveryDate, 120) AS DeliveryDate, b.ReservedQuantity, b.DispatchedQuantity,
           CASE WHEN b.AdvanceTaken = 1 THEN b.AdvanceTakenAmount END AS Advance
    FROM #link l INNER JOIN dbo.Bookings b ON b.Id = l.Id
    LEFT JOIN dbo.IMSUsers u ON u.Id = b.BookedById
    LEFT JOIN dbo.PlantTypes pt ON pt.Id = b.PlantId LEFT JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
    WHERE l.LinkKind = N'ORDER'
    ORDER BY l.OrderBookingId, b.Id;

    PRINT '=== DETAIL: AMBIGUOUS GROUPS (NOT linked) ===';
    SELECT g.GroupId AS CandidateGroup, g.Reasons, b.Id AS BookingId, LTRIM(RTRIM(REPLACE(REPLACE(b.CustomerName, NCHAR(13), N' '), NCHAR(10), N' '))) AS Customer,
           ISNULL(u.Name, ISNULL(b.BookedByOther, N'')) AS BookedBy, LTRIM(RTRIM(REPLACE(REPLACE(CONCAT(pt.Name, N' / ', ps.Name), NCHAR(13), N' '), NCHAR(10), N' '))) AS Variety, b.Quantity,
           ISNULL(b.Status, N'Pending') AS Status, CONVERT(VARCHAR(23), b.BookingDate, 121) AS BookingDateTime,
           CONVERT(VARCHAR(10), b.DeliveryDate, 120) AS DeliveryDate, CASE WHEN b.AdvanceTaken = 1 THEN b.AdvanceTakenAmount END AS Advance
    FROM #g g INNER JOIN #k k ON k.GroupId = g.GroupId INNER JOIN dbo.Bookings b ON b.Id = k.Id
    LEFT JOIN dbo.IMSUsers u ON u.Id = b.BookedById
    LEFT JOIN dbo.PlantTypes pt ON pt.Id = b.PlantId LEFT JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
    WHERE g.Lines >= 2 AND g.Reasons <> N''
    ORDER BY g.GroupId, b.Id;

    PRINT '=== DETAIL: BORDERLINE SINGLE ROWS (same identity 5-30 s apart, NOT linked) ===';
    SELECT k.Id AS BookingId, LTRIM(RTRIM(REPLACE(REPLACE(b.CustomerName, NCHAR(13), N' '), NCHAR(10), N' '))) AS Customer, ISNULL(u.Name, N'') AS BookedBy, LTRIM(RTRIM(REPLACE(REPLACE(CONCAT(pt.Name, N' / ', ps.Name), NCHAR(13), N' '), NCHAR(10), N' '))) AS Variety,
           b.Quantity, CONVERT(VARCHAR(23), b.BookingDate, 121) AS BookingDateTime, k.PrevGapMs, k.NextGapMs
    FROM #g g INNER JOIN #k k ON k.GroupId = g.GroupId INNER JOIN dbo.Bookings b ON b.Id = k.Id
    LEFT JOIN dbo.IMSUsers u ON u.Id = b.BookedById
    LEFT JOIN dbo.PlantTypes pt ON pt.Id = b.PlantId LEFT JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
    WHERE g.Lines = 1 AND ((k.PrevGapMs > @WindowMs AND k.PrevGapMs <= @NearMs) OR (k.NextGapMs > @WindowMs AND k.NextGapMs <= @NearMs))
    ORDER BY k.IdentityKey, k.Id;

    PRINT '=== DETAIL: REVISION SPLIT LINKS ===';
    SELECT l.OrderBookingId AS ProposedOrderBookingId, l.Id AS BookingId, l.LinkKind, b.ParentBookingId, LTRIM(RTRIM(REPLACE(REPLACE(b.CustomerName, NCHAR(13), N' '), NCHAR(10), N' '))) AS Customer,
           LTRIM(RTRIM(REPLACE(REPLACE(CONCAT(pt.Name, N' / ', ps.Name), NCHAR(13), N' '), NCHAR(10), N' '))) AS Variety, b.Quantity, ISNULL(b.Status, N'Pending') AS Status
    FROM #link l INNER JOIN dbo.Bookings b ON b.Id = l.Id
    LEFT JOIN dbo.PlantTypes pt ON pt.Id = b.PlantId LEFT JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
    WHERE l.LinkKind LIKE N'REVISION_SPLIT%'
    ORDER BY l.OrderBookingId, l.Id;

    -- 4) Apply inside the transaction (both modes), then verify.
    IF EXISTS (SELECT Id FROM #link GROUP BY Id HAVING COUNT(*) > 1)
        THROW 52105, 'A row was proposed for two orders -- nothing changed.', 1;

    UPDATE b SET b.OrderBookingId = l.OrderBookingId
    FROM dbo.Bookings b INNER JOIN #link l ON l.Id = b.Id
    WHERE b.OrderBookingId IS NULL;
    DECLARE @Updated INT = @@ROWCOUNT;

    IF @Updated <> (SELECT COUNT(*) FROM #link)
        THROW 52106, 'Verification failed: not every proposed row was linked.', 1;
    IF (SELECT COUNT(*) FROM dbo.Bookings) <> @RowsBefore
        THROW 52107, 'Verification failed: row count changed.', 1;
    IF (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(
            Id, SpeciesId, Quantity, CustomerName, BookingDate, Status, PlantId, DeliveryDate, AddedBy, Address, Contact, ReturnedQuantity,
            ActualDeliveryDate, AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails, BookedById, BookedByOther, StateId, DistrictId,
            ReservedQuantity, DispatchedQuantity, FulfilmentSource, RevisionNo, ParentBookingId, CancelledById, CancelledDate,
            CancellationReason, ModifiedDate, ModifiedBy)) FROM dbo.Bookings) <> @ChecksumBefore
        THROW 52108, 'Verification failed: a column other than OrderBookingId changed.', 1;
    -- every order number is a root that points to itself
    IF EXISTS (SELECT 1 FROM dbo.Bookings b INNER JOIN dbo.Bookings r ON r.Id = b.OrderBookingId WHERE ISNULL(r.OrderBookingId, -1) <> r.Id)
        THROW 52109, 'Verification failed: an order number is not its own root.', 1;
    -- every linked order has 2+ lines and one Booked By / customer identity
    IF EXISTS (SELECT 1 FROM (SELECT DISTINCT OrderBookingId FROM #link) o
               WHERE (SELECT COUNT(*) FROM dbo.Bookings b WHERE b.OrderBookingId = o.OrderBookingId) < 2)
        THROW 52110, 'Verification failed: a newly linked order has only one line.', 1;
    IF EXISTS (SELECT 1 FROM dbo.Bookings b INNER JOIN #link l ON l.Id = b.Id AND l.LinkKind = N'ORDER'
               GROUP BY b.OrderBookingId
               HAVING COUNT(DISTINCT ISNULL(b.BookedById, 0)) > 1 OR COUNT(DISTINCT ISNULL(b.CustomerName, N'')) > 1
                   OR COUNT(DISTINCT ISNULL(b.Contact, N'')) > 1)
        THROW 52111, 'Verification failed: an order mixes customers or Booked By.', 1;

    PRINT '=== VERIFIED: ' + CAST(@Updated AS VARCHAR(10)) + ' rows linked; no other column or row changed ===';

    IF N'$(MODE)' = N'COMMIT'
    BEGIN
        COMMIT TRANSACTION BookingOrderLinkBackfill;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION BookingOrderLinkBackfill;
        PRINT '=== DRYRUN -- rolled back, nothing changed ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
