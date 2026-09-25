using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using System.Linq;

namespace PlantStockManager.Data
{
    // dbo.CuttingStock + dbo.CuttingStockTransactions (Phase 15). Mirrors
    // EmptyPotInventoryRepository's GetOrCreateLockedAsync/
    // RecordTransactionAsync pattern exactly, so InternalTransferRepository
    // can treat all three stock kinds (EmptyPot/PottedPlant/Cutting) the
    // same way. PhysicalQuantity is never written anywhere except inside
    // RecordTransactionAsync, in the same database transaction as its
    // matching ledger row.
    public class CuttingStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public CuttingStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT c.Id, c.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
       c.AreaId, a.Name AS AreaName, a.AreaType, gp.Name AS GrowingPartnerName,
       c.PhysicalQuantity, c.InTransitQuantity, c.AvailableQuantity,
       c.CreatedDate, c.CreatedBy, c.ModifiedDate, c.ModifiedBy
FROM dbo.CuttingStock c
INNER JOIN dbo.PlantSpecies ps ON c.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON c.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id";

        public async Task<List<CuttingStock>> GetAllAsync(int? areaId = null, int? speciesId = null)
        {
            var list = new List<CuttingStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@AreaId IS NULL OR c.AreaId = @AreaId)
  AND (@SpeciesId IS NULL OR c.SpeciesId = @SpeciesId)
ORDER BY a.Name, ps.Name";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", (object?)speciesId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<CuttingStock?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE c.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Get-or-create the (SpeciesId, AreaId) pool, locked for the
        // duration of the caller's transaction -- same pattern as
        // EmptyPotInventoryRepository.GetOrCreateLockedAsync.
        public async Task<int> GetOrCreateLockedAsync(SqlConnection conn, SqlTransaction tx, int speciesId, int areaId, string? createdBy)
        {
            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE SpeciesId = @SpeciesId AND AreaId = @AreaId",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            lockCmd.Parameters.AddWithValue("@AreaId", areaId);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return (int)existingId;
            }

