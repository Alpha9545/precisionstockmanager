using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 8 (Stock History and Wastage Integration): a READ-ONLY
    // aggregation layer over the FIVE already-existing, already-dedicated
    // ledger tables -- dbo.SeedStockTransactions, dbo.CuttingStockTransactions,
    // dbo.EmptyPotInventoryTransactions, dbo.PottedPlantStockTransactions,
    // dbo.ReadyStockTransactions. No new history table is created (per the
    // explicit "do not create duplicate history tables if an existing
    // transaction system can support this" instruction) -- every one of
    // these ledgers already has the identical column shape (Id, <Stock>Id,
    // TransactionDate, TransactionType, ReferenceType, ReferenceId,
    // Quantity, BeforeQuantity, AfterQuantity computed, UserId, Remarks,
    // CreatedAt), which is exactly what makes a single UNION ALL query
    // possible without any schema change.
    //
    // This repository has NO writer methods -- every existing
    // RecordTransactionAsync-style method on SeedStockRepository/
    // CuttingStockRepository/EmptyPotInventoryRepository/
    // PottedPlantStockRepository/ReadyStockRepository is completely
    // untouched; this class only reads what they already wrote.
    public class StockLedgerRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public StockLedgerRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Unified "Purchase -> Stock IN / Issue -> Stock OUT / Production
        // -> Consumption+Production / Wastage -> Wastage transaction /
        // Ready -> Ready Stock / Sale-Dispatch -> Stock OUT" history
        // across every modern stock type, for a date range. stockType
        // (optional) narrows to exactly one of 'SeedStock' | 'CuttingStock'
        // | 'EmptyPot' | 'PottedPlant' | 'ReadyStock'.
        public async Task<List<UnifiedStockTransaction>> GetUnifiedHistoryAsync(DateTime from, DateTime to, string? stockType = null)
        {
            var list = new List<UnifiedStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT 'SeedStock' AS StockType, t.SeedStockId AS StockRowId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
       NULL AS PotSize, a.Name AS AreaName, t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, u.Name AS UserName, t.Remarks
FROM dbo.SeedStockTransactions t
INNER JOIN dbo.SeedStock s ON t.SeedStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.TransactionDate >= @From AND t.TransactionDate < @ToExclusive
  AND (@StockType IS NULL OR @StockType = 'SeedStock')

UNION ALL

SELECT 'CuttingStock', t.CuttingStockId, ps.Name, pt.Name,
       NULL, a.Name, t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, u.Name, t.Remarks
FROM dbo.CuttingStockTransactions t
INNER JOIN dbo.CuttingStock s ON t.CuttingStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.TransactionDate >= @From AND t.TransactionDate < @ToExclusive
  AND (@StockType IS NULL OR @StockType = 'CuttingStock')

UNION ALL

SELECT 'EmptyPot', t.EmptyPotInventoryId, NULL, NULL,
       s.PotSize, a.Name, t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, u.Name, t.Remarks
FROM dbo.EmptyPotInventoryTransactions t
INNER JOIN dbo.EmptyPotInventory s ON t.EmptyPotInventoryId = s.Id
LEFT JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.TransactionDate >= @From AND t.TransactionDate < @ToExclusive
  AND (@StockType IS NULL OR @StockType = 'EmptyPot')

UNION ALL

SELECT 'PottedPlant', t.PottedPlantStockId, ps.Name, pt.Name,
       s.PotSize, a.Name, t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, u.Name, t.Remarks
FROM dbo.PottedPlantStockTransactions t
INNER JOIN dbo.PottedPlantStock s ON t.PottedPlantStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.TransactionDate >= @From AND t.TransactionDate < @ToExclusive
  AND (@StockType IS NULL OR @StockType = 'PottedPlant')

UNION ALL

SELECT 'ReadyStock', t.ReadyStockId, ps.Name, pt.Name,
       NULL, a.Name, t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, u.Name, t.Remarks
FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock s ON t.ReadyStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.TransactionDate >= @From AND t.TransactionDate < @ToExclusive
  AND (@StockType IS NULL OR @StockType = 'ReadyStock')

ORDER BY TransactionDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@From", from.Date);
            cmd.Parameters.AddWithValue("@ToExclusive", to.Date.AddDays(1));
            cmd.Parameters.AddWithValue("@StockType", (object?)stockType ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new UnifiedStockTransaction
                {
                    StockType = reader.GetString(reader.GetOrdinal("StockType")),
                    StockRowId = reader.GetInt32(reader.GetOrdinal("StockRowId")),
                    SpeciesName = reader.IsDBNull(reader.GetOrdinal("SpeciesName")) ? null : reader.GetString(reader.GetOrdinal("SpeciesName")),
                    PlantTypeName = reader.IsDBNull(reader.GetOrdinal("PlantTypeName")) ? null : reader.GetString(reader.GetOrdinal("PlantTypeName")),
                    PotSize = reader.IsDBNull(reader.GetOrdinal("PotSize")) ? null : reader.GetString(reader.GetOrdinal("PotSize")),
                    AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                    TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                    TransactionType = reader.GetString(reader.GetOrdinal("TransactionType")),
                    ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                    ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                    Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                    BeforeQuantity = reader.GetDecimal(reader.GetOrdinal("BeforeQuantity")),
                    AfterQuantity = reader.GetDecimal(reader.GetOrdinal("AfterQuantity")),
                    UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                    Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks"))
                });
            }
            return list;
        }

        // Opening(0) + IN - OUT - Wastage = Closing, per stock pool, for
        // every one of the five modern stock types. Each stock type's own
        // TransactionType filter below matches exactly which ledger types
        // actually move that table's own Closing quantity column --
        // 'Reservation'/'ReservationRelease' on PottedPlantStock track
        // ReservedQuantity (a different column) and must be excluded, or
        // this would flag every reserved pool as a false discrepancy;
        // ReadyStock's Closing column (Quantity) only ever moves via
        // 'Confirmed'/'ReversalRemoval' -- 'Dispatch' there moves
        // DispatchedQuantity, a separate column, not Quantity itself.
        public async Task<List<StockReconciliationRow>> GetReconciliationAsync()
        {
            var list = new List<StockReconciliationRow>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT 'SeedStock' AS StockType, s.Id AS StockRowId, ps.Name AS SpeciesName, NULL AS PotSize, a.Name AS AreaName,
       ISNULL(SUM(CASE WHEN t.Quantity > 0 THEN t.Quantity ELSE 0 END), 0) AS TotalIn,
       ISNULL(SUM(CASE WHEN t.Quantity < 0 THEN t.Quantity ELSE 0 END), 0) AS TotalOut,
       0 AS TotalWastage,
       s.PhysicalQuantity AS ActualClosing
FROM dbo.SeedStock s
LEFT JOIN dbo.SeedStockTransactions t ON t.SeedStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
LEFT JOIN dbo.Area a ON s.AreaId = a.Id
GROUP BY s.Id, ps.Name, a.Name, s.PhysicalQuantity

UNION ALL

SELECT 'CuttingStock', s.Id, ps.Name, NULL, a.Name,
       ISNULL(SUM(CASE WHEN t.Quantity > 0 THEN t.Quantity ELSE 0 END), 0),
       ISNULL(SUM(CASE WHEN t.Quantity < 0 THEN t.Quantity ELSE 0 END), 0),
       0,
       s.PhysicalQuantity
FROM dbo.CuttingStock s
LEFT JOIN dbo.CuttingStockTransactions t ON t.CuttingStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
LEFT JOIN dbo.Area a ON s.AreaId = a.Id
GROUP BY s.Id, ps.Name, a.Name, s.PhysicalQuantity

UNION ALL

SELECT 'EmptyPot', s.Id, NULL, s.PotSize, a.Name,
       ISNULL(SUM(CASE WHEN t.Quantity > 0 THEN t.Quantity ELSE 0 END), 0),
       ISNULL(SUM(CASE WHEN t.Quantity < 0 THEN t.Quantity ELSE 0 END), 0),
       0,
       s.PhysicalQuantity
FROM dbo.EmptyPotInventory s
LEFT JOIN dbo.EmptyPotInventoryTransactions t ON t.EmptyPotInventoryId = s.Id
LEFT JOIN dbo.Area a ON s.AreaId = a.Id
GROUP BY s.Id, s.PotSize, a.Name, s.PhysicalQuantity

UNION ALL

SELECT 'PottedPlant', s.Id, ps.Name, s.PotSize, a.Name,
       ISNULL(SUM(CASE WHEN t.TransactionType NOT IN ('Reservation', 'ReservationRelease') AND t.Quantity > 0 THEN t.Quantity ELSE 0 END), 0),
       ISNULL(SUM(CASE WHEN t.TransactionType NOT IN ('Reservation', 'ReservationRelease') AND t.Quantity < 0 AND t.TransactionType <> 'Wastage' THEN t.Quantity ELSE 0 END), 0),
       ISNULL(SUM(CASE WHEN t.TransactionType = 'Wastage' THEN t.Quantity ELSE 0 END), 0),
       s.PhysicalQuantity
FROM dbo.PottedPlantStock s
LEFT JOIN dbo.PottedPlantStockTransactions t ON t.PottedPlantStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
LEFT JOIN dbo.Area a ON s.AreaId = a.Id
GROUP BY s.Id, ps.Name, s.PotSize, a.Name, s.PhysicalQuantity

UNION ALL

SELECT 'ReadyStock', s.Id, ps.Name, NULL, a.Name,
       ISNULL(SUM(CASE WHEN t.TransactionType IN ('Confirmed', 'ReversalRemoval') AND t.Quantity > 0 THEN t.Quantity ELSE 0 END), 0),
       ISNULL(SUM(CASE WHEN t.TransactionType IN ('Confirmed', 'ReversalRemoval') AND t.Quantity < 0 THEN t.Quantity ELSE 0 END), 0),
       0,
       s.Quantity AS ActualClosing
FROM dbo.ReadyStock s
LEFT JOIN dbo.ReadyStockTransactions t ON t.ReadyStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON s.AreaId = a.Id
GROUP BY s.Id, ps.Name, a.Name, s.Quantity

ORDER BY StockType, StockRowId";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new StockReconciliationRow
                {
                    StockType = reader.GetString(reader.GetOrdinal("StockType")),
                    StockRowId = reader.GetInt32(reader.GetOrdinal("StockRowId")),
                    SpeciesName = reader.IsDBNull(reader.GetOrdinal("SpeciesName")) ? null : reader.GetString(reader.GetOrdinal("SpeciesName")),
                    PotSize = reader.IsDBNull(reader.GetOrdinal("PotSize")) ? null : reader.GetString(reader.GetOrdinal("PotSize")),
                    AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                    TotalIn = reader.GetDecimal(reader.GetOrdinal("TotalIn")),
                    TotalOut = reader.GetDecimal(reader.GetOrdinal("TotalOut")),
                    TotalWastage = reader.GetDecimal(reader.GetOrdinal("TotalWastage")),
                    ActualClosing = reader.GetDecimal(reader.GetOrdinal("ActualClosing"))
                });
            }
            return list;
        }

        // Sowing/Confirmation-level "process wastage" -- Seed Sowing,
        // Cutting Sowing and Ready Confirmation wastage is recorded as a
        // business column (SeedSowings.WastageQuantity/
        // CuttingSowings.WastageQuantity), not a ledger transaction --
        // there is no physical "SeedlingStock" row that a wastage entry
        // could decrement (the seedlings that become wastage never had
        // their own persisted stock row to begin with; only the
        // sown-vs-ready-vs-wasted arithmetic on the Sowing itself tracks
        // them). Surfaced here read-only, exactly as already stored --
        // no ledger row is invented and no historical Sowing/Confirmation
        // data is touched.
        public async Task<List<ProcessWastageRow>> GetProcessWastageAsync(DateTime from, DateTime to)
        {
            var list = new List<ProcessWastageRow>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT 'SeedSowing' AS SourceType, sw.SowingCode AS BatchCode, ps.Name AS SpeciesName, a.Name AS AreaName,
       sw.SowingDate, sw.QuantitySown, sw.WastageQuantity
FROM dbo.SeedSowings sw
INNER JOIN dbo.PlantSpecies ps ON sw.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON sw.AreaId = a.Id
WHERE sw.WastageQuantity > 0 AND sw.SowingDate >= @From AND sw.SowingDate < @ToExclusive

UNION ALL

SELECT 'CuttingSowing', cw.SowingCode, ps.Name, a.Name,
       cw.SowingDate, cw.QuantitySown, cw.WastageQuantity
FROM dbo.CuttingSowings cw
INNER JOIN dbo.PlantSpecies ps ON cw.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON cw.AreaId = a.Id
WHERE cw.WastageQuantity > 0 AND cw.SowingDate >= @From AND cw.SowingDate < @ToExclusive

ORDER BY SowingDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@From", from.Date);
            cmd.Parameters.AddWithValue("@ToExclusive", to.Date.AddDays(1));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new ProcessWastageRow
                {
                    SourceType = reader.GetString(reader.GetOrdinal("SourceType")),
                    BatchCode = reader.GetString(reader.GetOrdinal("BatchCode")),
                    SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                    AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                    SowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate")),
                    QuantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown")),
                    WastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity"))
                });
            }
            return list;
        }
    }
}
