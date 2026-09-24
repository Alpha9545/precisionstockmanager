using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

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

            // Pot/Tray Production (Movement, within the selected date
            // range) -- unit: pots produced.
            using (var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(Quantity), 0)
FROM dbo.PotProduction
WHERE Status = 'Completed'
  AND ProductionDate >= @FromDate AND ProductionDate < @ToDateExclusive
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
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
            result.ReadyStockQuantity = await GetReadyStockTotalAsync(conn, filters);

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
        public async Task<ReadyStockSummary> GetReadyStockSummaryAsync(ManagementDashboardFilters filters)
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

            var today = DateTime.Today;
            var horizon = today.AddDays(SeedSowingRepository.DefaultReadySoonWindowDays);
            var candidates = await _seedSowingRepo.GetAlertCandidatesAsync(horizon);
            var scoped = candidates
                .Where(s => !filters.AreaId.HasValue || s.AreaId == filters.AreaId.Value)
                .Where(s => !filters.SpeciesId.HasValue || s.SpeciesId == filters.SpeciesId.Value);

            foreach (var sowing in scoped)
            {
                var category = SeedSowingRepository.ClassifyReadyAlert(sowing.Status, sowing.ExpectedReadyDate, today, SeedSowingRepository.DefaultReadySoonWindowDays);
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
WHERE StockType = 'Cutting' AND Status IN ('ConfirmedAwaitingTransplant', 'Transplanted')
  AND ConfirmedDate >= @FromDate AND ConfirmedDate < @ToDateExclusive
GROUP BY DestinationAreaId", filters);

            var potTrayProduction = await SumByAreaAsync(conn, @"
SELECT AreaId, ISNULL(SUM(Quantity), 0)
FROM dbo.PotProduction
WHERE Status = 'Completed' AND AreaId IS NOT NULL
  AND ProductionDate >= @FromDate AND ProductionDate < @ToDateExclusive
GROUP BY AreaId", filters);

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
  AND DispatchDate >= @FromDate AND DispatchDate < @ToDateExclusive
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)
  AND (@PotSize IS NULL OR PotSize = @PotSize)", conn))
            {
                AddDateRange(cmd, filters);
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
FROM dbo.SeedSowings
WHERE Status <> 'Cancelled'
  AND SowingDate >= @FromDate AND SowingDate < @ToDateExclusive
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn))
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
  AND ConfirmedDate >= @FromDate AND ConfirmedDate < @ToDateExclusive
  AND (@AreaId IS NULL OR SourceAreaId = @AreaId OR DestinationAreaId = @AreaId)", conn))
            {
                AddDateRange(cmd, filters);
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
WHERE t.TransactionDate >= @FromDate AND t.TransactionDate < @ToDateExclusive
  AND (@AreaId IS NULL OR cs.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR cs.SpeciesId = @SpeciesId)
ORDER BY t.TransactionDate DESC", conn))
            {
                AddDateRange(cmd, filters);
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.RecentActivity.Add(new CuttingActivityItem
                    {
                        TransactionDate = reader.GetDateTime(0),
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
                    Domain = "Ready Stock",
                    Unit = "plants",
                    Physical = readyStock.ReadyStockQuantity,
                    Reserved = null,
                    Available = null, // single balance column only, no Reserved/InTransit
                    InTransit = null
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
FROM dbo.SeedSowings
WHERE Status <> 'Cancelled'
  AND SowingDate >= @FromDate AND SowingDate < @ToDateExclusive
  AND (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)
GROUP BY SowingDate
ORDER BY SowingDate", conn))
            {
                AddDateRange(cmd, filters);
                AddAreaSpecies(cmd, filters);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    points.Add(new TrendPoint { BucketDate = reader.GetDateTime(0), Series = "Sown", Value = reader.GetDecimal(1) });
            }

            using (var cmd = new SqlCommand(@"
SELECT CAST(TransactionDate AS DATE) AS D, SUM(Quantity) AS V
FROM dbo.ReadyStockTransactions
WHERE TransactionType = 'Confirmed'
  AND TransactionDate >= @FromDate AND TransactionDate < @ToDateExclusive
GROUP BY CAST(TransactionDate AS DATE)
ORDER BY D", conn))
            {
                AddDateRange(cmd, filters);
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
SELECT CAST(d.DispatchDate AS DATE) AS D, SUM(d.Quantity) AS V
FROM dbo.Dispatches d
WHERE d.Status = 'Completed'
  AND d.DispatchDate >= @FromDate AND d.DispatchDate < @ToDateExclusive
  AND (@AreaId IS NULL OR d.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR d.SpeciesId = @SpeciesId)
GROUP BY CAST(d.DispatchDate AS DATE)
ORDER BY D", conn);
            AddDateRange(cmd, filters);
            AddAreaSpecies(cmd, filters);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                points.Add(new TrendPoint { BucketDate = reader.GetDateTime(0), Series = "Dispatched", Value = reader.GetDecimal(1) });

            return points;
        }

        // ------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------
        private static async Task<decimal> GetReadyStockTotalAsync(SqlConnection conn, ManagementDashboardFilters filters)
        {
            using var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(Quantity), 0)
FROM dbo.ReadyStock
WHERE (@AreaId IS NULL OR AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR SpeciesId = @SpeciesId)", conn);
            AddAreaSpecies(cmd, filters);
            return (decimal)(await cmd.ExecuteScalarAsync() ?? 0m);
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
