using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    public class ActualCuttingRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public ActualCuttingRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT
    ac.Id, ac.ActualCuttingCode, ac.CuttingPlanId, cp.PlanNumber AS CuttingPlanNumber,
    ac.MotherPlantId, mp.MotherPlantCode, ac.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    ac.CuttingDate, ac.PlannedQuantity, ac.ActualQuantity, ac.GoodQuantity, ac.DamagedQuantity, ac.RejectedQuantity,
    ac.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    ac.SupervisorId, sup.Name AS SupervisorName,
    ac.Remarks, ac.CreatedDate, ac.CreatedBy, ac.ModifiedDate, ac.ModifiedBy
FROM dbo.ActualCuttings ac
INNER JOIN dbo.CuttingPlans cp ON ac.CuttingPlanId = cp.Id
INNER JOIN dbo.MotherPlants mp ON ac.MotherPlantId = mp.Id
INNER JOIN dbo.PlantSpecies ps ON ac.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.IMSUsers r ON ac.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON ac.SupervisorId = sup.Id";

        public async Task<List<ActualCutting>> GetAllAsync(int? cuttingPlanId = null, int? motherPlantId = null)
        {
            var list = new List<ActualCutting>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@CuttingPlanId IS NULL OR ac.CuttingPlanId = @CuttingPlanId)
  AND (@MotherPlantId IS NULL OR ac.MotherPlantId = @MotherPlantId)
