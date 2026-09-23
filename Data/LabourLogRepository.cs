using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 13: pure record-keeping -- no stock table is touched here,
    // so unlike every other Phase 7+ repository this one has no
    // RecordTransactionAsync-style ledger call. TotalWage is computed
    // and stored server-side at save time.
    public class LabourLogRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public LabourLogRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT
    l.Id, l.LabourLogCode, l.WorkerId, w.Name AS WorkerName, l.WorkDate, l.AreaId, a.Name AS AreaName,
    l.WorkType, l.ReferenceType, l.ReferenceId, l.ReferenceCode,
    l.WageType, l.WageRate, l.UnitsWorked, l.TotalWage,
    l.SupervisorId, sup.Name AS SupervisorName,
    l.Status, l.Remarks, l.CreatedDate, l.CreatedBy, l.ModifiedDate, l.ModifiedBy
FROM dbo.LabourLogs l
INNER JOIN dbo.IMSUsers w ON l.WorkerId = w.Id
LEFT JOIN dbo.Area a ON l.AreaId = a.Id
LEFT JOIN dbo.IMSUsers sup ON l.SupervisorId = sup.Id";

        public async Task<List<LabourLog>> GetAllAsync(DateTime? fromDate = null, DateTime? toDate = null)
        {
            var list = new List<LabourLog>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@FromDate IS NULL OR l.WorkDate >= @FromDate)
  AND (@ToDate IS NULL OR l.WorkDate <= @ToDate)
ORDER BY l.WorkDate DESC, l.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@FromDate", (object?)fromDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ToDate", (object?)toDate ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<LabourLog?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE l.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(LabourLog entry)
        {
            if (entry.WorkerId <= 0)
                return (false, "Worker is required.", 0);
            if (entry.WageRate <= 0)
                return (false, "Wage Rate must be greater than zero.", 0);
            if (entry.UnitsWorked <= 0)
                return (false, "Units Worked must be greater than zero.", 0);
            if (string.IsNullOrWhiteSpace(entry.WorkType))
                return (false, "Work Type is required.", 0);

            entry.TotalWage = Math.Round(entry.WageRate * entry.UnitsWorked, 2);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "LBR", entry.WorkDate.Year);

                const string insertSql = @"
INSERT INTO dbo.LabourLogs
(LabourLogCode, WorkerId, WorkDate, AreaId, WorkType, ReferenceType, ReferenceId, ReferenceCode,
 WageType, WageRate, UnitsWorked, TotalWage, SupervisorId, Status, Remarks, CreatedDate, CreatedBy)
VALUES
(@Code, @WorkerId, @WorkDate, @AreaId, @WorkType, @ReferenceType, @ReferenceId, @ReferenceCode,
 @WageType, @WageRate, @UnitsWorked, @TotalWage, @SupervisorId, 'Recorded', @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@Code", code);
                cmd.Parameters.AddWithValue("@WorkerId", entry.WorkerId);
                cmd.Parameters.AddWithValue("@WorkDate", entry.WorkDate);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@WorkType", entry.WorkType);
                cmd.Parameters.AddWithValue("@ReferenceType", (object?)entry.ReferenceType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ReferenceId", (object?)entry.ReferenceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ReferenceCode", (object?)entry.ReferenceCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@WageType", entry.WageType);
                cmd.Parameters.AddWithValue("@WageRate", entry.WageRate);
                cmd.Parameters.AddWithValue("@UnitsWorked", entry.UnitsWorked);
                cmd.Parameters.AddWithValue("@TotalWage", entry.TotalWage);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                tx.Commit();
                entry.Id = newId;
                entry.LabourLogCode = code;
                entry.Status = "Recorded";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Non-financial fields only -- WorkerId/WageRate/UnitsWorked/
        // TotalWage are immutable after creation (this is a wage
        // record, not a draft); use CancelAsync for a mistaken entry.
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(LabourLog entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.LabourLogs
SET AreaId = @AreaId, WorkType = @WorkType, ReferenceType = @ReferenceType, ReferenceId = @ReferenceId,
    ReferenceCode = @ReferenceCode, SupervisorId = @SupervisorId, Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status = 'Recorded'";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@WorkType", entry.WorkType);
                cmd.Parameters.AddWithValue("@ReferenceType", (object?)entry.ReferenceType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ReferenceId", (object?)entry.ReferenceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ReferenceCode", (object?)entry.ReferenceCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Labour Log not found, or it is no longer 'Recorded' (already Cancelled).");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string sql = @"
UPDATE dbo.LabourLogs
SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status = 'Recorded'";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Labour Log not found, or it is already Cancelled.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static LabourLog Map(SqlDataReader reader)
        {
            return new LabourLog
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                LabourLogCode = reader.GetString(reader.GetOrdinal("LabourLogCode")),
                WorkerId = reader.GetInt32(reader.GetOrdinal("WorkerId")),
                WorkerName = reader.GetString(reader.GetOrdinal("WorkerName")),
                WorkDate = reader.GetDateTime(reader.GetOrdinal("WorkDate")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                WorkType = reader.GetString(reader.GetOrdinal("WorkType")),
                ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                ReferenceCode = reader.IsDBNull(reader.GetOrdinal("ReferenceCode")) ? null : reader.GetString(reader.GetOrdinal("ReferenceCode")),
                WageType = reader.GetString(reader.GetOrdinal("WageType")),
                WageRate = reader.GetDecimal(reader.GetOrdinal("WageRate")),
                UnitsWorked = reader.GetDecimal(reader.GetOrdinal("UnitsWorked")),
                TotalWage = reader.GetDecimal(reader.GetOrdinal("TotalWage")),
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
