using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 9: reserves quantity against dbo.PottedPlantStock for a
    // specific Species+PotSize+Area pool. A booking here NEVER touches
    // PhysicalQuantity -- it only raises ReservedQuantity (via
    // PottedPlantStockRepository.RecordReservationAsync), so Available
    // (Physical - Reserved) drops immediately for other bookings while
    // the physical stock itself is untouched until Phase 10 (Dispatch)
    // completes it. This is a NEW, separate module from the existing
    // dbo.Bookings/BookingRepository (which reserves nothing and works
    // against the old dbo.Inventory system instead) -- see the header
    // comment in Database/Phase9_BookingReservation.sql for why.
    public class PottedPlantBookingRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;

        public PottedPlantBookingRepository(
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
    bk.Id, bk.BookingCode, bk.PottedPlantStockId, bk.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    bk.PotSize, bk.AreaId, a.Name AS AreaName, bk.Quantity,
    bk.CustomerName, bk.Address, bk.Contact, bk.StateId, st.StateName, bk.DistrictId, di.DistrictName,
    bk.BookingDate, bk.DeliveryDate, bk.ActualDeliveryDate, bk.Status, bk.DispatchedQuantity,
    bk.AdvanceTaken, bk.AdvanceTakenAmount, bk.AdvanceTakenDetails,
    bk.BookedById, u.Name AS BookedByName, bk.BookedByOther,
    bk.Remarks, bk.CreatedDate, bk.CreatedBy, bk.ModifiedDate, bk.ModifiedBy,
    s.AvailableQuantity AS StockAvailableQuantity
FROM dbo.PottedPlantBookings bk
INNER JOIN dbo.PlantSpecies ps ON bk.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.PottedPlantStock s ON bk.PottedPlantStockId = s.Id
LEFT JOIN dbo.Area a ON bk.AreaId = a.Id
LEFT JOIN dbo.States st ON bk.StateId = st.StateId
LEFT JOIN dbo.Districts di ON bk.DistrictId = di.DistrictId
LEFT JOIN dbo.IMSUsers u ON bk.BookedById = u.Id";

        public async Task<List<PottedPlantBooking>> GetAllAsync(string? status = null)
        {
            var list = new List<PottedPlantBooking>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE (@Status IS NULL OR bk.Status = @Status) ORDER BY bk.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<PottedPlantBooking?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE bk.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(PottedPlantBooking entry, int? userId)
        {
            if (entry.Quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0);
            if (string.IsNullOrWhiteSpace(entry.CustomerName))
                return (false, "Customer Name is required.", 0);

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

                // 1b) Business invariant (Phase G, widened by Phase 7): a
                // Customer Booking (Destination #3, "Direct Customer
                // Sale") may ONLY be made against stock belonging to an
                // active Outlet OR Main Office Area -- never directly
                // against a Growing Partner/production Area or any other
                // Area type. This is enforced here, independently of the
                // posted value and of the page-level AreaAccessService
                // checks (which only gate WHICH Areas a given user may
                // reach, not WHICH Area types a Booking may target at
                // all). Re-derives the Area from the already-locked stock
                // row's real AreaId -- never a posted AreaId. The actual
                // "which Area types" predicate lives in
                // Services/PottedPlantDistributionRules.cs
                // (IsValidCustomerSaleSourceArea), shared with
                // InternalTransferRepository's own destination checks for
                // the other two Phase 7 destinations, so the three can
                // never silently drift apart.
                if (entry.AreaId == null)
                {
                    tx.Rollback();
                    return (false, "Selected stock is not assigned to an Area and cannot be booked.", 0);
                }

                var areaCheckCmd = new SqlCommand(
                    "SELECT IsActive, AreaType FROM dbo.Area WITH (HOLDLOCK) WHERE Id = @AreaId",
                    conn, tx);
                areaCheckCmd.Parameters.AddWithValue("@AreaId", entry.AreaId.Value);
                using var areaReader = await areaCheckCmd.ExecuteReaderAsync();
                var isValidSaleSource = false;
                if (await areaReader.ReadAsync())
                {
                    var areaIsActive = areaReader.GetBoolean(areaReader.GetOrdinal("IsActive"));
                    var areaType = areaReader.IsDBNull(areaReader.GetOrdinal("AreaType")) ? null : areaReader.GetString(areaReader.GetOrdinal("AreaType"));
                    isValidSaleSource = PottedPlantDistributionRules.IsValidCustomerSaleSourceArea(areaType, areaIsActive);
                }
                areaReader.Close();

                if (!isValidSaleSource)
                {
                    tx.Rollback();
                    return (false, "Customer bookings can only be made against stock belonging to an active Outlet or Main Office Area.", 0);
                }

                var bookingCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "BK", entry.BookingDate.Year);

                // 2) Insert the header row FIRST so its Id is available
                // as ReferenceId on the reservation ledger entry.
                const string insertSql = @"
INSERT INTO dbo.PottedPlantBookings
(BookingCode, PottedPlantStockId, SpeciesId, PotSize, AreaId, Quantity,
 CustomerName, Address, Contact, StateId, DistrictId,
 BookingDate, DeliveryDate, Status,
 AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails,
 BookedById, BookedByOther, Remarks, CreatedDate, CreatedBy)
VALUES
(@BookingCode, @PottedPlantStockId, @SpeciesId, @PotSize, @AreaId, @Quantity,
 @CustomerName, @Address, @Contact, @StateId, @DistrictId,
 @BookingDate, @DeliveryDate, 'Pending',
 @AdvanceTaken, @AdvanceTakenAmount, @AdvanceTakenDetails,
 @BookedById, @BookedByOther, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@BookingCode", bookingCode);
                cmd.Parameters.AddWithValue("@PottedPlantStockId", entry.PottedPlantStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                cmd.Parameters.AddWithValue("@CustomerName", entry.CustomerName);
                cmd.Parameters.AddWithValue("@Address", (object?)entry.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Contact", (object?)entry.Contact ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StateId", (object?)entry.StateId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DistrictId", (object?)entry.DistrictId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BookingDate", entry.BookingDate);
                cmd.Parameters.AddWithValue("@DeliveryDate", (object?)entry.DeliveryDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AdvanceTaken", entry.AdvanceTaken);
                cmd.Parameters.AddWithValue("@AdvanceTakenAmount", (object?)entry.AdvanceTakenAmount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AdvanceTakenDetails", (object?)entry.AdvanceTakenDetails ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BookedById", (object?)entry.BookedById ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BookedByOther", (object?)entry.BookedByOther ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                // 3) Reserve the quantity -- never touches
                // PhysicalQuantity, only ReservedQuantity.
                var (reserveSuccess, reserveMessage) = await _pottedPlantStockRepo.RecordReservationAsync(
                    conn, tx, entry.PottedPlantStockId, entry.Quantity, "Reservation", "PottedPlantBooking", newId, userId, entry.Remarks);
                if (!reserveSuccess)
                {
                    tx.Rollback();
                    return (false, reserveMessage, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.BookingCode = bookingCode;
                entry.Status = "Pending";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Updates non-stock-affecting fields only. PottedPlantStockId/
        // Quantity are immutable after creation -- use CancelAsync to
        // release the reservation entirely (a new booking can then be
        // made for a corrected quantity).
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(PottedPlantBooking entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.PottedPlantBookings
SET CustomerName = @CustomerName,
    Address = @Address,
    Contact = @Contact,
    StateId = @StateId,
    DistrictId = @DistrictId,
    DeliveryDate = @DeliveryDate,
    AdvanceTaken = @AdvanceTaken,
    AdvanceTakenAmount = @AdvanceTakenAmount,
    AdvanceTakenDetails = @AdvanceTakenDetails,
    BookedById = @BookedById,
    BookedByOther = @BookedByOther,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status = 'Pending'";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@CustomerName", entry.CustomerName);
                cmd.Parameters.AddWithValue("@Address", (object?)entry.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Contact", (object?)entry.Contact ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StateId", (object?)entry.StateId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DistrictId", (object?)entry.DistrictId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DeliveryDate", (object?)entry.DeliveryDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AdvanceTaken", entry.AdvanceTaken);
                cmd.Parameters.AddWithValue("@AdvanceTakenAmount", (object?)entry.AdvanceTakenAmount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AdvanceTakenDetails", (object?)entry.AdvanceTakenDetails ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BookedById", (object?)entry.BookedById ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BookedByOther", (object?)entry.BookedByOther ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Booking not found, or it is no longer Pending (already Cancelled).");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Releases whatever is STILL reserved on this Booking (Phase 21/
        // Phase G: not necessarily the full original Quantity any more --
        // a 'PartiallyDispatched' Booking has already had some of its
        // reservation converted into a real Dispatch, so only the
        // remaining, still-Reserved portion (Quantity - DispatchedQuantity)
        // is released here) via a 'ReservationRelease' ledger entry, then
        // marks the booking Cancelled. For a 'Pending' Booking
        // (DispatchedQuantity == 0) this is byte-for-byte the same
        // behavior as before Phase 21 -- remaining == the full Quantity.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT PottedPlantStockId, Quantity, DispatchedQuantity, Status FROM dbo.PottedPlantBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Booking not found.");
                }
                var pottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId"));
                var quantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var dispatchedQuantity = reader.GetDecimal(reader.GetOrdinal("DispatchedQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This booking is already Cancelled.");
                }
                if (status != "Pending" && status != "PartiallyDispatched")
                {
                    tx.Rollback();
                    return (false, $"This booking is '{status}' and can no longer be cancelled here.");
                }

                var remaining = quantity - dispatchedQuantity;

                var (releaseSuccess, releaseMessage) = await _pottedPlantStockRepo.RecordReservationAsync(
                    conn, tx, pottedPlantStockId, -remaining, "ReservationRelease", "PottedPlantBooking", id, userId, "Booking cancelled");
                if (!releaseSuccess)
                {
                    tx.Rollback();
                    return (false, releaseMessage);
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.PottedPlantBookings SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
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

        private static PottedPlantBooking Map(SqlDataReader reader)
        {
            return new PottedPlantBooking
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
                CustomerName = reader.GetString(reader.GetOrdinal("CustomerName")),
                Address = reader.IsDBNull(reader.GetOrdinal("Address")) ? null : reader.GetString(reader.GetOrdinal("Address")),
                Contact = reader.IsDBNull(reader.GetOrdinal("Contact")) ? null : reader.GetString(reader.GetOrdinal("Contact")),
                StateId = reader.IsDBNull(reader.GetOrdinal("StateId")) ? null : reader.GetInt32(reader.GetOrdinal("StateId")),
                StateName = reader.IsDBNull(reader.GetOrdinal("StateName")) ? null : reader.GetString(reader.GetOrdinal("StateName")),
                DistrictId = reader.IsDBNull(reader.GetOrdinal("DistrictId")) ? null : reader.GetInt32(reader.GetOrdinal("DistrictId")),
                DistrictName = reader.IsDBNull(reader.GetOrdinal("DistrictName")) ? null : reader.GetString(reader.GetOrdinal("DistrictName")),
                BookingDate = reader.GetDateTime(reader.GetOrdinal("BookingDate")),
                DeliveryDate = reader.IsDBNull(reader.GetOrdinal("DeliveryDate")) ? null : reader.GetDateTime(reader.GetOrdinal("DeliveryDate")),
                ActualDeliveryDate = reader.IsDBNull(reader.GetOrdinal("ActualDeliveryDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ActualDeliveryDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                DispatchedQuantity = reader.GetDecimal(reader.GetOrdinal("DispatchedQuantity")),
                AdvanceTaken = reader.GetBoolean(reader.GetOrdinal("AdvanceTaken")),
                AdvanceTakenAmount = reader.IsDBNull(reader.GetOrdinal("AdvanceTakenAmount")) ? null : reader.GetDecimal(reader.GetOrdinal("AdvanceTakenAmount")),
                AdvanceTakenDetails = reader.IsDBNull(reader.GetOrdinal("AdvanceTakenDetails")) ? null : reader.GetString(reader.GetOrdinal("AdvanceTakenDetails")),
                BookedById = reader.IsDBNull(reader.GetOrdinal("BookedById")) ? null : reader.GetInt32(reader.GetOrdinal("BookedById")),
                BookedByName = reader.IsDBNull(reader.GetOrdinal("BookedByName")) ? null : reader.GetString(reader.GetOrdinal("BookedByName")),
                BookedByOther = reader.IsDBNull(reader.GetOrdinal("BookedByOther")) ? null : reader.GetString(reader.GetOrdinal("BookedByOther")),
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
