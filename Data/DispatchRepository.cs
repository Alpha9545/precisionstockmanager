using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 10: completes a Phase 9 Booking (dbo.PottedPlantBookings) by
    // physically reducing dbo.PottedPlantStock. Per the stock rule
    // "Booking reserves but never physically reduces stock; Dispatch
    // completion reduces physical stock," dispatching writes TWO ledger
    // entries against the SAME dedicated ledger Phase 7 created
    // (dbo.PottedPlantStockTransactions):
    //   - 'Dispatch'          -> reduces PhysicalQuantity (via RecordTransactionAsync)
    //   - 'ReservationRelease' -> reduces ReservedQuantity (via RecordReservationAsync)
    //
    // Phase 21/Phase G: PARTIAL dispatch is now supported -- more than one
    // Dispatch row may reference the same Booking (UQ_Dispatches_Booking
    // dropped), each for a caller-supplied Quantity that must be > 0 and
    // <= the Booking's own remaining (Quantity - DispatchedQuantity) at
    // that moment, validated under the Booking row's own UPDLOCK/HOLDLOCK.
    // dbo.PottedPlantBookings.DispatchedQuantity is the running total this
    // maintains; the Booking's Status becomes 'PartiallyDispatched' while
    // DispatchedQuantity < Quantity, and 'Dispatched' only once it reaches
    // Quantity exactly. A single full-quantity dispatch (the only thing
    // Phase 10 originally supported) still behaves identically to before:
    // one Dispatch row, DispatchedQuantity jumps straight from 0 to
    // Quantity, Status goes straight from 'Pending' to 'Dispatched'.
    public class DispatchRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;

        public DispatchRepository(
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
    d.Id, d.DispatchCode, d.PottedPlantBookingId, d.PottedPlantStockId, d.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    d.PotSize, d.AreaId, a.Name AS AreaName, d.Quantity, d.DispatchDate, d.Status,
    d.ResponsiblePersonId, ru.Name AS ResponsiblePersonName,
    d.SupervisorId, su.Name AS SupervisorName,
    d.Remarks, d.CreatedDate, d.CreatedBy, d.ModifiedDate, d.ModifiedBy,
    bk.BookingCode, bk.CustomerName
FROM dbo.Dispatches d
INNER JOIN dbo.PlantSpecies ps ON d.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.PottedPlantBookings bk ON d.PottedPlantBookingId = bk.Id
LEFT JOIN dbo.Area a ON d.AreaId = a.Id
LEFT JOIN dbo.IMSUsers ru ON d.ResponsiblePersonId = ru.Id
LEFT JOIN dbo.IMSUsers su ON d.SupervisorId = su.Id";

        public async Task<List<Dispatch>> GetAllAsync(string? status = null)
        {
            var list = new List<Dispatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE (@Status IS NULL OR d.Status = @Status) ORDER BY d.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<Dispatch?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE d.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Bookings that still have something left to dispatch --
        // 'Pending' (nothing dispatched yet) or 'PartiallyDispatched'
        // (Phase 21/Phase G: some already dispatched, some still
        // Reserved) -- offered as the source of a new (possibly partial)
        // Dispatch on the Create page. DispatchedQuantity is returned so
        // the page can show/validate against RemainingQuantity; the
        // PRECISE "does this dispatch fit in what's remaining right now"
        // check still happens in InsertAsync under the Booking row's own
        // lock, since another dispatch could commit between this read and
        // that POST.
        public async Task<List<PottedPlantBooking>> GetDispatchableBookingsAsync()
        {
            var list = new List<PottedPlantBooking>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT bk.Id, bk.BookingCode, bk.PottedPlantStockId, bk.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
       bk.PotSize, bk.AreaId, a.Name AS AreaName, bk.Quantity, bk.DispatchedQuantity, bk.CustomerName, bk.Status
FROM dbo.PottedPlantBookings bk
INNER JOIN dbo.PlantSpecies ps ON bk.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.Area a ON bk.AreaId = a.Id
WHERE bk.Status IN ('Pending', 'PartiallyDispatched')
ORDER BY bk.BookingDate";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new PottedPlantBooking
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    BookingCode = reader.GetString(reader.GetOrdinal("BookingCode")),
                    PottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId")),
                    SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                    SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                    PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                    PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                    AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                    AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                    Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                    DispatchedQuantity = reader.GetDecimal(reader.GetOrdinal("DispatchedQuantity")),
                    CustomerName = reader.GetString(reader.GetOrdinal("CustomerName")),
                    Status = reader.GetString(reader.GetOrdinal("Status"))
                });
            }
            return list;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(Dispatch entry, int? userId)
        {
            // Phase 21/Phase G: entry.Quantity is now the CALLER-SUPPLIED
            // amount to dispatch NOW -- it may be a partial amount, not
            // necessarily the Booking's full Quantity. Validated below
            // against what's actually still remaining on the Booking,
            // under the Booking row's own lock.
            if (entry.Quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the target Booking and derive every stock
                // field from it directly -- never trusted from the
                // caller. Quantity is the one exception: it is the
                // caller's requested (partial or full) dispatch amount,
                // validated against RemainingQuantity below rather than
                // being overwritten.
                var bookingLockCmd = new SqlCommand(
                    "SELECT PottedPlantStockId, SpeciesId, PotSize, AreaId, Quantity, DispatchedQuantity, Status FROM dbo.PottedPlantBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                bookingLockCmd.Parameters.AddWithValue("@Id", entry.PottedPlantBookingId);
                using var bookingReader = await bookingLockCmd.ExecuteReaderAsync();
                if (!await bookingReader.ReadAsync())
                {
                    bookingReader.Close();
                    tx.Rollback();
                    return (false, "Selected Booking not found.", 0);
                }
                var status = bookingReader.GetString(bookingReader.GetOrdinal("Status"));
                if (status != "Pending" && status != "PartiallyDispatched")
                {
                    bookingReader.Close();
                    tx.Rollback();
                    return (false, $"This Booking is '{status}' and cannot be dispatched (only Pending or PartiallyDispatched bookings can be dispatched).", 0);
                }
                var bookingQuantity = bookingReader.GetDecimal(bookingReader.GetOrdinal("Quantity"));
                var dispatchedSoFar = bookingReader.GetDecimal(bookingReader.GetOrdinal("DispatchedQuantity"));
                entry.PottedPlantStockId = bookingReader.GetInt32(bookingReader.GetOrdinal("PottedPlantStockId"));
                entry.SpeciesId = bookingReader.GetInt32(bookingReader.GetOrdinal("SpeciesId"));
                entry.PotSize = bookingReader.GetString(bookingReader.GetOrdinal("PotSize"));
                entry.AreaId = bookingReader.IsDBNull(bookingReader.GetOrdinal("AreaId")) ? null : bookingReader.GetInt32(bookingReader.GetOrdinal("AreaId"));
                bookingReader.Close();

                // Test case 17 ("Dispatch greater than remaining booking
                // quantity -> rejected"): the precise, lock-protected
                // check -- fn_Dispatches_MatchesBooking's own CHECK is
                // only the coarse "<= full Quantity" backstop.
                var remaining = bookingQuantity - dispatchedSoFar;
                if (entry.Quantity > remaining)
                {
                    tx.Rollback();
                    return (false, $"Dispatch quantity ({entry.Quantity:N2}) exceeds this Booking's remaining quantity ({remaining:N2}).", 0);
                }

                var dispatchCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "DIS", entry.DispatchDate.Year);

                // 2) Insert the header row FIRST so its Id is available
                // as ReferenceId on both ledger entries.
                const string insertSql = @"
INSERT INTO dbo.Dispatches
(DispatchCode, PottedPlantBookingId, PottedPlantStockId, SpeciesId, PotSize, AreaId, Quantity,
 DispatchDate, Status, ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@DispatchCode, @PottedPlantBookingId, @PottedPlantStockId, @SpeciesId, @PotSize, @AreaId, @Quantity,
 @DispatchDate, 'Completed', @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@DispatchCode", dispatchCode);
                cmd.Parameters.AddWithValue("@PottedPlantBookingId", entry.PottedPlantBookingId);
                cmd.Parameters.AddWithValue("@PottedPlantStockId", entry.PottedPlantStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                cmd.Parameters.AddWithValue("@DispatchDate", entry.DispatchDate);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                // 3) Physical stock actually leaves -- exactly the
                // (possibly partial) entry.Quantity, not the Booking's
                // full Quantity.
                var (dispatchSuccess, dispatchMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, entry.PottedPlantStockId, -entry.Quantity, "Dispatch", "Dispatch", newId, userId, entry.Remarks);
                if (!dispatchSuccess)
                {
                    tx.Rollback();
                    return (false, dispatchMessage, 0);
                }

                // 3b) Phase 21/Phase G: SoldDispatchedQuantity has existed
                // on dbo.PottedPlantStock since Phase 7 as a "cumulative,
                // informational running total" but was never actually
                // incremented anywhere -- a pre-existing dormant-field gap
                // found while touching this method, fixed here since it's
                // purely informational/display (Pages/Production/
                // PottedPlantStock/{Index,Details}) and never enters any
                // Available/Reserved/Physical calculation.
                var soldDispatchedCmd = new SqlCommand(
                    "UPDATE dbo.PottedPlantStock SET SoldDispatchedQuantity = SoldDispatchedQuantity + @Quantity WHERE Id = @Id",
                    conn, tx);
                soldDispatchedCmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                soldDispatchedCmd.Parameters.AddWithValue("@Id", entry.PottedPlantStockId);
                await soldDispatchedCmd.ExecuteNonQueryAsync();

                // 4) The portion of the reservation this dispatch fulfils
                // comes off Reserved same as a cancellation would.
                var (releaseSuccess, releaseMessage) = await _pottedPlantStockRepo.RecordReservationAsync(
                    conn, tx, entry.PottedPlantStockId, -entry.Quantity, "ReservationRelease", "Dispatch", newId, userId, "Booking dispatched");
                if (!releaseSuccess)
                {
                    tx.Rollback();
                    return (false, releaseMessage, 0);
                }

                // 5) Update the Booking's running DispatchedQuantity and
                // resulting Status -- 'Dispatched' only once fully
                // consumed, 'PartiallyDispatched' otherwise. A single
                // full-quantity dispatch (dispatchedSoFar == 0 and
                // entry.Quantity == bookingQuantity) still goes straight
                // 'Pending' -> 'Dispatched', byte-for-byte the same
                // outcome as before Phase 21.
                var newDispatchedQuantity = dispatchedSoFar + entry.Quantity;
                var newBookingStatus = newDispatchedQuantity >= bookingQuantity ? "Dispatched" : "PartiallyDispatched";
                var updateBookingCmd = new SqlCommand(@"
UPDATE dbo.PottedPlantBookings
SET DispatchedQuantity = @DispatchedQuantity,
    Status = @Status,
    ActualDeliveryDate = CASE WHEN @Status = 'Dispatched' THEN SYSUTCDATETIME() ELSE ActualDeliveryDate END,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateBookingCmd.Parameters.AddWithValue("@DispatchedQuantity", newDispatchedQuantity);
                updateBookingCmd.Parameters.AddWithValue("@Status", newBookingStatus);
                updateBookingCmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                updateBookingCmd.Parameters.AddWithValue("@Id", entry.PottedPlantBookingId);
                await updateBookingCmd.ExecuteNonQueryAsync();

                tx.Commit();
                entry.Id = newId;
                entry.DispatchCode = dispatchCode;
                entry.Status = "Completed";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Non-stock-affecting fields only (ResponsiblePerson/Supervisor/
        // Remarks). PottedPlantStockId/Quantity/the parent Booking link
        // are immutable after creation -- use CancelAsync to reverse.
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(Dispatch entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.Dispatches
SET ResponsiblePersonId = @ResponsiblePersonId,
    SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status = 'Completed'";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Dispatch not found, or it is no longer Completed (already Cancelled).");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Reverses ONE Dispatch row: restores PhysicalQuantity (equal-and-
        // opposite 'Dispatch' ledger entry), restores ReservedQuantity
        // (equal-and-opposite 'ReservationRelease' entry) for exactly
        // that dispatch's own Quantity, and puts the parent Booking back
        // to whichever state correctly reflects what remains dispatched
        // (Phase 21/Phase G: 'Pending' if this was the only/last dispatch,
        // 'PartiallyDispatched' if other dispatches against the same
        // Booking still stand -- never unconditionally 'Pending' any
        // more, since a Booking can now have more than one Dispatch row).
        // Mirrors the reversal pattern used at every phase since Phase 7
        // (equal-and-opposite entries on the same TransactionType, not a
        // separate reversal-specific type). Rejected automatically by
        // RecordReservationAsync's own guard if re-reserving would exceed
        // PhysicalQuantity (e.g. some of it was separately wasted in the
        // meantime) -- "cancellation rejected if reversal would create an
        // invalid stock state" falls out of that existing guard with no
        // new code needed.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT PottedPlantBookingId, PottedPlantStockId, Quantity, Status FROM dbo.Dispatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Dispatch not found.");
                }
                var bookingId = reader.GetInt32(reader.GetOrdinal("PottedPlantBookingId"));
                var pottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId"));
                var quantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Dispatch is already Cancelled.");
                }
                if (status != "Completed")
                {
                    tx.Rollback();
                    return (false, $"This Dispatch is '{status}' and can no longer be cancelled here.");
                }

                // Lock the parent Booking too -- DispatchedQuantity/Status
                // are about to be read-then-written, and a concurrent new
                // partial dispatch against the same Booking must not race
                // this reversal.
                var bookingLockCmd = new SqlCommand(
                    "SELECT Quantity, DispatchedQuantity FROM dbo.PottedPlantBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                bookingLockCmd.Parameters.AddWithValue("@Id", bookingId);
                using var bookingReader = await bookingLockCmd.ExecuteReaderAsync();
                if (!await bookingReader.ReadAsync())
                {
                    bookingReader.Close();
                    tx.Rollback();
                    return (false, "Parent Booking no longer exists.");
                }
                var bookingQuantity = bookingReader.GetDecimal(bookingReader.GetOrdinal("Quantity"));
                var dispatchedSoFar = bookingReader.GetDecimal(bookingReader.GetOrdinal("DispatchedQuantity"));
                bookingReader.Close();

                // Restore physical stock -- exactly this dispatch's own
                // (possibly partial) Quantity.
                var (restoreSuccess, restoreMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, pottedPlantStockId, quantity, "Dispatch", "Dispatch", id, userId, "Dispatch cancelled");
                if (!restoreSuccess)
                {
                    tx.Rollback();
                    return (false, restoreMessage);
                }

                // Undo the informational SoldDispatchedQuantity running
                // total by the same amount (see InsertAsync step 3b).
                var soldDispatchedCmd = new SqlCommand(
                    "UPDATE dbo.PottedPlantStock SET SoldDispatchedQuantity = SoldDispatchedQuantity - @Quantity WHERE Id = @Id",
                    conn, tx);
                soldDispatchedCmd.Parameters.AddWithValue("@Quantity", quantity);
                soldDispatchedCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
                await soldDispatchedCmd.ExecuteNonQueryAsync();

                // Re-reserve the same quantity, since this Dispatch's
                // portion of the Booking's reservation must exist again.
                var (reserveSuccess, reserveMessage) = await _pottedPlantStockRepo.RecordReservationAsync(
                    conn, tx, pottedPlantStockId, quantity, "Reservation", "Dispatch", id, userId, "Dispatch cancelled -- booking re-reserved");
                if (!reserveSuccess)
                {
                    tx.Rollback();
                    return (false, reserveMessage);
                }

                var updateDispatchCmd = new SqlCommand(
                    "UPDATE dbo.Dispatches SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateDispatchCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateDispatchCmd.Parameters.AddWithValue("@Id", id);
                await updateDispatchCmd.ExecuteNonQueryAsync();

                // Recompute the Booking's own DispatchedQuantity/Status --
                // 'Pending' if nothing else is still dispatched, otherwise
                // 'PartiallyDispatched'. ActualDeliveryDate (only ever set
                // once fully 'Dispatched') is cleared in both cases.
                var newDispatchedQuantity = dispatchedSoFar - quantity;
                var newBookingStatus = newDispatchedQuantity <= 0 ? "Pending" : "PartiallyDispatched";
                var updateBookingCmd = new SqlCommand(@"
UPDATE dbo.PottedPlantBookings
SET DispatchedQuantity = @DispatchedQuantity,
    Status = @Status,
    ActualDeliveryDate = NULL,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateBookingCmd.Parameters.AddWithValue("@DispatchedQuantity", newDispatchedQuantity < 0 ? 0 : newDispatchedQuantity);
                updateBookingCmd.Parameters.AddWithValue("@Status", newBookingStatus);
                updateBookingCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateBookingCmd.Parameters.AddWithValue("@Id", bookingId);
                await updateBookingCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static Dispatch Map(SqlDataReader reader)
        {
            return new Dispatch
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                DispatchCode = reader.GetString(reader.GetOrdinal("DispatchCode")),
                PottedPlantBookingId = reader.GetInt32(reader.GetOrdinal("PottedPlantBookingId")),
                PottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                DispatchDate = reader.GetDateTime(reader.GetOrdinal("DispatchDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                BookingCode = reader.GetString(reader.GetOrdinal("BookingCode")),
                CustomerName = reader.GetString(reader.GetOrdinal("CustomerName"))
            };
        }
    }
}
