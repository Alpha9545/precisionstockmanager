using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 22/Phase H: dbo.SeedStock + dbo.SeedStockTransactions.
    // Mirrors PottedPlantStockRepository/EmptyPotInventoryRepository's
    // established shape exactly (RecordTransactionAsync/
    // ReserveInTransitAsync/ReleaseInTransitAsync/GetOrCreateLockedAsync)
    // -- new tables, but a faithful, deliberate mirror of the existing
    // pattern rather than a novel design.
    public class SeedStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public SeedStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT
    ss.Id, ss.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    ss.AreaId, a.Name AS AreaName, a.AreaType,
    ss.BatchNo, ss.SeedSourceId, src.Name AS SeedSourceName, ss.Unit,
    ss.PhysicalQuantity, ss.InTransitQuantity, ss.AvailableQuantity,
    ss.CreatedDate, ss.CreatedBy, ss.ModifiedDate, ss.ModifiedBy
FROM dbo.SeedStock ss
INNER JOIN dbo.PlantSpecies ps ON ss.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON ss.AreaId = a.Id
LEFT JOIN dbo.SeedSources src ON ss.SeedSourceId = src.Id";

        public async Task<List<SeedStock>> GetAllAsync()
        {
            var list = new List<SeedStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY a.Name, ps.Name, ss.BatchNo";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<SeedStock?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE ss.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Creates a new, empty (PhysicalQuantity = 0) pool for a
        // Species+Area+Lot combination that does not exist yet.
        // AddStockAsync is used afterwards to bring in physical
        // quantity. Mirrors EmptyPotInventoryRepository.InsertAsync.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(SeedStock entry)
        {
            if (entry.SpeciesId <= 0)
                return (false, "Species/Variety is required.", 0);
            if (entry.AreaId <= 0)
                return (false, "Area is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                // Phase B: new seed is received ONLY into Main Office Seed
                // Stock (the single operational source for Direct Sowing).
                using (var areaCmd = new SqlCommand("SELECT AreaType, IsActive FROM dbo.Area WHERE Id = @AreaId", conn))
                {
                    areaCmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                    using var areaReader = await areaCmd.ExecuteReaderAsync();
                    if (!await areaReader.ReadAsync()
                        || !PlantStockManager.Services.DirectSowingRules.IsMainOfficeSeedLocation(
                               areaReader.IsDBNull(0) ? null : areaReader.GetString(0), areaReader.GetBoolean(1)))
                        return (false, "Seed Stock can only be created at an active Main Office Area.", 0);
                }

                const string insertSql = @"
INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, SeedSourceId, Unit, PhysicalQuantity, InTransitQuantity, CreatedDate, CreatedBy)
VALUES (@SpeciesId, @AreaId, @BatchNo, @SeedSourceId, @Unit, 0, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@BatchNo", entry.BatchNo ?? string.Empty);
                cmd.Parameters.AddWithValue("@SeedSourceId", (object?)entry.SeedSourceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(entry.Unit) ? "pcs" : entry.Unit);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();
                return (true, null, newId);
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return (false, "A Seed Stock pool for this Species/Area/Lot combination already exists.", 0);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, 0);
            }
        }

        // Records a manual stock receipt ('StockIn') directly against an
        // existing pool -- e.g. Main Office receiving seed from a
        // supplier. Mirrors EmptyPotInventoryRepository.AddStockAsync.
        public async Task<(bool Success, string? Message)> AddStockAsync(int seedStockId, decimal quantity, int? userId, string? remarks)
        {
            if (quantity <= 0)
                return (false, "Quantity to add must be greater than zero.");
            if (!PlantStockManager.Services.DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity to add must be a whole number.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var (success, message) = await RecordTransactionAsync(conn, tx, seedStockId, quantity, "StockIn", "SeedStock", seedStockId, userId, remarks);
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

        // Finds-or-creates the destination pool at (SpeciesId, AreaId,
        // BatchNo) -- called under the caller's own transaction, while
        // the source row is already locked, mirroring
        // PottedPlantStockRepository.GetOrCreateLockedAsync exactly.
        public async Task<int> GetOrCreateLockedAsync(
            SqlConnection conn, SqlTransaction tx, int speciesId, int areaId, string batchNo, int? seedSourceId, string? createdBy)
        {
            batchNo ??= string.Empty;

            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE SpeciesId = @SpeciesId AND AreaId = @AreaId AND BatchNo = @BatchNo",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            lockCmd.Parameters.AddWithValue("@AreaId", areaId);
            lockCmd.Parameters.AddWithValue("@BatchNo", batchNo);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return (int)existingId;
            }

            const string insertSql = @"
INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, SeedSourceId, Unit, PhysicalQuantity, InTransitQuantity, CreatedDate, CreatedBy)
VALUES (@SpeciesId, @AreaId, @BatchNo, @SeedSourceId, 'pcs', 0, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            insertCmd.Parameters.AddWithValue("@AreaId", areaId);
            insertCmd.Parameters.AddWithValue("@BatchNo", batchNo);
            insertCmd.Parameters.AddWithValue("@SeedSourceId", (object?)seedSourceId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)await insertCmd.ExecuteScalarAsync();
        }

        // Records a signed stock movement and writes the matching
        // ledger row in the SAME transaction -- PhysicalQuantity is
        // never changed any other way. Enforces PhysicalQuantity >= 0
        // under lock. Mirrors PottedPlantStockRepository.RecordTransactionAsync.
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int seedStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var lockCmd = new SqlCommand("SELECT PhysicalQuantity FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", seedStockId);
            var beforeObj = await lockCmd.ExecuteScalarAsync();
            if (beforeObj == null || beforeObj == DBNull.Value)
                return (false, "Seed Stock record not found.");

            var before = (decimal)beforeObj;
            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"This movement would take Physical Quantity negative (current {before:N2}, change {quantityDelta:N2}).");

            var updateCmd = new SqlCommand(
                "UPDATE dbo.SeedStock SET PhysicalQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", seedStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string insertTxSql = @"
INSERT INTO dbo.SeedStockTransactions
(SeedStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@SeedStockId, SYSUTCDATETIME(), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";
            var insertTxCmd = new SqlCommand(insertTxSql, conn, tx);
            insertTxCmd.Parameters.AddWithValue("@SeedStockId", seedStockId);
            insertTxCmd.Parameters.AddWithValue("@TransactionType", transactionType);
            insertTxCmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            insertTxCmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            insertTxCmd.Parameters.AddWithValue("@Quantity", quantityDelta);
            insertTxCmd.Parameters.AddWithValue("@BeforeQuantity", before);
            insertTxCmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            insertTxCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await insertTxCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // Raises InTransitQuantity only -- PhysicalQuantity and the
        // ledger are untouched (nothing has actually moved yet).
        // Refuses if not enough is available so the same physical seed
        // stock can't be issued twice while an earlier issue is still
        // awaiting confirmation. Returns the pool's AreaId/SpeciesId/
        // BatchNo/SeedSourceId so the caller doesn't need a second
        // lookup. Mirrors PottedPlantStockRepository.ReserveInTransitAsync
        // exactly (this table has no ReservedQuantity concept at all, so
        // "available to issue" is simply Physical - InTransit here).
        public async Task<(bool Success, string? Message, int AreaId, int SpeciesId, string BatchNo, int? SeedSourceId)> ReserveInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int seedStockId, decimal quantity)
        {
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0, 0, string.Empty, null);

            var lockCmd = new SqlCommand(
                "SELECT AreaId, SpeciesId, BatchNo, SeedSourceId, PhysicalQuantity, InTransitQuantity FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", seedStockId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Seed Stock record not found.", 0, 0, string.Empty, null);
            }
            var areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
            var speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
            var batchNo = reader.GetString(reader.GetOrdinal("BatchNo"));
            var seedSourceId = reader.IsDBNull(reader.GetOrdinal("SeedSourceId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SeedSourceId"));
            var physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
            var inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
            reader.Close();

            var availableToIssue = physical - inTransit;
            if (quantity > availableToIssue)
                return (false, $"Insufficient available Seed Stock to issue (available {availableToIssue:N2}, requested {quantity:N2}).", 0, 0, string.Empty, null);

            var updateCmd = new SqlCommand(
                "UPDATE dbo.SeedStock SET InTransitQuantity = InTransitQuantity + @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", seedStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null, areaId, speciesId, batchNo, seedSourceId);
        }

        // Releases a previously-reserved quantity back to "available to
        // issue" without touching PhysicalQuantity or the ledger --
        // used when a pending Seed Issue is rejected outright (releases
        // the full issued quantity, since nothing ever left) or when a
        // discrepancy is confirmed (releases just the shortfall).
        // Mirrors PottedPlantStockRepository.ReleaseInTransitAsync exactly.
        public async Task<(bool Success, string? Message)> ReleaseInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int seedStockId, decimal quantity)
        {
            if (quantity < 0)
                return (false, "Release quantity cannot be negative.");
            if (quantity == 0)
                return (true, null);

            var lockCmd = new SqlCommand(
                "SELECT InTransitQuantity FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", seedStockId);
            var inTransitObj = await lockCmd.ExecuteScalarAsync();
            if (inTransitObj == null || inTransitObj == DBNull.Value)
                return (false, "Seed Stock record not found.");

            var inTransit = (decimal)inTransitObj;
            if (quantity > inTransit)
                return (false, $"Cannot release {quantity:N2} -- only {inTransit:N2} is currently in transit for this pool.");

            var updateCmd = new SqlCommand(
                "UPDATE dbo.SeedStock SET InTransitQuantity = InTransitQuantity - @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", seedStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        public async Task<List<SeedStockTransaction>> GetTransactionsAsync(int seedStockId)
        {
            var list = new List<SeedStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.SeedStockId, ps.Name AS SpeciesName, a.Name AS AreaName, t.TransactionDate, t.TransactionType,
       t.ReferenceType, t.ReferenceId, t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName,
       t.Remarks, t.CreatedAt
FROM dbo.SeedStockTransactions t
INNER JOIN dbo.SeedStock s ON t.SeedStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.SeedStockId = @Id
ORDER BY t.CreatedAt DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", seedStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new SeedStockTransaction
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    SeedStockId = reader.GetInt32(reader.GetOrdinal("SeedStockId")),
                    SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                    AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                    TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                    TransactionType = reader.GetString(reader.GetOrdinal("TransactionType")),
                    ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                    ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                    Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                    BeforeQuantity = reader.GetDecimal(reader.GetOrdinal("BeforeQuantity")),
                    AfterQuantity = reader.GetDecimal(reader.GetOrdinal("AfterQuantity")),
                    UserId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? null : reader.GetInt32(reader.GetOrdinal("UserId")),
                    UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                    Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
                });
            }
            return list;
        }

        private static SeedStock Map(SqlDataReader reader)
        {
            return new SeedStock
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                AreaType = reader.IsDBNull(reader.GetOrdinal("AreaType")) ? null : reader.GetString(reader.GetOrdinal("AreaType")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                SeedSourceId = reader.IsDBNull(reader.GetOrdinal("SeedSourceId")) ? null : reader.GetInt32(reader.GetOrdinal("SeedSourceId")),
                SeedSourceName = reader.IsDBNull(reader.GetOrdinal("SeedSourceName")) ? null : reader.GetString(reader.GetOrdinal("SeedSourceName")),
                Unit = reader.GetString(reader.GetOrdinal("Unit")),
                PhysicalQuantity = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity")),
                InTransitQuantity = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity")),
                AvailableQuantity = reader.GetDecimal(reader.GetOrdinal("AvailableQuantity")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