ORDER BY ac.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@CuttingPlanId", (object?)cuttingPlanId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MotherPlantId", (object?)motherPlantId ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<ActualCutting?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE ac.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var entry = Map(reader);
                reader.Close();
                entry.DeliveredQuantityRecorded = await GetDeliveredQuantityAsync(conn, id);
                return entry;
            }
            return null;
        }

        // Records (not Cancelled) that still have remaining GoodQuantity
        // capacity for a new Cutting Delivery. Used by the Cutting
        // Delivery Create/Edit pages' dropdown. Guarded so Phase 4 keeps
        // working standalone before Phase 5's table exists.
        public async Task<List<ActualCutting>> GetOpenForDeliveryAsync()
        {
            var list = new List<ActualCutting>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var hasDeliveries = await TableExistsAsync(conn, "CuttingDeliveries");
            var deliveredExpr = hasDeliveries
                ? "(SELECT ISNULL(SUM(cd.DeliveredQuantity), 0) FROM dbo.CuttingDeliveries cd WHERE cd.ActualCuttingId = ac.Id AND cd.Status <> 'Cancelled')"
                : "0";

            var sql = BaseSelect + $@"
WHERE ac.GoodQuantity > {deliveredExpr}
ORDER BY ac.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            reader.Close();

            if (hasDeliveries)
            {
                foreach (var entry in list)
                {
                    entry.DeliveredQuantityRecorded = await GetDeliveredQuantityAsync(conn, entry.Id);
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

        // Sum of CuttingDeliveries.DeliveredQuantity already recorded
        // against this Actual Cutting. CuttingDeliveries doesn't exist
        // until Phase 5 -- guard so Phase 4 works standalone.
        private static async Task<decimal> GetDeliveredQuantityAsync(SqlConnection conn, int actualCuttingId)
        {
            var checkCmd = new SqlCommand("SELECT OBJECT_ID('dbo.CuttingDeliveries')", conn);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists == null || exists == DBNull.Value)
                return 0;

            var cmd = new SqlCommand("SELECT ISNULL(SUM(DeliveredQuantity), 0) FROM dbo.CuttingDeliveries WHERE ActualCuttingId = @Id AND Status <> 'Cancelled'", conn);
            cmd.Parameters.AddWithValue("@Id", actualCuttingId);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        // Sum of ActualQuantity already recorded against a Cutting Plan,
        // optionally excluding one row (used by Update to compute "every
        // OTHER row's total" the same way the DB-level CHECK function
        // does). Callers needing this for a NEW insert should pass
        // excludeId = null.
        public async Task<decimal> GetRecordedQuantityForPlanAsync(SqlConnection conn, SqlTransaction? tx, int cuttingPlanId, int? excludeId)
        {
            var cmd = tx != null
                ? new SqlCommand("SELECT ISNULL(SUM(ActualQuantity), 0) FROM dbo.ActualCuttings WHERE CuttingPlanId = @CuttingPlanId AND (@ExcludeId IS NULL OR Id <> @ExcludeId)", conn, tx)
                : new SqlCommand("SELECT ISNULL(SUM(ActualQuantity), 0) FROM dbo.ActualCuttings WHERE CuttingPlanId = @CuttingPlanId AND (@ExcludeId IS NULL OR Id <> @ExcludeId)", conn);
            cmd.Parameters.AddWithValue("@CuttingPlanId", cuttingPlanId);
            cmd.Parameters.AddWithValue("@ExcludeId", (object?)excludeId ?? DBNull.Value);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(ActualCutting entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // Lock the parent Cutting Plan row for the duration of this
                // transaction so two concurrent Actual Cutting inserts
                // against the SAME plan can't both read the "before" total
                // and both pass a since-stale planned-quantity check --
                // exactly the same HOLDLOCK discipline BatchNumberRepository
                // uses for sequence generation.
                var lockCmd = new SqlCommand("SELECT PlannedQuantity FROM dbo.CuttingPlans WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.CuttingPlanId);
                var plannedObj = await lockCmd.ExecuteScalarAsync();
                if (plannedObj == null)
                {
                    tx.Rollback();
                    return (false, "Cutting Plan not found.", 0);
                }
                var plannedQuantity = (decimal)plannedObj;

                var alreadyRecorded = await GetRecordedQuantityForPlanAsync(conn, tx, entry.CuttingPlanId, excludeId: null);
                if (alreadyRecorded + entry.ActualQuantity > plannedQuantity)
                {
                    tx.Rollback();
                    return (false, $"Actual Cutting quantity ({entry.ActualQuantity:N2}) would push the total recorded against this Cutting Plan to {(alreadyRecorded + entry.ActualQuantity):N2}, exceeding its Planned Quantity of {plannedQuantity:N2}.", 0);
                }

                entry.PlannedQuantity = plannedQuantity;
                var actualCuttingCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "AC", entry.CuttingDate.Year);

                const string insertSql = @"
INSERT INTO dbo.ActualCuttings
(ActualCuttingCode, CuttingPlanId, MotherPlantId, SpeciesId, CuttingDate, PlannedQuantity,
 ActualQuantity, GoodQuantity, DamagedQuantity, RejectedQuantity,
 ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@ActualCuttingCode, @CuttingPlanId, @MotherPlantId, @SpeciesId, @CuttingDate, @PlannedQuantity,
 @ActualQuantity, @GoodQuantity, @DamagedQuantity, @RejectedQuantity,
 @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@ActualCuttingCode", actualCuttingCode);
                cmd.Parameters.AddWithValue("@CuttingPlanId", entry.CuttingPlanId);
                cmd.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@CuttingDate", entry.CuttingDate.Date);
                cmd.Parameters.AddWithValue("@PlannedQuantity", entry.PlannedQuantity);
                cmd.Parameters.AddWithValue("@ActualQuantity", entry.ActualQuantity);
                cmd.Parameters.AddWithValue("@GoodQuantity", entry.GoodQuantity);
                cmd.Parameters.AddWithValue("@DamagedQuantity", entry.DamagedQuantity);
                cmd.Parameters.AddWithValue("@RejectedQuantity", entry.RejectedQuantity);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                tx.Commit();
                entry.Id = newId;
                entry.ActualCuttingCode = actualCuttingCode;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        public async Task<(bool Success, string? Message)> UpdateAsync(ActualCutting entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand("SELECT PlannedQuantity FROM dbo.CuttingPlans WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.CuttingPlanId);
                var plannedObj = await lockCmd.ExecuteScalarAsync();
                if (plannedObj == null)
                {
                    tx.Rollback();
                    return (false, "Cutting Plan not found.");
                }
                var plannedQuantity = (decimal)plannedObj;

                var otherRowsTotal = await GetRecordedQuantityForPlanAsync(conn, tx, entry.CuttingPlanId, excludeId: entry.Id);
                if (otherRowsTotal + entry.ActualQuantity > plannedQuantity)
                {
                    tx.Rollback();
                    return (false, $"Actual Cutting quantity ({entry.ActualQuantity:N2}) would push the total recorded against this Cutting Plan to {(otherRowsTotal + entry.ActualQuantity):N2}, exceeding its Planned Quantity of {plannedQuantity:N2}.");
                }

                const string updateSql = @"
UPDATE dbo.ActualCuttings
SET CuttingDate = @CuttingDate,
    ActualQuantity = @ActualQuantity,
    GoodQuantity = @GoodQuantity,
    DamagedQuantity = @DamagedQuantity,
    RejectedQuantity = @RejectedQuantity,
    SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id";

                using var cmd = new SqlCommand(updateSql, conn, tx);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@CuttingDate", entry.CuttingDate.Date);
                cmd.Parameters.AddWithValue("@ActualQuantity", entry.ActualQuantity);
                cmd.Parameters.AddWithValue("@GoodQuantity", entry.GoodQuantity);
                cmd.Parameters.AddWithValue("@DamagedQuantity", entry.DamagedQuantity);
                cmd.Parameters.AddWithValue("@RejectedQuantity", entry.RejectedQuantity);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                tx.Commit();

                return rows > 0 ? (true, null) : (false, "Actual Cutting record not found.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static ActualCutting Map(SqlDataReader reader)
        {
            return new ActualCutting
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                ActualCuttingCode = reader.GetString(reader.GetOrdinal("ActualCuttingCode")),
                CuttingPlanId = reader.GetInt32(reader.GetOrdinal("CuttingPlanId")),
                CuttingPlanNumber = reader.GetString(reader.GetOrdinal("CuttingPlanNumber")),
                MotherPlantId = reader.GetInt32(reader.GetOrdinal("MotherPlantId")),
                MotherPlantCode = reader.GetString(reader.GetOrdinal("MotherPlantCode")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                CuttingDate = reader.GetDateTime(reader.GetOrdinal("CuttingDate")),
                PlannedQuantity = reader.GetDecimal(reader.GetOrdinal("PlannedQuantity")),
                ActualQuantity = reader.GetDecimal(reader.GetOrdinal("ActualQuantity")),
                GoodQuantity = reader.GetDecimal(reader.GetOrdinal("GoodQuantity")),
                DamagedQuantity = reader.GetDecimal(reader.GetOrdinal("DamagedQuantity")),
                RejectedQuantity = reader.GetDecimal(reader.GetOrdinal("RejectedQuantity")),
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
