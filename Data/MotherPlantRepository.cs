using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    public class MotherPlantRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public MotherPlantRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT
    mp.Id, mp.MotherPlantCode, mp.PolyhouseId, ph.Name AS PolyhouseName,
    mp.SpeciesId, ps.Name AS SpeciesName, ps.Color AS SpeciesColor, pt.Name AS PlantTypeName,
    mp.AreaId, a.Name AS AreaName,
    mp.ResponsiblePersonId, u.Name AS ResponsiblePersonName,
    mp.SupervisorId, sup.Name AS SupervisorName,
    mp.PlantingDate, mp.MotherPlantQuantity, mp.CuttingPeriodDays, mp.CuttingRate,
    mp.ExpectedCuttingQuantity, mp.ExpectedMonthlyCuttingQuantity,
    mp.Status, mp.Remarks,
    mp.CreatedDate, mp.CreatedBy, mp.ModifiedDate, mp.ModifiedBy
FROM dbo.MotherPlants mp
INNER JOIN dbo.Polyhouses ph ON mp.PolyhouseId = ph.Id
INNER JOIN dbo.PlantSpecies ps ON mp.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.Area a ON mp.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON mp.ResponsiblePersonId = u.Id
LEFT JOIN dbo.IMSUsers sup ON mp.SupervisorId = sup.Id";

        public async Task<List<MotherPlant>> GetAllAsync(int? polyhouseId = null, int? speciesId = null, string? status = null, int? responsiblePersonId = null)
        {
            var list = new List<MotherPlant>();

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@PolyhouseId IS NULL OR mp.PolyhouseId = @PolyhouseId)
  AND (@SpeciesId IS NULL OR mp.SpeciesId = @SpeciesId)
  AND (@Status IS NULL OR mp.Status = @Status)
  AND (@ResponsiblePersonId IS NULL OR mp.ResponsiblePersonId = @ResponsiblePersonId)
ORDER BY mp.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", (object?)speciesId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)responsiblePersonId ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<MotherPlant?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE mp.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(MotherPlant entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                entry.ExpectedCuttingQuantity = MotherPlant.CalculateExpectedCuttingQuantity(entry.MotherPlantQuantity, entry.CuttingRate);
                entry.ExpectedMonthlyCuttingQuantity = MotherPlant.CalculateExpectedMonthlyCuttingQuantity(entry.ExpectedCuttingQuantity, entry.CuttingPeriodDays);

                var motherPlantCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "MP", entry.PlantingDate.Year);

                const string insertSql = @"
INSERT INTO dbo.MotherPlants
(MotherPlantCode, PolyhouseId, SpeciesId, AreaId, ResponsiblePersonId, SupervisorId, PlantingDate,
 MotherPlantQuantity, CuttingPeriodDays, CuttingRate,
 ExpectedCuttingQuantity, ExpectedMonthlyCuttingQuantity,
 Status, Remarks, CreatedDate, CreatedBy)
