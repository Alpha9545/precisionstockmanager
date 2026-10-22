using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Daily Operations Report (Management Dashboard, System Administrator only).
    // Reads every figure for a RANGE of India (Asia/Kolkata) calendar days in ONE
    // batched, fully parameterized command -- one round trip whether the page
    // shows one day or a two-month history (no per-day / per-employee queries).
    // Read-only: nothing here writes, and no snapshot table exists or is needed.
    //
    // India-day rules per column kind (the reason every figure lands on the right
    // day whatever the web or SQL server's own time zone is):
    //   DATE / business date stored at midnight  -> used as is
    //   SYSUTCDATETIME() timestamp                -> bounded by the UTC instants of the
    //                                                India midnights (@FromUtc/@ToUtc),
    //                                                bucketed by DATEADD(+330 min)
    //   dbo.Bookings.BookingDate (GETDATE(), SQL  -> shifted by SQL Server's own exact
    //   Server local time)                           offset, as BookingRepository does
    // Adding +05:30 to a midnight business date never changes its day, so columns
    // holding a mix of both kinds (dbo.PottedPlantBookings.BookingDate) are still exact.
    //
    // Full definitions and limitations: docs/DAILY_OPERATIONS_REPORT.md.
    public class DailyOperationsReportRepository
    {
        // Ready Stock ledger rows that move RESERVED quantity only; every other
        // row ('Confirmed', 'ReversalRemoval', 'Dispatch', 'Wastage', ...) moves
        // the physical balance (Quantity - DispatchedQuantity) by its Quantity --
        // see ReadyStockRepository.RecordTransactionAsync / RecordDispatchAsync.
        // Ledger rows that only move a HOLD (booking reservation, or plants in transit to a
        // Mother Plant Area) and never change Quantity: excluded from physical movements.
        private const string ReservationOnlyTypes = "('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')";

        private readonly DatabaseHelper _dbHelper;

        public DailyOperationsReportRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<DailyOperationsSourceData> GetAsync(DateTime fromIndia, DateTime toIndia)
        {
            var from = fromIndia.Date;
            var to = toIndia.Date;
            if (to < from) (from, to) = (to, from);
            var toExclusive = to.AddDays(1);

            var data = new DailyOperationsSourceData { From = from, To = to };

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var bedReady = await BedSowingSchema.ReadyAsync(conn);
            var shape = (bedReady, await PotBatchSchema.ReadyStockSourceAsync(conn), await WorkflowCorrectionsSchema.TransplantWasteAsync(conn));
            using var cmd = new SqlCommand(BatchSqlByShape.GetOrAdd(shape, k => BuildBatchSql(k.Item1, k.Item2, k.Item3)), conn);
            cmd.Parameters.Add("@FromDate", SqlDbType.Date).Value = from;
            cmd.Parameters.Add("@ToDateExclusive", SqlDbType.Date).Value = toExclusive;
            cmd.Parameters.Add("@FromUtc", SqlDbType.DateTime2).Value = AppClock.IndiaToUtc(from);
            cmd.Parameters.Add("@ToUtc", SqlDbType.DateTime2).Value = AppClock.IndiaToUtc(toExclusive);
            cmd.Parameters.Add("@IndiaOffsetMinutes", SqlDbType.Int).Value = AppClock.IndiaOffsetMinutes;

            using var r = await cmd.ExecuteReaderAsync();
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            decimal M(int i) => r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));
            DateTime? D(int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);

            async Task ReadFiguresAsync(List<DayFigure> into, bool hasKey, bool hasLines = false)
            {
                while (await r.ReadAsync())
                {
                    var c = hasKey ? 1 : 0;
                    into.Add(new DayFigure(r.GetDateTime(0).Date, hasKey ? S(1)?.Trim() : null,
                        r.GetInt32(c + 1), M(c + 2), hasLines ? r.GetInt32(c + 3) : 0));
                }
                await r.NextResultAsync();
            }

            await ReadFiguresAsync(data.Sowing, hasKey: true);
            await ReadFiguresAsync(data.SeedlingBookings, hasKey: false, hasLines: true);
            await ReadFiguresAsync(data.PottedBookings, hasKey: false);
            await ReadFiguresAsync(data.SeedlingDispatches, hasKey: false);
            await ReadFiguresAsync(data.PottedDispatches, hasKey: false);
            await ReadFiguresAsync(data.OutletSales, hasKey: false);
            await ReadFiguresAsync(data.Fertilizer, hasKey: true);
            await ReadFiguresAsync(data.Wastage, hasKey: false);
            await ReadFiguresAsync(data.SeedLotWastage, hasKey: true);

            while (await r.ReadAsync())
                data.PlantTypes.Add(new NamedId(r.GetInt32(0), ManagementDashboardCalculations.CleanName(S(1), $"Plant Type {r.GetInt32(0)}")));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.ReadyStockMovements.Add(new PlantTypeMovement(r.GetInt32(0), D(1)?.Date, M(3), S(2)![0]));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.ReadyStockCurrent.Add(new ReadyStockBalance(r.GetInt32(0), M(1), M(2), M(3)));
            await r.NextResultAsync();

            while (await r.ReadAsync())
                data.Employees.Add(new NamedId(r.GetInt32(0), ManagementDashboardCalculations.CleanName(S(1), S(2) ?? $"User {r.GetInt32(0)}")));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.Logins.Add(new EmployeeLogin(r.GetInt32(0), r.GetDateTime(1).Date, r.GetDateTime(2), r.GetInt32(3)));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.Activity.Add(new EmployeeActivityDay(r.GetInt32(0), r.GetDateTime(1).Date));
            await r.NextResultAsync();

            if (await r.ReadAsync())
            {
                data.Coverage = new DailyOperationsCoverage
                {
                    SowingFrom = D(0)?.Date,
                    SeedlingBookingsFrom = D(1)?.Date,
                    PottedBookingsFrom = D(2)?.Date,
                    SeedlingDispatchFrom = D(3)?.Date,
                    PottedDispatchFrom = D(4)?.Date,
                    OutletSalesFrom = D(5)?.Date,
                    FertilizerFrom = D(6)?.Date,
                    WastageFrom = D(7)?.Date,
                    ReadyStockFrom = D(8)?.Date,
                    LoginTrackingStartUtc = D(9),
                    ReadyStockUnreconciledBatches = r.GetInt32(10),
                };
            }
            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.RecordingLags.Add(new RecordingLag(r.GetString(0), r.GetDateTime(1).Date, r.GetDateTime(2).Date, M(3)));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.ReadyStockDatingIssues.Add(new ReadyStockDatingIssue(r.GetInt32(0), r.GetDateTime(1).Date, r.GetDateTime(2).Date, M(3)));
            return data;
        }

        // StockKind of a Ready Stock ledger row (Models/DailyOperationsReport.cs).
        private static string KindSql(string column) =>
            $"CASE WHEN {column} IN ('Reservation', 'ReservationRelease') THEN 'R' WHEN {column} IN ('TransferHold', 'TransferHoldRelease') THEN 'T' ELSE 'O' END";

        // India day of a SYSUTCDATETIME() column / of dbo.Bookings.BookingDate (GETDATE()).
        private static string UtcDay(string column) => $"CAST(DATEADD(MINUTE, @IndiaOffsetMinutes, {column}) AS date)";
        private static string ServerLocalDay(string column) => $"CAST(DATEADD(MINUTE, @IndiaOffsetMinutes - @ServerOffset, {column}) AS date)";

        // 2026-10-12: two variants -- with / without the Bed Sowing tables (BedSowingSchema).
        // one SQL text per combination of applied migrations (bed sowing; 2026-10-15 pots from Ready Stock; 2026-10-16
        // transplant waste), built once -- the Wastage section uses exactly the Wastage page's sources.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(bool, bool, bool), string> BatchSqlByShape = new();

        private static string BuildBatchSql(bool bedReady, bool potsFromReadyStock = false, bool transplantWaste = false) => $@"
