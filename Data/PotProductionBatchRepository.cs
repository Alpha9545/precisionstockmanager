using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 31 (Phase 6): the batch header for Cutting-to-Potted-Plant
    // Production -- Cutting Stock -> plan a batch (Area/Variety/Species/
    // Pot Size/Cutting source/Expected Ready Date/planned cutting
    // quantity) -> one or more daily dbo.PotProduction entries (still
    // recorded through PotProductionRepository.InsertFromCuttingStockAsync,
    // unchanged in its own stock-movement logic, now batch-aware) ->
    // Ready confirmation. Mirrors CuttingSowingRepository's shape
    // (InsertAsync/GetAlertCandidatesAsync/CancelAsync) but this header
    // creates NO stock movement of its own -- it is a plan only; every
    // quantity change happens at the daily-entry step, exactly like
    // ReadyConfirmation only creating Ready Stock on the first actual
    // confirmation, never at Sowing time.
    public class PotProductionBatchRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public PotProductionBatchRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT
    b.Id, b.BatchCode, b.SourceCuttingStockId,
    b.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    b.AreaId, a.Name AS AreaName, gp.Name AS GrowingPartnerName,
    b.PotSize, b.EmptyPotInventoryId, b.ExpectedReadyDate,
    b.PlannedCuttingQuantity, b.CuttingQuantityConsumedTotal, b.QuantityProducedTotal,
    b.Status, b.SupervisorId, sup.Name AS SupervisorName,
    b.Remarks, b.CreatedDate, b.CreatedBy, b.CreatedById, b.ModifiedDate, b.ModifiedBy
