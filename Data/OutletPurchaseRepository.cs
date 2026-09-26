using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase E: potted plants an Outlet buys directly from an outside
    // supplier -- credits the EXISTING dbo.PottedPlantStock at that Outlet
    // ('Purchase'). Distinct from a Main Office purchase order: different
    // stock location, no vendor/PO paperwork.
    public class OutletPurchaseRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;

        public OutletPurchaseRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo,
            EmptyPotInventoryRepository emptyPotRepo, PottedPlantStockRepository pottedPlantStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _emptyPotRepo = emptyPotRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
        }

        private const string BaseSelect = @"
SELECT p.Id, p.PurchaseCode, p.PurchaseDate, p.SupplierName, p.OutletAreaId, a.Name AS OutletAreaName,
       p.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName, ps.Color AS SpeciesColor,
       p.PotSize, p.Quantity, p.PottedPlantStockId, p.Remarks, p.CreatedById, p.CreatedBy, p.CreatedDate
FROM dbo.OutletPurchases p
INNER JOIN dbo.Area a ON a.Id = p.OutletAreaId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = p.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId";

        public async Task<List<OutletPurchase>> GetAllAsync(int? outletAreaId = null)
        {
            var list = new List<OutletPurchase>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE (@AreaId IS NULL OR p.OutletAreaId = @AreaId) ORDER BY p.PurchaseDate DESC, p.Id DESC", conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)outletAreaId ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(Map(r));
            return list;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(OutletPurchase entry, int? userId)
        {
            var (ok, error) = OutletRules.ValidatePurchase(entry.Quantity, entry.SupplierName);
            if (!ok)
                return (false, error, 0);
            if (entry.PurchaseDate == default || entry.PurchaseDate.Date > DateTime.Today)
                return (false, "Enter a purchase date that is not in the future.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var areaCmd = new SqlCommand("SELECT AreaType, IsActive FROM dbo.Area WHERE Id = @Id", conn, tx);
                areaCmd.Parameters.AddWithValue("@Id", entry.OutletAreaId);
                using (var ar = await areaCmd.ExecuteReaderAsync())
                {
                    if (!await ar.ReadAsync() || ar.IsDBNull(0) || !OutletRules.IsActiveOutlet(ar.GetString(0), ar.GetBoolean(1)))
                    {
                        ar.Close();
                        tx.Rollback();
                        return (false, "Choose an active Outlet.", 0);
                    }
                }

                var sizeCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PotSizes WHERE Name = @Name AND IsActive = 1", conn, tx);
                sizeCmd.Parameters.AddWithValue("@Name", entry.PotSize ?? "");
                if ((int)(await sizeCmd.ExecuteScalarAsync())! == 0)
                {
                    tx.Rollback();
                    return (false, "Choose a pot size from the list.", 0);
                }

                var speciesCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Id = @Id", conn, tx);
                speciesCmd.Parameters.AddWithValue("@Id", entry.SpeciesId);
                if ((int)(await speciesCmd.ExecuteScalarAsync())! == 0)
                {
                    tx.Rollback();
                    return (false, "Choose a variety from the list.", 0);
                }

                // The stock row needs an EmptyPotInventoryId link (schema
                // requirement, not a physical empty-pot movement) -- same
                // resolve-or-create pattern as an Area-to-Area transfer.
                var poolId = await _emptyPotRepo.GetOrCreateLockedAsync(conn, tx, entry.PotSize!, entry.OutletAreaId, entry.CreatedBy);
                var stockId = await _pottedPlantStockRepo.GetOrCreateLockedAsync(conn, tx, entry.SpeciesId, entry.PotSize!, entry.OutletAreaId, poolId, entry.CreatedBy);

                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "OPU", entry.PurchaseDate.Year);
                var insert = new SqlCommand(@"
INSERT INTO dbo.OutletPurchases
(PurchaseCode, PurchaseDate, SupplierName, OutletAreaId, SpeciesId, PotSize, Quantity, PottedPlantStockId, Remarks, CreatedById, CreatedBy)
VALUES (@Code, @PurchaseDate, @SupplierName, @AreaId, @SpeciesId, @PotSize, @Quantity, @StockId, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@PurchaseDate", entry.PurchaseDate.Date);
                insert.Parameters.AddWithValue("@SupplierName", entry.SupplierName.Trim());
                insert.Parameters.AddWithValue("@AreaId", entry.OutletAreaId);
                insert.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                insert.Parameters.AddWithValue("@PotSize", entry.PotSize!);
                insert.Parameters.AddWithValue("@Quantity", entry.Quantity);
                insert.Parameters.AddWithValue("@StockId", stockId);
                insert.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                var newId = (int)(await insert.ExecuteScalarAsync())!;

                var (stockOk, stockMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, stockId, entry.Quantity, "Purchase", "OutletPurchase", newId, userId,
                    $"Purchase {code} from {entry.SupplierName.Trim()}");
                if (!stockOk)
                {
                    tx.Rollback();
                    return (false, stockMessage, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.PurchaseCode = code;
                entry.PottedPlantStockId = stockId;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        private static OutletPurchase Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            PurchaseCode = r.GetString(1),
            PurchaseDate = r.GetDateTime(2),
            SupplierName = r.GetString(3),
            OutletAreaId = r.GetInt32(4),
            OutletAreaName = r.GetString(5),
            SpeciesId = r.GetInt32(6),
            SpeciesName = r.GetString(7),
            PlantTypeName = r.GetString(8),
            SpeciesColor = r.IsDBNull(9) ? null : r.GetString(9),
            PotSize = r.GetString(10),
            Quantity = r.GetDecimal(11),
            PottedPlantStockId = r.GetInt32(12),
            Remarks = r.IsDBNull(13) ? null : r.GetString(13),
            CreatedById = r.IsDBNull(14) ? null : r.GetInt32(14),
            CreatedBy = r.IsDBNull(15) ? null : r.GetString(15),
            CreatedDate = r.GetDateTime(16)
        };
    }
}
