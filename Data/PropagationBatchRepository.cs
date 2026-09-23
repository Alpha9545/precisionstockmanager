using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    public class PropagationBatchRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public PropagationBatchRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT
    pb.Id, pb.BatchCode, pb.CuttingDeliveryId, cd.DeliveryCode AS CuttingDeliveryCode,
    pb.MotherPlantId, mp.MotherPlantCode, pb.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    pb.AreaId, a.Name AS AreaName,
    pb.PropagationDate, pb.Quantity, pb.SurvivedQuantity, pb.LossQuantity, pb.Status,
    pb.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    pb.SupervisorId, sup.Name AS SupervisorName,
    pb.Remarks, pb.CreatedDate, pb.CreatedBy, pb.ModifiedDate, pb.ModifiedBy
FROM dbo.PropagationBatches pb
INNER JOIN dbo.CuttingDeliveries cd ON pb.CuttingDeliveryId = cd.Id
INNER JOIN dbo.MotherPlants mp ON pb.MotherPlantId = mp.Id
INNER JOIN dbo.PlantSpecies ps ON pb.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.Area a ON pb.AreaId = a.Id
LEFT JOIN dbo.IMSUsers r ON pb.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON pb.SupervisorId = sup.Id";

        public async Task<List<PropagationBatch>> GetAllAsync(int? cuttingDeliveryId = null, string? status = null)
        {
            var list = new List<PropagationBatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@CuttingDeliveryId IS NULL OR pb.CuttingDeliveryId = @CuttingDeliveryId)
  AND (@Status IS NULL OR pb.Status = @Status)