            const string insertSql = @"
INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy)
VALUES (@SpeciesId, @AreaId, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            insertCmd.Parameters.AddWithValue("@AreaId", areaId);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)await insertCmd.ExecuteScalarAsync();
        }

        // quantityDelta: positive for stock coming in ('Harvest', the
        // destination side of a confirmed 'Transfer'), negative for
        // stock going out (the source side of a confirmed 'Transfer',
        // future 'Potted' consumption). Never called outside of an
        // existing transaction -- callers own the SqlTransaction.
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int cuttingStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var lockCmd = new SqlCommand("SELECT PhysicalQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            var beforeObj = await lockCmd.ExecuteScalarAsync();
            if (beforeObj == null)
                return (false, "Cutting Stock record not found.");

            var before = (decimal)beforeObj;
            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"This would take Cutting stock negative (available {before:N2}, requested change {quantityDelta:N2}).");

            var updateCmd = new SqlCommand("UPDATE dbo.CuttingStock SET PhysicalQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string ledgerSql = @"
INSERT INTO dbo.CuttingStockTransactions
(CuttingStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@CuttingStockId, SYSUTCDATETIME(), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";

            var ledgerCmd = new SqlCommand(ledgerSql, conn, tx);
            ledgerCmd.Parameters.AddWithValue("@CuttingStockId", cuttingStockId);
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

        // Phase 16 (Model B): reserves quantity against this pool's
        // AvailableQuantity for a NEW outbound Cutting transfer being
        // created ("Give Cutting to Main Office") -- raises
        // InTransitQuantity, leaves PhysicalQuantity and the ledger
        // untouched (nothing has actually moved yet), and refuses if not
        // enough is available so the same physical cuttings can't be sent
        // twice while a transfer is already in flight. Returns the pool's
        // AreaId/SpeciesId so the caller (InsertAsync) doesn't need a
        // second lookup.
        public async Task<(bool Success, string? Message, int AreaId, int SpeciesId)> ReserveInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int cuttingStockId, decimal quantity)
        {
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0, 0);

            var lockCmd = new SqlCommand(
                "SELECT AreaId, SpeciesId, PhysicalQuantity, InTransitQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Cutting Stock record not found.", 0, 0);
            }
            var areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
            var speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
            var physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
            var inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
            reader.Close();

            var available = physical - inTransit;
            if (quantity > available)
                return (false, $"Insufficient available Cutting stock (available {available:N2}, requested {quantity:N2}).", 0, 0);

            var updateCmd = new SqlCommand(
                "UPDATE dbo.CuttingStock SET InTransitQuantity = InTransitQuantity + @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null, areaId, speciesId);
        }

        // Phase 16 (Model B): releases a previously-reserved quantity back
        // to AvailableQuantity without touching PhysicalQuantity or the
        // ledger -- used when Main Office confirms receipt with a
        // discrepancy (releases just the shortfall) and when a pending
        // transfer is rejected outright (releases the full sent quantity,
        // since nothing ever left).
        public async Task<(bool Success, string? Message)> ReleaseInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int cuttingStockId, decimal quantity)
        {
            if (quantity < 0)
                return (false, "Release quantity cannot be negative.");
            if (quantity == 0)
                return (true, null);

            var lockCmd = new SqlCommand(
                "SELECT InTransitQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            var inTransitObj = await lockCmd.ExecuteScalarAsync();
            if (inTransitObj == null || inTransitObj == DBNull.Value)
                return (false, "Cutting Stock record not found.");

            var inTransit = (decimal)inTransitObj;
            if (quantity > inTransit)
                return (false, $"Cannot release {quantity:N2} -- only {inTransit:N2} is currently in transit for this pool.");

            var updateCmd = new SqlCommand(
                "UPDATE dbo.CuttingStock SET InTransitQuantity = InTransitQuantity - @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // Phase 16 (Model B): the ONE point a Cutting transfer's stock
        // actually moves. Decreases PhysicalQuantity AND releases the
        // matching InTransitQuantity together, and writes exactly one
        // 'Transplanted' ledger row -- one-sided, by design: nothing is
        // credited anywhere else. The destination Polyhouse never gets a
        // CuttingStock row touched on its behalf.
        public async Task<(bool Success, string? Message)> RecordTransplantAsync(
            SqlConnection conn, SqlTransaction tx, int cuttingStockId, decimal quantity,
            string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            if (quantity < 0)
                return (false, "Transplant quantity cannot be negative.");

            var lockCmd = new SqlCommand(
                "SELECT PhysicalQuantity, InTransitQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Cutting Stock record not found.");
            }
            var before = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
            var inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
            reader.Close();

            if (quantity > before)
                return (false, $"This would take Cutting stock negative (available {before:N2}, requested change {quantity:N2}).");
            var releaseFromInTransit = Math.Min(quantity, inTransit);
            var after = before - quantity;

            var updateCmd = new SqlCommand(
                "UPDATE dbo.CuttingStock SET PhysicalQuantity = @After, InTransitQuantity = InTransitQuantity - @Release, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Release", releaseFromInTransit);
            updateCmd.Parameters.AddWithValue("@Id", cuttingStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string ledgerSql = @"
INSERT INTO dbo.CuttingStockTransactions
(CuttingStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@CuttingStockId, SYSUTCDATETIME(), 'Transplanted', @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";

            var ledgerCmd = new SqlCommand(ledgerSql, conn, tx);
            ledgerCmd.Parameters.AddWithValue("@CuttingStockId", cuttingStockId);
            ledgerCmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Quantity", -quantity);
            ledgerCmd.Parameters.AddWithValue("@BeforeQuantity", before);
            ledgerCmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await ledgerCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // "Enter Cutting": a fresh, independent harvest record -- no
        // CuttingPlan/ActualCutting link, per the business owner's
        // confirmed decision. Owns its own transaction (unlike
        // RecordTransactionAsync, which assumes a caller-owned one)
        // because nothing else needs to happen alongside it.
        public async Task<(bool Success, string? Message, int CuttingStockId)> EnterCuttingAsync(
            int speciesId, int areaId, decimal quantity, int? userId, string? createdBy, string? remarks)
        {
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var stockId = await GetOrCreateLockedAsync(conn, tx, speciesId, areaId, createdBy);
                var (success, message) = await RecordTransactionAsync(conn, tx, stockId, quantity, "Harvest", null, null, userId, remarks);
                if (!success)
                {
                    tx.Rollback();
                    return (false, message, 0);
                }

                tx.Commit();
                return (true, null, stockId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        public async Task<List<CuttingStockTransaction>> GetTransactionsAsync(int cuttingStockId)
        {
            var list = new List<CuttingStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.CuttingStockId, ps.Name AS SpeciesName, a.Name AS AreaName,
       t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName, t.Remarks, t.CreatedAt
FROM dbo.CuttingStockTransactions t
INNER JOIN dbo.CuttingStock c ON t.CuttingStockId = c.Id
INNER JOIN dbo.PlantSpecies ps ON c.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON c.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.CuttingStockId = @Id
ORDER BY t.CreatedAt DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", cuttingStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(MapTransaction(reader));
            }
            return list;
        }

        // Phase 3 (Mother Plant -> Cutting Production): "Actual Cutting
        // Quantity" and harvest history for one Mother Plant record.
        // CuttingStock/its ledger are pooled by (SpeciesId, AreaId), not by
        // MotherPlantId (Model B deliberately has no Cutting Plan/Actual
        // Cutting link -- see EnterCutting.cshtml.cs) -- so a Mother Plant's
        // own harvested quantity is every 'Harvest' transaction against ITS
        // OWN Species+Area pool, from its Planting Date onward (a later
        // Mother Plant batch of the same Species+Area does not inherit an
        // earlier batch's harvest history). Read-only; no schema change,
        // no new table -- same "query existing data" approach as every
        // other read-only aggregate in this codebase (e.g. Phase E).
        public async Task<(decimal TotalQuantity, List<CuttingStockTransaction> Recent)> GetHarvestSummaryAsync(int speciesId, int areaId, DateTime since)
        {
            var recent = new List<CuttingStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.CuttingStockId, ps.Name AS SpeciesName, a.Name AS AreaName,
       t.TransactionDate, t.TransactionType, t.ReferenceType, t.ReferenceId,
       t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName, t.Remarks, t.CreatedAt
FROM dbo.CuttingStockTransactions t
INNER JOIN dbo.CuttingStock c ON t.CuttingStockId = c.Id
INNER JOIN dbo.PlantSpecies ps ON c.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON c.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE c.SpeciesId = @SpeciesId AND c.AreaId = @AreaId
  AND t.TransactionType = 'Harvest' AND t.TransactionDate >= @Since
ORDER BY t.TransactionDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            cmd.Parameters.AddWithValue("@Since", since.Date);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                recent.Add(MapTransaction(reader));
            }
            return (recent.Sum(t => t.Quantity), recent);
        }

        private static CuttingStockTransaction MapTransaction(SqlDataReader reader)
        {
            return new CuttingStockTransaction
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                CuttingStockId = reader.GetInt32(reader.GetOrdinal("CuttingStockId")),
                SpeciesName = reader.IsDBNull(reader.GetOrdinal("SpeciesName")) ? null : reader.GetString(reader.GetOrdinal("SpeciesName")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
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
            };
        }

        private static CuttingStock Map(SqlDataReader reader)
        {
            return new CuttingStock
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.IsDBNull(reader.GetOrdinal("SpeciesName")) ? null : reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.IsDBNull(reader.GetOrdinal("PlantTypeName")) ? null : reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                AreaType = reader.IsDBNull(reader.GetOrdinal("AreaType")) ? null : reader.GetString(reader.GetOrdinal("AreaType")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
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
