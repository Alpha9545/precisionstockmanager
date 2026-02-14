using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using System.Data;
using System.Text;

using static System.Runtime.InteropServices.JavaScript.JSType;


namespace PlantStockManager.Data
{
    public class InventoryRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public InventoryRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }


        public async Task<List<Inventory>> GetInventoryRecords(int? polyhouseId, int? plantTypeId, int? speciesId)
        {
            var inventoryRecords = new List<Inventory>();

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
        SELECT 
            i.Id, 
            i.PolyhouseId, 
            p.Name AS Polyhouse, 
            i.PlantId, 
            pt.Name AS PlantType, 
            i.SpeciesId, 
            ps.Name AS Species, 
            i.LastUpdated, 
            i.RemainingQuantity,
            DATEDIFF(DAY, i.LastUpdated, GETDATE()) AS DaysPassed, 
            s.locationDesc,
            s.Id,
            s.SeedingDate,
            e.Name
        FROM Inventory i
        INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
        INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
        INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
        INNER JOIN SeedEntries s ON i.SeedEntryId = s.Id 
        left join IMSUsers e ON s.SupervisorId = e.Id
        WHERE (@PolyhouseId IS NULL OR i.PolyhouseId = @PolyhouseId)
        AND (@PlantTypeId IS NULL OR i.PlantId = @PlantTypeId)
        AND (@SpeciesId IS NULL OR i.SpeciesId = @SpeciesId)
        AND i.IsUtilized = 'N'
        ORDER BY s.SeedingDate ";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            inventoryRecords.Add(new Inventory
                            {
                                Id = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                Quantity = reader.GetInt32(8),
                                LastUpdated = reader.GetDateTime(7),
                                LocationDesc = reader.IsDBNull(10) ? null : reader.GetString(10),
                                SeedEntryId =reader.GetInt32(11),
                                SeedingDate = reader.GetDateTime(12),
                                Supervisor = reader.GetString(13)
                            });
                        }
                    }
                }
            }

            return inventoryRecords;
        }


        public async Task<List<Inventory>> GetInventoryRecordsByDate(
    int? polyhouseId, int? plantTypeId, int? speciesId,
    DateTime? seedingFrom, DateTime? seedingTo, int userId)
        {
       
            var inventoryRecords = new List<Inventory>();

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
SELECT 
    i.Id, 
    i.PolyhouseId, 
    p.Name AS Polyhouse, 
    i.PlantId, 
    pt.Name AS PlantType, 
    i.SpeciesId, 
    ps.Name AS Species, 
    i.LastUpdated, 
    i.RemainingQuantity,
    DATEDIFF(DAY, i.LastUpdated, GETDATE()) AS DaysPassed, 
    s.locationDesc,
    s.Id,
    s.SeedingDate,
    e.Name
FROM Inventory i
INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
INNER JOIN SeedEntries s ON i.SeedEntryId = s.Id
left join IMSUsers e ON s.SupervisorId = e.Id
WHERE (@PolyhouseId IS NULL OR i.PolyhouseId = @PolyhouseId)
  AND (@PlantTypeId IS NULL OR i.PlantId = @PlantTypeId)
  AND (@SpeciesId   IS NULL OR i.SpeciesId = @SpeciesId)
  -- Date filters on SeedingDate (inclusive From, inclusive To)
  AND (@SeedingFrom IS NULL OR CONVERT(date, s.SeedingDate) >= @SeedingFrom)
  AND (@SeedingTo   IS NULL OR CONVERT(date, s.SeedingDate) < DATEADD(day, 1, @SeedingTo))
 And s.SupervisorId = @userId
ORDER BY s.SeedingDate";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SeedingFrom", (object)(seedingFrom?.Date) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SeedingTo", (object)(seedingTo?.Date) ?? DBNull.Value);
                    cmd.Parameters.Add("@userId", SqlDbType.Int).Value = userId;


                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            inventoryRecords.Add(new Inventory
                            {
                                Id = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                LastUpdated = reader.GetDateTime(7),
                                Quantity = reader.GetInt32(8),
                                LocationDesc = reader.IsDBNull(10) ? null : reader.GetString(10),
                                SeedEntryId = reader.GetInt32(11),
                                SeedingDate = reader.GetDateTime(12),
                                Supervisor = reader.GetString(13)
                            });
                        }
                    }
                }
            }

            return inventoryRecords;
        }


        public async Task<List<Inventory>> GetUtilizedInventory(int? polyhouseId, int? plantTypeId, int? speciesId, DateTime? dateFrom, DateTime? dateTo)
        {
            var utilizedInventory = new List<Inventory>();

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
        SELECT 
            i.Id, 
            i.PolyhouseId, 
            p.Name AS Polyhouse, 
            i.PlantId, 
            pt.Name AS PlantType, 
            i.SpeciesId, 
            ps.Name AS Species, 
            i.LastUpdated, 
            i.Quantity,
            it.Id AS InventoryTransactionsId,
            it.TransactionDate,
	        it.QuantityUtilized,
             b.CustomerName,
             i.SeedEntryId,
             se.SeedingDate,
             b.DeliveryDate
        FROM Inventory i
           INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
           INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
INNER JOIN InventoryTransactions it ON i.Id = it.InventoryId
INNER JOIN Bookings b ON b.id = it.BookingId
INNER JOIN SeedEntries se ON i.SeedEntryId = se.Id
WHERE 
   it.TransactionType = 'Allocation' 
   AND (@PolyhouseId IS NULL OR i.PolyhouseId = @PolyhouseId)
  AND (@PlantTypeId IS NULL OR i.PlantId = @PlantTypeId)
  AND (@SpeciesId IS NULL OR i.SpeciesId = @SpeciesId)
  AND (@DateFrom IS NULL OR i.LastUpdated >= DATEADD(DAY, -1, @DateFrom))
  AND (@DateTo IS NULL OR i.LastUpdated <= DATEADD(DAY, 1, @DateTo))
ORDER BY i.Id ";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DateFrom", (object)dateFrom ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DateTo", (object)dateTo ?? DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            utilizedInventory.Add(new Inventory
                            {
                                Id = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                Quantity = reader.GetInt32(8),
                                LastUpdated = reader.GetDateTime(7),
                                InventoryTransactionId = reader.GetInt32(9),
                                InventoryTransactionDate = reader.GetDateTime(10),
                                UtilizedQuantity = reader.GetInt32(11),
                                CustomerName = reader.IsDBNull(12) ? null : reader.GetString(12),
                                SeedEntryId = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                                SeedingDate = reader.GetDateTime(14),
                                TentativeDeliveryDate = reader.GetDateTime(15)
                            });
                        }
                    }
                }
            }

            return utilizedInventory;
        }


        public async Task<List<Inventory>> GetAllocatedBookingsInventory(int? polyhouseId, int? plantTypeId, int? speciesId, DateTime? dateFrom, DateTime? dateTo, string? searchCustomer = null)
        {
            var utilizedInventory = new List<Inventory>();

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
        SELECT 
            b.Id, 
            i.PolyhouseId, 
            p.Name AS Polyhouse, 
            i.PlantId, 
            pt.Name AS PlantType, 
            i.SpeciesId, 
            ps.Name AS Species, 
            i.LastUpdated, 
            i.Quantity,
            it.Id AS InventoryTransactionsId,
            it.TransactionDate,
	        it.QuantityUtilized,
             b.CustomerName,
             b.BookingDate,
             b.Quantity, 
             b.ActualDeliveryDate,
             d.DistrictName
        FROM Inventory i
           INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
           INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
INNER JOIN InventoryTransactions it ON i.Id = it.InventoryId
INNER JOIN Bookings b ON b.id = it.BookingId
INNER JOIN Districts d ON b.DistrictId = d.DistrictId
WHERE it.TransactionType = 'Allocation' AND
(@PolyhouseId IS NULL OR i.PolyhouseId = @PolyhouseId)
AND (@PlantTypeId IS NULL OR i.PlantId = @PlantTypeId)
AND (@SpeciesId IS NULL OR i.SpeciesId = @SpeciesId)
AND (@DateFrom IS NULL OR b.ActualDeliveryDate >= @DateFrom)
AND (@DateTo IS NULL OR b.ActualDeliveryDate <= @DateTo)
AND (@SearchCustomer IS NULL OR b.CustomerName LIKE '%' + @SearchCustomer + '%')
ORDER BY i.Id ";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DateFrom", (object)dateFrom ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DateTo", (object)dateTo ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SearchCustomer", (object)searchCustomer ?? DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            utilizedInventory.Add(new Inventory
                            {
                                BookingId = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                Quantity = reader.GetInt32(8),
                                LastUpdated = reader.GetDateTime(7),
                                InventoryTransactionId = reader.GetInt32(9),
                                InventoryTransactionDate = reader.GetDateTime(10),
                                UtilizedQuantity = reader.GetInt32(11),
                                CustomerName = reader.GetString(12),
                                BookingDate = reader.GetDateTime(13),
                                BookingQuantity = reader.GetInt32(14),
                                ActualDeliveryDate = reader.GetDateTime(15),
                                District = reader.IsDBNull(16) ? null : reader.GetString(16)
                                
                            });
                        }
                    }
                }
            }

            return utilizedInventory;
        }

        public async Task<List<Inventory>> GetAllocatedBookings(
    int? polyhouseId,
    int? plantTypeId,
    int? speciesId,
    DateTime? dateFrom,
    DateTime? dateTo,
    string? searchCustomer)
        {
            var result = new List<Inventory>();

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = new StringBuilder(@"
    SELECT 
        b.Id,
        p.Name,
        pt.Name,
        ps.Name,
        b.CustomerName,
        b.BookingDate,
        b.Quantity,
        it.QuantityUtilized,
        b.ActualDeliveryDate,
        d.DistrictName
    FROM InventoryTransactions it
    INNER JOIN Inventory i ON i.Id = it.InventoryId
    INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
    INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
    INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
    INNER JOIN Bookings b ON b.Id = it.BookingId
    INNER JOIN Districts d ON b.DistrictId = d.DistrictId
    WHERE it.TransactionType = 'Allocation'
    ");

            if (polyhouseId.HasValue)
                sql.Append(" AND i.PolyhouseId = @PolyhouseId");

            if (plantTypeId.HasValue)
                sql.Append(" AND i.PlantId = @PlantTypeId");

            if (speciesId.HasValue)
                sql.Append(" AND i.SpeciesId = @SpeciesId");

            if (dateFrom.HasValue)
                sql.Append(" AND b.ActualDeliveryDate >= @DateFrom");

            if (dateTo.HasValue)
                sql.Append(" AND b.ActualDeliveryDate <= @DateTo");

            if (!string.IsNullOrWhiteSpace(searchCustomer))
            {
                sql.Append(" AND LOWER(b.CustomerName) LIKE '%' + LOWER(@SearchCustomer) + '%'");
            }

            sql.Append(" ORDER BY b.Id DESC");

            using var cmd = new SqlCommand(sql.ToString(), conn);

            if (polyhouseId.HasValue)
                cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);

            if (plantTypeId.HasValue)
                cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);

            if (speciesId.HasValue)
                cmd.Parameters.AddWithValue("@SpeciesId", speciesId);

            if (dateFrom.HasValue)
                cmd.Parameters.AddWithValue("@DateFrom", dateFrom);

            if (dateTo.HasValue)
                cmd.Parameters.AddWithValue("@DateTo", dateTo);

            if (!string.IsNullOrWhiteSpace(searchCustomer))
                cmd.Parameters.AddWithValue("@SearchCustomer", searchCustomer);

            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new Inventory
                {
                    BookingId = reader.GetInt32(0),
                    PolyhouseName = reader.GetString(1),
                    PlantTypeName = reader.GetString(2),
                    SpeciesName = reader.GetString(3),
                    CustomerName = reader.GetString(4),
                    BookingDate = reader.GetDateTime(5),
                    BookingQuantity = reader.GetInt32(6),
                    UtilizedQuantity = reader.GetInt32(7),
                    ActualDeliveryDate = reader.GetDateTime(8),
                    District = reader.GetString(9)
                });
            }

            return result;
        }


        public async Task<List<Inventory>> SearchBookingsByCustomer(string customerName)
{
    var result = new List<Inventory>();

    if (string.IsNullOrWhiteSpace(customerName))
        return result;

    // Normalize input: trim + single spaces
    customerName = System.Text.RegularExpressions.Regex
        .Replace(customerName.Trim(), @"\s+", " ");

    var words = customerName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    using (var conn = _dbHelper.GetConnection())
    {
        await conn.OpenAsync();

        var sql = new StringBuilder(@"
SELECT 
    b.Id AS BookingId,
    i.PolyhouseId,
    p.Name AS PolyhouseName,
    i.PlantId,
    pt.Name AS PlantTypeName,
    i.SpeciesId,
    ps.Name AS SpeciesName,
    i.LastUpdated,
    i.Quantity,
    it.Id AS InventoryTransactionId,
    it.TransactionDate AS InventoryTransactionDate,
    it.QuantityUtilized,
    b.CustomerName,
    b.BookingDate,
    b.Quantity AS BookingQuantity,
    b.ActualDeliveryDate,
    d.DistrictName
FROM Inventory i
INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
INNER JOIN InventoryTransactions it ON i.Id = it.InventoryId
INNER JOIN Bookings b ON b.Id = it.BookingId
INNER JOIN Districts d ON b.DistrictId = d.DistrictId
WHERE it.TransactionType = 'Allocation'
");

        // Add dynamic LIKE clauses
        for (int i = 0; i < words.Length; i++)
        {
            sql.Append($" AND b.CustomerName LIKE @word{i}");
        }

        sql.Append(" ORDER BY b.BookingDate DESC;");

        using (var cmd = new SqlCommand(sql.ToString(), conn))
        {
            for (int i = 0; i < words.Length; i++)
            {
                cmd.Parameters.AddWithValue($"@word{i}", $"%{words[i]}%");
            }

            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    result.Add(new Inventory
                    {
                        BookingId = reader.GetInt32(0),
                        PolyhouseId = reader.GetInt32(1),
                        PolyhouseName = reader.GetString(2),
                        PlantId = reader.GetInt32(3),
                        PlantTypeName = reader.GetString(4),
                        SpeciesId = reader.GetInt32(5),
                        SpeciesName = reader.GetString(6),
                        LastUpdated = reader.GetDateTime(7),
                        Quantity = reader.GetInt32(8),
                        InventoryTransactionId = reader.GetInt32(9),
                        InventoryTransactionDate = reader.GetDateTime(10),
                        UtilizedQuantity = reader.GetInt32(11),
                        CustomerName = reader.GetString(12),
                        BookingDate = reader.GetDateTime(13),
                        BookingQuantity = reader.GetInt32(14),
                        ActualDeliveryDate = reader.GetDateTime(15),
                        District = reader.IsDBNull(16) ? null : reader.GetString(16)
                    });
                }
            }
        }
    }

    return result;
}


        public async Task<(bool Success, string Message, int NewQuantity)> UpdateSorting(int id, int quantity)
        {
            using (SqlConnection connection = _dbHelper.GetConnection())
            {
                await connection.OpenAsync();

                using (SqlTransaction transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // Fetch the current record
                        string selectQuery = "SELECT  RemainingQuantity, WastedQuantity FROM Inventory WHERE Id = @Id";
                        using (SqlCommand selectCommand = new SqlCommand(selectQuery, connection, transaction))
                        {
                            selectCommand.Parameters.AddWithValue("@Id", id);
                            using (SqlDataReader reader = await selectCommand.ExecuteReaderAsync())
                            {
                                if (!reader.Read())
                                {
                                    return (false, "Record not found", 0);
                                }

                                int remainingQuantity = Convert.ToInt32(reader["RemainingQuantity"]);
                                int totalWastedQuantity = Convert.ToInt32(reader["WastedQuantity"]);
                                reader.Close();

                                if (quantity > remainingQuantity || quantity < 0)
                                {
                                    return (false, "Invalid quantity", quantity);
                                }
                                 
                              
                                int sortedLossedQuantity = remainingQuantity - quantity;
                                int wastedQuantityAddition = totalWastedQuantity + sortedLossedQuantity;

                                string isUtilized = quantity == 0 ? "Y" : "N";

                                // Update the Inventory table
                                string updateQuery = @"UPDATE Inventory 
                                               SET RemainingQuantity = @NewRemainingQuantity, 
                                                   WastedQuantity = @WastedQuantity,
                                                   IsUtilized = @IsUtilized 
                                               WHERE Id = @Id";

                                using (SqlCommand updateCommand = new SqlCommand(updateQuery, connection, transaction))
                                {
                                    updateCommand.Parameters.AddWithValue("@NewRemainingQuantity", quantity);
                                    updateCommand.Parameters.AddWithValue("@WastedQuantity", wastedQuantityAddition);
                                    updateCommand.Parameters.AddWithValue("@IsUtilized", isUtilized);
                                    updateCommand.Parameters.AddWithValue("@Id", id);

                                    await updateCommand.ExecuteNonQueryAsync();
                                }

                                // Insert into InventoryTransactions
                                string insertQuery = @"INSERT INTO InventoryTransactions (InventoryId, QuantityWasted, TransactionType, TransactionDate, UpdatedOn, AddedBy) 
                                               VALUES (@InventoryId, @QuantityWasted, 'Sorting', getdate(), getdate(), 'Lalit')";

                                using (SqlCommand insertCommand = new SqlCommand(insertQuery, connection, transaction))
                                {
                                    insertCommand.Parameters.AddWithValue("@InventoryId", id);
                                    insertCommand.Parameters.AddWithValue("@QuantityWasted", sortedLossedQuantity);

                                    await insertCommand.ExecuteNonQueryAsync();
                                }

                                // Commit the transaction
                                transaction.Commit();
                                return (true, "Sorting updated successfully", quantity);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Rollback transaction on error
                        transaction.Rollback();
                        return (false, "An error occurred: " + ex.Message, 0);
                    }
                }
            }
        }

        public async Task<(bool Success, string Message, int NewQuantity)> RevertLastSorting(int id, string? userName = null)
        {
            using var connection = _dbHelper.GetConnection();
            await connection.OpenAsync();

            using var tx = connection.BeginTransaction();
            try
            {
                // 1) Pick the most recent *unreverted* Sorting (ignore newer non-sorting)
                const string getLastUnrevertedSortingTxSql = @"
            SELECT TOP 1 Id, ISNULL(QuantityWasted, 0) AS QuantityWasted
            FROM InventoryTransactions
            WHERE InventoryId = @Id
              AND TransactionType = 'Sorting'
              AND (Notes IS NULL OR CHARINDEX('[REVERTED', Notes) = 0)  -- <-- robust flag check
            ORDER BY COALESCE(TransactionDate, UpdatedOn) DESC, Id DESC;";  // <-- don't depend on UpdatedOn alone

                int sortingTxId, qtyWasted;
                using (var cmd = new SqlCommand(getLastUnrevertedSortingTxSql, connection, tx))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync())
                        return (false, "No unreverted Sorting transaction found.", 0);

                    sortingTxId = Convert.ToInt32(r["Id"]);
                    qtyWasted = Convert.ToInt32(r["QuantityWasted"]);
                }

                // 2) Load current inventory
                int remainingQuantity, wastedQuantity;
                const string getInvSql = @"SELECT RemainingQuantity, WastedQuantity FROM Inventory WHERE Id = @Id;";
                using (var invCmd = new SqlCommand(getInvSql, connection, tx))
                {
                    invCmd.Parameters.AddWithValue("@Id", id);
                    using var invR = await invCmd.ExecuteReaderAsync();
                    if (!await invR.ReadAsync())
                        return (false, "Inventory record not found.", 0);

                    remainingQuantity = Convert.ToInt32(invR["RemainingQuantity"]);
                    wastedQuantity = Convert.ToInt32(invR["WastedQuantity"]);
                }

                // 3) Restore counts
                int restoredRemaining = remainingQuantity + qtyWasted;
                int restoredWasted = Math.Max(0, wastedQuantity - qtyWasted);
                string isUtilized = restoredRemaining == 0 ? "Y" : "N";

                const string updInvSql = @"
            UPDATE Inventory
            SET RemainingQuantity = @Remaining,
                WastedQuantity    = @Wasted,
                IsUtilized        = @IsUtilized
            WHERE Id = @Id;";
                using (var upd = new SqlCommand(updInvSql, connection, tx))
                {
                    upd.Parameters.AddWithValue("@Remaining", restoredRemaining);
                    upd.Parameters.AddWithValue("@Wasted", restoredWasted);
                    upd.Parameters.AddWithValue("@IsUtilized", isUtilized);
                    upd.Parameters.AddWithValue("@Id", id);
                    await upd.ExecuteNonQueryAsync();
                }

                // 4) Flag the original Sorting row so we skip it next time
                //    IMPORTANT: don't bump UpdatedOn here, so ordering remains stable.
                const string flagOriginalSql = @"
            UPDATE InventoryTransactions
            SET Notes = CONCAT(
                    ISNULL(NULLIF(Notes, ''), ''),
                    CASE WHEN ISNULL(Notes, '') = '' THEN '' ELSE ' ' END,
                    '[REVERTED ', CONVERT(varchar(19), GETDATE(), 120), ' by ', @By, ']'
                )
            WHERE Id = @TxId;";
                using (var flagCmd = new SqlCommand(flagOriginalSql, connection, tx))
                {
                    flagCmd.Parameters.AddWithValue("@TxId", sortingTxId);
                    flagCmd.Parameters.AddWithValue("@By", (object?)userName ?? "System");
                    await flagCmd.ExecuteNonQueryAsync();
                }

                // 5) Audit trail (optional but recommended)
                const string insertRevertSql = @"
            INSERT INTO InventoryTransactions
                (InventoryId, QuantityWasted, TransactionType, TransactionDate, UpdatedOn, AddedBy, Notes)
            VALUES
                (@InventoryId, @QuantityRestored, 'Adjustment', GETDATE(), GETDATE(), @By, @Notes);";
                using (var ins = new SqlCommand(insertRevertSql, connection, tx))
                {
                    ins.Parameters.AddWithValue("@InventoryId", id);
                    ins.Parameters.AddWithValue("@QuantityRestored", qtyWasted);
                    ins.Parameters.AddWithValue("@By", (object?)userName ?? "System");
                    ins.Parameters.AddWithValue("@Notes", $"Revert of Sorting TxId={sortingTxId}");
                    await ins.ExecuteNonQueryAsync();
                }

                tx.Commit();
                return (true, "Sorting reverted.", restoredRemaining);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, "Failed to revert: " + ex.Message, 0);
            }
        }






        public async Task<List<Inventory>> GetWastedStockReport(
    int? polyhouseId, int? plantTypeId, int? speciesId, DateTime? fromDate, DateTime? toDate)
        {
            List<Inventory> results = new();

            var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
        SELECT i.SeedEntryId,
            se.SeedsPlanted,
            se.SeedingDate,
            e.Name,
            p.Name AS PolyhouseName,
            pt.Name AS PlantTypeName,
            ps.Name AS SpeciesName,
            ISNULL(se.SeedsPlanted - se.TraysAlive, 0) AS TrayWasted,
            ISNULL(se.TraysAlive - se.HardeningAlive, 0) AS HardeningWasted,
            ISNULL(se.HardeningAlive - se.AliveCount, 0) AS InventoryWasted,
            i.WastedQuantity AS SortingWasted, 
            (ISNULL(se.SeedsPlanted - se.TraysAlive, 0) + 
             ISNULL(se.TraysAlive - se.HardeningAlive, 0) + 
             ISNULL(se.HardeningAlive - se.AliveCount, 0)) + i.WastedQuantity AS TotalWasted,
            i.LastUpdated,
            se.locationDesc,
            e.Name
        FROM Inventory i
        INNER JOIN SeedEntries se ON i.SeedEntryId = se.Id
        INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
        INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
        INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
        left join IMSUsers e ON se.SupervisorId = e.Id
        WHERE (@polyhouseId IS NULL OR p.Id = @polyhouseId)
          AND (@plantTypeId IS NULL OR pt.Id = @plantTypeId)
          AND (@speciesId IS NULL OR ps.Id = @speciesId)
          AND se.SeedingDate BETWEEN @fromDate AND @toDate
        ORDER BY i.LastUpdated DESC;";

            cmd.Parameters.AddWithValue("@polyhouseId", (object?)polyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@plantTypeId", (object?)plantTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@speciesId", (object?)speciesId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fromDate", fromDate ?? DateTime.Today.AddDays(-30));
            cmd.Parameters.AddWithValue("@toDate", toDate ?? DateTime.Today);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new Inventory
                {
                    SeedEntryId = Convert.ToInt32(reader["SeedEntryId"]),
                    SeedsPlanted = reader["SeedsPlanted"].ToString(),
                    PolyhouseName = reader["PolyhouseName"].ToString(),
                    PlantTypeName = reader["PlantTypeName"].ToString(),
                    SpeciesName = reader["SpeciesName"].ToString(),
                    WastedInTrays = Convert.ToInt32(reader["TrayWasted"]),
                    WastedInHardening = Convert.ToInt32(reader["HardeningWasted"]),
                    WastedInInventory = Convert.ToInt32(reader["InventoryWasted"]),
                    WastedInSorting = Convert.ToInt32(reader["SortingWasted"]),
                    TotalWasted = Convert.ToInt32(reader["TotalWasted"]),
                    SeedingDate = Convert.ToDateTime(reader["SeedingDate"]),
                    Supervisor = reader["Name"].ToString()
                });
            }

            return results;
        }


        public async Task RestoreInventoryQuantity(int inventoryId, int quantity)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var query = @"UPDATE Inventory
        SET RemainingQuantity = RemainingQuantity + @Qty,
            IsUtilized = CASE WHEN IsUtilized = 'Y' THEN 'N' ELSE IsUtilized END
        WHERE Id = @Id";
            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@Qty", quantity);
            cmd.Parameters.AddWithValue("@Id", inventoryId);

            await cmd.ExecuteNonQueryAsync();
        }

    }
}