DECLARE @ServerOffset INT = {ManagementDashboardRepository.ServerOffsetSql};
DECLARE @BookFrom DATETIME2 = DATEADD(MINUTE, @ServerOffset, @FromUtc), @BookTo DATETIME2 = DATEADD(MINUTE, @ServerOffset, @ToUtc);

-- 1. Sowing: every non-Cancelled dbo.SeedSowings row (Direct Seed Sowing AND
--    Cutting Sowing -- one row per sowing, so nothing is counted twice), by its
--    business SowingDate. Same rule as ManagementDashboardRepository.GetSeedSummaryAsync.
--    2026-10-12: a Bed Sowing counts once, on its own sowing date; the tray batches
--    transplanted from it are NOT sowings again (BedSowingSchema.SowingActivitySql).
SELECT CAST(SowingDate AS date), SourceType, COUNT(*), ISNULL(SUM(QuantitySown), 0)
FROM {BedSowingSchema.SowingActivitySql(bedReady)} s
WHERE SowingDate >= @FromDate AND SowingDate < @ToDateExclusive
GROUP BY CAST(SowingDate AS date), SourceType;

-- 2. Seedling booking ORDERS (Booking Number = ISNULL(OrderBookingId, Id), see
--    Services/BookingOrders.cs), dated by the order's FIRST line -- a variety line
--    added to an existing order later (booking revision) never re-counts that order
--    on the revision day. Every status counts (a booking request came in), as in
--    the existing Daily Report. Lines / quantity = that order's lines booked the
--    same day (a line added on a later day belongs to that later day's activity).
SELECT {ServerLocalDay("b.FirstBooked")}, COUNT(DISTINCT b.OrderId), CAST(ISNULL(SUM(b.Quantity), 0) AS DECIMAL(18,2)), COUNT(*)
FROM (SELECT ISNULL(OrderBookingId, Id) AS OrderId, BookingDate, Quantity,
             MIN(BookingDate) OVER (PARTITION BY ISNULL(OrderBookingId, Id)) AS FirstBooked
      FROM dbo.Bookings) b
WHERE b.FirstBooked >= @BookFrom AND b.FirstBooked < @BookTo
  AND {ServerLocalDay("b.BookingDate")} = {ServerLocalDay("b.FirstBooked")}
GROUP BY {ServerLocalDay("b.FirstBooked")};

-- 3. Potted plant bookings (one dbo.PottedPlantBookings row = one booking code), every status.
SELECT {UtcDay("BookingDate")}, COUNT(*), ISNULL(SUM(Quantity), 0)
FROM dbo.PottedPlantBookings
WHERE BookingDate >= @FromUtc AND BookingDate < @ToUtc
GROUP BY {UtcDay("BookingDate")};

-- 4. Seedling dispatches: dispatch EVENTS (not lines); quantity = sum of their lines.
SELECT d.DispatchDate, COUNT(*), ISNULL(SUM(l.Qty), 0)
FROM dbo.SeedlingDispatches d
OUTER APPLY (SELECT SUM(x.Quantity) AS Qty FROM dbo.SeedlingDispatchLines x WHERE x.SeedlingDispatchId = d.Id) l
WHERE d.Status = 'Completed' AND d.DispatchDate >= @FromDate AND d.DispatchDate < @ToDateExclusive
GROUP BY d.DispatchDate;

-- 5. Potted plant dispatches: Completed only (a cancelled dispatch is not a dispatch).
SELECT {UtcDay("DispatchDate")}, COUNT(*), ISNULL(SUM(Quantity), 0)
FROM dbo.Dispatches
WHERE Status = 'Completed' AND DispatchDate >= @FromUtc AND DispatchDate < @ToUtc
GROUP BY {UtcDay("DispatchDate")};

-- 6. Outlet sales: number of sales (dbo.OutletSales headers) by SaleDate.
SELECT SaleDate, COUNT(*), CAST(0 AS DECIMAL(18,2))
FROM dbo.OutletSales
WHERE SaleDate >= @FromDate AND SaleDate < @ToDateExclusive
GROUP BY SaleDate;

-- 7. Fertilizer usage by IssueDate, per unit of the issued stock (never summed across units).
SELECT fu.IssueDate, LTRIM(RTRIM(um.UnitName)), COUNT(*), ISNULL(SUM(fu.UsedQuantity), 0)
FROM dbo.FertilizerUsage fu
INNER JOIN dbo.FertilizerStock fs ON fs.StockId = fu.StockId
INNER JOIN dbo.UnitMaster um ON um.UnitId = fs.UnitId
WHERE fu.IssueDate >= @FromDate AND fu.IssueDate < @ToDateExclusive
GROUP BY fu.IssueDate, LTRIM(RTRIM(um.UnitName));

-- 8. Wastage: Data/WastageRepository.cs's own loss sources (each loss from exactly
--    one row), bucketed by India day. Seed-lot losses are in seed units, so they
--    are reported separately (9) rather than added to plants.
SELECT w.WDate, COUNT(*), ISNULL(SUM(w.Quantity), 0)
FROM ({WastageRepository.SourcesSql(indiaDays: true, bedReady, potsFromReadyStock, transplantWaste)}
) w
WHERE w.WDate >= @FromDate AND w.WDate < @ToDateExclusive AND w.ProductionType <> N'{WastageRepository.SeedLotProductionType}'
GROUP BY w.WDate;

