using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using PlantStockManager.Models;
using System.Data;

namespace PlantStockManager.Data
{
    public class SeedEntryRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly SeedBankRepository _bankRepo;
        private readonly TransactionRepository _txRepo;

        public SeedEntryRepository(DatabaseHelper dbHelper,  SeedBankRepository bankRepo, TransactionRepository txRepo)
        {
            _dbHelper = dbHelper;
            _bankRepo = bankRepo;
            _txRepo = txRepo;
        }

        public async Task<int> GetAvailableAsync(int plantId, int speciesId, int? sourceId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = @"
                        SELECT ISNULL(SUM(Available),0)
                        FROM dbo.SeedCuttingBank
                        WHERE PlantId = @p AND SpeciesId = @s AND (@src IS NULL OR SourceId = @src);";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@p", plantId);
            cmd.Parameters.AddWithValue("@s", speciesId);
            cmd.Parameters.AddWithValue("@src", (object?)sourceId ?? DBNull.Value);

            var v = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(v);
        }

        public async Task UtilizeAsync(int plantId, int speciesId, int? sourceId, int utilizeQty, int? seedEntryId, string userName, string? remarks = null)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            using var cmd = new SqlCommand("dbo.SeedCutting_UtilizeFromSource", conn);
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@PlantId", plantId);
            cmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            cmd.Parameters.AddWithValue("@SourceId", (object?)sourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UtilizeQty", utilizeQty);
            cmd.Parameters.AddWithValue("@SeedEntryId", (object?)seedEntryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserName", userName);
            cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }

        //public async Task<bool> InsertSeedEntry(SeedEntries entry)
        //{
        //    using (var conn = _dbHelper.GetConnection())
        //    {
        //        await conn.OpenAsync();
        //        using (var transaction = conn.BeginTransaction())
        //        {
        //            try
        //            {
        //                var cmd = new SqlCommand(@"
        //            INSERT INTO SeedEntries (PlantId, SpeciesId, SeedSourceId, OtherSeedSource, PolyhouseId, SeedingDate, SeedsPlanted, SupervisorId, CreatedBy, CreatedOn)
        //            VALUES (@PlantId, @SpeciesId, @SeedSourceId, @OtherSeedSource, @PolyhouseId, @SeedingDate, @SeedsPlanted,@Supervisor, @CreatedBy, GETDATE());
        //            SELECT SCOPE_IDENTITY();", conn, transaction);

        //                cmd.Parameters.AddWithValue("@PlantId", entry.PlantId);
        //                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
        //                cmd.Parameters.AddWithValue("@SeedSourceId", entry.SeedSourceId);
        //                cmd.Parameters.AddWithValue("@OtherSeedSource", (object?)entry.OtherSeedSource ?? DBNull.Value);
        //                cmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId);
        //                cmd.Parameters.AddWithValue("@SeedingDate", entry.SeedingDate);
        //                cmd.Parameters.AddWithValue("@SeedsPlanted", entry.SeedsPlanted);
        //                cmd.Parameters.AddWithValue("@Supervisor", entry.Supervisor);
        //                cmd.Parameters.AddWithValue("@CreatedBy", entry.CreatedBy);

        //                int seedEntryId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        //                var transactionCmd = new SqlCommand(@"
        //            INSERT INTO Transactions (SeedEntryId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
        //            VALUES (@SeedEntryId, 'Sowing', @Quantity, GETDATE(), @UpdatedBy, GETDATE());", conn, transaction);

        //                transactionCmd.Parameters.AddWithValue("@SeedEntryId", seedEntryId);
        //                transactionCmd.Parameters.AddWithValue("@Quantity", entry.SeedsPlanted);
        //                transactionCmd.Parameters.AddWithValue("@UpdatedBy", entry.CreatedBy);

        //                await transactionCmd.ExecuteNonQueryAsync();

        //                transaction.Commit();
        //                return true;
        //            }
        //            catch
        //            {
        //                transaction.Rollback();
        //                return false;
        //            }
        //        }
        //    }
        //}

        public async Task<int> InsertSeedEntryReturnIdAsync(SqlConnection conn, SqlTransaction tx, SeedEntries e)
        {
            const string sql = @"
INSERT INTO dbo.SeedEntries
( PolyhouseId, PlantId, SpeciesId, SeedingDate, SeedsPlanted, SupervisorId, CreatedBy, CreatedOn)
VALUES ( @PolyhouseId, @PlantId, @SpeciesId, @SeedingDate, @SeedsPlanted, @Supervisor, @CreatedBy, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            using var cmd = new SqlCommand(sql, conn, tx);
            //cmd.Parameters.AddWithValue("@SeedSourceId", e.SeedSourceId);
            cmd.Parameters.AddWithValue("@PolyhouseId", e.PolyhouseId);
            cmd.Parameters.AddWithValue("@PlantId", e.PlantId);
            cmd.Parameters.AddWithValue("@SpeciesId", e.SpeciesId);
            cmd.Parameters.AddWithValue("@SeedingDate", e.SeedingDate);
            cmd.Parameters.AddWithValue("@SeedsPlanted", e.SeedsPlanted);
            cmd.Parameters.AddWithValue("@Supervisor", e.Supervisor);
            cmd.Parameters.AddWithValue("@OtherSeedSource", (object?)e.OtherSeedSource ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedBy", e.CreatedBy);
            var id = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(id);
        }

        private static async Task<(int PlantId, int SpeciesId, int SeedsPlanted)> GetCurrentAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            const string sql = @"
SELECT PlantId, SpeciesId, SeedsPlanted
FROM dbo.SeedEntries WITH (UPDLOCK, ROWLOCK)
WHERE Id = @Id;";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("Seed entry not found.");
            return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
        public async Task<(bool ok, string? message)> UpdateSowingWithStockAdjustAsync(SeedEntries edited, string userName)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Get current state (lock row)
                var current = await GetCurrentAsync(conn, tx, edited.Id);
                var delta = edited.SeedsPlanted - current.SeedsPlanted;

                // 2) If increasing, verify availability and consume delta
                if (delta > 0)
                {
                    // quick server check across all sources
                    var available = await _bankRepo.GetAvailableAsync(current.PlantId, current.SpeciesId);
                    if (delta > available)
                        return (false, $"Only {available} available. Reduce quantity.");

                    await _bankRepo.ConsumeAsync(conn, tx,
                        current.PlantId, current.SpeciesId, delta, edited.Id, userName, "Sowing Edit Increase");
                }
                else if (delta < 0)
                {
                    // return (-delta) to bank
                    await _bankRepo.UnconsumeAsync(conn, tx,
                        edited.Id, -delta, userName, "Sowing Edit Decrease");
                }

                // 3) Update entry fields
                const string updateSql = @"
UPDATE dbo.SeedEntries
SET PolyhouseId   = @PolyhouseId,
    PlantId       = @PlantId,
    SpeciesId     = @SpeciesId,
    SeedingDate   = @SeedingDate,
    SeedsPlanted  = @SeedsPlanted,
    SupervisorId    = @SupervisorId,
    TraysAlive    = @TraysAlive,
    TraysDate     = @TraysDate,
    HardeningAlive= @HardeningAlive,
    HardeningDate = @HardeningDate,
    AliveCount    = @AliveCount,
    InventoryDate = @InventoryDate,
    locationDesc  = @locationDesc
WHERE Id = @Id;";

                using (var cmd = new SqlCommand(updateSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", edited.PolyhouseId);
                    cmd.Parameters.AddWithValue("@PlantId", edited.PlantId);
                    cmd.Parameters.AddWithValue("@SpeciesId", edited.SpeciesId);
                    cmd.Parameters.AddWithValue("@SeedingDate", edited.SeedingDate);
                    cmd.Parameters.AddWithValue("@SeedsPlanted", edited.SeedsPlanted);
                    cmd.Parameters.AddWithValue("@SupervisorId", edited.Supervisor);
                    cmd.Parameters.AddWithValue("@TraysAlive", (object?)edited.TraysAlive ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@TraysDate", (object?)edited.TraysDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@HardeningAlive", (object?)edited.HardeningAlive ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@HardeningDate", (object?)edited.HardeningDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@AliveCount", (object?)edited.AliveCount ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@InventoryDate", (object?)edited.InventoryDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@locationDesc", (object?)edited.locationDesc ?? DBNull.Value);
                   
                    cmd.Parameters.AddWithValue("@Id", edited.Id);
                    await cmd.ExecuteNonQueryAsync();
                }

                // 4) Log edit transaction (delta can be 0)
             
                    await _txRepo.InsertSowingEditTransactionAsync(conn, tx, edited.Id, delta, userName);
               

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        public async Task<(bool ok, string? message)> DeleteSowingWithRevertAsync(int id, string userName)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // Get essential info
                const string q = @"SELECT PlantId, SpeciesId, SeedsPlanted FROM dbo.SeedEntries WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id;";
                int seedsPlanted = 0;
                using (var cmd = new SqlCommand(q, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync())
                        return (false, "Sowing record not found.");
                    seedsPlanted = r.GetInt32(2);
                }

                // Return all consumed qty for this entry
                if (seedsPlanted > 0)
                    await _bankRepo.UnconsumeAsync(conn, tx, id, seedsPlanted, userName, "Delete Sowing");

                using (var upd = new SqlCommand("DELETE FROM dbo.SeedCuttingTx WHERE SeedEntryId = @Id;", conn, tx))
                {
                    upd.Parameters.AddWithValue("@Id", id);
                    await upd.ExecuteNonQueryAsync();
                }
                // Delete child tx if you want (optional; or keep for audit)
                // DELETE FROM dbo.Transactions WHERE SeedEntryId = @Id;

                // Delete entry
                using (var del = new SqlCommand("DELETE FROM dbo.SeedEntries WHERE Id = @Id;", conn, tx))
                {
                    del.Parameters.AddWithValue("@Id", id);
                    await del.ExecuteNonQueryAsync();
                }

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }



        public async Task<List<SeedEntries>> GetSowingRecords(int? polyhouseId, int? plantTypeId, int? speciesId, DateTime? fromDate, DateTime? toDate)
        {
            var sowingRecords = new List<SeedEntries>();

            // Default date logic: if both are null, set fromDate to 15 days ago, toDate to today
            if (!fromDate.HasValue && !toDate.HasValue)
            {
                fromDate = DateTime.Today.AddDays(-15);
                toDate = DateTime.Today;
            }

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
SELECT 
    s.Id, 
    s.PolyhouseId, 
    p.Name AS Polyhouse, 
    s.PlantId, 
    pt.Name AS PlantType, 
    s.SpeciesId, 
    ps.Name AS Species, 
    s.SeedingDate, 
    s.SeedsPlanted,
    DATEDIFF(DAY, s.SeedingDate, GETDATE()) AS DaysPassed,
    s.TraysAlive,
    s.HardeningAlive,
    s.AliveCount,
    s.SeedSourceId,
    s.OtherSeedSource,
    s.TraysDate,
    s.HardeningDate,
    s.InventoryDate,
    s.location,
    s.locationdesc,
    se.Name,
    e.Name
FROM SeedEntries s
INNER JOIN Polyhouses p ON s.PolyhouseId = p.Id
INNER JOIN PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN PlantTypes pt ON s.PlantId = pt.Id
Inner JOIN IMSUsers e ON s.SupervisorId = e.Id
LEFT OUTER JOIN SeedSources se ON s.SeedSourceId = se.Id
WHERE (@PolyhouseId IS NULL OR s.PolyhouseId = @PolyhouseId)
AND (@PlantTypeId IS NULL OR s.PlantId = @PlantTypeId)
AND (@SpeciesId IS NULL OR s.SpeciesId = @SpeciesId)
AND (@FromDate IS NULL OR s.SeedingDate >= @FromDate)
AND (@ToDate IS NULL OR s.SeedingDate <= @ToDate)
ORDER BY s.SeedingDate";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@FromDate", (object)fromDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ToDate", (object)toDate ?? DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            sowingRecords.Add(new SeedEntries
                            {
                                Id = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                SeedingDate = reader.GetDateTime(7),
                                SeedsPlanted = reader.GetInt32(8),
                                DaysPassed = reader.GetInt32(9),
                                TraysAlive = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                                HardeningAlive = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                                AliveCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                                //SeedSourceId = reader.GetInt32(13),
                                //OtherSeedSource = reader.IsDBNull(14) ? null : reader.GetString(14),
                                TraysDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
                                HardeningDate = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                                InventoryDate = reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                                Location = reader.IsDBNull(18) ? null : reader.GetString(18),
                                locationDesc = reader.IsDBNull(19) ? null : reader.GetString(19),
                                SeedSourceName = reader.IsDBNull(20) ? null : reader.GetString(20),
                                Supervisor = reader.IsDBNull(21) ? null : reader.GetString(21)
                            });
                        }
                    }
                }
            }

            return sowingRecords;
        }

        public async Task<List<SeedEntries>> GetSowingRecordsByMonth(int? polyhouseId, int? plantTypeId, int? speciesId, int SelectedMonth, int SelectedYear)
        {
            var sowingRecords = new List<SeedEntries>();

            // Default date logic: if both are null, set fromDate to 15 days ago, toDate to today
           

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
SELECT 
    s.Id, 
    s.PolyhouseId, 
    p.Name AS Polyhouse, 
    s.PlantId, 
    pt.Name AS PlantType, 
    s.SpeciesId, 
    ps.Name AS Species, 
    s.SeedingDate, 
    s.SeedsPlanted,
    DATEDIFF(DAY, s.SeedingDate, GETDATE()) AS DaysPassed,
    s.TraysAlive,
    s.HardeningAlive,
    s.AliveCount,
    s.SeedSourceId,
    s.OtherSeedSource,
    s.TraysDate,
    s.HardeningDate,
    s.InventoryDate,
    s.location,
    s.locationdesc,
    se.Name,
    e.Name
FROM SeedEntries s
INNER JOIN Polyhouses p ON s.PolyhouseId = p.Id
INNER JOIN PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN PlantTypes pt ON s.PlantId = pt.Id
Inner JOIN Employee e ON s.SupervisorId = e.Id
LEFT OUTER JOIN SeedSources se ON s.SeedSourceId = se.Id
WHERE (@PolyhouseId IS NULL OR s.PolyhouseId = @PolyhouseId)
AND (@PlantTypeId IS NULL OR s.PlantId = @PlantTypeId)
AND (@SpeciesId IS NULL OR s.SpeciesId = @SpeciesId)
AND MONTH(s.SeedingDate) = @Month AND YEAR(s.SeedingDate) = @Year
ORDER BY s.SeedingDate";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Month", SelectedMonth);
                    cmd.Parameters.AddWithValue("@Year", SelectedYear);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            sowingRecords.Add(new SeedEntries
                            {
                                Id = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                SeedingDate = reader.GetDateTime(7),
                                SeedsPlanted = reader.GetInt32(8),
                                DaysPassed = reader.GetInt32(9),
                                TraysAlive = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                                HardeningAlive = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                                AliveCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                                SeedSourceId = reader.GetInt32(13),
                                OtherSeedSource = reader.IsDBNull(14) ? null : reader.GetString(14),
                                TraysDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
                                HardeningDate = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                                InventoryDate = reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                                Location = reader.IsDBNull(18) ? null : reader.GetString(18),
                                locationDesc = reader.IsDBNull(19) ? null : reader.GetString(19),
                                SeedSourceName = reader.IsDBNull(20) ? null : reader.GetString(20),
                                Supervisor = reader.IsDBNull(21) ? null : reader.GetString(21)
                            });
                        }
                    }
                }
            }

            return sowingRecords;
        }


        public async Task<bool> UpdateSowing(SeedEntries entry)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        // Update Booking Record
                        var cmd = new SqlCommand(@"
                    UPDATE SeedEntries 
                    SET SeedSourceId = @SeedSourceId,
                        OtherSeedSource = @OtherSeedSource,
                        PolyhouseId = @PolyhouseId,
                        PlantId = @PlantId, 
                        SpeciesId = @SpeciesId,
                        SeedingDate = @SeedingDate, 
                        SeedsPlanted = @SeedsPlanted,
                        SupervisorId = @Supervisor,
                        TraysAlive = @TraysAlive,
                        TraysDate = @TraysDate,
                        HardeningAlive = @HardeningAlive,
                        HardeningDate = @HardeningDate,
                        AliveCount = @AliveCount,
                        InventoryDate = @InventoryDate,
                        locationDesc = @LocationDesc,
                        CreatedBy = @CreatedBy,
                        CreatedOn = GETDATE()
                    WHERE Id = @Id", conn, transaction);

                        cmd.Parameters.AddWithValue("@Id", entry.Id);
                        cmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId);
                        cmd.Parameters.AddWithValue("@PlantId", entry.PlantId);
                        cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                        cmd.Parameters.AddWithValue("@SeedSourceId", entry.SeedSourceId);
                        cmd.Parameters.AddWithValue("@OtherSeedSource", (object?)entry.OtherSeedSource ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@SeedingDate", entry.SeedingDate);
                        cmd.Parameters.AddWithValue("@SeedsPlanted", entry.SeedsPlanted);
                        cmd.Parameters.AddWithValue("@Supervisor", entry.Supervisor);
                        cmd.Parameters.AddWithValue("@CreatedBy", entry.CreatedBy);
                        cmd.Parameters.AddWithValue("@TraysAlive", entry.TraysAlive.HasValue ? (object)entry.TraysAlive.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@TraysDate", entry.TraysDate.HasValue ? (object)entry.TraysDate : DBNull.Value);
                        cmd.Parameters.AddWithValue("@HardeningAlive", entry.HardeningAlive.HasValue ? (object)entry.HardeningAlive.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@HardeningDate", entry.HardeningDate.HasValue ? (object)entry.HardeningDate : DBNull.Value);
                        // Corrected AliveCount parameter:
                        cmd.Parameters.AddWithValue("@AliveCount", entry.AliveCount.HasValue ? (object)entry.AliveCount.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@InventoryDate", entry.InventoryDate.HasValue ? (object)entry.InventoryDate : DBNull.Value);
                        cmd.Parameters.AddWithValue("@LocationDesc", string.IsNullOrEmpty(entry.locationDesc) ? (object)DBNull.Value : entry.locationDesc);

                        int rowsAffected = await cmd.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            // Update Transaction Record
                            var transactionCmd = new SqlCommand(@"
                        UPDATE Transactions 
                        SET Quantity = @Quantity,
                            UpdatedBy = @UpdatedBy,
                            UpdatedOn = GETDATE()
                        WHERE SeedEntryId = @SeedEntryId", conn, transaction);

                            transactionCmd.Parameters.AddWithValue("@SeedEntryId", entry.Id);
                            transactionCmd.Parameters.AddWithValue("@Quantity", entry.SeedsPlanted);
                            transactionCmd.Parameters.AddWithValue("@UpdatedBy", entry.CreatedBy);

                            await transactionCmd.ExecuteNonQueryAsync();

                            transaction.Commit();
                            return true;
                        }

                        transaction.Rollback();
                        return false;
                    }
                    catch
                    {
                        transaction.Rollback();
                        return false;
                    }
                }
            }
        }


        public async Task<(bool Success, string Message)> DeleteSowingRecord(int sowingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            using var tran = conn.BeginTransaction();
            try
            {
                // Check if any Inventory record exists for the SeedEntry
                int inventoryId = 0;
                var checkInventorySql = "SELECT TOP 1 Id FROM Inventory WHERE SeedEntryId = @SowingId";
                using (var cmd = new SqlCommand(checkInventorySql, conn, tran))
                {
                    cmd.Parameters.AddWithValue("@SowingId", sowingId);
                    var result = await cmd.ExecuteScalarAsync();
                    if (result != null)
                    {
                        inventoryId = Convert.ToInt32(result);
                    }
                }

                // If Inventory exists, check if it's used in InventoryTransactions
                if (inventoryId > 0)
                {
                    var checkInventoryTxSql = "SELECT COUNT(*) FROM InventoryTransactions WHERE InventoryId = @InventoryId";
                    using (var cmd = new SqlCommand(checkInventoryTxSql, conn, tran))
                    {
                        cmd.Parameters.AddWithValue("@InventoryId", inventoryId);
                        var txCount = (int)await cmd.ExecuteScalarAsync();

                        if (txCount > 0)
                        {
                            return (false, "Cannot delete this sowing record because it's used in Allocating booking.");
                        }
                    }
                }

               

                // Delete from Inventory
                var deleteInventory = "DELETE FROM Inventory WHERE SeedEntryId = @SowingId";
                using (var cmd = new SqlCommand(deleteInventory, conn, tran))
                {
                    cmd.Parameters.AddWithValue("@SowingId", sowingId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Delete from SeedEntries
                var deleteSeedEntry = "DELETE FROM SeedEntries WHERE Id = @SowingId";
                using (var cmd = new SqlCommand(deleteSeedEntry, conn, tran))
                {
                    cmd.Parameters.AddWithValue("@SowingId", sowingId);
                    await cmd.ExecuteNonQueryAsync();
                }

                await tran.CommitAsync();
                return (true, "Sowing record deleted successfully.");
            }
            catch (Exception ex)
            {
                await tran.RollbackAsync();
                return (false, "Error occurred while deleting: " + ex.Message);
            }
        }


    }
}
