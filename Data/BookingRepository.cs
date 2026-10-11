using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

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

        // Daily Report (Step 3A): "new seedling bookings created TODAY" is
        // BookingDate, NOT DeliveryDate -- the only date GetBookingRecords
        // (above) and SeedlingFulfilmentRepository.ListBookingsAsync filter
        // by is DeliveryDate ("scheduled for delivery"), which answers a
        // different question. This is a new, minimal, read-only aggregate
        // (count + quantity only, not the full booking-detail row shape
        // GetBookingRecords returns, since the daily report needs neither
        // customer/address/etc. nor a full in-memory list). Exclusive upper
        // bound (never "<= to.Date") because BookingDate is a DATETIME
        // column, unlike the pure DATE columns elsewhere in this app.
        // Counts every status (Pending/Confirmed/Cancelled/...) -- "how many
        // booking requests came in today," the same convention
        // ManagementDashboardRepository.GetOutletSalesSummaryAsync already
        // uses for TotalBookingsInRange (potted-plant bookings), which has
        // no status filter either.
        // Count = unique customer ORDERS (EffectiveOrderId = ISNULL(OrderBookingId, Id)):
        // one order with three varieties is ONE booking. Quantity = all variety lines.
        // from / toExclusive are India business dates (Asia/Kolkata). BookingDate is written
        // with GETDATE() -- SQL Server's local time -- so the India-day bounds are turned into
        // UTC and then into SQL Server local time with SQL Server's own offset. On a server
        // that runs on India time this is exactly the old "BookingDate >= @From" comparison.
        public async Task<(int Count, decimal Quantity)> GetBookingCountByBookingDateAsync(DateTime from, DateTime toExclusive)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