-- 9. Seed-lot wastage, per Seed Stock unit.
SELECT {UtcDay("t.TransactionDate")}, LTRIM(RTRIM(ss.Unit)), COUNT(*), ISNULL(SUM(-t.Quantity), 0)
FROM dbo.SeedStockTransactions t
INNER JOIN dbo.SeedStock ss ON ss.Id = t.SeedStockId
WHERE t.TransactionType = 'Wastage' AND t.TransactionDate >= @FromUtc AND t.TransactionDate < @ToUtc
GROUP BY {UtcDay("t.TransactionDate")}, LTRIM(RTRIM(ss.Unit));

-- 10. Plant Types (the Ready Stock categories).
SELECT Id, Name FROM dbo.PlantTypes ORDER BY Id;

-- 11. Ready Stock movements per Plant Type and KIND -- O owned (every row except the holds),
--     R reserved ('Reservation*'), T in transit ('TransferHold*') -- one opening row (Date NULL =
--     everything before the range) plus one row per India day inside it. TransactionDate is the
--     business date where the operation has one (2026-10-10, Services/LedgerDates.cs).
SELECT ps.PlantTypeId,
       CASE WHEN t.TransactionDate < @FromUtc THEN NULL ELSE {UtcDay("t.TransactionDate")} END,
       {KindSql("t.TransactionType")},
       SUM(t.Quantity)
FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
WHERE t.TransactionDate < @ToUtc AND ps.PlantTypeId IS NOT NULL
GROUP BY ps.PlantTypeId, CASE WHEN t.TransactionDate < @FromUtc THEN NULL ELSE {UtcDay("t.TransactionDate")} END, {KindSql("t.TransactionType")};

-- 12. Current Ready Stock per Plant Type: owned (approved - dispatched), reserved, in transit.
SELECT ps.PlantTypeId, SUM(rs.Quantity - rs.DispatchedQuantity), SUM(rs.ReservedQuantity), SUM(rs.InTransitQuantity)
FROM dbo.ReadyStock rs
INNER JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
WHERE ps.PlantTypeId IS NOT NULL
GROUP BY ps.PlantTypeId;

-- 13. Employees: active users (dbo.IMSUsers.IsActive = 1), the existing Daily Report definition.
SELECT Id, Name, Username FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Name, Id;

-- 14. Successful logins per employee per India day (dbo.UserLoginHistory, one row per login).
SELECT UserId, {UtcDay("LoginAt")}, MIN(LoginAt), COUNT(*)
FROM dbo.UserLoginHistory
WHERE LoginAt >= @FromUtc AND LoginAt < @ToUtc
GROUP BY UserId, {UtcDay("LoginAt")};

