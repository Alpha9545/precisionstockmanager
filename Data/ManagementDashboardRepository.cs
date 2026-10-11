using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 26 (Phase L): the Management Dashboard's dedicated reporting
    // repository. EVERY method here is READ-ONLY -- nothing in this
    // class ever opens a transaction or writes a row. Every query reads
    // from an existing authoritative table/ledger (no new tables, no
    // duplicated stock columns -- see PROJECT_DOCUMENTATION.md
    // Decision 23 for the full per-KPI source mapping) and uses a
    // range predicate ("Date >= @FromDate AND Date < @ToDateExclusive")
    // rather than CAST(DateColumn AS DATE) = @Date wherever a WHERE
    // clause filters by date, per Step 5. A CAST does appear in a
    // handful of GROUP BY clauses below purely to bucket a
    // DATETIME2 column down to its calendar day for a trend chart --
    // that is a projection/aggregation concern, not a filter, and does
    // not block the index seek on the WHERE range predicate.
    //
    // Every aggregate here is exactly one query per section/metric
    // group (never one query per row, never a full-table LINQ scan like
    // the pre-existing Pages/Production/Dashboard/Index.cshtml.cs uses)
    // -- Step 6's "no N+1, no SELECT *, no duplicate queries for the
    // same metric." Where two sections would otherwise need the exact
    // same number (e.g. the current dbo.ReadyStock total, needed by
    // both Section A and Section B), it is computed once by a private
    // helper and reused, never queried twice.
    public class ManagementDashboardRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly SeedSowingRepository _seedSowingRepo;

        public ManagementDashboardRepository(DatabaseHelper dbHelper, SeedSowingRepository seedSowingRepo)
        {
            _dbHelper = dbHelper;
            _seedSowingRepo = seedSowingRepo;
        }

        // ------------------------------------------------------------
        // Section A: Overall Production Summary
        // ------------------------------------------------------------
        public async Task<ProductionSummary> GetProductionSummaryAsync(ManagementDashboardFilters filters)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var result = new ProductionSummary();

            // Mother Plants (Balance) -- unit: plants. No SQL-level
            // aggregate method exists on MotherPlantRepository (per
            // inspection), so this dedicated aggregate query is new,
            // not a duplicate of anything MotherPlantRepository already
            // offers.
            using (var cmd = new SqlCommand(@"
SELECT COUNT(*) AS Cnt, ISNULL(SUM(MotherPlantQuantity), 0) AS Qty
FROM dbo.MotherPlants
WHERE Status = 'Active'
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
            {
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.MotherPlantActiveCount = reader.GetInt32(0);
                    result.MotherPlantActiveQuantity = reader.GetDecimal(1);
                }
            }

            // Cutting Stock (Balance) -- unit: cuttings.
            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(PhysicalQuantity), 0), ISNULL(SUM(InTransitQuantity), 0)
FROM dbo.CuttingStock
WHERE (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
            {
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.CuttingStockPhysical = reader.GetDecimal(0);
                    result.CuttingStockInTransit = reader.GetDecimal(1);
                    result.CuttingStockAvailable = result.CuttingStockPhysical - result.CuttingStockInTransit;
                }
            }

            // Pot Production (Movement, within the selected date range) --
            // unit: pots produced (Phase D: daily entries of pot batches).
            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(e.Quantity), 0)
FROM dbo.PotProductionEntries e
INNER JOIN dbo.PotProductionBatches b ON b.Id = e.BatchId
WHERE e.ProductionDate >= @FromDate AND e.ProductionDate < @ToDateExclusive
  AND (@AreaId IS NULL OR b.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR b.SpeciesId = @SpeciesId)", conn))
            {
                AddDateRange(cmd, filters);
                AddAreaSpecies(cmd, filters);
                result.PotProductionQuantityInRange = (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
            }

            // Potted Plant Stock (Balance) -- unit: potted plants. Fully
            // instrumented (Physical/Reserved/InTransit all real
            // columns; Available = Physical - Reserved, mirroring
            // PottedPlantStock.AvailableQuantity's own C# formula
            // exactly -- never redefined to also subtract InTransit).
            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(PhysicalQuantity), 0), ISNULL(SUM(ReservedQuantity), 0), ISNULL(SUM(InTransitQuantity), 0)
FROM dbo.PottedPlantStock
WHERE (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)
  AND (@PotSize IS NULL OR PotSize = @PotSize)", conn))
            {
                AddAreaSpeciesPotSize(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.PottedPlantPhysical = reader.GetDecimal(0);
                    result.PottedPlantReserved = reader.GetDecimal(1);
                    result.PottedPlantInTransit = reader.GetDecimal(2);
                    result.PottedPlantAvailable = result.PottedPlantPhysical - result.PottedPlantReserved;
                }
            }

            // Ready Stock (Balance) -- unit: plants confirmed Ready
            // through Phase K only. Computed once, shared with Section B.
            (result.ReadyStockQuantity, result.ReadyStockReserved, result.ReadyStockInTransit) = await GetReadyStockTotalsAsync(conn, filters);

            // Seed Stock (Balance) -- grouped by Unit; Step 3's "do not
            // mix units" rule means these are never summed together.
            result.SeedStock = await GetSeedStockByUnitAsync(conn, filters, mainOfficeOnly: false);

            // Empty Pot Inventory (Balance) -- unit: pots. No
            // InTransit/Available column exists on this table (gap
            // identified during inspection) -- only Physical is real.
            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(PhysicalQuantity), 0)
FROM dbo.EmptyPotInventory
WHERE IsActive = 1
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@PotSize IS NULL OR PotSize = @PotSize)", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PotSize", (object?)filters.PotSize ?? DBNull.Value);
                result.EmptyPotPhysical = (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
            }

            return result;
        }

        // ------------------------------------------------------------
        // Section B: Ready Stock / Production Readiness. Sown/Confirmed/
        // Remaining deliberately ignore the date range -- an open
        // Sowing from 40 days ago that is still only partially
        // confirmed must still count today (mirrors
        // SeedSowingRepository.GetReadyForConfirmationAsync's own
        // "Status = 'Sown' AND ConfirmedReadyQuantity < QuantitySown"
        // filter, not a date filter). Overdue/ReadyToday/ReadySoon
        // reuse SeedSowingRepository.GetAlertCandidatesAsync +
        // ClassifyReadyAlert EXACTLY as Pages/Production/ReadyAlerts/
        // Index.cshtml.cs does -- never a second classification.
        // ------------------------------------------------------------
        // today: the business date to classify against. null keeps the original server
        // date (DateTime.Today) for existing callers such as the Daily Report; the
        // Management Dashboard passes the India date (AppClock.Today).
        public async Task<ReadyStockSummary> GetReadyStockSummaryAsync(ManagementDashboardFilters filters, DateTime? today = null)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var result = new ReadyStockSummary();

            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(QuantitySown), 0), ISNULL(SUM(ConfirmedReadyQuantity), 0), ISNULL(SUM(WastageQuantity), 0)
