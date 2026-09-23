using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    public class CuttingPlanRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public CuttingPlanRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT
    cp.Id, cp.PlanNumber, cp.MotherPlantId, mp.MotherPlantCode, cp.SpeciesId,
    ps.Name AS SpeciesName, pt.Name AS PlantTypeName, ph.Name AS PolyhouseName,
    cp.PlannedCuttingDate, cp.PlannedQuantity, cp.CuttingRate,
    cp.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    cp.SupervisorId, sup.Name AS SupervisorName,
    cp.Status, cp.Remarks,
    cp.CreatedDate, cp.CreatedBy, cp.ModifiedDate, cp.ModifiedBy
FROM dbo.CuttingPlans cp
INNER JOIN dbo.MotherPlants mp ON cp.MotherPlantId = mp.Id
INNER JOIN dbo.Polyhouses ph ON mp.PolyhouseId = ph.Id
INNER JOIN dbo.PlantSpecies ps ON cp.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.IMSUsers r ON cp.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON cp.SupervisorId = sup.Id";

        public async Task<List<CuttingPlan>> GetAllAsync(int? motherPlantId = null, string? status = null)
        {
            var list = new List<CuttingPlan>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@MotherPlantId IS NULL OR cp.MotherPlantId = @MotherPlantId)
  AND (@Status IS NULL OR cp.Status = @Status)
ORDER BY cp.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@MotherPlantId", (object?)motherPlantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Plans that can still accept Actual Cutting entries: not
        // Cancelled, and with remaining capacity (PlannedQuantity minus
        // whatever Actual Cutting has already recorded against them).
        // Used by the Actual Cutting Create/Edit pages' dropdown.
        public async Task<List<CuttingPlan>> GetOpenForActualCuttingAsync()
        {
            var list = new List<CuttingPlan>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var hasActualCuttings = await TableExistsAsync(conn, "ActualCuttings");
            var recordedExpr = hasActualCuttings
                ? "(SELECT ISNULL(SUM(ac.ActualQuantity), 0) FROM dbo.ActualCuttings ac WHERE ac.CuttingPlanId = cp.Id)"
                : "0";

            var sql = BaseSelect + $@"
WHERE cp.Status <> 'Cancelled'
  AND cp.PlannedQuantity > {recordedExpr}
ORDER BY cp.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var plan = Map(reader);
                list.Add(plan);
            }
            reader.Close(); // release the reader before issuing further commands on the same connection (no MARS configured)

            if (hasActualCuttings)
            {
                foreach (var plan in list)
                {
                    plan.ActualCuttingQuantityRecorded = await GetActualCuttingQuantityAsync(conn, plan.Id);
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

        public async Task<CuttingPlan?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE cp.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var plan = Map(reader);
                reader.Close(); // release the reader before issuing a further command on the same connection (no MARS configured)
                plan.ActualCuttingQuantityRecorded = await GetActualCuttingQuantityAsync(conn, id);
                return plan;
            }
            return null;
        }

        // Sum of ActualCuttings.ActualQuantity already recorded against this
        // plan -- used by the Details page's traceability view and, later,
        // by Actual Cutting's own validation (a plan shouldn't silently
        // accept far more actual cutting than it was planned for, though
        // that's enforced when Actual Cutting is built in Phase 4, not here).
        private static async Task<decimal> GetActualCuttingQuantityAsync(SqlConnection conn, int cuttingPlanId)
        {
            // ActualCuttings doesn't exist until Phase 4. Guard so Phase 3
            // can be used standalone without erroring.
            var checkCmd = new SqlCommand("SELECT OBJECT_ID('dbo.ActualCuttings')", conn);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists == null || exists == DBNull.Value)
                return 0;

            var cmd = new SqlCommand("SELECT ISNULL(SUM(ActualQuantity), 0) FROM dbo.ActualCuttings WHERE CuttingPlanId = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", cuttingPlanId);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(CuttingPlan entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var planNumber = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "CUT", entry.PlannedCuttingDate.Year);

                const string insertSql = @"
INSERT INTO dbo.CuttingPlans
(PlanNumber, MotherPlantId, SpeciesId, PlannedCuttingDate, PlannedQuantity, CuttingRate,
 ResponsiblePersonId, SupervisorId, Status, Remarks, CreatedDate, CreatedBy)
VALUES
(@PlanNumber, @MotherPlantId, @SpeciesId, @PlannedCuttingDate, @PlannedQuantity, @CuttingRate,
 @ResponsiblePersonId, @SupervisorId, @Status, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@PlanNumber", planNumber);
                cmd.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@PlannedCuttingDate", entry.PlannedCuttingDate.Date);
                cmd.Parameters.AddWithValue("@PlannedQuantity", entry.PlannedQuantity);
                cmd.Parameters.AddWithValue("@CuttingRate", entry.CuttingRate);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(entry.Status) ? "Planned" : entry.Status);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                tx.Commit();
                entry.Id = newId;
                entry.PlanNumber = planNumber;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        public async Task<(bool Success, string? Message)> UpdateAsync(CuttingPlan entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                const string updateSql = @"
UPDATE dbo.CuttingPlans
SET MotherPlantId = @MotherPlantId,
    SpeciesId = @SpeciesId,
    PlannedCuttingDate = @PlannedCuttingDate,
    PlannedQuantity = @PlannedQuantity,
    CuttingRate = @CuttingRate,
    ResponsiblePersonId = @ResponsiblePersonId,
    SupervisorId = @SupervisorId,
    Status = @Status,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id";

                using var cmd = new SqlCommand(updateSql, conn, tx);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@PlannedCuttingDate", entry.PlannedCuttingDate.Date);
                cmd.Parameters.AddWithValue("@PlannedQuantity", entry.PlannedQuantity);
                cmd.Parameters.AddWithValue("@CuttingRate", entry.CuttingRate);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Status", entry.Status);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                tx.Commit();

                return rows > 0 ? (true, null) : (false, "Cutting Plan not found.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static CuttingPlan Map(SqlDataReader reader)
        {
            return new CuttingPlan
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                PlanNumber = reader.GetString(reader.GetOrdinal("PlanNumber")),
                MotherPlantId = reader.GetInt32(reader.GetOrdinal("MotherPlantId")),
                MotherPlantCode = reader.GetString(reader.GetOrdinal("MotherPlantCode")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                PolyhouseName = reader.GetString(reader.GetOrdinal("PolyhouseName")),
                PlannedCuttingDate = reader.GetDateTime(reader.GetOrdinal("PlannedCuttingDate")),
                PlannedQuantity = reader.GetDecimal(reader.GetOrdinal("PlannedQuantity")),
                CuttingRate = reader.GetDecimal(reader.GetOrdinal("CuttingRate")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