-- 15. Qualifying recorded activity per employee per India day -- the approved source
--     list of UserLoginHistoryRepository.GetTodaysActivityAsync (stock ledgers' UserId,
--     seedling/potted bookings' BookedById, fertilizer issues' EnteredById). Ledger rows by
--     CreatedAt -- the day the employee RECORDED them (TransactionDate may be a business date).
SELECT DISTINCT a.UserId, a.D FROM (
    SELECT t.UserId, {UtcDay("t.CreatedAt")} AS D FROM dbo.SeedStockTransactions t WHERE t.CreatedAt >= @FromUtc AND t.CreatedAt < @ToUtc
    UNION ALL SELECT t.UserId, {UtcDay("t.CreatedAt")} FROM dbo.CuttingStockTransactions t WHERE t.CreatedAt >= @FromUtc AND t.CreatedAt < @ToUtc
    UNION ALL SELECT t.UserId, {UtcDay("t.CreatedAt")} FROM dbo.ReadyStockTransactions t WHERE t.CreatedAt >= @FromUtc AND t.CreatedAt < @ToUtc
    UNION ALL SELECT t.UserId, {UtcDay("t.CreatedAt")} FROM dbo.PottedPlantStockTransactions t WHERE t.CreatedAt >= @FromUtc AND t.CreatedAt < @ToUtc
    UNION ALL SELECT t.UserId, {UtcDay("t.CreatedAt")} FROM dbo.EmptyPotInventoryTransactions t WHERE t.CreatedAt >= @FromUtc AND t.CreatedAt < @ToUtc
    UNION ALL SELECT b.BookedById, {ServerLocalDay("b.BookingDate")} FROM dbo.Bookings b WHERE b.BookingDate >= @BookFrom AND b.BookingDate < @BookTo
    UNION ALL SELECT pb.BookedById, {UtcDay("pb.BookingDate")} FROM dbo.PottedPlantBookings pb WHERE pb.BookingDate >= @FromUtc AND pb.BookingDate < @ToUtc
    UNION ALL SELECT fu.EnteredById, fu.IssueDate FROM dbo.FertilizerUsage fu WHERE fu.IssueDate >= @FromDate AND fu.IssueDate < @ToDateExclusive
) a
WHERE a.UserId IS NOT NULL;

-- 16. Coverage: the first India day each source holds any record, the start of login
--     tracking, and how many Ready Stock batches the ledger does NOT fully explain.
SELECT
    (SELECT CAST(MIN(SowingDate) AS date) FROM dbo.SeedSowings),
    (SELECT {ServerLocalDay("MIN(BookingDate)")} FROM dbo.Bookings),
    (SELECT {UtcDay("MIN(BookingDate)")} FROM dbo.PottedPlantBookings),
    (SELECT MIN(DispatchDate) FROM dbo.SeedlingDispatches),
    (SELECT {UtcDay("MIN(DispatchDate)")} FROM dbo.Dispatches),
    (SELECT MIN(SaleDate) FROM dbo.OutletSales),
    (SELECT MIN(IssueDate) FROM dbo.FertilizerUsage),
    (SELECT {UtcDay("MIN(x.D)")} FROM (
        SELECT MIN(ConfirmationDate) AS D FROM dbo.ReadyConfirmations
        UNION ALL SELECT MIN(TransactionDate) FROM dbo.SeedStockTransactions
        UNION ALL SELECT MIN(TransactionDate) FROM dbo.CuttingStockTransactions
        UNION ALL SELECT MIN(TransactionDate) FROM dbo.ReadyStockTransactions
        UNION ALL SELECT MIN(TransactionDate) FROM dbo.PottedPlantStockTransactions
        UNION ALL SELECT MIN(ReadyDate) FROM dbo.PotProductionBatches) x),
    (SELECT {UtcDay("MIN(TransactionDate)")} FROM dbo.ReadyStockTransactions),
    (SELECT MIN(LoginAt) FROM dbo.UserLoginHistory),
    (SELECT COUNT(*)
     FROM dbo.ReadyStock rs
     LEFT JOIN (SELECT ReadyStockId,
                       SUM(CASE WHEN TransactionType NOT IN {ReservationOnlyTypes} THEN Quantity ELSE 0 END) AS Net,
                       SUM(CASE WHEN TransactionType IN ('Reservation', 'ReservationRelease') THEN Quantity ELSE 0 END) AS Reserved,
                       SUM(CASE WHEN TransactionType IN ('TransferHold', 'TransferHoldRelease') THEN Quantity ELSE 0 END) AS InTransit
                FROM dbo.ReadyStockTransactions
                GROUP BY ReadyStockId) l ON l.ReadyStockId = rs.Id
     WHERE rs.Quantity - rs.DispatchedQuantity <> ISNULL(l.Net, 0)
        OR rs.ReservedQuantity <> ISNULL(l.Reserved, 0)
        OR rs.InTransitQuantity <> ISNULL(l.InTransit, 0));

