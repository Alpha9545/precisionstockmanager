using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase D: Office purchase of empty pots -> the receiving Office store's
    // Empty Pot stock ('StockIn'). From there pots are issued to a specific
    // production Area (InternalTransfers, StockType 'EmptyPot').
    public class EmptyPotPurchaseRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly EmptyPotInventoryRepository _emptyPotRepo;

        public EmptyPotPurchaseRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo, EmptyPotInventoryRepository emptyPotRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _emptyPotRepo = emptyPotRepo;
        }

        public async Task<List<EmptyPotPurchase>> GetAllAsync(DateTime? from = null, DateTime? to = null)
        {
            var list = new List<EmptyPotPurchase>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT p.Id, p.PurchaseCode, p.PurchaseDate, p.PotSize, p.Quantity, p.SupplierName, p.InvoiceRef, p.AreaId, a.Name AS AreaName,
       p.EmptyPotInventoryId, p.Remarks, p.CreatedById, p.CreatedBy, p.CreatedDate
FROM dbo.EmptyPotPurchases p
INNER JOIN dbo.Area a ON a.Id = p.AreaId
WHERE (@From IS NULL OR p.PurchaseDate >= @From) AND (@To IS NULL OR p.PurchaseDate <= @To)
ORDER BY p.PurchaseDate DESC, p.Id DESC", conn);
            cmd.Parameters.AddWithValue("@From", (object?)from?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)to?.Date ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new EmptyPotPurchase
                {
                    Id = r.GetInt32(0),
                    PurchaseCode = r.GetString(1),
                    PurchaseDate = r.GetDateTime(2),
                    PotSize = r.GetString(3),
                    Quantity = r.GetDecimal(4),
                    SupplierName = r.GetString(5),
                    InvoiceRef = r.IsDBNull(6) ? null : r.GetString(6),
                    AreaId = r.GetInt32(7),
                    AreaName = r.GetString(8),
                    EmptyPotInventoryId = r.GetInt32(9),
                    Remarks = r.IsDBNull(10) ? null : r.GetString(10),
                    CreatedById = r.IsDBNull(11) ? null : r.GetInt32(11),
                    CreatedBy = r.IsDBNull(12) ? null : r.GetString(12),
                    CreatedDate = r.GetDateTime(13)
                });
            }
            return list;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(EmptyPotPurchase entry, int? userId)
        {
            if (entry.Quantity <= 0 || !DirectSowingRules.IsWholeNumber(entry.Quantity))
                return (false, "Quantity must be a whole number greater than zero.", 0);
            if (string.IsNullOrWhiteSpace(entry.SupplierName))
                return (false, "Supplier is required.", 0);
            if (entry.PurchaseDate == default || entry.PurchaseDate.Date > DateTime.Today)
                return (false, "Enter a purchase date that is not in the future.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                // Pot size must be an active master value.
                var sizeCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PotSizes WHERE Name = @Name AND IsActive = 1", conn, tx);
                sizeCmd.Parameters.AddWithValue("@Name", entry.PotSize ?? "");
                if ((int)(await sizeCmd.ExecuteScalarAsync())! == 0)
                {
                    tx.Rollback();
                    return (false, "Choose a pot size from the list.", 0);
                }
                // Pots are received into an Office (Main Office) store.
                var areaCmd = new SqlCommand("SELECT AreaType, IsActive FROM dbo.Area WHERE Id = @Id", conn, tx);
                areaCmd.Parameters.AddWithValue("@Id", entry.AreaId);
                using (var ar = await areaCmd.ExecuteReaderAsync())
                {
                    if (!await ar.ReadAsync() || ar.IsDBNull(0) || ar.GetString(0) != DirectSowingRules.MainOfficeAreaType || !ar.GetBoolean(1))
                    {
                        ar.Close();
                        tx.Rollback();
                        return (false, "Purchased pots are received into an active Main Office store.", 0);
                    }
                }

                var poolId = await _emptyPotRepo.GetOrCreateLockedAsync(conn, tx, entry.PotSize!, entry.AreaId, entry.CreatedBy);
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "EPP", entry.PurchaseDate.Year);

                var insert = new SqlCommand(@"
INSERT INTO dbo.EmptyPotPurchases
(PurchaseCode, PurchaseDate, PotSize, Quantity, SupplierName, InvoiceRef, AreaId, EmptyPotInventoryId, Remarks, CreatedById, CreatedBy)
VALUES (@Code, @PurchaseDate, @PotSize, @Quantity, @SupplierName, @InvoiceRef, @AreaId, @PoolId, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@PurchaseDate", entry.PurchaseDate.Date);
                insert.Parameters.AddWithValue("@PotSize", entry.PotSize!);
                insert.Parameters.AddWithValue("@Quantity", entry.Quantity);
                insert.Parameters.AddWithValue("@SupplierName", entry.SupplierName.Trim());
                insert.Parameters.AddWithValue("@InvoiceRef", string.IsNullOrWhiteSpace(entry.InvoiceRef) ? DBNull.Value : entry.InvoiceRef.Trim());
                insert.Parameters.AddWithValue("@AreaId", entry.AreaId);
                insert.Parameters.AddWithValue("@PoolId", poolId);
                insert.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                var newId = (int)(await insert.ExecuteScalarAsync())!;

                var (ok, message) = await _emptyPotRepo.RecordTransactionAsync(
                    conn, tx, poolId, entry.Quantity, "StockIn", "EmptyPotPurchase", newId, userId,
                    $"Purchase {code} from {entry.SupplierName.Trim()}" + (string.IsNullOrWhiteSpace(entry.InvoiceRef) ? "" : $" (invoice {entry.InvoiceRef.Trim()})"));
                if (!ok)
                {
                    tx.Rollback();
                    return (false, message, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.PurchaseCode = code;
                entry.EmptyPotInventoryId = poolId;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }
    }
}