FROM dbo.SeedSowings
WHERE Status = 'Sown'
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
            {
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.SownQuantity = reader.GetDecimal(0);
                    result.ConfirmedReadyQuantity = reader.GetDecimal(1);
                    // Phase B: wastage recorded at approval is accounted for too.
                    result.RemainingQuantity = result.SownQuantity - result.ConfirmedReadyQuantity - reader.GetDecimal(2);
                }
            }

            var classifyDay = (today ?? DateTime.Today).Date;
            var horizon = classifyDay.AddDays(SeedSowingRepository.DefaultReadySoonWindowDays);
            var candidates = await _seedSowingRepo.GetAlertCandidatesAsync(horizon);
            var scoped = candidates
                .Where(s => !filters.AreaId.HasValue || s.AreaId == filters.AreaId.Value)
                .Where(s => !filters.SpeciesId.HasValue || s.SpeciesId == filters.SpeciesId.Value);

            foreach (var sowing in scoped)
            {
                var category = SeedSowingRepository.ClassifyReadyAlert(sowing.Status, sowing.ExpectedReadyDate, classifyDay, SeedSowingRepository.DefaultReadySoonWindowDays);
                switch (category)
                {
                    case "Overdue":
                        result.OverdueCount++;
                        result.OverdueQuantity += sowing.RemainingReadyQuantity;
                        break;
                    case "ReadyToday":
                        result.ReadyTodayCount++;
                        result.ReadyTodayQuantity += sowing.RemainingReadyQuantity;
                        break;
                    case "ReadySoon":
                        result.ReadySoonCount++;
                        result.ReadySoonQuantity += sowing.RemainingReadyQuantity;
                        break;
                }
            }

            result.ReadyStockQuantity = await GetReadyStockTotalAsync(conn, filters);
            result.ReadyStockQuantityByPlantType = await GetReadyStockByPlantTypeAsync(conn, filters);
            return result;
        }

        // ------------------------------------------------------------
        // Section C: Growing Partner Summary. Six GROUP BY AreaId
        // queries (never one query per Area/GrowingPartner row) merged
        // in C# onto the small set of Growing-Partner-owned Areas.
        // Species filtering is intentionally NOT applied here: several
        // of the underlying tables (SeedIssues, InternalTransfers) do
        // not carry SpeciesId directly without an extra join the spec's
        // own "identify the gap" instruction says to flag rather than
        // paper over -- this section is Area/GrowingPartner-scoped
        // only. Documented in Decision 23 and the Phase L report.
        // ------------------------------------------------------------
        public async Task<List<GrowingPartnerSummaryRow>> GetGrowingPartnerSummaryAsync(ManagementDashboardFilters filters)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var rows = new List<GrowingPartnerSummaryRow>();
            using (var cmd = new SqlCommand(@"
SELECT a.Id, a.Name, a.AreaType, a.GrowingPartnerId, gp.Name
FROM dbo.Area a
INNER JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
WHERE (@AreaId IS NULL OR a.Id = @AreaId)
  AND (@GrowingPartnerId IS NULL OR a.GrowingPartnerId = @GrowingPartnerId)
ORDER BY gp.Name, a.Name", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@GrowingPartnerId", (object?)filters.GrowingPartnerId ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add(new GrowingPartnerSummaryRow
                    {
                        AreaId = reader.GetInt32(0),
                        AreaName = reader.GetString(1),
                        AreaType = reader.IsDBNull(2) ? null : reader.GetString(2),
                        GrowingPartnerId = reader.GetInt32(3),
                        GrowingPartnerName = reader.GetString(4)
                    });
                }
            }
            if (rows.Count == 0)
                return rows;

            var starterMaterial = await SumByAreaAsync(conn, @"
SELECT DestinationAreaId, ISNULL(SUM(ConfirmedQuantity), 0)
FROM dbo.SeedIssues
WHERE Status = 'Completed' AND ConfirmedDate >= @FromDate AND ConfirmedDate < @ToDateExclusive
GROUP BY DestinationAreaId", filters);

            var cuttingReceived = await SumByAreaAsync(conn, @"
SELECT DestinationAreaId, ISNULL(SUM(ConfirmedQuantity), 0)
FROM dbo.InternalTransfers
WHERE StockType = 'Cutting' AND Status IN ('Completed', 'ConfirmedAwaitingTransplant', 'Transplanted')
  AND ConfirmedDate >= @FromDate AND ConfirmedDate < @ToDateExclusive
GROUP BY DestinationAreaId", filters);

            var potTrayProduction = await SumByAreaAsync(conn, @"
SELECT b.AreaId, ISNULL(SUM(e.Quantity), 0)
FROM dbo.PotProductionEntries e
INNER JOIN dbo.PotProductionBatches b ON b.Id = e.BatchId
WHERE e.ProductionDate >= @FromDate AND e.ProductionDate < @ToDateExclusive
GROUP BY b.AreaId", filters);

            var currentPottedStock = await SumByAreaAsync(conn, @"
SELECT AreaId, ISNULL(SUM(PhysicalQuantity), 0)
FROM dbo.PottedPlantStock
WHERE AreaId IS NOT NULL
GROUP BY AreaId", filters, noDateParams: true);

            var transfersToOutlet = await SumByAreaAsync(conn, @"
SELECT SourceAreaId, ISNULL(SUM(Quantity), 0)
FROM dbo.InternalTransfers
WHERE StockType = 'GrowingPartnerToOutlet' AND Status = 'Completed'
  AND ConfirmedDate >= @FromDate AND ConfirmedDate < @ToDateExclusive
GROUP BY SourceAreaId", filters);

            var pendingCounts = new Dictionary<int, int>();
            using (var cmd = new SqlCommand(@"
SELECT DestinationAreaId, COUNT(*) FROM dbo.SeedIssues WHERE Status = 'PendingConfirmation' GROUP BY DestinationAreaId
UNION ALL
SELECT DestinationAreaId, COUNT(*) FROM dbo.InternalTransfers WHERE StockType = 'MainOfficeIssue' AND Status = 'PendingConfirmation' GROUP BY DestinationAreaId", conn))
            {
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (reader.IsDBNull(0)) continue;
                    var areaId = reader.GetInt32(0);
                    var cnt = reader.GetInt32(1);
                    pendingCounts[areaId] = pendingCounts.TryGetValue(areaId, out var existing) ? existing + cnt : cnt;
                }
            }

            foreach (var row in rows)
            {
                row.StarterMaterialReceived = starterMaterial.GetValueOrDefault(row.AreaId);
                row.CuttingReceived = cuttingReceived.GetValueOrDefault(row.AreaId);
                row.PotTrayProduction = potTrayProduction.GetValueOrDefault(row.AreaId);
                row.CurrentPottedStock = currentPottedStock.GetValueOrDefault(row.AreaId);
                row.TransfersToOutlet = transfersToOutlet.GetValueOrDefault(row.AreaId);
                row.PendingConfirmations = pendingCounts.GetValueOrDefault(row.AreaId);
            }

            return rows;
        }

        // ------------------------------------------------------------
        // Section D: Outlet / Sales Summary. Booking != Dispatch: the
        // Booking-status counts/remaining quantity below are read
        // straight off dbo.PottedPlantBookings' own Status/
        // DispatchedQuantity columns (never inferred from Dispatches),
        // and CompletedDispatchQuantityInRange comes only from
        // dbo.Dispatches (never treated as if a Booking's own Quantity
        // were "dispatched").
        // ------------------------------------------------------------
        public async Task<OutletSalesSummary> GetOutletSalesSummaryAsync(ManagementDashboardFilters filters)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var result = new OutletSalesSummary();

            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(s.PhysicalQuantity), 0), ISNULL(SUM(s.PhysicalQuantity - s.ReservedQuantity), 0)
FROM dbo.PottedPlantStock s
INNER JOIN dbo.Area a ON s.AreaId = a.Id
WHERE a.AreaType = 'Outlet'
  AND (@AreaId IS NULL OR a.Id = @AreaId)
  AND (@SpeciesId IS NULL OR s.SpeciesId = @SpeciesId)
  AND (@PotSize IS NULL OR s.PotSize = @PotSize)", conn))
            {
                AddAreaSpeciesPotSize(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.OutletStockPhysical = reader.GetDecimal(0);
                    result.OutletStockAvailable = reader.GetDecimal(1);
                }
            }

            using (var cmd = new SqlCommand(@"
SELECT Status, COUNT(*), ISNULL(SUM(Quantity - DispatchedQuantity), 0)
FROM dbo.PottedPlantBookings
WHERE (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)
  AND (@PotSize IS NULL OR PotSize = @PotSize)
GROUP BY Status", conn))
            {
                AddAreaSpeciesPotSize(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var status = reader.GetString(0);
                    var count = reader.GetInt32(1);
                    var remaining = reader.GetDecimal(2);
                    switch (status)
                    {
                        case "Pending":
                            result.PendingBookings = count;
                            result.RemainingBookingQuantity += remaining;
                            break;
                        case "PartiallyDispatched":
                            result.PartiallyDispatchedBookings = count;
                            result.RemainingBookingQuantity += remaining;
                            break;
                        case "Dispatched":
                            result.DispatchedBookings = count;
                            break;
                        case "Cancelled":
                            result.CancelledBookings = count;
                            break;
                    }
                }
            }

            using (var cmd = new SqlCommand(@"
SELECT COUNT(*)
FROM dbo.PottedPlantBookings
WHERE BookingDate >= @FromDate AND BookingDate < @ToDateExclusive
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)
  AND (@PotSize IS NULL OR PotSize = @PotSize)", conn))
            {
                AddDateRange(cmd, filters);
                AddAreaSpeciesPotSize(cmd, filters);
                result.TotalBookingsInRange = (int)(await cmd.ExecuteScalarAsync() ?? 0);
            }

            using (var cmd = new SqlCommand(@"
SELECT COUNT(*), ISNULL(SUM(Quantity), 0)
FROM dbo.Dispatches
WHERE Status = 'Completed'
  AND DispatchDate >= @FromUtc AND DispatchDate < @ToUtcExclusive   -- DispatchDate is SYSUTCDATETIME()
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)
  AND (@PotSize IS NULL OR PotSize = @PotSize)", conn))
            {
                AddUtcDateRange(cmd, filters);
                AddAreaSpeciesPotSize(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.CompletedDispatchCountInRange = reader.GetInt32(0);
                    result.CompletedDispatchQuantityInRange = reader.GetDecimal(1);
                }
            }

            return result;
        }

        // ------------------------------------------------------------
        // Section E: Seed Summary. Uses only the current dbo.SeedStock/
        // SeedStockTransactions/SeedIssues/SeedSowings architecture --
        // the legacy SeedCuttingBank/Data pipeline is never touched.
        // ------------------------------------------------------------
        // allAreasSeedStock: the exact same (SpeciesId/AreaId-filtered)
        // per-Unit balance GetProductionSummaryAsync already computed
        // for Section A's "Seed Stock" card -- passed in and reused
        // here rather than re-issuing the identical GROUP BY query a
        // second time (Step 6's "no duplicate queries for the same
        // metric"). Only the Main-Office-only breakdown below is a
        // genuinely different query (a different WHERE, AreaType =
        // 'MainOffice').
        public async Task<SeedSummary> GetSeedSummaryAsync(ManagementDashboardFilters filters, List<UnitQuantity> allAreasSeedStock)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var result = new SeedSummary
            {
                MainOfficeSeedStock = await GetSeedStockByUnitAsync(conn, filters, mainOfficeOnly: true),
                SeedStockRemaining = allAreasSeedStock
            };

            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(IssuedQuantity), 0)
FROM dbo.SeedIssues
WHERE Status = 'Completed'
  AND IssueDate >= @FromDate AND IssueDate < @ToDateExclusive
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
            {
                AddDateRange(cmd, filters);
                cmd.Parameters.AddWithValue("@SpeciesId", (object?)filters.SpeciesId ?? DBNull.Value);
                result.SeedIssuedQuantityInRange = (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
            }

            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(ConfirmedQuantity), 0)
FROM dbo.SeedIssues
WHERE Status = 'Completed'
  AND ConfirmedDate >= @FromDate AND ConfirmedDate < @ToDateExclusive
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
            {
                AddDateRange(cmd, filters);
                cmd.Parameters.AddWithValue("@SpeciesId", (object?)filters.SpeciesId ?? DBNull.Value);
                result.SeedReceivedQuantityInRange = (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
            }

            using (var cmd = new SqlCommand(@"
SELECT COUNT(*), ISNULL(SUM(QuantitySown), 0)
FROM #SOWING# s
WHERE SowingDate >= @FromDate AND SowingDate < @ToDateExclusive
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)".Replace("#SOWING#", BedSowingSchema.SowingActivitySql(await BedSowingSchema.ReadyAsync(conn))), conn))
            {
                AddDateRange(cmd, filters);
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.SowingActivityCountInRange = reader.GetInt32(0);
                    result.SeedSownQuantityInRange = reader.GetDecimal(1);
                }
            }

            using (var cmd = new SqlCommand(@"
SELECT COUNT(*)
FROM dbo.SeedIssues
WHERE Status = 'PendingConfirmation'
  AND (@AreaId IS NULL OR DestinationAreaId = @AreaId)", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
                result.PendingSeedReceipts = (int)(await cmd.ExecuteScalarAsync() ?? 0);
            }

            return result;
        }

        // ------------------------------------------------------------
        // Section F: Cutting Summary. Respects the existing
        // PendingConfirmation/ConfirmedAwaitingTransplant/Transplanted/
        // Rejected workflow (Phase 15/16, Model B) -- never
        // reclassified.
        // ------------------------------------------------------------
        public async Task<CuttingSummary> GetCuttingSummaryAsync(ManagementDashboardFilters filters)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var result = new CuttingSummary();

            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(PhysicalQuantity), 0), ISNULL(SUM(InTransitQuantity), 0)
FROM dbo.CuttingStock
WHERE (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
            {
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.CuttingPhysical = reader.GetDecimal(0);
                    result.CuttingInTransit = reader.GetDecimal(1);
                    result.CuttingAvailable = result.CuttingPhysical - result.CuttingInTransit;
                }
            }

            using (var cmd = new SqlCommand(@"
SELECT Status, COUNT(*), ISNULL(SUM(Quantity), 0)
FROM dbo.InternalTransfers
WHERE StockType = 'Cutting' AND Status IN ('PendingConfirmation', 'ConfirmedAwaitingTransplant')
  AND (@AreaId IS NULL OR SourceAreaId = @AreaId OR DestinationAreaId = @AreaId OR PendingConfirmationAreaId = @AreaId)
GROUP BY Status", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var status = reader.GetString(0);
                    var count = reader.GetInt32(1);
                    var qty = reader.GetDecimal(2);
                    if (status == "PendingConfirmation")
                    {
                        result.PendingConfirmationCount = count;
                        result.PendingConfirmationQuantity = qty;
                    }
                    else if (status == "ConfirmedAwaitingTransplant")
                    {
                        result.ConfirmedAwaitingTransplantCount = count;
                        result.ConfirmedAwaitingTransplantQuantity = qty;
                    }
                }
            }

            using (var cmd = new SqlCommand(@"
SELECT COUNT(*), ISNULL(SUM(ConfirmedQuantity), 0)
FROM dbo.InternalTransfers
WHERE StockType = 'Cutting' AND Status = 'Transplanted'
  AND ConfirmedDate >= @FromUtc AND ConfirmedDate < @ToUtcExclusive   -- ConfirmedDate is SYSUTCDATETIME()
  AND (@AreaId IS NULL OR SourceAreaId = @AreaId OR DestinationAreaId = @AreaId)", conn))
            {
                AddUtcDateRange(cmd, filters);
                cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.TransplantedCountInRange = reader.GetInt32(0);
                    result.TransplantedQuantityInRange = reader.GetDecimal(1);
                }
            }

            using (var cmd = new SqlCommand(@"
SELECT TOP 15 t.TransactionDate, t.TransactionType, t.Quantity, a.Name, ps.Name
FROM dbo.CuttingStockTransactions t
INNER JOIN dbo.CuttingStock cs ON t.CuttingStockId = cs.Id
INNER JOIN dbo.Area a ON cs.AreaId = a.Id
INNER JOIN dbo.PlantSpecies ps ON cs.SpeciesId = ps.Id
WHERE t.TransactionDate >= @FromUtc AND t.TransactionDate < @ToUtcExclusive   -- TransactionDate is SYSUTCDATETIME()
  AND (@AreaId IS NULL OR cs.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR cs.SpeciesId = @SpeciesId)
ORDER BY t.TransactionDate DESC", conn))
            {
                AddUtcDateRange(cmd, filters);
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.RecentActivity.Add(new CuttingActivityItem
                    {
                        TransactionDate = AppClock.ToIndiaTime(reader.GetDateTime(0)),
                        TransactionType = reader.GetString(1),
                        Quantity = reader.GetDecimal(2),
                        AreaName = reader.IsDBNull(3) ? null : reader.GetString(3),
                        SpeciesName = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            return result;
        }

        // ------------------------------------------------------------
        // Section G: Inventory/Stock Health. Assembled from the
        // section results already computed above -- zero additional
        // queries (Step 6). Physical/Reserved/Available/InTransit are
        // left NULL ("N/A" in the UI) for exactly the domains where
        // inspection found no such column/concept, per the explicit
        // "do not invent these quantities" instruction:
        //   - EmptyPotInventory has no InTransit/Available/Reserved.
        //   - ReadyStock has only a single balance column (no Reserved/
        //     InTransit/Available concept at all).
        //   - CuttingStock/SeedStock have Physical/InTransit/Available
        //     but no Reserved (that concept exists only on
        //     PottedPlantStock, tied to Bookings).
        // ------------------------------------------------------------
        public List<StockHealthRow> BuildStockHealth(ProductionSummary production, CuttingSummary cutting, ReadyStockSummary readyStock, SeedSummary seed)
        {
            var rows = new List<StockHealthRow>
            {
                new StockHealthRow
                {
                    Domain = "Potted Plant Stock",
                    Unit = "plants",
                    Physical = production.PottedPlantPhysical,
                    Reserved = production.PottedPlantReserved,
                    Available = production.PottedPlantAvailable,
                    InTransit = production.PottedPlantInTransit
                },
                new StockHealthRow
                {
                    Domain = "Cutting Stock",
                    Unit = "cuttings",
                    Physical = cutting.CuttingPhysical,
                    Reserved = null, // no Reserved concept for this domain
                    Available = cutting.CuttingAvailable,
                    InTransit = cutting.CuttingInTransit
                },
                new StockHealthRow
                {
                    Domain = "Empty Pot Inventory",
                    Unit = "pots",
                    Physical = production.EmptyPotPhysical,
                    Reserved = null,
                    Available = null, // no InTransit/Available column exists
                    InTransit = null
                },
                new StockHealthRow
                {
                    // dbo.ReadyStock DOES carry ReservedQuantity (seedling booking
                    // reservations) and a computed AvailableQuantity = Quantity -
                    // ReservedQuantity - DispatchedQuantity; the earlier "single balance
                    // column" note was wrong and hid reserved seedlings. 2026-10-09: plants
                    // in transit to a Mother Plant Area are at neither location -- shown as
                    // In Transit, not as physical or available stock.
                    Domain = "Ready Stock (seedlings)",
                    Unit = "plants",
                    Physical = production.ReadyStockQuantity - production.ReadyStockInTransit,
                    Reserved = production.ReadyStockReserved,
                    Available = production.ReadyStockQuantity - production.ReadyStockInTransit - production.ReadyStockReserved,
                    InTransit = production.ReadyStockInTransit
                }
            };

            foreach (var seedUnit in seed.SeedStockRemaining)
            {
                rows.Add(new StockHealthRow
                {
                    Domain = $"Seed Stock ({seedUnit.Unit})",
                    Unit = seedUnit.Unit,
                    Physical = seedUnit.Physical,
                    Reserved = null, // no Reserved concept for this domain
                    Available = seedUnit.Available,
                    InTransit = seedUnit.InTransit
                });
            }

            return rows;
        }

        // ------------------------------------------------------------
        // Charts. Sown-vs-Ready is a genuine two-series, date-stamped
        // comparison; Outlet Dispatch is a genuine date-stamped
        // movement trend. Deliberately NOT added: a standalone "Ready
        // Stock trend" (would duplicate the ConfirmedReady series
        // below) and a "stock level over time" trend for any balance
        // table (no historical snapshot table exists anywhere in this
        // app -- only ledgers with movement dates -- so a true balance
        // trend cannot be computed without replaying every transaction,
        // which Step 8 explicitly warns against fabricating). See the
        // Phase L report's Charts section for the full reasoning.
        // ------------------------------------------------------------
        public async Task<List<TrendPoint>> GetSowingVsReadyTrendAsync(ManagementDashboardFilters filters)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var points = new List<TrendPoint>();

            using (var cmd = new SqlCommand(@"
SELECT SowingDate AS D, SUM(QuantitySown) AS V
FROM #SOWING# s
WHERE SowingDate >= @FromDate AND SowingDate < @ToDateExclusive
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)
GROUP BY SowingDate
ORDER BY SowingDate".Replace("#SOWING#", BedSowingSchema.SowingActivitySql(await BedSowingSchema.ReadyAsync(conn))), conn))
            {
                AddDateRange(cmd, filters);
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    points.Add(new TrendPoint { BucketDate = reader.GetDateTime(0), Series = "Sown", Value = reader.GetDecimal(1) });
            }

            using (var cmd = new SqlCommand(@"
SELECT CAST(DATEADD(MINUTE, @OffsetMinutes, t.TransactionDate) AS DATE) AS D, SUM(t.Quantity) AS V
FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId
WHERE t.TransactionType = 'Confirmed'
  AND t.TransactionDate >= @FromUtc AND t.TransactionDate < @ToUtcExclusive   -- TransactionDate is SYSUTCDATETIME()
  AND (@AreaId IS NULL OR rs.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR rs.SpeciesId = @SpeciesId)
GROUP BY CAST(DATEADD(MINUTE, @OffsetMinutes, t.TransactionDate) AS DATE)
ORDER BY D", conn))
            {
                AddUtcDateRange(cmd, filters);
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    points.Add(new TrendPoint { BucketDate = reader.GetDateTime(0), Series = "Confirmed Ready", Value = reader.GetDecimal(1) });
            }

            return points;
        }

        public async Task<List<TrendPoint>> GetOutletDispatchTrendAsync(ManagementDashboardFilters filters)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var points = new List<TrendPoint>();

            using var cmd = new SqlCommand(@"
SELECT CAST(DATEADD(MINUTE, @OffsetMinutes, d.DispatchDate) AS DATE) AS D, SUM(d.Quantity) AS V
FROM dbo.Dispatches d
WHERE d.Status = 'Completed'
  AND d.DispatchDate >= @FromUtc AND d.DispatchDate < @ToUtcExclusive   -- DispatchDate is SYSUTCDATETIME()
  AND (@AreaId IS NULL OR d.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR d.SpeciesId = @SpeciesId)
GROUP BY CAST(DATEADD(MINUTE, @OffsetMinutes, d.DispatchDate) AS DATE)
ORDER BY D", conn);
            AddUtcDateRange(cmd, filters);
            AddAreaSpecies(cmd, filters);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                points.Add(new TrendPoint { BucketDate = reader.GetDateTime(0), Series = "Dispatched", Value = reader.GetDecimal(1) });

            return points;
        }

        // ------------------------------------------------------------
        // Management overview (top of the page) -- company-wide, ONE
        // round trip: a single batch with several result sets. Every
        // Ready Stock figure (totals, Top 3, Plant Type, Area/Polyhouse)
        // is summed in C# from result set 1 alone, so the numbers always
        // reconcile and nothing is counted twice. Formulas mirror the
        // existing owners of each rule:
        //   Ready Stock   physical = Quantity - DispatchedQuantity, available =
        //                 Quantity - ReservedQuantity - DispatchedQuantity
        //                 (dbo.ReadyStock's own PhysicalQuantity/AvailableQuantity
        //                 computed columns; SeedlingFulfilmentRepository.GetAvailabilityAsync)
        //   Potted stock  available = Physical - Reserved (PottedPlantStock.AvailableQuantity)
        //   Booking order ISNULL(OrderBookingId, Id) (BookingOrders.EffectiveOrderSql);
        //                 remaining = max(0, Quantity - Dispatched - legacy-fulfilled)
        //                 (SeedlingBookingSummary.RemainingQuantity)
        //   Low stock     IsActive AND AvailableQuantity < MinimumStock (StoreItem.IsLow)
        // Date handling: the period is India business dates. DATE columns are
        // compared directly; SYSUTCDATETIME() columns against the UTC bounds of
        // those dates (AppClock.IndiaToUtc).
        // ------------------------------------------------------------
        public sealed class OverviewData
        {
            public List<ReadyStockSlice> ReadyStock { get; } = new();
            public List<PottedStockByArea> PottedStock { get; } = new();
            public SeedlingOrderSummary Seedlings { get; } = new();
            public List<VarietyQuantity> OpenBookingByVariety { get; } = new();
            public ProductionActivity Production { get; } = new();
            public int MaintenanceClosedInPeriod { get; set; }
            public StoreSnapshot Store { get; } = new();
            public List<RecentPost> RecentProblems { get; } = new();
            public List<ActivityItem> RecentActivity { get; } = new();
        }

        public const int RecentActivityPerSource = 5;

        public async Task<OverviewData> GetOverviewAsync(DashboardPeriod period, DateTime todayIndia)
        {
            var data = new OverviewData();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
-- 1. Ready Stock by Area x Polyhouse x Variety
SELECT rs.AreaId, a.Name, a.AreaType, rs.PolyhouseId, ph.Name, ph.AreaId, rs.SpeciesId, ps.Name, pt.Name,
       SUM(rs.Quantity - rs.DispatchedQuantity - rs.InTransitQuantity), SUM(rs.ReservedQuantity),
       SUM(rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity - rs.InTransitQuantity), COUNT(*), pa.Name
FROM dbo.ReadyStock rs
LEFT JOIN dbo.Area a ON a.Id = rs.AreaId
LEFT JOIN dbo.Polyhouses ph ON ph.Id = rs.PolyhouseId
LEFT JOIN dbo.Area pa ON pa.Id = ph.AreaId
LEFT JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
LEFT JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
WHERE rs.Quantity - rs.DispatchedQuantity - rs.InTransitQuantity <> 0 OR rs.ReservedQuantity <> 0   -- in transit: at neither location
GROUP BY rs.AreaId, a.Name, a.AreaType, rs.PolyhouseId, ph.Name, ph.AreaId, pa.Name, rs.SpeciesId, ps.Name, pt.Name;

-- 2. Potted Plant Stock by Area
SELECT s.AreaId, a.Name, a.AreaType, SUM(s.PhysicalQuantity), SUM(s.ReservedQuantity), SUM(s.InTransitQuantity)
FROM dbo.PottedPlantStock s
LEFT JOIN dbo.Area a ON a.Id = s.AreaId
GROUP BY s.AreaId, a.Name, a.AreaType;

-- Open (Pending) seedling booking lines, computed once
DECLARE @open TABLE (OrderId INT, SpeciesId INT NULL, PlantId INT, Quantity DECIMAL(18,2), Dispatched DECIMAL(18,2),
                     Reserved DECIMAL(18,2), DeliveryDate DATE NULL, Remaining DECIMAL(18,2));
INSERT INTO @open
SELECT ISNULL(b.OrderBookingId, b.Id), b.SpeciesId, b.PlantId, b.Quantity, b.DispatchedQuantity, b.ReservedQuantity, b.DeliveryDate,
       CASE WHEN b.Quantity - b.DispatchedQuantity - ISNULL(leg.Qty, 0) > 0 THEN b.Quantity - b.DispatchedQuantity - ISNULL(leg.Qty, 0) ELSE 0 END
FROM dbo.Bookings b
LEFT JOIN (SELECT it.BookingId, SUM(CAST(it.QuantityUtilized AS DECIMAL(18,2))) AS Qty
           FROM dbo.InventoryTransactions it
           WHERE it.TransactionType = 'Allocation' AND it.BookingId IS NOT NULL
           GROUP BY it.BookingId) leg ON leg.BookingId = b.Id
WHERE ISNULL(b.Status, 'Pending') = 'Pending';

-- 3. Open seedling orders (orders = DISTINCT OrderId, quantities = SUM over lines)
SELECT COUNT(DISTINCT OrderId), COUNT(*), ISNULL(SUM(Quantity), 0), ISNULL(SUM(Dispatched), 0), ISNULL(SUM(Remaining), 0), ISNULL(SUM(Reserved), 0),
       COUNT(DISTINCT CASE WHEN DeliveryDate < @Today AND Remaining > 0 THEN OrderId END),
       ISNULL(SUM(CASE WHEN DeliveryDate < @Today THEN Remaining END), 0),
       COUNT(DISTINCT CASE WHEN DeliveryDate = @Today AND Remaining > 0 THEN OrderId END),
       ISNULL(SUM(CASE WHEN DeliveryDate = @Today THEN Remaining END), 0)
FROM @open;

-- 4. Still to dispatch, by Variety
SELECT o.SpeciesId, ps.Name, pt.Name, SUM(o.Remaining)
FROM @open o
LEFT JOIN dbo.PlantSpecies ps ON ps.Id = o.SpeciesId
LEFT JOIN dbo.PlantTypes pt ON pt.Id = ISNULL(ps.PlantTypeId, o.PlantId)
WHERE o.Remaining > 0
GROUP BY o.SpeciesId, ps.Name, pt.Name;

-- 5. Production in the period
SELECT (SELECT COUNT(*) FROM #SOWING# s WHERE s.SowingDate >= @FromDate AND s.SowingDate < @ToDateExclusive),
       (SELECT ISNULL(SUM(s.QuantitySown), 0) FROM #SOWING# s WHERE s.SowingDate >= @FromDate AND s.SowingDate < @ToDateExclusive),
       (SELECT COUNT(*) FROM dbo.CuttingProductions WHERE CuttingDate >= @FromDate AND CuttingDate < @ToDateExclusive),
       (SELECT ISNULL(SUM(Quantity), 0) FROM dbo.CuttingProductions WHERE CuttingDate >= @FromDate AND CuttingDate < @ToDateExclusive),
       (SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PotProductionEntries WHERE ProductionDate >= @FromDate AND ProductionDate < @ToDateExclusive),
       (SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStockTransactions WHERE TransactionType = 'Confirmed' AND TransactionDate >= @FromUtc AND TransactionDate < @ToUtcExclusive),
       (SELECT COUNT(*) FROM dbo.MaintenanceTickets WHERE Status = 'Closed' AND ClosedAt >= @FromUtc AND ClosedAt < @ToUtcExclusive);

-- 6. Store requests waiting
SELECT ISNULL(SUM(CASE WHEN Status = 'Pending' THEN 1 ELSE 0 END), 0), ISNULL(SUM(CASE WHEN Status = 'Approved' THEN 1 ELSE 0 END), 0)
FROM dbo.StoreRequests;

-- 7. Store items below minimum (StoreItem.IsLow)
SELECT i.ItemCode, i.Name, i.AvailableQuantity, i.MinimumStock, LTRIM(RTRIM(u.UnitName))
FROM dbo.StoreItems i
LEFT JOIN dbo.UnitMaster u ON u.UnitId = i.UnitId
WHERE i.IsActive = 1 AND i.AvailableQuantity < i.MinimumStock
ORDER BY i.Name;

-- 8. Latest active problems
SELECT TOP 5 p.Id, p.PostNo, p.Title, a.Name, p.Status, p.IsPermanent, p.CreatedAt
FROM dbo.SuggestionPosts p
LEFT JOIN dbo.Area a ON a.Id = p.AreaId
WHERE p.IsDeleted = 0 AND p.TypeCode = N'Problem' AND (p.IsPermanent = 1 OR p.ExpiresAt > SYSUTCDATETIME())
ORDER BY p.CreatedAt DESC, p.Id DESC;

-- 9. Recent activity: the latest few of each kind, newest first by Id (clustered key).
--    Columns: Kind, WhenUtc, Id, Code, Party, Detail, Extra, Quantity, Lines. Every time is
--    returned as UTC; dbo.Bookings.BookingDate (GETDATE(), SQL Server local time) is shifted by
--    SQL Server's own exact offset, so nothing depends on the web server's time zone.
SELECT TOP (@PerSource) 'Booking', DATEADD(MINUTE, -@ServerOffset, MIN(b.BookingDate)), ISNULL(b.OrderBookingId, b.Id),
       NULL, MAX(b.CustomerName), NULL, NULL, CAST(SUM(b.Quantity) AS DECIMAL(18,2)), COUNT(*)
FROM dbo.Bookings b
GROUP BY ISNULL(b.OrderBookingId, b.Id)
ORDER BY MAX(b.Id) DESC;
SELECT TOP (@PerSource) 'Seedling Dispatch', d.CreatedDate, d.BookingId, d.DispatchCode, d.CustomerName, NULL, NULL, d.TotalQuantity, NULL
FROM dbo.SeedlingDispatches d ORDER BY d.Id DESC;
SELECT TOP (@PerSource) 'Potted Dispatch', d.CreatedDate, NULL, d.DispatchCode, ps.Name, d.PotSize, NULL, d.Quantity, NULL
FROM dbo.Dispatches d LEFT JOIN dbo.PlantSpecies ps ON ps.Id = d.SpeciesId ORDER BY d.Id DESC;
SELECT TOP (@PerSource) 'Ready Stock', t.TransactionDate, NULL, rs.BatchNo, ps.Name, NULL, NULL, t.Quantity, NULL
FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId
LEFT JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
WHERE t.TransactionType = 'Confirmed' ORDER BY t.Id DESC;
SELECT TOP (@PerSource) 'Maintenance', t.RequestedAt, t.Id, t.TicketNo, a.Name, t.Title, t.Priority, NULL, NULL
FROM dbo.MaintenanceTickets t LEFT JOIN dbo.Area a ON a.Id = t.AreaId ORDER BY t.Id DESC;
SELECT TOP (@PerSource) 'Store Request', r.RequestedAt, r.Id, r.RequestNo, a.Name, r.Status, NULL, NULL, NULL
FROM dbo.StoreRequests r LEFT JOIN dbo.Area a ON a.Id = r.AreaId ORDER BY r.Id DESC;
SELECT TOP (@PerSource) p.TypeCode, p.CreatedAt, p.Id, p.PostNo, a.Name, p.Title, NULL, NULL, NULL
FROM dbo.SuggestionPosts p LEFT JOIN dbo.Area a ON a.Id = p.AreaId WHERE p.IsDeleted = 0 ORDER BY p.Id DESC;".Replace("#SOWING#", BedSowingSchema.SowingActivitySql(await BedSowingSchema.ReadyAsync(conn))), conn);
            cmd.Parameters.Add("@Today", System.Data.SqlDbType.Date).Value = todayIndia.Date;
            cmd.Parameters.Add("@FromDate", System.Data.SqlDbType.Date).Value = period.From.Date;
            cmd.Parameters.Add("@ToDateExclusive", System.Data.SqlDbType.Date).Value = period.ToExclusive.Date;
            cmd.Parameters.Add("@FromUtc", System.Data.SqlDbType.DateTime2).Value = AppClock.IndiaToUtc(period.From.Date);
            cmd.Parameters.Add("@ToUtcExclusive", System.Data.SqlDbType.DateTime2).Value = AppClock.IndiaToUtc(period.ToExclusive.Date);
            cmd.Parameters.Add("@PerSource", System.Data.SqlDbType.Int).Value = RecentActivityPerSource;
            cmd.CommandText = "DECLARE @ServerOffset INT = " + ServerOffsetSql + ";\n" + cmd.CommandText;

            using var r = await cmd.ExecuteReaderAsync();
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            decimal M(int i) => r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));

            while (await r.ReadAsync())
                data.ReadyStock.Add(new ReadyStockSlice
                {
                    AreaId = r.GetInt32(0),
                    AreaName = ManagementDashboardCalculations.CleanName(S(1), $"Area {r.GetInt32(0)}"),
                    AreaType = S(2),
                    PolyhouseId = I(3),
                    PolyhouseName = S(4) is { } ph ? ManagementDashboardCalculations.CleanName(ph, "") : null,
                    PolyhouseAreaId = I(5),
                    PolyhouseAreaName = S(13) is { } pan ? ManagementDashboardCalculations.CleanName(pan, "") : null,
                    SpeciesId = r.GetInt32(6),
                    VarietyName = ManagementDashboardCalculations.CleanName(S(7), $"Variety {r.GetInt32(6)}"),
                    PlantTypeName = S(8) is { } pt ? ManagementDashboardCalculations.CleanName(pt, "") : null,
                    Physical = M(9), Reserved = M(10), Available = M(11), Batches = r.GetInt32(12),
                });

            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.PottedStock.Add(new PottedStockByArea
                {
                    AreaId = I(0),
                    AreaName = ManagementDashboardCalculations.CleanName(S(1), "(No Area)"),
                    AreaType = S(2),
                    Physical = M(3), Reserved = M(4), InTransit = M(5),
                });

            await r.NextResultAsync();
            if (await r.ReadAsync())
            {
                var s = data.Seedlings;
                s.OpenOrders = r.GetInt32(0);
                s.OpenLines = r.GetInt32(1);
                s.OpenBookedQuantity = M(2);
                s.OpenDispatchedQuantity = M(3);
                s.OpenRemainingQuantity = M(4);
                s.OpenReservedQuantity = M(5);
                s.OverdueOrders = r.GetInt32(6);
                s.OverdueRemainingQuantity = M(7);
                s.DueTodayOrders = r.GetInt32(8);
                s.DueTodayRemainingQuantity = M(9);
            }

            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.OpenBookingByVariety.Add(new VarietyQuantity
                {
                    SpeciesId = I(0),
                    VarietyName = ManagementDashboardCalculations.CleanName(S(1), ManagementDashboardCalculations.NoVariety),
                    PlantTypeName = S(2) is { } pt ? ManagementDashboardCalculations.CleanName(pt, "") : null,
                    Quantity = M(3),
                });

            await r.NextResultAsync();
            if (await r.ReadAsync())
            {
                var p = data.Production;
                p.Sowings = r.GetInt32(0);
                p.SownQuantity = M(1);
                p.CuttingEntries = r.GetInt32(2);
                p.CuttingsProduced = M(3);
                p.PotsProduced = M(4);
                p.ReadyApprovedQuantity = M(5);
                data.MaintenanceClosedInPeriod = r.GetInt32(6);
            }

            await r.NextResultAsync();
            if (await r.ReadAsync())
            {
                data.Store.PendingReview = r.GetInt32(0);
                data.Store.ApprovedAwaitingIssue = r.GetInt32(1);
            }

            await r.NextResultAsync();
            while (await r.ReadAsync())
            {
                data.Store.LowStockCount++;
                if (data.Store.LowStockItems.Count < 5)
                    data.Store.LowStockItems.Add(new StoreLowItem { ItemCode = r.GetString(0), Name = r.GetString(1), Available = M(2), Minimum = M(3), Unit = S(4) });
            }

            await r.NextResultAsync();
            while (await r.ReadAsync())
                data.RecentProblems.Add(new RecentPost
                {
                    Id = r.GetInt32(0), PostNo = S(1), Title = r.GetString(2), AreaName = S(3), Status = r.GetString(4),
                    IsPermanent = r.GetBoolean(5), CreatedAtIndia = AppClock.ToIndiaTime(r.GetDateTime(6)),
                });

            for (var source = 0; source < 7; source++)
            {
                await r.NextResultAsync();
                while (await r.ReadAsync())
                {
                    var kind = r.GetString(0);
                    var id = I(2);
                    data.RecentActivity.Add(new ActivityItem
                    {
                        Kind = kind,
                        WhenIndia = AppClock.ToIndiaTime(r.GetDateTime(1)),
                        Id = id,
                        Text = ManagementDashboardCalculations.ActivityText(kind, id, S(3), S(4), S(5), S(6),
                            r.IsDBNull(7) ? null : M(7), I(8)),
                        Page = kind switch
                        {
                            "Booking" or "Seedling Dispatch" => "/Bookings/BookingDetails",
                            "Potted Dispatch" => "/Production/Dispatch/Index",
                            "Ready Stock" => "/Production/ReadyStock/Index",
                            "Maintenance" => "/Maintenance/Details",
                            "Store Request" => "/Store/RequestDetails",
                            _ => "/Suggestions/Details",
                        },
                    });
                }
            }

            return data;
        }

        // SQL Server's own current UTC offset in minutes (exact, unlike a DATEDIFF of two
        // clock reads). dbo.Bookings.BookingDate is written with GETDATE(), i.e. SQL Server
        // local time: shifting a UTC instant by this offset gives the matching BookingDate.
        public const string ServerOffsetSql = "DATEPART(TZOFFSET, SYSDATETIMEOFFSET())";

        // Newest first across every source; at most `take` items.
        public static List<ActivityItem> MergeActivity(IEnumerable<ActivityItem> items, int take)
            => items.OrderByDescending(a => a.WhenIndia).ThenBy(a => a.Kind).Take(take).ToList();

        // ------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------
        // Ready Stock balance: physical = Quantity - DispatchedQuantity, and the
        // part of it reserved for seedling bookings (ReservedQuantity).
        // 2026-10-09: + plants in transit to a Mother Plant Area (part of the total, at neither location).
        private static async Task<(decimal Physical, decimal Reserved, decimal InTransit)> GetReadyStockTotalsAsync(SqlConnection conn, ManagementDashboardFilters filters)
        {
            using var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(Quantity - DispatchedQuantity), 0), ISNULL(SUM(ReservedQuantity), 0), ISNULL(SUM(InTransitQuantity), 0)   -- Phase C: physical = approved - dispatched
FROM dbo.ReadyStock
WHERE (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn);
            AddAreaSpecies(cmd, filters);
            using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
        }

        private static async Task<decimal> GetReadyStockTotalAsync(SqlConnection conn, ManagementDashboardFilters filters)
            => (await GetReadyStockTotalsAsync(conn, filters)).Physical;

        // Step 8B/8D (Daily Report by Plant Type), redefined 2026-10-10 (docs/STOCK_DEFINITIONS.md):
        // Ready Stock AVAILABLE for booking/dispatch per Plant Type =
        // Quantity - Reserved - Dispatched - InTransit (the computed AvailableQuantity) --
        // the WhatsApp template's fixed "Ready Stock" label cannot be renamed, so the figure
        // is the primary operational one; never reserved or in-transit plants.
        // Starts FROM dbo.PlantTypes (LEFT JOIN out to PlantSpecies/ReadyStock)
        // so every current PlantType is always one entry in the result, 0
        // when it has no Ready Stock -- never a missing entry. ORDER BY pt.Id
        // is preserved into the returned List itself (a plain reader loop
        // appending in row order) so a caller never has to re-sort or depend
        // on a Dictionary's unordered enumeration to get a stable sequence.
        private static async Task<List<ReadyStockPlantTypeQuantity>> GetReadyStockByPlantTypeAsync(SqlConnection conn, ManagementDashboardFilters filters)
        {
            var result = new List<ReadyStockPlantTypeQuantity>();
            using var cmd = new SqlCommand(@"
SELECT pt.Name, ISNULL(SUM(rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity - rs.InTransitQuantity), 0)
FROM dbo.PlantTypes pt
LEFT JOIN dbo.PlantSpecies ps ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.ReadyStock rs ON rs.SpeciesId = ps.Id
  AND (@AreaId IS NULL OR rs.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR rs.SpeciesId = @SpeciesId)
GROUP BY pt.Id, pt.Name
ORDER BY pt.Id", conn);
            AddAreaSpecies(cmd, filters);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new ReadyStockPlantTypeQuantity(reader.GetString(0), reader.GetDecimal(1)));
            return result;
        }

        private static async Task<List<UnitQuantity>> GetSeedStockByUnitAsync(SqlConnection conn, ManagementDashboardFilters filters, bool mainOfficeOnly)
        {
            var list = new List<UnitQuantity>();
            var sql = mainOfficeOnly
                ? @"
SELECT ss.Unit, ISNULL(SUM(ss.PhysicalQuantity), 0), ISNULL(SUM(ss.InTransitQuantity), 0)
FROM dbo.SeedStock ss
INNER JOIN dbo.Area a ON ss.AreaId = a.Id
WHERE a.AreaType = 'MainOffice'
  AND (@SpeciesId IS NULL OR ss.SpeciesId = @SpeciesId)
GROUP BY ss.Unit"
                : @"
SELECT ss.Unit, ISNULL(SUM(ss.PhysicalQuantity), 0), ISNULL(SUM(ss.InTransitQuantity), 0)
FROM dbo.SeedStock ss
WHERE (@AreaId IS NULL OR ss.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR ss.SpeciesId = @SpeciesId)
GROUP BY ss.Unit";

            using var cmd = new SqlCommand(sql, conn);
            if (!mainOfficeOnly)
                cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", (object?)filters.SpeciesId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var physical = reader.GetDecimal(1);
                var inTransit = reader.GetDecimal(2);
                list.Add(new UnitQuantity { Unit = reader.GetString(0), Physical = physical, InTransit = inTransit, Available = physical - inTransit });
            }
            return list;
        }

        // GROUP BY AreaId helper shared by the Growing Partner Summary's
        // six batched aggregate queries -- one query, a dictionary
        // result, merged onto every Area row in C#, never one query
        // per Area.
        private static async Task<Dictionary<int, decimal>> SumByAreaAsync(SqlConnection conn, string sql, ManagementDashboardFilters filters, bool noDateParams = false)
        {
            var dict = new Dictionary<int, decimal>();
            using var cmd = new SqlCommand(sql, conn);
            if (!noDateParams)
                AddDateRange(cmd, filters);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(0)) continue;
                dict[reader.GetInt32(0)] = reader.GetDecimal(1);
            }
            return dict;
        }

        private static void AddDateRange(SqlCommand cmd, ManagementDashboardFilters filters)
        {
            cmd.Parameters.AddWithValue("@FromDate", filters.EffectiveFromDate);
            cmd.Parameters.AddWithValue("@ToDateExclusive", filters.EffectiveToDateExclusive);
        }

        // For SYSUTCDATETIME() columns: the UTC instants that bound the selected India
        // dates, plus the India offset for bucketing such a column by India day.
        private static void AddUtcDateRange(SqlCommand cmd, ManagementDashboardFilters filters)
        {
            cmd.Parameters.AddWithValue("@FromUtc", AppClock.IndiaToUtc(filters.EffectiveFromDate));
            cmd.Parameters.AddWithValue("@ToUtcExclusive", AppClock.IndiaToUtc(filters.EffectiveToDateExclusive));
            cmd.Parameters.AddWithValue("@OffsetMinutes", AppClock.IndiaOffsetMinutes);
        }

        private static void AddAreaSpecies(SqlCommand cmd, ManagementDashboardFilters filters)
        {
            cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", (object?)filters.SpeciesId ?? DBNull.Value);
        }

        private static void AddAreaSpeciesPotSize(SqlCommand cmd, ManagementDashboardFilters filters)
        {
            cmd.Parameters.AddWithValue("@AreaId", (object?)filters.AreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", (object?)filters.SpeciesId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PotSize", (object?)filters.PotSize ?? DBNull.Value);
        }
    }
}