DECLARE @ServerOffset INT = " + ManagementDashboardRepository.ServerOffsetSql + @";
SELECT COUNT(DISTINCT ISNULL(OrderBookingId, Id)), CAST(ISNULL(SUM(Quantity), 0) AS DECIMAL(18,2))
FROM dbo.Bookings
WHERE BookingDate >= DATEADD(MINUTE, @ServerOffset, @FromUtc) AND BookingDate < DATEADD(MINUTE, @ServerOffset, @ToUtc)", conn);
            cmd.Parameters.Add("@FromUtc", System.Data.SqlDbType.DateTime2).Value = PlantStockManager.Services.AppClock.IndiaToUtc(from.Date);
            cmd.Parameters.Add("@ToUtc", System.Data.SqlDbType.DateTime2).Value = PlantStockManager.Services.AppClock.IndiaToUtc(toExclusive.Date);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return (reader.GetInt32(0), reader.GetDecimal(1));
            return (0, 0m);
        }

        // One single-variety booking (kept for existing callers): an order with one line.
        // Returns the new Booking Id, or 0 when nothing was saved.
        public async Task<int> InsertBookingAsync(Booking entry)
        {
            var (ok, _, orderId, _) = await InsertOrderAsync(entry,
                new[] { new BookingOrders.NewLine(entry.PlantId, entry.SpeciesId, entry.Quantity, entry.DeliveryDate == default ? null : entry.DeliveryDate) });
            return ok ? orderId : 0;
        }

        // A customer ORDER ("Add Another Species"): every variety line becomes its own
        // dbo.Bookings row (own quantity, delivery date, status and fulfilment), all
        // saved in ONE transaction and linked by OrderBookingId = the first line's Id
        // (the Booking Number). Customer-level fields (customer, contact, address,
        // state, district, Booked By, AddedBy) come from `order` and are the same on
        // every line; the advance is recorded once, on the first line. Any invalid
        // line or database error rolls the whole order back -- no partial order.
        public async Task<(bool Success, string? Message, int OrderId, List<int> LineIds)> InsertOrderAsync(
            Booking order, IReadOnlyList<BookingOrders.NewLine> lines)
        {
            var (valid, error) = BookingOrders.ValidateNewOrder(lines);
            if (!valid)
                return (false, error, 0, new List<int>());

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var ids = new List<int>();
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var check = new SqlCommand("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Id = @S AND PlantTypeId = @P", conn, transaction);
                    check.Parameters.AddWithValue("@S", line.SpeciesId);
                    check.Parameters.AddWithValue("@P", line.PlantId);
                    if ((int)(await check.ExecuteScalarAsync())! != 1)
                    {
                        transaction.Rollback();
                        return (false, lines.Count > 1
                            ? $"Variety {i + 1} does not belong to the selected species. Nothing was saved."
                            : "The selected variety does not belong to the selected species.", 0, new List<int>());
                    }

                    var withAdvance = i == 0 && order.AdvanceTaken;
                    var cmd = new SqlCommand(@"
INSERT INTO Bookings
(PlantId, SpeciesId, CustomerName, DeliveryDate, Quantity, Status, AddedBy, BookingDate,
 Address, Contact, AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails,
 BookedById, StateId, DistrictId, OrderBookingId)
VALUES
(@PlantId, @SpeciesId, @CustomerName, @DeliveryDate, @Quantity, 'Pending', @AddedBy, GETDATE(),
 @Address, @Contact, @AdvanceTaken, @AdvanceTakenAmount, @AdvanceTakenDetails,
 @BookedById, @StateId, @DistrictId, @OrderBookingId);
SELECT CAST(SCOPE_IDENTITY() AS int);", conn, transaction);

                    cmd.Parameters.AddWithValue("@PlantId", line.PlantId);
                    cmd.Parameters.AddWithValue("@SpeciesId", line.SpeciesId);
                    cmd.Parameters.AddWithValue("@CustomerName", (object?)order.CustomerName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DeliveryDate", line.DeliveryDate!.Value.Date);
                    cmd.Parameters.AddWithValue("@Quantity", line.Quantity);
                    cmd.Parameters.AddWithValue("@AddedBy", (object?)order.AddedBy ?? "System");
                    cmd.Parameters.AddWithValue("@Address", (object?)order.Address ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Contact", (object?)order.Contact ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@AdvanceTaken", withAdvance);
                    cmd.Parameters.AddWithValue("@AdvanceTakenAmount", withAdvance ? (object?)order.AdvanceTakenAmount ?? DBNull.Value : 0m);
                    cmd.Parameters.AddWithValue("@AdvanceTakenDetails", withAdvance ? (object?)order.AdvanceTakenDetails ?? DBNull.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@BookedById", (object?)order.BookedById ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@StateId", (object?)order.StateId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DistrictId", (object?)order.DistrictId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@OrderBookingId", ids.Count == 0 ? DBNull.Value : ids[0]);

                    var bookingId = (int)(await cmd.ExecuteScalarAsync())!;
                    if (ids.Count == 0)
                    {
                        // the first line IS the order: it carries its own Id as the Booking Number
                        var self = new SqlCommand("UPDATE Bookings SET OrderBookingId = Id WHERE Id = @Id", conn, transaction);
                        self.Parameters.AddWithValue("@Id", bookingId);
                        await self.ExecuteNonQueryAsync();
                    }
                    ids.Add(bookingId);

                    // a 'Booking' log row per line, as before
                    var txCmd = new SqlCommand(@"
INSERT INTO Transactions (BookingId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
VALUES (@BookingId, @TransactionType, @Quantity, GETDATE(), @UpdatedBy, GETDATE());", conn, transaction);
                    txCmd.Parameters.AddWithValue("@BookingId", bookingId);
                    txCmd.Parameters.AddWithValue("@TransactionType", "Booking");
                    txCmd.Parameters.AddWithValue("@Quantity", line.Quantity);
                    txCmd.Parameters.AddWithValue("@UpdatedBy", (object?)order.AddedBy ?? "System");
                    await txCmd.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                return (true, ids.Count == 1 ? $"Booking #{ids[0]} saved." : $"Booking #{ids[0]} saved with {ids.Count} varieties.", ids[0], ids);
            }
            catch (Exception ex)
            {
                try { transaction.Rollback(); } catch { }
                return (false, "The booking could not be saved (nothing was saved): " + ex.Message, 0, new List<int>());
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

        // The "Booking By" dropdown of the seedling booking lists: the people / names that occur on dbo.Bookings.
        // ownerUserId ("own bookings only" users): only the names on that user's own bookings.
        public async Task<List<BookedByFilter.Option>> GetBookedByOptionsAsync(int? ownerUserId = null)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            // An int formatted invariantly -- not user text -- so it is safe inside the scope SQL.
            var scope = ownerUserId.HasValue
                ? "b.BookedById = " + ownerUserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            return await BookedBySql.LoadOptionsAsync(conn, "dbo.Bookings b", "b", scope, null);
        }

        // userId: unused. It used to silently restrict every user except Id 1 to
        // only their own BookedById rows -- dbo.Bookings has no Area scoping, so
        // that arbitrary per-user filter (not the page's own Booking.View/
        // Booking.Direct/Dispatch.View/Reports.View authorization) was hiding
        // this global report's rows from every ordinary user. Removed; kept in
        // the signature to avoid touching every call site for a parameter that
        // may still be wired up for a future, genuinely Area-scoped filter.
        //
        // bookedBy (Correction #6): the optional "Booking By" filter on the existing BookedById / BookedByOther columns.
        // It is one more AND on the same WHERE (parameters only); null / "All" leaves the result exactly as it was.
        // dbo.Bookings has no Area, so which bookings a user may list is decided by the pages' permissions, unchanged --
        // the filter can only narrow that list.
        //
        // ownerUserId (BookingOwnershipService.OwnerFilter): "own bookings only" users get only the lines of ORDERS whose
        // owner (the order's first line BookedById) is their own Id -- one more AND in SQL, so the Booking By filter / PDF
        // can never widen it. null = everyone's rows. Each row is one variety line; OrderBookingId / OrderLineCount let
        // the pages group the lines into customer orders (BookingOrders.Group).
        public async Task<List<Booking>> GetBookingRecords(int? plantTypeId, int? speciesId, int month, int year, string status, int? userId,
            BookedByFilter? bookedBy = null, int? ownerUserId = null)
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
    d.DistrictName,
    b.OrderBookingId,
    CASE WHEN b.OrderBookingId IS NULL THEN 1
         ELSE (SELECT COUNT(*) FROM Bookings ol WHERE ol.OrderBookingId = b.OrderBookingId) END AS OrderLineCount
FROM Bookings b
INNER JOIN Bookings     ord ON ord.Id = ISNULL(b.OrderBookingId, b.Id)
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
                sql += BookedBySql.Clause("b.BookedById", "b.BookedByOther");
                // "own bookings only": the ORDER's owner (its first line), so a restricted user
                // sees every line of their own orders and no line of anyone else's.
                sql += "  AND (@Owner IS NULL OR ord.BookedById = @Owner)";

                sql += " ORDER BY b.DeliveryDate, ISNULL(b.OrderBookingId, b.Id), b.Id;";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@Owner", System.Data.SqlDbType.Int).Value = (object?)ownerUserId ?? DBNull.Value;
                BookedBySql.AddParameters(cmd, bookedBy);
                cmd.Parameters.AddWithValue("@Year", year);
                cmd.Parameters.AddWithValue("@Status", status);
                if (plantTypeId.HasValue) cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId.Value);
                if (speciesId.HasValue) cmd.Parameters.AddWithValue("@SpeciesId", speciesId.Value);
                if (month > 0) cmd.Parameters.AddWithValue("@Month", month);
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
                int o_OrderBookingId = r.GetOrdinal("OrderBookingId");
                int o_OrderLineCount = r.GetOrdinal("OrderLineCount");

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
                        OrderBookingId = r.IsDBNull(o_OrderBookingId) ? null : r.GetInt32(o_OrderBookingId),
                        OrderLineCount = r.GetInt32(o_OrderLineCount),
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
       AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails, BookedById, StateId, DistrictId, OrderBookingId
FROM Bookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                read.Parameters.AddWithValue("@Id", entry.Id);
                int plantId, quantity; int? speciesId, bookedById, stateId, districtId, orderBookingId; DateTime? delivery; string status;
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
                    orderBookingId = r.IsDBNull(14) ? null : r.GetInt32(14);
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

                // Customer-level fields belong to the ORDER: when they change they are applied to
                // every line of the order (below), so the order never becomes inconsistent.
                var siblingCount = 0;
                if (orderBookingId.HasValue)
                {
                    var sib = new SqlCommand("SELECT COUNT(*) FROM Bookings WITH (UPDLOCK, HOLDLOCK) WHERE OrderBookingId = @O AND Id <> @Id", conn, tx);
                    sib.Parameters.AddWithValue("@O", orderBookingId.Value);
                    sib.Parameters.AddWithValue("@Id", entry.Id);
                    siblingCount = (int)(await sib.ExecuteScalarAsync())!;
                }
                var orderLevelChanged = changes.Any(c => c.StartsWith("Customer:") || c.StartsWith("Address:") || c.StartsWith("Contact:")
                                                        || c.StartsWith("Booked by:") || c.StartsWith("State:") || c.StartsWith("District:"));
                if (orderLevelChanged && siblingCount > 0)
                    changes.Add($"customer details applied to all {siblingCount + 1} varieties of Booking #{orderBookingId}");

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

                if (orderLevelChanged && siblingCount > 0)
                {
                    var siblings = new SqlCommand(@"
UPDATE Bookings
SET CustomerName = @CustomerName, Address = @Address, Contact = @Contact,
    BookedById = @BookedById, BookedByOther = NULL, StateId = @StateId, DistrictId = @DistrictId,
    ModifiedBy = @UpdatedBy, ModifiedDate = SYSUTCDATETIME()
WHERE OrderBookingId = @O AND Id <> @BookingId;", conn, tx);
                    siblings.Parameters.AddWithValue("@O", orderBookingId!.Value);
                    siblings.Parameters.AddWithValue("@BookingId", entry.Id);
                    siblings.Parameters.AddWithValue("@CustomerName", (object?)entry.CustomerName ?? DBNull.Value);
                    siblings.Parameters.AddWithValue("@Address", entry.Address ?? (object)DBNull.Value);
                    siblings.Parameters.AddWithValue("@Contact", entry.Contact ?? (object)DBNull.Value);
                    siblings.Parameters.AddWithValue("@BookedById", (object?)entry.BookedById ?? DBNull.Value);
                    siblings.Parameters.AddWithValue("@StateId", (object?)entry.StateId ?? DBNull.Value);
                    siblings.Parameters.AddWithValue("@DistrictId", (object?)entry.DistrictId ?? DBNull.Value);
                    siblings.Parameters.AddWithValue("@UpdatedBy", entry.AddedBy ?? (object)DBNull.Value);
                    await siblings.ExecuteNonQueryAsync();
                }

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
