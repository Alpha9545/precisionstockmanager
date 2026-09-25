using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    public class BookingRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly SeedlingFulfilmentRepository _fulfilmentRepo;

        public BookingRepository(DatabaseHelper dbHelper, SeedlingFulfilmentRepository fulfilmentRepo)
        {
            _dbHelper = dbHelper;
            _fulfilmentRepo = fulfilmentRepo;
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



        // Phase C: editing a booking no longer destroys history.
        //   * every change is recorded in dbo.BookingRevisions (previous and new
        //     values, reason, user, date) -- a reason is required;
        //   * BookingDate and AddedBy are no longer overwritten (ModifiedBy /
        //     ModifiedDate record who changed it);
        //   * quantity / variety changes go through the same revision logic as
        //     the Revise page (reservations above the new need are released,
        //     never below what has been dispatched);
        //   * only Pending bookings can be edited.
        // The old 'Booking' row in dbo.Transactions is left as it was written.
        public async Task<(bool Success, string Message)> UpdateBooking(Booking entry, string? reason, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var read = new SqlCommand(@"
SELECT PlantId, SpeciesId, Quantity, DeliveryDate, ISNULL(Status, 'Pending'), CustomerName, Address, Contact,
       AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails, BookedById, StateId, DistrictId
FROM Bookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                read.Parameters.AddWithValue("@Id", entry.Id);
                int plantId, quantity; int? speciesId, bookedById, stateId, districtId; DateTime? delivery; string status;
                string? customer, address, contact, advDetails; bool? advTaken; decimal? advAmount;
                using (var r = await read.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "Booking not found."); }
                    plantId = r.GetInt32(0); speciesId = r.IsDBNull(1) ? null : r.GetInt32(1); quantity = r.GetInt32(2);
                    delivery = r.IsDBNull(3) ? null : r.GetDateTime(3); status = r.GetString(4);
                    customer = r.IsDBNull(5) ? null : r.GetString(5); address = r.IsDBNull(6) ? null : r.GetString(6);
                    contact = r.IsDBNull(7) ? null : r.GetString(7); advTaken = r.IsDBNull(8) ? null : r.GetBoolean(8);
                    advAmount = r.IsDBNull(9) ? null : r.GetDecimal(9); advDetails = r.IsDBNull(10) ? null : r.GetString(10);
                    bookedById = r.IsDBNull(11) ? null : r.GetInt32(11); stateId = r.IsDBNull(12) ? null : r.GetInt32(12);
                    districtId = r.IsDBNull(13) ? null : r.GetInt32(13);
                }
                if (status != "Pending") { tx.Rollback(); return (false, $"Only Pending bookings can be edited (this one is {status})."); }

                var changes = new List<string>();
                void Diff(string label, object? before, object? after)
                {
                    var b = Convert.ToString(before)?.Trim() ?? ""; var a = Convert.ToString(after)?.Trim() ?? "";
                    if (b != a) changes.Add($"{label}: '{b}' -> '{a}'");
                }
                Diff("Customer", customer, entry.CustomerName);
                Diff("Address", address, entry.Address);
                Diff("Contact", contact, entry.Contact);
                Diff("Advance taken", advTaken ?? false, entry.AdvanceTaken);
                Diff("Advance amount", advAmount?.ToString("0.00"), entry.AdvanceTakenAmount?.ToString("0.00"));
                Diff("Advance details", advDetails, entry.AdvanceTakenDetails);
                Diff("Booked by", bookedById, entry.BookedById);
                Diff("State", stateId, entry.StateId);
                Diff("District", districtId, entry.DistrictId);

                var keyChanged = plantId != entry.PlantId || speciesId != entry.SpeciesId || quantity != entry.Quantity
                                 || delivery?.Date != entry.DeliveryDate.Date;
                if (!keyChanged && changes.Count == 0) { tx.Rollback(); return (true, "No changes to save."); }
                if (string.IsNullOrWhiteSpace(reason)) { tx.Rollback(); return (false, "A reason for the change is required (it is kept in the booking history)."); }

                var (ok, message) = await _fulfilmentRepo.ApplyRevisionAsync(conn, tx, entry.Id,
                    new SeedlingFulfilmentRepository.RevisionRequest
                    {
                        NewPlantId = entry.PlantId,
                        NewSpeciesId = entry.SpeciesId,
                        NewQuantity = entry.Quantity,
                        NewDeliveryDate = entry.DeliveryDate,
                        Reason = reason,
                        OtherChanges = changes.Count > 0 ? string.Join("; ", changes) : null
                    },
                    new SeedlingFulfilmentRepository.Actor(entry.AddedBy, userId));
                if (!ok) { tx.Rollback(); return (false, message ?? "Failed to update Booking."); }

                var cmd = new SqlCommand(@"
UPDATE Bookings
SET CustomerName        = @CustomerName,
    Address             = @Address,
    Contact             = @Contact,
    AdvanceTaken        = @AdvanceTaken,
    AdvanceTakenAmount  = @AdvanceTakenAmount,
    AdvanceTakenDetails = @AdvanceTakenDetails,
    BookedById          = @BookedById,
    BookedByOther       = NULL,
    StateId             = @StateId,
    DistrictId          = @DistrictId,
    ModifiedBy          = @UpdatedBy,
    ModifiedDate        = SYSUTCDATETIME()
WHERE Id = @BookingId;", conn, tx);
                cmd.Parameters.AddWithValue("@BookingId", entry.Id);
                cmd.Parameters.AddWithValue("@CustomerName", (object?)entry.CustomerName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Address", entry.Address ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Contact", entry.Contact ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@AdvanceTaken", entry.AdvanceTaken);
                cmd.Parameters.AddWithValue("@AdvanceTakenAmount", (object?)entry.AdvanceTakenAmount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AdvanceTakenDetails", (object?)entry.AdvanceTakenDetails ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@UpdatedBy", entry.AddedBy ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@BookedById", (object?)entry.BookedById ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StateId", (object?)entry.StateId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DistrictId", (object?)entry.DistrictId ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, message ?? "Booking updated successfully!");
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, "Failed to update Booking: " + ex.Message);
            }
        }

        // Phase C: the former DeleteBookingWithTransactions (a hard DELETE of
        // the booking and its dbo.Transactions rows) is removed. Bookings are
        // cancelled instead (SeedlingFulfilmentRepository.CancelAsync), which
        // keeps the history and releases any reservation.

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
