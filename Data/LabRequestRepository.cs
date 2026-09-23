using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 12: sends a quantity of existing dbo.PottedPlantStock out to
    // a lab and later records however much comes back -- both movements
    // post against the SAME pool's existing ledger
    // (dbo.PottedPlantStockTransactions, Phase 7) via
    // PottedPlantStockRepository.RecordTransactionAsync, continuing
    // Decision 1 exactly as Phases 8-10 did. 'LabSent' reduces
    // PhysicalQuantity when the request is created; 'LabReceived'
    // increases it by the (independently entered, usually larger)
    // ReceivedQuantity when a result is recorded.
    public class LabRequestRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;

        public LabRequestRepository(
            DatabaseHelper dbHelper,
            BatchNumberRepository batchNumberRepo,
            PottedPlantStockRepository pottedPlantStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
        }

        private const string BaseSelect = @"
SELECT
    lr.Id, lr.LabRequestCode, lr.PottedPlantStockId, lr.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    lr.PotSize, lr.AreaId, a.Name AS AreaName, lr.LabName,
    lr.SentDate, lr.SentQuantity, lr.ExpectedResultDate, lr.Status,
    lr.ReceivedDate, lr.ReceivedQuantity, lr.ResultNotes,
    lr.ResponsiblePersonId, u.Name AS ResponsiblePersonName,
    lr.Remarks, lr.CreatedDate, lr.CreatedBy, lr.ModifiedDate, lr.ModifiedBy,
    s.AvailableQuantity AS StockAvailableQuantity
