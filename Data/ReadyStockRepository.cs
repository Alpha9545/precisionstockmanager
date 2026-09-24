using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 25 (Phase K): dbo.ReadyStock + dbo.ReadyStockTransactions --
    // the confirmed-ready stock pool and its dedicated ledger, mirroring
    // SeedStockRepository/PottedPlantStockRepository's established
    // "GetOrCreateLockedAsync + RecordTransactionAsync, one dedicated
    // ledger per stock entity" shape exactly. The one deliberate
    // difference: this pool's business key is SeedSowingId ALONE
    // (UNIQUE), not a Species+Area+Lot composite -- see Decision 22 in
    // PROJECT_DOCUMENTATION.md for why the SeedSowing-scoped grain is
    // required here (batch-identity/traceability preservation).
    public class ReadyStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public ReadyStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT
    rs.Id, rs.SeedSowingId, sw.SowingCode, rs.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    rs.AreaId, a.Name AS AreaName, rs.PolyhouseId, COALESCE(rph.Name, ph.Name) AS PolyhouseName,
    rs.BatchNo, rs.CavityType, rs.SowingDate, rs.Quantity, rs.FirstConfirmationDate,
    sw.QuantitySown, sw.ConfirmedReadyQuantity, sw.WastageQuantity, sw.Status AS SowingStatus,
    appr.ApprovedByName, appr.ApprovalDate,
    rs.CreatedDate, rs.CreatedBy, rs.ModifiedDate, rs.ModifiedBy
FROM dbo.ReadyStock rs
INNER JOIN dbo.SeedSowings sw ON rs.SeedSowingId = sw.Id
INNER JOIN dbo.PlantSpecies ps ON rs.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON rs.AreaId = a.Id
LEFT JOIN dbo.Polyhouses ph ON a.PolyhouseId = ph.Id
LEFT JOIN dbo.Polyhouses rph ON rs.PolyhouseId = rph.Id
OUTER APPLY (
    SELECT TOP 1 u.Name AS ApprovedByName, rc.ConfirmationDate AS ApprovalDate
    FROM dbo.ReadyConfirmations rc
    LEFT JOIN dbo.IMSUsers u ON u.Id = rc.ApprovedById
    WHERE rc.ReadyStockId = rs.Id AND rc.Status = 'Confirmed'
    ORDER BY rc.ConfirmationDate DESC
) appr";

        public async Task<List<ReadyStock>> GetAllAsync()
        {
            var list = new List<ReadyStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY rs.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<ReadyStock?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE rs.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<ReadyStock?> GetBySeedSowingIdAsync(int seedSowingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE rs.SeedSowingId = @SeedSowingId";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Finds-or-creates the ONE ReadyStock row for this Sowing --
        // called under the caller's own transaction
        // (ReadyConfirmationRepository.ConfirmAsync), with the parent
        // SeedSowings row already locked by the caller. Never created
        // eagerly at Sowing time -- only the first time a confirmation
        // actually happens, per the "no automatic Ready Stock creation"
        // rule. Mirrors SeedStockRepository.GetOrCreateLockedAsync.
        public async Task<int> GetOrCreateLockedAsync(
            SqlConnection conn, SqlTransaction tx, int seedSowingId, int speciesId, int areaId, int? polyhouseId,
            string batchNo, string cavityType, DateTime sowingDate, string? createdBy)
        {
            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE SeedSowingId = @SeedSowingId",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return (int)existingId;
            }

            const string insertSql = @"
INSERT INTO dbo.ReadyStock (SeedSowingId, SpeciesId, AreaId, PolyhouseId, BatchNo, CavityType, SowingDate, Quantity, CreatedDate, CreatedBy)
VALUES (@SeedSowingId, @SpeciesId, @AreaId, @PolyhouseId, @BatchNo, @CavityType, @SowingDate, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
            insertCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            insertCmd.Parameters.AddWithValue("@AreaId", areaId);
            insertCmd.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@BatchNo", batchNo ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@CavityType", cavityType);
            insertCmd.Parameters.AddWithValue("@SowingDate", sowingDate.Date);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)await insertCmd.ExecuteScalarAsync();
        }

        // Records a signed movement against an existing (locked)
        // ReadyStock row and writes the matching ledger row in the SAME
        // transaction -- Quantity is never changed any other way.
        // Positive quantityDelta = 'Confirmed'; negative =
        // 'ReversalRemoval' (a cancelled confirmation). Mirrors
        // SeedStockRepository.RecordTransactionAsync exactly.
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int readyStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var lockCmd = new SqlCommand("SELECT Quantity FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", readyStockId);
            var beforeObj = await lockCmd.ExecuteScalarAsync();
            if (beforeObj == null || beforeObj == DBNull.Value)
                return (false, "Ready Stock record not found.");

            var before = (decimal)beforeObj;
            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"This movement would take Ready Stock quantity negative (current {before:N2}, change {quantityDelta:N2}).");

            var updateCmd = new SqlCommand(@"
UPDATE dbo.ReadyStock
SET Quantity = @After,
    FirstConfirmationDate = CASE WHEN FirstConfirmationDate IS NULL AND @After > 0 THEN SYSUTCDATETIME() ELSE FirstConfirmationDate END,
    ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", readyStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string insertTxSql = @"
INSERT INTO dbo.ReadyStockTransactions
(ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@ReadyStockId, SYSUTCDATETIME(), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";
            var insertTxCmd = new SqlCommand(insertTxSql, conn, tx);
            insertTxCmd.Parameters.AddWithValue("@ReadyStockId", readyStockId);
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

        public async Task<List<ReadyStockTransaction>> GetTransactionsAsync(int readyStockId)
        {
            var list = new List<ReadyStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.ReadyStockId, ps.Name AS SpeciesName, rs.BatchNo, t.TransactionDate, t.TransactionType,
       t.ReferenceType, t.ReferenceId, t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName,
       t.Remarks, t.CreatedAt
FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock rs ON t.ReadyStockId = rs.Id
INNER JOIN dbo.PlantSpecies ps ON rs.SpeciesId = ps.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.ReadyStockId = @Id
ORDER BY t.CreatedAt DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", readyStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new ReadyStockTransaction
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    ReadyStockId = reader.GetInt32(reader.GetOrdinal("ReadyStockId")),
                    SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                    BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
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

        private static ReadyStock Map(SqlDataReader reader)
        {
            return new ReadyStock
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SeedSowingId = reader.GetInt32(reader.GetOrdinal("SeedSowingId")),
                SowingCode = reader.GetString(reader.GetOrdinal("SowingCode")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                PolyhouseId = reader.IsDBNull(reader.GetOrdinal("PolyhouseId")) ? null : reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                QuantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown")),
                ApprovedReadyQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity")),
                WastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity")),
                SowingStatus = reader.GetString(reader.GetOrdinal("SowingStatus")),
                ApprovedByName = reader.IsDBNull(reader.GetOrdinal("ApprovedByName")) ? null : reader.GetString(reader.GetOrdinal("ApprovedByName")),
                ApprovalDate = reader.IsDBNull(reader.GetOrdinal("ApprovalDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ApprovalDate")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                CavityType = reader.GetString(reader.GetOrdinal("CavityType")),
                SowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                FirstConfirmationDate = reader.IsDBNull(reader.GetOrdinal("FirstConfirmationDate")) ? null : reader.GetDateTime(reader.GetOrdinal("FirstConfirmationDate")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