ORDER BY pb.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@CuttingDeliveryId", (object?)cuttingDeliveryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<PropagationBatch?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE pb.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var entry = Map(reader);
                reader.Close();
                entry.PottedQuantityRecorded = await GetPottedQuantityAsync(conn, id);
                return entry;
            }
            return null;
        }

        // Batches that have been assessed (ReadyForPotting or Completed)
        // and still have remaining SurvivedQuantity capacity for Pot
        // Production. Used by the Pot Production Create page's dropdown.
        // Guarded so Phase 6 keeps working standalone before Phase 7's
        // table exists.
        public async Task<List<PropagationBatch>> GetOpenForPotProductionAsync()
        {
            var list = new List<PropagationBatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var hasPotProduction = await TableExistsAsync(conn, "PotProduction");
            var pottedExpr = hasPotProduction
                ? "(SELECT ISNULL(SUM(pp.Quantity), 0) FROM dbo.PotProduction pp WHERE pp.PropagationBatchId = pb.Id AND pp.Status <> 'Cancelled')"
                : "0";

            var sql = BaseSelect + $@"
WHERE pb.Status IN ('ReadyForPotting', 'Completed')
  AND pb.SurvivedQuantity > {pottedExpr}
ORDER BY pb.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            reader.Close();

            if (hasPotProduction)
            {
                foreach (var entry in list)
                {
                    entry.PottedQuantityRecorded = await GetPottedQuantityAsync(conn, entry.Id);
                }
            }

            return list;
        }

        private static async Task<bool> TableExistsAsync(SqlConnection conn, string tableName)
        {
            var cmd = new SqlCommand("SELECT OBJECT_ID('dbo.' + @TableName)", conn);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            var result = await cmd.ExecuteScalarAsync();
            return result != null && result != DBNull.Value;
        }

        // Sum of PotProduction.Quantity already recorded against this
        // Propagation Batch. PotProduction doesn't exist until Phase 7 --
        // guard so Phase 6 works standalone.
        private static async Task<decimal> GetPottedQuantityAsync(SqlConnection conn, int propagationBatchId)
        {
            var checkCmd = new SqlCommand("SELECT OBJECT_ID('dbo.PotProduction')", conn);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists == null || exists == DBNull.Value)
                return 0;

            var cmd = new SqlCommand("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PotProduction WHERE PropagationBatchId = @Id AND Status <> 'Cancelled'", conn);
            cmd.Parameters.AddWithValue("@Id", propagationBatchId);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        // Sum of Quantity already recorded against a Cutting Delivery,
        // optionally excluding one row -- used the same way as the other
        // phases' capacity checks.
        public async Task<decimal> GetRecordedQuantityForDeliveryAsync(SqlConnection conn, SqlTransaction? tx, int cuttingDeliveryId, int? excludeId)
        {
            const string sqlText = @"
SELECT ISNULL(SUM(Quantity), 0)
FROM dbo.PropagationBatches
WHERE CuttingDeliveryId = @CuttingDeliveryId
  AND Status <> 'Cancelled'
  AND (@ExcludeId IS NULL OR Id <> @ExcludeId)";

            var cmd = tx != null ? new SqlCommand(sqlText, conn, tx) : new SqlCommand(sqlText, conn);
            cmd.Parameters.AddWithValue("@CuttingDeliveryId", cuttingDeliveryId);
            cmd.Parameters.AddWithValue("@ExcludeId", (object?)excludeId ?? DBNull.Value);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(PropagationBatch entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // Lock the parent Cutting Delivery row for the duration
                // of this transaction so two concurrent Propagation Batch
                // inserts against the SAME delivery can't both read the
                // "before" total and both pass a since-stale NetQuantity
                // check -- the same HOLDLOCK discipline used throughout
                // this workflow.
                var lockCmd = new SqlCommand("SELECT NetQuantity, MotherPlantId, SpeciesId FROM dbo.CuttingDeliveries WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.CuttingDeliveryId);
                using var lockReader = await lockCmd.ExecuteReaderAsync();
                if (!await lockReader.ReadAsync())
                {
                    lockReader.Close();
                    tx.Rollback();
                    return (false, "Cutting Delivery record not found.", 0);
                }
                var netQuantity = lockReader.GetDecimal(lockReader.GetOrdinal("NetQuantity"));
                var motherPlantId = lockReader.GetInt32(lockReader.GetOrdinal("MotherPlantId"));
                var speciesId = lockReader.GetInt32(lockReader.GetOrdinal("SpeciesId"));
                lockReader.Close();

                var alreadyPropagated = await GetRecordedQuantityForDeliveryAsync(conn, tx, entry.CuttingDeliveryId, excludeId: null);
                if (alreadyPropagated + entry.Quantity > netQuantity)
                {
                    tx.Rollback();
                    return (false, $"Quantity ({entry.Quantity:N2}) would push the total propagated against this Cutting Delivery to {(alreadyPropagated + entry.Quantity):N2}, exceeding its Net Quantity of {netQuantity:N2}.", 0);
                }

                // Traceability fields are always derived from the locked
                // Cutting Delivery row, never trusted from the caller.
                entry.MotherPlantId = motherPlantId;
                entry.SpeciesId = speciesId;

                var batchCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "PROP", entry.PropagationDate.Year);

                const string insertSql = @"
INSERT INTO dbo.PropagationBatches
(BatchCode, CuttingDeliveryId, MotherPlantId, SpeciesId, AreaId, PropagationDate, Quantity,
 SurvivedQuantity, LossQuantity, Status, ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@BatchCode, @CuttingDeliveryId, @MotherPlantId, @SpeciesId, @AreaId, @PropagationDate, @Quantity,
 0, 0, 'Propagating', @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@BatchCode", batchCode);
                cmd.Parameters.AddWithValue("@CuttingDeliveryId", entry.CuttingDeliveryId);
                cmd.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PropagationDate", entry.PropagationDate.Date);
                cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                tx.Commit();
                entry.Id = newId;
                entry.BatchCode = batchCode;
                entry.Status = "Propagating";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Updates the LIFECYCLE fields only (AreaId, PropagationDate,
        // Status, SurvivedQuantity, LossQuantity, people, remarks).
        // CuttingDeliveryId/MotherPlantId/SpeciesId/Quantity are
        // immutable after creation -- the page model re-derives them
        // from the existing DB record before calling this, exactly like
        // Phase 4/5's Edit pages do.
        //
        // Business rule: moving Status to ReadyForPotting or Completed
        // requires SurvivedQuantity + LossQuantity to reconcile EXACTLY
        // to Quantity (nothing left un-assessed). Propagating and
        // Cancelled do not require reconciliation, since the assessment
        // may still be in progress (or moot, if cancelled).
        public async Task<(bool Success, string? Message)> UpdateAsync(PropagationBatch entry)
        {
            if ((entry.Status == "ReadyForPotting" || entry.Status == "Completed")
                && !PropagationBatch.CanReconcile(entry.SurvivedQuantity, entry.LossQuantity, entry.Quantity))
            {
                return (false, "Survived + Loss must add up exactly to the batch Quantity before it can move to Ready for Potting or Completed.");
            }

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                const string updateSql = @"
UPDATE dbo.PropagationBatches
SET AreaId = @AreaId,
    PropagationDate = @PropagationDate,
    SurvivedQuantity = @SurvivedQuantity,
    LossQuantity = @LossQuantity,
    Status = @Status,
    ResponsiblePersonId = @ResponsiblePersonId,
    SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id";

                using var cmd = new SqlCommand(updateSql, conn, tx);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PropagationDate", entry.PropagationDate.Date);
                cmd.Parameters.AddWithValue("@SurvivedQuantity", entry.SurvivedQuantity);
                cmd.Parameters.AddWithValue("@LossQuantity", entry.LossQuantity);
                cmd.Parameters.AddWithValue("@Status", entry.Status);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                tx.Commit();

                return rows > 0 ? (true, null) : (false, "Propagation Batch record not found.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static PropagationBatch Map(SqlDataReader reader)
        {
            return new PropagationBatch
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                BatchCode = reader.GetString(reader.GetOrdinal("BatchCode")),
                CuttingDeliveryId = reader.GetInt32(reader.GetOrdinal("CuttingDeliveryId")),
                CuttingDeliveryCode = reader.GetString(reader.GetOrdinal("CuttingDeliveryCode")),
                MotherPlantId = reader.GetInt32(reader.GetOrdinal("MotherPlantId")),
                MotherPlantCode = reader.GetString(reader.GetOrdinal("MotherPlantCode")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                PropagationDate = reader.GetDateTime(reader.GetOrdinal("PropagationDate")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                SurvivedQuantity = reader.GetDecimal(reader.GetOrdinal("SurvivedQuantity")),
                LossQuantity = reader.GetDecimal(reader.GetOrdinal("LossQuantity")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