VALUES
(@MotherPlantCode, @PolyhouseId, @SpeciesId, @AreaId, @ResponsiblePersonId, @SupervisorId, @PlantingDate,
 @MotherPlantQuantity, @CuttingPeriodDays, @CuttingRate,
 @ExpectedCuttingQuantity, @ExpectedMonthlyCuttingQuantity,
 @Status, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@MotherPlantCode", motherPlantCode);
                cmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PlantingDate", entry.PlantingDate.Date);
                cmd.Parameters.AddWithValue("@MotherPlantQuantity", entry.MotherPlantQuantity);
                cmd.Parameters.AddWithValue("@CuttingPeriodDays", entry.CuttingPeriodDays);
                cmd.Parameters.AddWithValue("@CuttingRate", entry.CuttingRate);
                cmd.Parameters.AddWithValue("@ExpectedCuttingQuantity", entry.ExpectedCuttingQuantity);
                cmd.Parameters.AddWithValue("@ExpectedMonthlyCuttingQuantity", entry.ExpectedMonthlyCuttingQuantity);
                cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(entry.Status) ? "Active" : entry.Status);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                tx.Commit();
                entry.Id = newId;
                entry.MotherPlantCode = motherPlantCode;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        public async Task<(bool Success, string? Message)> UpdateAsync(MotherPlant entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                entry.ExpectedCuttingQuantity = MotherPlant.CalculateExpectedCuttingQuantity(entry.MotherPlantQuantity, entry.CuttingRate);
                entry.ExpectedMonthlyCuttingQuantity = MotherPlant.CalculateExpectedMonthlyCuttingQuantity(entry.ExpectedCuttingQuantity, entry.CuttingPeriodDays);

                const string updateSql = @"
UPDATE dbo.MotherPlants
SET PolyhouseId = @PolyhouseId,
    SpeciesId = @SpeciesId,
    AreaId = @AreaId,
    ResponsiblePersonId = @ResponsiblePersonId,
    SupervisorId = @SupervisorId,
    PlantingDate = @PlantingDate,
    MotherPlantQuantity = @MotherPlantQuantity,
    CuttingPeriodDays = @CuttingPeriodDays,
    CuttingRate = @CuttingRate,
    ExpectedCuttingQuantity = @ExpectedCuttingQuantity,
    ExpectedMonthlyCuttingQuantity = @ExpectedMonthlyCuttingQuantity,
    Status = @Status,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id";

                using var cmd = new SqlCommand(updateSql, conn, tx);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PlantingDate", entry.PlantingDate.Date);
                cmd.Parameters.AddWithValue("@MotherPlantQuantity", entry.MotherPlantQuantity);
                cmd.Parameters.AddWithValue("@CuttingPeriodDays", entry.CuttingPeriodDays);
                cmd.Parameters.AddWithValue("@CuttingRate", entry.CuttingRate);
                cmd.Parameters.AddWithValue("@ExpectedCuttingQuantity", entry.ExpectedCuttingQuantity);
                cmd.Parameters.AddWithValue("@ExpectedMonthlyCuttingQuantity", entry.ExpectedMonthlyCuttingQuantity);
                cmd.Parameters.AddWithValue("@Status", entry.Status);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                tx.Commit();

                return rows > 0 ? (true, null) : (false, "Mother Plant batch not found.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        // Read-only: how many records still use this Mother Plant, per relationship.
        public async Task<List<DependencyCount>> GetDeletionCheckAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await DependencyChecker.CountAsync(conn, null, DeletionRules.MotherPlantTable, DeletionRules.MotherPlantDependencies, id);
        }

        public async Task<DeleteResult> DeleteAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var result = await DeleteAsync(conn, tx, id);
                if (result.Succeeded) tx.Commit(); else tx.Rollback();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        // Deletes the Mother Plant only if nothing references it (same locked
        // check-then-delete as AreaRepository.DeleteAsync). Child records are
        // never deleted. Runs on the caller's transaction.
        public async Task<DeleteResult> DeleteAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            string? code;
            using (var lockCmd = new SqlCommand("SELECT MotherPlantCode FROM dbo.MotherPlants WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("@Id", id);
                code = await lockCmd.ExecuteScalarAsync() as string;
            }
            if (code == null)
                return new DeleteResult(DeleteOutcome.NotFound, "Mother Plant batch not found.", Array.Empty<DependencyCount>());

            var dependencies = await DependencyChecker.CountAsync(conn, tx, DeletionRules.MotherPlantTable, DeletionRules.MotherPlantDependencies, id);
            if (!DeletionRules.CanDelete(dependencies))
                return new DeleteResult(DeleteOutcome.Blocked, DeletionRules.BlockedMessage("Mother Plant batch", dependencies), dependencies);

            const string savepoint = "BeforeMotherPlantDelete";
            tx.Save(savepoint);
            try
            {
                using var delete = new SqlCommand("DELETE FROM dbo.MotherPlants WHERE Id = @Id", conn, tx);
                delete.Parameters.AddWithValue("@Id", id);
                await delete.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                tx.Rollback(savepoint);
                var again = await DependencyChecker.CountAsync(conn, tx, DeletionRules.MotherPlantTable, DeletionRules.MotherPlantDependencies, id);
                return new DeleteResult(DeleteOutcome.Blocked, DeletionRules.BlockedMessage("Mother Plant batch", again), again);
            }
            return new DeleteResult(DeleteOutcome.Deleted, DeletionRules.DeletedMessage("Mother Plant batch", code), dependencies);
        }

        // Deactivate: status becomes 'Removed' (no new cuttings can be
        // recorded for it); the batch and all of its history are kept.
        public async Task<(bool Success, string? Message)> DeactivateAsync(int id, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await DeactivateAsync(conn, null, id, modifiedBy);
        }

        public async Task<(bool Success, string? Message)> DeactivateAsync(SqlConnection conn, SqlTransaction? tx, int id, string? modifiedBy)
        {
            using var cmd = new SqlCommand(@"
UPDATE dbo.MotherPlants
SET Status = @Status, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status <> @Status", conn, tx);
            cmd.Parameters.AddWithValue("@Status", DeletionRules.MotherPlantDeactivatedStatus);
            cmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", id);
            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0
                ? (true, null)
                : (false, "Mother Plant batch not found, or it is already deactivated.");
        }

        private static MotherPlant Map(SqlDataReader reader)
        {
            return new MotherPlant
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                MotherPlantCode = reader.GetString(reader.GetOrdinal("MotherPlantCode")),
                PolyhouseId = reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                PolyhouseName = reader.GetString(reader.GetOrdinal("PolyhouseName")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                SpeciesColor = reader.IsDBNull(reader.GetOrdinal("SpeciesColor")) ? null : reader.GetString(reader.GetOrdinal("SpeciesColor")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                PlantingDate = reader.GetDateTime(reader.GetOrdinal("PlantingDate")),
                MotherPlantQuantity = reader.GetDecimal(reader.GetOrdinal("MotherPlantQuantity")),
                CuttingPeriodDays = reader.GetInt32(reader.GetOrdinal("CuttingPeriodDays")),
                CuttingRate = reader.GetDecimal(reader.GetOrdinal("CuttingRate")),
                ExpectedCuttingQuantity = reader.GetDecimal(reader.GetOrdinal("ExpectedCuttingQuantity")),
                ExpectedMonthlyCuttingQuantity = reader.GetDecimal(reader.GetOrdinal("ExpectedMonthlyCuttingQuantity")),
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
