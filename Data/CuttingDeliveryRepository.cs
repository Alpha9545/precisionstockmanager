using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    public class CuttingDeliveryRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public CuttingDeliveryRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT
    cd.Id, cd.DeliveryCode, cd.ActualCuttingId, ac.ActualCuttingCode,
    cd.MotherPlantId, mp.MotherPlantCode, cd.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    cd.DeliveryDate, cd.DeliveredQuantity, cd.LossQuantity, cd.RemovedQuantity, cd.RejectedQuantity, cd.DamagedQuantity,
    cd.Status,
    cd.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    cd.SupervisorId, sup.Name AS SupervisorName,
    cd.Remarks, cd.CreatedDate, cd.CreatedBy, cd.ModifiedDate, cd.ModifiedBy
FROM dbo.CuttingDeliveries cd
INNER JOIN dbo.ActualCuttings ac ON cd.ActualCuttingId = ac.Id
INNER JOIN dbo.MotherPlants mp ON cd.MotherPlantId = mp.Id
INNER JOIN dbo.PlantSpecies ps ON cd.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.IMSUsers r ON cd.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON cd.SupervisorId = sup.Id";

        public async Task<List<CuttingDelivery>> GetAllAsync(int? actualCuttingId = null, string? status = null)
        {
            var list = new List<CuttingDelivery>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@ActualCuttingId IS NULL OR cd.ActualCuttingId = @ActualCuttingId)
  AND (@Status IS NULL OR cd.Status = @Status)
