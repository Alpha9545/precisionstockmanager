using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Stock repository for dbo.EmptyPotInventory, with its own dedicated
    // ledger (dbo.EmptyPotInventoryTransactions) per the approved
    // "separate ledger per stock entity" decision. PhysicalQuantity on
    // the stock row is NEVER updated directly outside of a matching
    // ledger insert in the SAME transaction -- every method below either
    // writes both together or writes neither.
    public class EmptyPotInventoryRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public EmptyPotInventoryRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Phase E: adds GrowingPartnerName via Area.GrowingPartnerId, no new
        // column on dbo.EmptyPotInventory.
        private const string BaseSelect = @"
SELECT e.Id, e.PotSize, e.AreaId, a.Name AS AreaName, gp.Name AS GrowingPartnerName, e.PhysicalQuantity, e.IsActive,
       e.CreatedDate, e.CreatedBy, e.ModifiedDate, e.ModifiedBy
FROM dbo.EmptyPotInventory e
LEFT JOIN dbo.Area a ON e.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id";

        public async Task<List<EmptyPotInventory>> GetAllAsync(bool activeOnly = false)
        {
            var list = new List<EmptyPotInventory>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + (activeOnly ? " WHERE e.IsActive = 1" : "") + " ORDER BY e.PotSize, a.Name";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<EmptyPotInventory?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE e.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // A Pot Size's stock is tracked per-Area since Phase 8 -- looks
        // up the pool for a specific (PotSize, AreaId) combination.
        // areaId = null matches the legacy/"unassigned location" pool.
        public async Task<EmptyPotInventory?> GetByPotSizeAndAreaAsync(string potSize, int? areaId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE e.PotSize = @PotSize AND (e.AreaId = @AreaId OR (e.AreaId IS NULL AND @AreaId IS NULL))";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@PotSize", potSize);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Defines a new Pot Size record in a specific Area. Starts at
        // zero -- use AddStockAsync afterwards to bring in physical
        // stock, so every quantity change (including the very first
        // one) goes through the ledger.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(EmptyPotInventory entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string insertSql = @"
INSERT INTO dbo.EmptyPotInventory (PotSize, AreaId, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
VALUES (@PotSize, @AreaId, 0, @IsActive, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IsActive", entry.IsActive);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();
                entry.Id = newId;
                return (true, null, newId);
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
            {
                return (false, $"A Pot Size named '{entry.PotSize}' already exists in that Area.", 0);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, 0);
            }
        }

        // Gets the existing (PotSize, AreaId) pool row, locked for the
        // duration of the caller's transaction, or creates a new zeroed
        // row and locks that instead. Must be called from inside an
        // existing transaction -- used by InternalTransferRepository to
        // resolve/create the destination pool.
        public async Task<int> GetOrCreateLockedAsync(SqlConnection conn, SqlTransaction tx, string potSize, int? areaId, string? createdBy)
        {
            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.EmptyPotInventory WITH (UPDLOCK, HOLDLOCK) WHERE PotSize = @PotSize AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@PotSize", potSize);
            lockCmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return (int)existingId;
            }

            const string insertSql = @"
INSERT INTO dbo.EmptyPotInventory (PotSize, AreaId, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
VALUES (@PotSize, @AreaId, 0, 1, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@PotSize", potSize);
            insertCmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)await insertCmd.ExecuteScalarAsync();
        }

        // Records a stock movement against an existing Pot Size and
        // writes the matching ledger row in the SAME transaction --
        // PhysicalQuantity is never changed any other way.
        //
        // quantityDelta: positive for stock brought in ('StockIn'),
        // negative for consumption/removal ('Consumption', 'Adjustment',
        // etc). The caller supplies transactionType/referenceType/
        // referenceId so this same method can be reused both for manual
        // stock-in (Pages/Production/EmptyPotInventory/AddStock) and for
        // the automatic consumption written by PotProductionRepository.
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int emptyPotInventoryId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var lockCmd = new SqlCommand("SELECT PhysicalQuantity FROM dbo.EmptyPotInventory WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", emptyPotInventoryId);
            var beforeObj = await lockCmd.ExecuteScalarAsync();
            if (beforeObj == null)
                return (false, "Empty Pot Inventory record not found.");

            var before = (decimal)beforeObj;
            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"This would take Empty Pot stock negative (available {before:N2}, requested change {quantityDelta:N2}).");

            var updateCmd = new SqlCommand("UPDATE dbo.EmptyPotInventory SET PhysicalQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", emptyPotInventoryId);
            await updateCmd.ExecuteNonQueryAsync();

            const string ledgerSql = @"
INSERT INTO dbo.EmptyPotInventoryTransactions
(EmptyPotInventoryId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@EmptyPotInventoryId, SYSUTCDATETIME(), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";

            var ledgerCmd = new SqlCommand(ledgerSql, conn, tx);
            ledgerCmd.Parameters.AddWithValue("@EmptyPotInventoryId", emptyPotInventoryId);
            ledgerCmd.Parameters.AddWithValue("@TransactionType", transactionType);
            ledgerCmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Quantity", quantityDelta);
            ledgerCmd.Parameters.AddWithValue("@BeforeQuantity", before);
            ledgerCmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await ledgerCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // Convenience wrapper for manual stock movements (Add Stock /
        // Adjustment pages) that own their own connection+transaction,
        // as opposed to PotProductionRepository, which calls
        // RecordTransactionAsync directly inside its own larger
        // transaction spanning multiple tables.
        public async Task<(bool Success, string? Message)> AddStockAsync(int emptyPotInventoryId, decimal quantityDelta, string transactionType, int? userId, string? remarks)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var (success, message) = await RecordTransactionAsync(conn, tx, emptyPotInventoryId, quantityDelta, transactionType, "ManualAdjustment", null, userId, remarks);
                if (!success)
                {
                    tx.Rollback();
                    return (false, message);
                }

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        public async Task<List<EmptyPotInventoryTransaction>> GetTransactionsAsync(int emptyPotInventoryId)
        {
            var list = new List<EmptyPotInventoryTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.EmptyPotInventoryId, e.PotSize, t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName, t.Remarks, t.CreatedAt
FROM dbo.EmptyPotInventoryTransactions t
INNER JOIN dbo.EmptyPotInventory e ON t.EmptyPotInventoryId = e.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.EmptyPotInventoryId = @Id
ORDER BY t.CreatedAt DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", emptyPotInventoryId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new EmptyPotInventoryTransaction
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    EmptyPotInventoryId = reader.GetInt32(reader.GetOrdinal("EmptyPotInventoryId")),
                    PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                    TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                    TransactionType = reader.GetString(reader.GetOrdinal("TransactionType")),
                    ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                    ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                    Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                    BeforeQuantity = reader.GetDecimal(reader.GetOrdinal("BeforeQuantity")),
                    UserId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? null : reader.GetInt32(reader.GetOrdinal("UserId")),
                    UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                    Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
                });
            }
            return list;
        }

        private static EmptyPotInventory Map(SqlDataReader reader)
        {
            return new EmptyPotInventory
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                PhysicalQuantity = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity")),
                IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