-- 17. Movements whose business date differs from the India day they were recorded on (rows
--     written before business dates were stored, 2026-10-10): business date or recorded day
--     inside the range. Reported in the notes; never changed.
SELECT x.What, x.BusinessDate, {UtcDay("x.Recorded")}, SUM(x.Plants)
FROM (
    SELECT N'seedling dispatches' AS What, d.DispatchDate AS BusinessDate, t.TransactionDate AS Recorded, -t.Quantity AS Plants
    FROM dbo.ReadyStockTransactions t JOIN dbo.SeedlingDispatches d ON t.ReferenceType = 'SeedlingDispatch' AND d.Id = t.ReferenceId
    WHERE t.TransactionType = 'Dispatch'
    UNION ALL SELECT N'outlet tray sales', s.SaleDate, t.TransactionDate, -t.Quantity
    FROM dbo.ReadyStockTransactions t JOIN dbo.OutletSaleItems i ON t.ReferenceType = 'OutletSaleItem' AND i.Id = t.ReferenceId
    JOIN dbo.OutletSales s ON s.Id = i.SaleId
    WHERE t.TransactionType = 'Dispatch'
    UNION ALL SELECT N'outlet tray wastage', w.WastageDate, t.TransactionDate, -t.Quantity
    FROM dbo.ReadyStockTransactions t JOIN dbo.OutletWastages w ON t.ReferenceType = 'OutletWastage' AND w.Id = t.ReferenceId
    WHERE t.TransactionType = 'Wastage'
    UNION ALL SELECT N'transplanting', tp.TransplantDate, t.TransactionDate, -t.Quantity
    FROM dbo.ReadyStockTransactions t JOIN dbo.ReadyStockTransplants tp ON t.ReferenceType = 'ReadyStockTransplant' AND tp.Id = t.ReferenceId
    WHERE t.TransactionType IN ('TransplantOut', 'TransplantLoss')
    UNION ALL SELECT N'transfers to Mother Plant Areas', it.TransferDate, t.TransactionDate, t.Quantity
    FROM dbo.ReadyStockTransactions t JOIN dbo.InternalTransfers it ON t.ReferenceType = 'InternalTransfer' AND it.Id = t.ReferenceId
    WHERE t.TransactionType = 'TransferHold'
) x
WHERE {UtcDay("x.Recorded")} <> x.BusinessDate
  AND ((x.BusinessDate >= @FromDate AND x.BusinessDate < @ToDateExclusive) OR (x.Recorded >= @FromUtc AND x.Recorded < @ToUtc))
GROUP BY x.What, x.BusinessDate, {UtcDay("x.Recorded")};

-- 18. (2026-10-11) Ledger rows dated on an India day BEFORE their batch first held stock (its first
--     owned increase: approval, transfer-in, transplant-in) -- e.g. a dispatch or transfer dated
--     before the batch was approved Ready. Those days of that Plant Type cannot be reliably
--     reconstructed; the report says so (never moves or corrects the rows).
WITH BatchStart AS (
    SELECT ReadyStockId, MIN(TransactionDate) AS StartUtc
    FROM dbo.ReadyStockTransactions
    WHERE Quantity > 0 AND TransactionType NOT IN {ReservationOnlyTypes}
    GROUP BY ReadyStockId)
SELECT ps.PlantTypeId, {UtcDay("t.TransactionDate")}, {UtcDay("b.StartUtc")}, SUM(ABS(t.Quantity))
FROM dbo.ReadyStockTransactions t
JOIN BatchStart b ON b.ReadyStockId = t.ReadyStockId
JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId
JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
WHERE ps.PlantTypeId IS NOT NULL
  AND {UtcDay("t.TransactionDate")} < {UtcDay("b.StartUtc")}
  AND {UtcDay("t.TransactionDate")} < @ToDateExclusive AND {UtcDay("b.StartUtc")} > @FromDate
GROUP BY ps.PlantTypeId, {UtcDay("t.TransactionDate")}, {UtcDay("b.StartUtc")};";
    }
}
