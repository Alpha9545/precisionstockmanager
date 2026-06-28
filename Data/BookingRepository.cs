using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    public class BookingRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public BookingRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<int> InsertBookingAsync(Booking entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();

            try
            {
                var cmd = new SqlCommand(@"
INSERT INTO Bookings
(PlantId, SpeciesId, CustomerName, DeliveryDate, Quantity, Status, AddedBy, BookingDate,
 Address, Contact, AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails,
 BookedById, StateId, DistrictId)
VALUES
(@PlantId, @SpeciesId, @CustomerName, @DeliveryDate, @Quantity, 'Pending', @AddedBy, GETDATE(),
 @Address, @Contact, @AdvanceTaken, @AdvanceTakenAmount, @AdvanceTakenDetails,
 @BookedById, @StateId, @DistrictId);
SELECT CAST(SCOPE_IDENTITY() AS int);", conn, transaction);

                cmd.Parameters.AddWithValue("@PlantId", entry.PlantId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@CustomerName", entry.CustomerName);
                cmd.Parameters.AddWithValue("@DeliveryDate", entry.DeliveryDate);
                cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                cmd.Parameters.AddWithValue("@AddedBy", entry.AddedBy);
                cmd.Parameters.AddWithValue("@Address", entry.Address);
                cmd.Parameters.AddWithValue("@Contact", entry.Contact);
                cmd.Parameters.AddWithValue("@AdvanceTaken", entry.AdvanceTaken);
                cmd.Parameters.AddWithValue("@AdvanceTakenAmount", (object?)entry.AdvanceTakenAmount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AdvanceTakenDetails", (object?)entry.AdvanceTakenDetails ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BookedById", (object?)entry.BookedById ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StateId", entry.StateId);
                cmd.Parameters.AddWithValue("@DistrictId", entry.DistrictId);

                int bookingId = (int)await cmd.ExecuteScalarAsync();

                // Log a “Booking” transaction — no need to bind BookingType from the form
                var txCmd = new SqlCommand(@"
INSERT INTO Transactions (BookingId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
VALUES (@BookingId, @TransactionType, @Quantity, GETDATE(), @UpdatedBy, GETDATE());",
                    conn, transaction);

                txCmd.Parameters.AddWithValue("@BookingId", bookingId);
                txCmd.Parameters.AddWithValue("@TransactionType", "Booking");
                txCmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                txCmd.Parameters.AddWithValue("@UpdatedBy", entry.AddedBy);

                await txCmd.ExecuteNonQueryAsync();

                transaction.Commit();
                return bookingId;
            }
            catch
            {
                transaction.Rollback();
                return 0;
            }
        }

        //        public async Task<bool> InsertBooking(Booking entry)
        //        {
        //            using var conn = _dbHelper.GetConnection();
        //            await conn.OpenAsync();
        //            using var transaction = conn.BeginTransaction();

        //            try
        //            {
        //                var cmd = new SqlCommand(@"
        //INSERT INTO Bookings
        //(PlantId, SpeciesId, CustomerName, DeliveryDate, Quantity, Status, AddedBy, BookingDate,
        // Address, Contact, AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails,
        // BookedById, StateId, DistrictId)
        //VALUES
        //(@PlantId, @SpeciesId, @CustomerName, @DeliveryDate, @Quantity, 'Pending', @AddedBy, GETDATE(),
        // @Address, @Contact, @AdvanceTaken, @AdvanceTakenAmount, @AdvanceTakenDetails,
        // @BookedById, @StateId, @DistrictId);
        //SELECT SCOPE_IDENTITY();", conn, transaction);

        //                cmd.Parameters.AddWithValue("@PlantId", entry.PlantId);
        //                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
        //                cmd.Parameters.AddWithValue("@CustomerName", entry.CustomerName);
        //                cmd.Parameters.AddWithValue("@DeliveryDate", entry.DeliveryDate);
        //                cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
        //                cmd.Parameters.AddWithValue("@AddedBy", entry.AddedBy);
        //                cmd.Parameters.AddWithValue("@Address", entry.Address);
        //                cmd.Parameters.AddWithValue("@Contact", entry.Contact);
        //                cmd.Parameters.AddWithValue("@AdvanceTaken", entry.AdvanceTaken);
        //                cmd.Parameters.AddWithValue("@AdvanceTakenAmount", (object?)entry.AdvanceTakenAmount ?? DBNull.Value);
        //                cmd.Parameters.AddWithValue("@AdvanceTakenDetails", (object?)entry.AdvanceTakenDetails ?? DBNull.Value);
        //                cmd.Parameters.AddWithValue("@BookedById", (object?)entry.BookedById ?? DBNull.Value);
        //                cmd.Parameters.AddWithValue("@StateId", entry.StateId);
        //                cmd.Parameters.AddWithValue("@DistrictId", entry.DistrictId);

        //                int bookingId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        //                var txCmd = new SqlCommand(@"
        //INSERT INTO Transactions (BookingId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
        //VALUES (@BookingId, @BookingType, @Quantity, GETDATE(), @UpdatedBy, GETDATE());",
        //                    conn, transaction);

        //                txCmd.Parameters.AddWithValue("@BookingId", bookingId);
        //                txCmd.Parameters.AddWithValue("@BookingType", entry.BookingType);
        //                txCmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
        //                txCmd.Parameters.AddWithValue("@UpdatedBy", entry.AddedBy);

        //                await txCmd.ExecuteNonQueryAsync();

        //                transaction.Commit();
        //                return true;
        //            }
        //            catch
        //            {
        //                transaction.Rollback();
        //                return false;
        //            }
        //        }





        //    public async Task<List<Booking>> GetBookingRecords(int? plantTypeId, int? speciesId, int month, int year, string status)
        //    {
        //        var bookings = new List<Booking>();
        //        using (var conn = _dbHelper.GetConnection())
        //        {
        //            await conn.OpenAsync();
        //            var query = @"
        //SELECT b.CustomerName, b.BookingDate, b.Status, b.DeliveryDate,
        //       pt.Name AS PlantTypeName, ps.Name AS SpeciesName, b.Quantity, b.PlantId, b.SpeciesId, b.Address, b.Contact, b.AdvanceTaken, b.AdvanceTakenAmount, b.AdvanceTakenDetails, b.id, b.BookedById, e.Name, b.BookedByOther
        //FROM Bookings b
        //INNER JOIN PlantTypes pt ON b.PlantId = pt.Id
        //INNER JOIN PlantSpecies ps ON b.SpeciesId = ps.Id
        //LEFT JOIN IMSUsers e ON b.BookedById = e.Id
        //WHERE YEAR(b.DeliveryDate) = @Year
        //AND b.Status = @Status ";


        //            if (plantTypeId.HasValue)
        //                query += " AND b.PlantId = @PlantTypeId";

        //            if (speciesId.HasValue)
        //                query += " AND b.SpeciesId = @SpeciesId";

        //            if (month > 0)
        //                query += " AND MONTH(b.DeliveryDate) = @Month";

        //            query += " ORDER BY b.DeliveryDate ";

        //            var cmd = new SqlCommand(query, conn);
        //            if (month > 0) cmd.Parameters.AddWithValue("@Month", month);
        //            cmd.Parameters.AddWithValue("@Year", year);
        //            cmd.Parameters.AddWithValue("@Status", status);
        //            if (plantTypeId.HasValue) cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId.Value);
        //            if (speciesId.HasValue) cmd.Parameters.AddWithValue("@SpeciesId", speciesId.Value);

        //            using (var reader = await cmd.ExecuteReaderAsync())
        //            {
        //                while (await reader.ReadAsync())
        //                {
        //                    bookings.Add(new Booking
        //                    {
        //                        CustomerName = reader.GetString(0),
        //                        BookingDate = reader.GetDateTime(1),
        //                        Status = reader.GetString(2),
        //                        DeliveryDate = reader.GetDateTime(3),
        //                        PlantTypeName = reader.GetString(4),
        //                        SpeciesName = reader.GetString(5),
        //                        Quantity = reader.GetInt32(6),
        //                        PlantId = reader.GetInt32(7),
        //                        SpeciesId = reader.GetInt32(8),
        //                        Address = reader.GetString(9),
        //                        Contact = reader.GetString(10),
        //                        AdvanceTaken = reader.IsDBNull(11) ? false : reader.GetBoolean(11), // Handle NULL
        //                        AdvanceTakenAmount = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12), // Handle NULL
        //                        AdvanceTakenDetails = reader.IsDBNull(13) ? string.Empty : reader.GetString(13), // Handle NULL
        //                        Id = reader.GetInt32(14),
        //                        BookedById = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
        //                        BookedByName = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
        //                        BookedByOther = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),



        //                    });
        //                }
        //            }
        //        }
        //        return bookings;
        //    }

        public async Task<List<Booking>> GetBookingRecords(int? plantTypeId, int? speciesId, int month, int year, string status, int? userId)
        {
            var bookings = new List<Booking>();

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();

                var sql = @"
SELECT
    b.Id,
    b.CustomerName,
    b.BookingDate,
    b.Status,
    b.DeliveryDate,
    b.Quantity,
    b.Address,
    b.Contact,
    b.AdvanceTaken,
    b.AdvanceTakenAmount,
    b.AdvanceTakenDetails,
    b.PlantId,
    b.SpeciesId,
    pt.Name     AS PlantTypeName,
    ps.Name     AS SpeciesName,
    b.BookedById,
    u.Name      AS BookedByName,
    b.BookedByOther,
    b.StateId,
    s.StateName,
    b.DistrictId,
    d.DistrictName
FROM Bookings b
INNER JOIN PlantTypes   pt ON b.PlantId   = pt.Id
INNER JOIN PlantSpecies ps ON b.SpeciesId = ps.Id
LEFT  JOIN IMSUsers     u  ON b.BookedById = u.Id 
LEFT  JOIN States       s  ON b.StateId    = s.StateId
LEFT  JOIN Districts    d  ON b.DistrictId = d.DistrictId
WHERE YEAR(b.DeliveryDate) = @Year
  AND b.Status = @Status
";

                if (plantTypeId.HasValue) sql += "  AND b.PlantId = @PlantTypeId";
                if (speciesId.HasValue) sql += "  AND b.SpeciesId = @SpeciesId";
                if (month > 0) sql += "  AND MONTH(b.DeliveryDate) = @Month";

                // Special user-based filter: if the logged-in user is 14 or 15, restrict to that booking id
                if (userId.HasValue && (userId.Value != 1))
                {
                    sql += "  AND b.BookedById = @BookingIdForUser";
                }
                sql += " ORDER BY b.DeliveryDate;";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Year", year);
                cmd.Parameters.AddWithValue("@Status", status);
                if (plantTypeId.HasValue) cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId.Value);
                if (speciesId.HasValue) cmd.Parameters.AddWithValue("@SpeciesId", speciesId.Value);
                if (month > 0) cmd.Parameters.AddWithValue("@Month", month);
                if (userId.HasValue && (userId.Value != 1)) cmd.Parameters.AddWithValue("@BookingIdForUser", userId);
                using var r = await cmd.ExecuteReaderAsync();

                // Resolve ordinals once
                int o_Id = r.GetOrdinal("Id");
                int o_CustomerName = r.GetOrdinal("CustomerName");
                int o_BookingDate = r.GetOrdinal("BookingDate");
                int o_Status = r.GetOrdinal("Status");
                int o_DeliveryDate = r.GetOrdinal("DeliveryDate");
                int o_Quantity = r.GetOrdinal("Quantity");
                int o_Address = r.GetOrdinal("Address");
                int o_Contact = r.GetOrdinal("Contact");
                int o_AdvanceTaken = r.GetOrdinal("AdvanceTaken");
                int o_AdvanceTakenAmount = r.GetOrdinal("AdvanceTakenAmount");
                int o_AdvanceTakenDetails = r.GetOrdinal("AdvanceTakenDetails");
                int o_PlantId = r.GetOrdinal("PlantId");
                int o_SpeciesId = r.GetOrdinal("SpeciesId");
                int o_PlantTypeName = r.GetOrdinal("PlantTypeName");
                int o_SpeciesName = r.GetOrdinal("SpeciesName");
                int o_BookedById = r.GetOrdinal("BookedById");
                int o_BookedByName = r.GetOrdinal("BookedByName");
                int o_BookedByOther = r.GetOrdinal("BookedByOther");
                int o_StateId = r.GetOrdinal("StateId");
                int o_StateName = r.GetOrdinal("StateName");
                int o_DistrictId = r.GetOrdinal("DistrictId");
                int o_DistrictName = r.GetOrdinal("DistrictName");

                while (await r.ReadAsync())
                {
                    bookings.Add(new Booking
                    {
                        Id = r.GetInt32(o_Id),
                        CustomerName = r.GetString(o_CustomerName),
                        BookingDate = r.GetDateTime(o_BookingDate),
                        Status = r.GetString(o_Status),
                        DeliveryDate = r.GetDateTime(o_DeliveryDate),
                        Quantity = r.GetInt32(o_Quantity),
                        Address = r.IsDBNull(o_Address) ? "" : r.GetString(o_Address),
                        Contact = r.IsDBNull(o_Contact) ? "" : r.GetString(o_Contact),

                        AdvanceTaken = !r.IsDBNull(o_AdvanceTaken) && r.GetBoolean(o_AdvanceTaken),
                        AdvanceTakenAmount = r.IsDBNull(o_AdvanceTakenAmount) ? 0m : r.GetDecimal(o_AdvanceTakenAmount),
                        AdvanceTakenDetails = r.IsDBNull(o_AdvanceTakenDetails) ? "" : r.GetString(o_AdvanceTakenDetails),

                        PlantId = r.GetInt32(o_PlantId),
                        SpeciesId = r.GetInt32(o_SpeciesId),
                        PlantTypeName = r.GetString(o_PlantTypeName),
                        SpeciesName = r.GetString(o_SpeciesName),

                        BookedById = r.IsDBNull(o_BookedById) ? 0 : r.GetInt32(o_BookedById),
                        BookedByName = r.IsDBNull(o_BookedByName) ? "" : r.GetString(o_BookedByName),
                        BookedByOther = r.IsDBNull(o_BookedByOther) ? "" : r.GetString(o_BookedByOther),

                        StateId = r.IsDBNull(o_StateId) ? 0 : r.GetInt32(o_StateId),
                        StateName = r.IsDBNull(o_StateName) ? "" : r.GetString(o_StateName),
                        DistrictId = r.IsDBNull(o_DistrictId) ? 0 : r.GetInt32(o_DistrictId),
                        DistrictName = r.IsDBNull(o_DistrictName) ? "" : r.GetString(o_DistrictName),
                    });
                }
            }

            return bookings;
        }



        public async Task<bool> UpdateBooking(Booking entry)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        var cmd = new SqlCommand(@"
UPDATE Bookings 
SET PlantId             = @PlantId, 
    SpeciesId           = @SpeciesId,
    CustomerName        = @CustomerName, 
    DeliveryDate        = @DeliveryDate, 
    Quantity            = @Quantity,
    Address             = @Address, 
    Contact             = @Contact,
    AdvanceTaken        = @AdvanceTaken,
    AdvanceTakenAmount  = @AdvanceTakenAmount,
    AdvanceTakenDetails = @AdvanceTakenDetails,
    AddedBy             = @UpdatedBy,
    BookingDate         = GETDATE(),
    BookedById          = @BookedById,
    BookedByOther       = NULL,
    StateId             = @StateId,
    DistrictId          = @DistrictId
WHERE Id = @BookingId;", conn, transaction);

                        cmd.Parameters.AddWithValue("@BookingId", entry.Id);
                        cmd.Parameters.AddWithValue("@PlantId", entry.PlantId);
                        cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                        cmd.Parameters.AddWithValue("@CustomerName", entry.CustomerName);
                        cmd.Parameters.AddWithValue("@DeliveryDate", entry.DeliveryDate);
                        cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                        cmd.Parameters.AddWithValue("@Address", entry.Address ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Contact", entry.Contact ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@AdvanceTaken", entry.AdvanceTaken);
                        cmd.Parameters.AddWithValue("@AdvanceTakenAmount", (object?)entry.AdvanceTakenAmount ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@AdvanceTakenDetails", (object?)entry.AdvanceTakenDetails ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@UpdatedBy", entry.AddedBy ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@BookedById", (object?)entry.BookedById ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@StateId", entry.StateId);
                        cmd.Parameters.AddWithValue("@DistrictId", entry.DistrictId);

                        int rowsAffected = await cmd.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            var transactionCmd = new SqlCommand(@"
UPDATE Transactions 
SET Quantity = @Quantity,
    UpdatedBy = @UpdatedBy,
    UpdatedOn = GETDATE()
WHERE BookingId = @BookingId;", conn, transaction);

                            transactionCmd.Parameters.AddWithValue("@BookingId", entry.Id);
                            transactionCmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                            transactionCmd.Parameters.AddWithValue("@UpdatedBy", entry.AddedBy ?? (object)DBNull.Value);

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


        public async Task<(bool Success, string Message)> DeleteBookingWithTransactions(int bookingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Delete related transactions FIRST.
                // TODO: Replace table names/columns to match your schema if different.
                // Common names: BookingTransactions, BookingPayments, BookingLedger, etc.
                var deleteTxSql = @"DELETE FROM Transactions WHERE BookingId = @Id;";
                using (var cmdTx = new SqlCommand(deleteTxSql, conn, tx))
                {
                    cmdTx.Parameters.AddWithValue("@Id", bookingId);
                    await cmdTx.ExecuteNonQueryAsync();
                }

                // 2) Delete the booking row.
                var deleteBookingSql = @"DELETE FROM Bookings WHERE Id = @Id;";
                int affected;
                using (var cmdBk = new SqlCommand(deleteBookingSql, conn, tx))
                {
                    cmdBk.Parameters.AddWithValue("@Id", bookingId);
                    affected = await cmdBk.ExecuteNonQueryAsync();
                }

                if (affected == 0)
                {
                    tx.Rollback();
                    return (false, "Booking not found.");
                }

                tx.Commit();
                return (true, "Deleted");
            }
            catch (SqlException ex)
            {
                tx.Rollback();
                // FK violations etc. will land here if other dependent tables exist.
                return (false, "Delete failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, "Delete failed: " + ex.Message);
            }
        }

        public async Task<Booking?> GetBookingById(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var query = "SELECT * FROM Bookings WHERE Id = @Id";
            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@Id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Booking
                {
                    Id = reader.GetInt32(0),
                    CustomerName = reader["CustomerName"].ToString(),
                    Quantity = reader.GetInt32(reader.GetOrdinal("Quantity")),
                    Status = reader["Status"].ToString()
                };
            }
            return null;
        }

        public async Task UpdateStatus(int id, string status)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var query = "UPDATE Bookings SET Status = @Status WHERE Id = @Id";
            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@Status", status);
            cmd.Parameters.AddWithValue("@Id", id);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<State>> GetAllStatesAsync()
        {
            var list = new List<State>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand("SELECT StateId, StateName FROM States ORDER BY StateName", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new State { StateId = r.GetInt32(0), StateName = r.GetString(1) });
            }
            return list;
        }
    

    

        public async Task<List<District>> GetByStateAsync(int stateId)
        {
            var list = new List<District>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand("SELECT DistrictId, DistrictName, StateId FROM Districts WHERE StateId=@sid ORDER BY DistrictName", conn);
            cmd.Parameters.AddWithValue("@sid", stateId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new District
                {
                    DistrictId = r.GetInt32(0),
                    DistrictName = r.GetString(1),
                    StateId = r.GetInt32(2)
                });
            }
            return list;
        }

    }
}