FROM dbo.PotProductionBatches b
INNER JOIN dbo.PlantSpecies ps ON b.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON b.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
LEFT JOIN dbo.IMSUsers sup ON b.SupervisorId = sup.Id";

        public async Task<List<PotProductionBatch>> GetAllAsync(string? status = null)
        {
            var list = new List<PotProductionBatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE (@Status IS NULL OR b.Status = @Status) ORDER BY b.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        public async Task<PotProductionBatch?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE b.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return Map(reader);
            return null;
        }

        public async Task<List<PotProductionBatch>> GetByAreaAsync(int areaId)
        {
            var list = new List<PotProductionBatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE b.AreaId = @AreaId ORDER BY b.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        // Ready Alerts candidate list (rule 7) -- an active batch (never
        // yet Ready-confirmed) whose Expected Ready Date is inside the
        // window. Uses the SAME classification function as Seed Sowing
        // and Cutting Sowing (SeedSowingRepository.ClassifyReadyAlert
        // takes only primitive status/date/window arguments -- no
        // sowing-specific logic, reused unchanged).
        public async Task<List<PotProductionBatch>> GetAlertCandidatesAsync(DateTime horizon)
        {
            var list = new List<PotProductionBatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE b.Status = 'InProduction' AND b.ExpectedReadyDate <= @Horizon
ORDER BY b.ExpectedReadyDate";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Horizon", horizon.Date);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        // Plans a new batch. NO stock movement happens here -- Area,
        // Species and the Empty Pot pool are all derived from the locked
        // source Cutting Stock row (rule 5: an Area can never use empty
        // pots issued to another Area -- structurally enforced by fixing
        // this batch's own EmptyPotInventoryId, once, to a pool that
        // already belongs to the source's own Area). PlannedCuttingQuantity
        // is checked against the source's own available quantity as an
        // early, non-authoritative guard -- the authoritative check
        // happens again, under lock, on every daily entry
        // (PotProductionRepository.InsertFromCuttingStockAsync).
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(PotProductionBatch entry, int? userId, Func<int, bool>? canAccessArea = null)
        {
            if (entry.SourceCuttingStockId <= 0)
                return (false, "Source Cutting Stock is required.", 0);
            if (entry.PlannedCuttingQuantity <= 0)
                return (false, "Planned Cutting Quantity must be greater than zero.", 0);
            if (string.IsNullOrWhiteSpace(entry.PotSize))
                return (false, "Pot Size is required.", 0);
            if (entry.ExpectedReadyDate == default)
                return (false, "Expected Ready Date is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the source Cutting Stock pool -- same discipline
                // as CuttingSowingRepository.InsertAsync's own step 1.
                var lockCmd = new SqlCommand(@"
SELECT cs.SpeciesId, cs.AreaId, cs.PhysicalQuantity, cs.InTransitQuantity, a.IsActive
FROM dbo.CuttingStock cs WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Area a ON a.Id = cs.AreaId
WHERE cs.Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.SourceCuttingStockId);
                int speciesId, areaId;
                decimal physical, inTransit;
                bool areaActive;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Selected Cutting Stock pool not found.", 0);
                    }
                    speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
                    areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
                    physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
                    inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
                    areaActive = reader.GetBoolean(reader.GetOrdinal("IsActive"));
                }

                if (!areaActive)
                {
                    tx.Rollback();
                    return (false, "The selected Cutting Stock pool's Area is inactive.", 0);
                }
                if (canAccessArea != null && !canAccessArea(areaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to plan Pot Production from the selected Area.", 0);
                }

                var available = physical - inTransit;
                if (entry.PlannedCuttingQuantity > available)
                {
                    tx.Rollback();
                    return (false, $"Insufficient available Cutting Stock (available {available:N2}, requested {entry.PlannedCuttingQuantity:N2}).", 0);
                }

                // 2) The Pot Size's pool must already belong to the SAME
                // Area as the source Cutting Stock -- rule 5, structurally.
                var potCmd = new SqlCommand(
                    "SELECT Id, IsActive FROM dbo.EmptyPotInventory WHERE PotSize = @PotSize AND AreaId = @AreaId",
                    conn, tx);
                potCmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                potCmd.Parameters.AddWithValue("@AreaId", areaId);
                int emptyPotInventoryId;
                using (var potReader = await potCmd.ExecuteReaderAsync())
                {
                    if (!await potReader.ReadAsync())
                    {
                        potReader.Close();
                        tx.Rollback();
                        return (false, "Selected Pot Size does not exist in Empty Pot Inventory for this Cutting Stock's Area.", 0);
                    }
                    emptyPotInventoryId = potReader.GetInt32(potReader.GetOrdinal("Id"));
                    var potActive = potReader.GetBoolean(potReader.GetOrdinal("IsActive"));
                    if (!potActive)
                    {
                        potReader.Close();
                        tx.Rollback();
                        return (false, "Selected Pot Size is inactive.", 0);
                    }
                }

                entry.SpeciesId = speciesId;
                entry.AreaId = areaId;
                entry.EmptyPotInventoryId = emptyPotInventoryId;

                var batchCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "PPB", DateTime.Today.Year);

                const string insertSql = @"
INSERT INTO dbo.PotProductionBatches
(BatchCode, SourceCuttingStockId, SpeciesId, AreaId, PotSize, EmptyPotInventoryId, ExpectedReadyDate,
 PlannedCuttingQuantity, Status, SupervisorId, Remarks, CreatedDate, CreatedBy, CreatedById)
VALUES
(@BatchCode, @SourceCuttingStockId, @SpeciesId, @AreaId, @PotSize, @EmptyPotInventoryId, @ExpectedReadyDate,
 @PlannedCuttingQuantity, 'InProduction', @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@BatchCode", batchCode);
                cmd.Parameters.AddWithValue("@SourceCuttingStockId", entry.SourceCuttingStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                cmd.Parameters.AddWithValue("@EmptyPotInventoryId", entry.EmptyPotInventoryId);
                cmd.Parameters.AddWithValue("@ExpectedReadyDate", entry.ExpectedReadyDate.Date);
                cmd.Parameters.AddWithValue("@PlannedCuttingQuantity", entry.PlannedCuttingQuantity);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedById", (object?)(entry.CreatedById ?? userId) ?? DBNull.Value);

                var newId = (int)(await cmd.ExecuteScalarAsync())!;

                tx.Commit();
                entry.Id = newId;
                entry.BatchCode = batchCode;
                entry.Status = "InProduction";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Called by PotProductionRepository.InsertFromCuttingStockAsync,
        // under the SAME transaction as the daily entry it belongs to --
        // locks the batch row, checks it is still open and that the new
        // entry would not exceed the plan (rule 4), and returns its
        // fixed Area/PotSize/EmptyPotInventoryId/SourceCuttingStockId so
        // the caller can verify the entry never drifts from its own
        // batch's own values.
        public async Task<(bool Ok, string? Error, PotProductionBatch? Batch)> LockForDailyEntryAsync(
            SqlConnection conn, SqlTransaction tx, int batchId, decimal cuttingQuantityConsumed)
        {
            var lockCmd = new SqlCommand(@"
SELECT SourceCuttingStockId, AreaId, PotSize, EmptyPotInventoryId, SpeciesId,
       PlannedCuttingQuantity, CuttingQuantityConsumedTotal, QuantityProducedTotal, Status
FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", batchId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Pot Production Batch not found.", null);
            }
            var batch = new PotProductionBatch
            {
                Id = batchId,
                SourceCuttingStockId = reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                EmptyPotInventoryId = reader.GetInt32(reader.GetOrdinal("EmptyPotInventoryId")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                PlannedCuttingQuantity = reader.GetDecimal(reader.GetOrdinal("PlannedCuttingQuantity")),
                CuttingQuantityConsumedTotal = reader.GetDecimal(reader.GetOrdinal("CuttingQuantityConsumedTotal")),
                QuantityProducedTotal = reader.GetDecimal(reader.GetOrdinal("QuantityProducedTotal")),
                Status = reader.GetString(reader.GetOrdinal("Status"))
            };
            reader.Close();

            if (batch.Status != "InProduction")
                return (false, $"This Pot Production Batch is {batch.Status} and can no longer take daily production entries.", null);

            if (batch.CuttingQuantityConsumedTotal + cuttingQuantityConsumed > batch.PlannedCuttingQuantity)
                return (false, $"This entry ({cuttingQuantityConsumed:N2} cuttings) would push the batch's total consumed to " +
                    $"{(batch.CuttingQuantityConsumedTotal + cuttingQuantityConsumed):N2}, exceeding its planned quantity of {batch.PlannedCuttingQuantity:N2}.", null);

            return (true, null, batch);
        }

        // Rolls the running totals forward -- called by
        // PotProductionRepository.InsertFromCuttingStockAsync, under the
        // same lock/transaction as LockForDailyEntryAsync above, right
        // after its own daily dbo.PotProduction row and stock-movement
        // calls succeed.
        public static async Task AccumulateDailyEntryAsync(
            SqlConnection conn, SqlTransaction tx, int batchId, decimal cuttingQuantityConsumed, decimal quantityProduced)
        {
            var cmd = new SqlCommand(@"
UPDATE dbo.PotProductionBatches
SET CuttingQuantityConsumedTotal = CuttingQuantityConsumedTotal + @Consumed,
    QuantityProducedTotal = QuantityProducedTotal + @Produced
WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Consumed", cuttingQuantityConsumed);
            cmd.Parameters.AddWithValue("@Produced", quantityProduced);
            cmd.Parameters.AddWithValue("@Id", batchId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Rule 6: only the batch's own assigned Supervisor may confirm it
        // Ready -- reuses the EXISTING assigned-supervisor rule
        // (DirectSowingRules.CanApprove, unchanged), the same rule Direct
        // Sowing/Cutting Sowing already use for Ready Confirmation.
        public async Task<(bool Success, string? Message)> ConfirmReadyAsync(int id, int? userId, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SupervisorId, CreatedById, CreatedBy, Status FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                int? supervisorId, createdById;
                string? createdBy;
                string status;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Pot Production Batch not found.");
                    }
                    supervisorId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                    createdById = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    createdBy = reader.IsDBNull(2) ? null : reader.GetString(2);
                    status = reader.GetString(3);
                }

                if (status == "Ready")
                {
                    tx.Rollback();
                    return (false, "This Pot Production Batch is already Ready.");
                }
                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Pot Production Batch is Cancelled and cannot be confirmed Ready.");
                }

                var (ok, error) = DirectSowingRules.CanApprove(supervisorId, createdById, createdBy, userId, null);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error);
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.PotProductionBatches SET Status = 'Ready', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@Id", id);
                await updateCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        // Whole-batch cancel, only while nothing has been produced yet --
        // there is nothing to reverse (no stock was ever moved by the
        // batch header itself), so this simply closes the plan. Once any
        // daily entry exists, cancel/reverse those individual
        // dbo.PotProduction rows instead (PotProductionRepository.CancelAsync,
        // unchanged).
        public async Task<(bool Success, string? Message)> CancelBatchAsync(int id, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT CuttingQuantityConsumedTotal, Status FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                decimal consumedTotal;
                string status;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Pot Production Batch not found.");
                    }
                    consumedTotal = reader.GetDecimal(0);
                    status = reader.GetString(1);
                }

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Pot Production Batch is already Cancelled.");
                }
                if (status == "Ready")
                {
                    tx.Rollback();
                    return (false, "This Pot Production Batch is already Ready and cannot be cancelled.");
                }
                if (consumedTotal > 0)
                {
                    tx.Rollback();
                    return (false, "This Pot Production Batch already has daily production recorded against it and cannot be cancelled. Cancel the individual daily entries instead.");
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.PotProductionBatches SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@Id", id);
                await updateCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static PotProductionBatch Map(SqlDataReader reader)
        {
            return new PotProductionBatch
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                BatchCode = reader.GetString(reader.GetOrdinal("BatchCode")),
                SourceCuttingStockId = reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                EmptyPotInventoryId = reader.GetInt32(reader.GetOrdinal("EmptyPotInventoryId")),
                ExpectedReadyDate = reader.GetDateTime(reader.GetOrdinal("ExpectedReadyDate")),
                PlannedCuttingQuantity = reader.GetDecimal(reader.GetOrdinal("PlannedCuttingQuantity")),
                CuttingQuantityConsumedTotal = reader.GetDecimal(reader.GetOrdinal("CuttingQuantityConsumedTotal")),
                QuantityProducedTotal = reader.GetDecimal(reader.GetOrdinal("QuantityProducedTotal")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                CreatedById = reader.IsDBNull(reader.GetOrdinal("CreatedById")) ? null : reader.GetInt32(reader.GetOrdinal("CreatedById")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