ORDER BY cd.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ActualCuttingId", (object?)actualCuttingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<CuttingDelivery?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE cd.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var entry = Map(reader);
                reader.Close();
                entry.PropagatedQuantityRecorded = await GetPropagatedQuantityAsync(conn, id);
                return entry;
            }
            return null;
        }

        // Records (not Cancelled) that still have remaining NetQuantity
        // capacity for a new Propagation Batch. Used by the Propagation
        // Batch Create/Edit pages' dropdown. Guarded so Phase 5 keeps
        // working standalone before Phase 6's table exists.
        public async Task<List<CuttingDelivery>> GetOpenForPropagationAsync()
        {
            var list = new List<CuttingDelivery>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var hasPropagation = await TableExistsAsync(conn, "PropagationBatches");
            var propagatedExpr = hasPropagation
                ? "(SELECT ISNULL(SUM(pb.Quantity), 0) FROM dbo.PropagationBatches pb WHERE pb.CuttingDeliveryId = cd.Id AND pb.Status <> 'Cancelled')"
                : "0";

            var sql = BaseSelect + $@"
WHERE cd.Status <> 'Cancelled'
  AND cd.NetQuantity > {propagatedExpr}
ORDER BY cd.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            reader.Close();

            if (hasPropagation)
            {
                foreach (var entry in list)
                {
                    entry.PropagatedQuantityRecorded = await GetPropagatedQuantityAsync(conn, entry.Id);
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

        // Sum of PropagationBatches.Quantity already recorded against
        // this Cutting Delivery. PropagationBatches doesn't exist until
        // Phase 6 -- guard so Phase 5 works standalone.
        private static async Task<decimal> GetPropagatedQuantityAsync(SqlConnection conn, int cuttingDeliveryId)
        {
            var checkCmd = new SqlCommand("SELECT OBJECT_ID('dbo.PropagationBatches')", conn);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists == null || exists == DBNull.Value)
                return 0;

            var cmd = new SqlCommand("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PropagationBatches WHERE CuttingDeliveryId = @Id AND Status <> 'Cancelled'", conn);
            cmd.Parameters.AddWithValue("@Id", cuttingDeliveryId);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        // Sum of DeliveredQuantity already recorded against an Actual
        // Cutting, optionally excluding one row (used by Update to
        // compute "every OTHER row's total" the same way the DB-level
        // CHECK function does). Callers needing this for a NEW insert
        // should pass excludeId = null.
        public async Task<decimal> GetRecordedQuantityForActualCuttingAsync(SqlConnection conn, SqlTransaction? tx, int actualCuttingId, int? excludeId)
        {
            const string sqlText = @"
SELECT ISNULL(SUM(DeliveredQuantity), 0)
FROM dbo.CuttingDeliveries
WHERE ActualCuttingId = @ActualCuttingId
  AND Status <> 'Cancelled'
  AND (@ExcludeId IS NULL OR Id <> @ExcludeId)";

            var cmd = tx != null ? new SqlCommand(sqlText, conn, tx) : new SqlCommand(sqlText, conn);
            cmd.Parameters.AddWithValue("@ActualCuttingId", actualCuttingId);
            cmd.Parameters.AddWithValue("@ExcludeId", (object?)excludeId ?? DBNull.Value);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(CuttingDelivery entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // Lock the parent Actual Cutting row for the duration of
                // this transaction so two concurrent Cutting Delivery
                // inserts against the SAME actual-cutting record can't
                // both read the "before" total and both pass a
                // since-stale GoodQuantity check -- the same HOLDLOCK
                // discipline used throughout this workflow
                // (BatchNumberRepository, ActualCuttingRepository).
                var lockCmd = new SqlCommand("SELECT GoodQuantity, MotherPlantId, SpeciesId FROM dbo.ActualCuttings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.ActualCuttingId);
                using var lockReader = await lockCmd.ExecuteReaderAsync();
                if (!await lockReader.ReadAsync())
                {
                    lockReader.Close();
                    tx.Rollback();
                    return (false, "Actual Cutting record not found.", 0);
                }
                var goodQuantity = lockReader.GetDecimal(lockReader.GetOrdinal("GoodQuantity"));
                var motherPlantId = lockReader.GetInt32(lockReader.GetOrdinal("MotherPlantId"));
                var speciesId = lockReader.GetInt32(lockReader.GetOrdinal("SpeciesId"));
                lockReader.Close();

                var alreadyDelivered = await GetRecordedQuantityForActualCuttingAsync(conn, tx, entry.ActualCuttingId, excludeId: null);
                if (alreadyDelivered + entry.DeliveredQuantity > goodQuantity)
                {
                    tx.Rollback();
                    return (false, $"Delivered quantity ({entry.DeliveredQuantity:N2}) would push the total delivered against this Actual Cutting to {(alreadyDelivered + entry.DeliveredQuantity):N2}, exceeding its Good Quantity of {goodQuantity:N2}.", 0);
                }

                if (!CuttingDelivery.IsWithinDelivered(entry.LossQuantity, entry.RemovedQuantity, entry.RejectedQuantity, entry.DamagedQuantity, entry.DeliveredQuantity))
                {
                    tx.Rollback();
                    return (false, "Loss + Removed + Rejected + Damaged cannot exceed the Delivered Quantity.", 0);
                }

                // Traceability fields are always derived from the locked
                // Actual Cutting row, never trusted from the caller.
                entry.MotherPlantId = motherPlantId;
                entry.SpeciesId = speciesId;

                var deliveryCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "CD", entry.DeliveryDate.Year);

                const string insertSql = @"
INSERT INTO dbo.CuttingDeliveries
(DeliveryCode, ActualCuttingId, MotherPlantId, SpeciesId, DeliveryDate, DeliveredQuantity,
 LossQuantity, RemovedQuantity, RejectedQuantity, DamagedQuantity, Status,
 ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@DeliveryCode, @ActualCuttingId, @MotherPlantId, @SpeciesId, @DeliveryDate, @DeliveredQuantity,
 @LossQuantity, @RemovedQuantity, @RejectedQuantity, @DamagedQuantity, @Status,
 @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@DeliveryCode", deliveryCode);
                cmd.Parameters.AddWithValue("@ActualCuttingId", entry.ActualCuttingId);
                cmd.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@DeliveryDate", entry.DeliveryDate.Date);
                cmd.Parameters.AddWithValue("@DeliveredQuantity", entry.DeliveredQuantity);
                cmd.Parameters.AddWithValue("@LossQuantity", entry.LossQuantity);
                cmd.Parameters.AddWithValue("@RemovedQuantity", entry.RemovedQuantity);
                cmd.Parameters.AddWithValue("@RejectedQuantity", entry.RejectedQuantity);
                cmd.Parameters.AddWithValue("@DamagedQuantity", entry.DamagedQuantity);
                cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(entry.Status) ? "Completed" : entry.Status);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                tx.Commit();
                entry.Id = newId;
                entry.DeliveryCode = deliveryCode;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        public async Task<(bool Success, string? Message)> UpdateAsync(CuttingDelivery entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand("SELECT GoodQuantity FROM dbo.ActualCuttings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.ActualCuttingId);
                var goodObj = await lockCmd.ExecuteScalarAsync();
                if (goodObj == null)
                {
                    tx.Rollback();
                    return (false, "Actual Cutting record not found.");
                }
                var goodQuantity = (decimal)goodObj;

                if (entry.Status != "Cancelled")
                {
                    var otherRowsTotal = await GetRecordedQuantityForActualCuttingAsync(conn, tx, entry.ActualCuttingId, excludeId: entry.Id);
                    if (otherRowsTotal + entry.DeliveredQuantity > goodQuantity)
                    {
                        tx.Rollback();
                        return (false, $"Delivered quantity ({entry.DeliveredQuantity:N2}) would push the total delivered against this Actual Cutting to {(otherRowsTotal + entry.DeliveredQuantity):N2}, exceeding its Good Quantity of {goodQuantity:N2}.");
                    }
                }

                if (!CuttingDelivery.IsWithinDelivered(entry.LossQuantity, entry.RemovedQuantity, entry.RejectedQuantity, entry.DamagedQuantity, entry.DeliveredQuantity))
                {
                    tx.Rollback();
                    return (false, "Loss + Removed + Rejected + Damaged cannot exceed the Delivered Quantity.");
                }

                const string updateSql = @"
UPDATE dbo.CuttingDeliveries
SET DeliveryDate = @DeliveryDate,
    DeliveredQuantity = @DeliveredQuantity,
    LossQuantity = @LossQuantity,
    RemovedQuantity = @RemovedQuantity,
    RejectedQuantity = @RejectedQuantity,
    DamagedQuantity = @DamagedQuantity,
    Status = @Status,
    SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id";

                using var cmd = new SqlCommand(updateSql, conn, tx);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@DeliveryDate", entry.DeliveryDate.Date);
                cmd.Parameters.AddWithValue("@DeliveredQuantity", entry.DeliveredQuantity);
                cmd.Parameters.AddWithValue("@LossQuantity", entry.LossQuantity);
                cmd.Parameters.AddWithValue("@RemovedQuantity", entry.RemovedQuantity);
                cmd.Parameters.AddWithValue("@RejectedQuantity", entry.RejectedQuantity);
                cmd.Parameters.AddWithValue("@DamagedQuantity", entry.DamagedQuantity);
                cmd.Parameters.AddWithValue("@Status", entry.Status);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                tx.Commit();

                return rows > 0 ? (true, null) : (false, "Cutting Delivery record not found.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static CuttingDelivery Map(SqlDataReader reader)
        {
            return new CuttingDelivery
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                DeliveryCode = reader.GetString(reader.GetOrdinal("DeliveryCode")),
                ActualCuttingId = reader.GetInt32(reader.GetOrdinal("ActualCuttingId")),
                ActualCuttingCode = reader.GetString(reader.GetOrdinal("ActualCuttingCode")),
                MotherPlantId = reader.GetInt32(reader.GetOrdinal("MotherPlantId")),
                MotherPlantCode = reader.GetString(reader.GetOrdinal("MotherPlantCode")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                DeliveryDate = reader.GetDateTime(reader.GetOrdinal("DeliveryDate")),
                DeliveredQuantity = reader.GetDecimal(reader.GetOrdinal("DeliveredQuantity")),
                LossQuantity = reader.GetDecimal(reader.GetOrdinal("LossQuantity")),
                RemovedQuantity = reader.GetDecimal(reader.GetOrdinal("RemovedQuantity")),
                RejectedQuantity = reader.GetDecimal(reader.GetOrdinal("RejectedQuantity")),
                DamagedQuantity = reader.GetDecimal(reader.GetOrdinal("DamagedQuantity")),
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