FROM dbo.LabRequests lr
INNER JOIN dbo.PlantSpecies ps ON lr.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.PottedPlantStock s ON lr.PottedPlantStockId = s.Id
LEFT JOIN dbo.Area a ON lr.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON lr.ResponsiblePersonId = u.Id";

        public async Task<List<LabRequest>> GetAllAsync(string? status = null)
        {
            var list = new List<LabRequest>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE (@Status IS NULL OR lr.Status = @Status) ORDER BY lr.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<LabRequest?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE lr.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(LabRequest entry, int? userId)
        {
            if (entry.SentQuantity <= 0)
                return (false, "Sent Quantity must be greater than zero.", 0);
            if (string.IsNullOrWhiteSpace(entry.LabName))
                return (false, "Lab Name is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the target stock pool and derive
                // SpeciesId/PotSize/AreaId from it directly -- never
                // trusted from the caller.
                var stockLockCmd = new SqlCommand(
                    "SELECT SpeciesId, PotSize, AreaId FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                stockLockCmd.Parameters.AddWithValue("@Id", entry.PottedPlantStockId);
                using var stockReader = await stockLockCmd.ExecuteReaderAsync();
                if (!await stockReader.ReadAsync())
                {
                    stockReader.Close();
                    tx.Rollback();
                    return (false, "Selected Potted Plant Stock pool not found.", 0);
                }
                entry.SpeciesId = stockReader.GetInt32(stockReader.GetOrdinal("SpeciesId"));
                entry.PotSize = stockReader.GetString(stockReader.GetOrdinal("PotSize"));
                entry.AreaId = stockReader.IsDBNull(stockReader.GetOrdinal("AreaId")) ? null : stockReader.GetInt32(stockReader.GetOrdinal("AreaId"));
                stockReader.Close();

                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "LAB", entry.SentDate.Year);

                // 2) Insert the header row FIRST so its Id is available
                // as ReferenceId on the ledger entry.
                const string insertSql = @"
INSERT INTO dbo.LabRequests
(LabRequestCode, PottedPlantStockId, SpeciesId, PotSize, AreaId, LabName,
 SentDate, SentQuantity, ExpectedResultDate, Status, ResponsiblePersonId, Remarks, CreatedDate, CreatedBy)
VALUES
(@Code, @PottedPlantStockId, @SpeciesId, @PotSize, @AreaId, @LabName,
 @SentDate, @SentQuantity, @ExpectedResultDate, 'Sent', @ResponsiblePersonId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@Code", code);
                cmd.Parameters.AddWithValue("@PottedPlantStockId", entry.PottedPlantStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@LabName", entry.LabName);
                cmd.Parameters.AddWithValue("@SentDate", entry.SentDate);
                cmd.Parameters.AddWithValue("@SentQuantity", entry.SentQuantity);
                cmd.Parameters.AddWithValue("@ExpectedResultDate", (object?)entry.ExpectedResultDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                // 3) Physical stock actually leaves for the lab.
                var (sendSuccess, sendMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, entry.PottedPlantStockId, -entry.SentQuantity, "LabSent", "LabRequest", newId, userId, entry.Remarks);
                if (!sendSuccess)
                {
                    tx.Rollback();
                    return (false, sendMessage, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.LabRequestCode = code;
                entry.Status = "Sent";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Records the lab's result: adds ReceivedQuantity (independent
        // of SentQuantity -- multiplication) back into the SAME pool
        // and marks the request Completed.
        public async Task<(bool Success, string? Message)> RecordResultAsync(
            int id, decimal receivedQuantity, DateTime receivedDate, string? resultNotes, string? modifiedBy, int? userId)
        {
            if (receivedQuantity <= 0)
                return (false, "Received Quantity must be greater than zero.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT PottedPlantStockId, Status FROM dbo.LabRequests WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Lab Request not found.");
                }
                var pottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status != "Sent")
                {
                    tx.Rollback();
                    return (false, $"This Lab Request is '{status}' and cannot have a result recorded (only 'Sent' requests can be completed).");
                }

                var (receiveSuccess, receiveMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, pottedPlantStockId, receivedQuantity, "LabReceived", "LabRequest", id, userId, resultNotes);
                if (!receiveSuccess)
                {
                    tx.Rollback();
                    return (false, receiveMessage);
                }

                var updateCmd = new SqlCommand(
                    @"UPDATE dbo.LabRequests
                      SET Status = 'Completed', ReceivedDate = @ReceivedDate, ReceivedQuantity = @ReceivedQuantity,
                          ResultNotes = @ResultNotes, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
                      WHERE Id = @Id",
                    conn, tx);
                updateCmd.Parameters.AddWithValue("@ReceivedDate", receivedDate);
                updateCmd.Parameters.AddWithValue("@ReceivedQuantity", receivedQuantity);
                updateCmd.Parameters.AddWithValue("@ResultNotes", (object?)resultNotes ?? DBNull.Value);
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

        // Only while still 'Sent' (no result recorded yet) -- reverses
        // the 'LabSent' ledger entry with an equal-and-opposite one,
        // mirrors the reversal pattern used at every phase since 7.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT PottedPlantStockId, SentQuantity, Status FROM dbo.LabRequests WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Lab Request not found.");
                }
                var pottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId"));
                var sentQuantity = reader.GetDecimal(reader.GetOrdinal("SentQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status != "Sent")
                {
                    tx.Rollback();
                    return (false, $"This Lab Request is '{status}' and can no longer be cancelled here.");
                }

                var (restoreSuccess, restoreMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, pottedPlantStockId, sentQuantity, "LabSent", "LabRequest", id, userId, "Lab Request cancelled -- sample restored");
                if (!restoreSuccess)
                {
                    tx.Rollback();
                    return (false, restoreMessage);
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.LabRequests SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
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

        private static LabRequest Map(SqlDataReader reader)
        {
            return new LabRequest
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                LabRequestCode = reader.GetString(reader.GetOrdinal("LabRequestCode")),
                PottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                LabName = reader.GetString(reader.GetOrdinal("LabName")),
                SentDate = reader.GetDateTime(reader.GetOrdinal("SentDate")),
                SentQuantity = reader.GetDecimal(reader.GetOrdinal("SentQuantity")),
                ExpectedResultDate = reader.IsDBNull(reader.GetOrdinal("ExpectedResultDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ExpectedResultDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ReceivedDate = reader.IsDBNull(reader.GetOrdinal("ReceivedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ReceivedDate")),
                ReceivedQuantity = reader.IsDBNull(reader.GetOrdinal("ReceivedQuantity")) ? null : reader.GetDecimal(reader.GetOrdinal("ReceivedQuantity")),
                ResultNotes = reader.IsDBNull(reader.GetOrdinal("ResultNotes")) ? null : reader.GetString(reader.GetOrdinal("ResultNotes")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                StockAvailableQuantity = reader.IsDBNull(reader.GetOrdinal("StockAvailableQuantity")) ? null : reader.GetDecimal(reader.GetOrdinal("StockAvailableQuantity"))
            };
        }
    }
}
