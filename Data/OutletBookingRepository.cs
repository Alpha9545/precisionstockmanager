using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase E: a customer order across several potted-plant and/or
    // ready-tray items, reserved now, collected fully or partially later,
    // cancel releases whatever is still open -- the same Physical/Reserved/
    // Available shape every other stock in this app already uses, for both
    // stock types, spanning several items under one customer/booking.
    public class OutletBookingRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly ReadyStockRepository _readyStockRepo;

        public OutletBookingRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo,
            PottedPlantStockRepository pottedPlantStockRepo, ReadyStockRepository readyStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _readyStockRepo = readyStockRepo;
        }

        // StockId = PottedPlantStockId when StockType="Potted", or
        // ReadyStockId when StockType="Tray". Quantity = pots or WHOLE TRAYS.
        public record BookingItemInput(string StockType, int StockId, decimal Quantity);

        private const string HeaderSelect = @"
SELECT b.Id, b.BookingCode, b.OutletAreaId, a.Name AS OutletAreaName, b.CustomerName, b.CustomerContact, b.BookingDate,
       b.RequiredDate, b.Status, b.Remarks, b.CreatedById, b.CreatedBy, b.CreatedDate,
       b.CancelledById, b.CancelledDate, b.CancellationReason, b.ModifiedBy, b.ModifiedDate
FROM dbo.OutletBookings b
INNER JOIN dbo.Area a ON a.Id = b.OutletAreaId";

        private const string ItemSelect = @"
SELECT i.Id, i.BookingId, i.OutletAreaId, i.StockType, i.PottedPlantStockId, i.ReadyStockId, i.SpeciesId,
       ps.Name AS SpeciesName, pt.Name AS PlantTypeName, ps.Color AS SpeciesColor, i.PotSize, i.CavityType,
       i.Quantity, i.CollectedQuantity, i.CreatedDate
FROM dbo.OutletBookingItems i
INNER JOIN dbo.PlantSpecies ps ON ps.Id = i.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId";

        public async Task<List<OutletBooking>> GetAllAsync(int? outletAreaId = null, string? status = null)
        {
            var list = new List<OutletBooking>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using (var cmd = new SqlCommand(HeaderSelect + " WHERE (@AreaId IS NULL OR b.OutletAreaId = @AreaId) AND (@Status IS NULL OR b.Status = @Status) ORDER BY b.BookingDate DESC, b.Id DESC", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)outletAreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    list.Add(MapHeader(r));
            }
            if (list.Count == 0) return list;

            using (var cmd = new SqlCommand(ItemSelect + " WHERE i.BookingId IN (SELECT Id FROM dbo.OutletBookings WHERE (OutletAreaId = @AreaId OR @AreaId IS NULL) AND (Status = @Status OR @Status IS NULL))", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)outletAreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                var byBooking = list.ToDictionary(b => b.Id);
                while (await r.ReadAsync())
                {
                    var item = MapItem(r);
                    if (byBooking.TryGetValue(item.BookingId, out var booking))
                        booking.Items.Add(item);
                }
            }
            return list;
        }

        public async Task<OutletBooking?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            OutletBooking? booking;
            using (var cmd = new SqlCommand(HeaderSelect + " WHERE b.Id = @Id", conn))
            {
                cmd.Parameters.AddWithValue("@Id", id);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return null;
                booking = MapHeader(r);
            }
            using (var cmd = new SqlCommand(ItemSelect + " WHERE i.BookingId = @Id", conn))
            {
                cmd.Parameters.AddWithValue("@Id", id);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    booking.Items.Add(MapItem(r));
            }
            return booking;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(
            OutletBooking header, IReadOnlyList<BookingItemInput> items, int outletAreaId, int? userId)
        {
            var (custOk, custError) = OutletRules.ValidateCustomer(header.CustomerName);
            if (!custOk)
                return (false, custError, 0);
            var (itemsOk, itemsError) = OutletRules.ValidateSaleHasItems(items.Count);
            if (!itemsOk)
                return (false, itemsError, 0);
            foreach (var line in items)
            {
                var (ok, error) = OutletRules.ValidateItemQuantity(line.Quantity);
                if (!ok)
                    return (false, error, 0);
            }

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "OBK", header.BookingDate.Year);
                var headerCmd = new SqlCommand(@"
INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, CustomerContact, BookingDate, RequiredDate, Remarks, CreatedById, CreatedBy)
VALUES (@Code, @AreaId, @Customer, @Contact, @Date, @Required, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                headerCmd.Parameters.AddWithValue("@Code", code);
                headerCmd.Parameters.AddWithValue("@AreaId", outletAreaId);
                headerCmd.Parameters.AddWithValue("@Customer", header.CustomerName.Trim());
                headerCmd.Parameters.AddWithValue("@Contact", string.IsNullOrWhiteSpace(header.CustomerContact) ? DBNull.Value : header.CustomerContact.Trim());
                headerCmd.Parameters.AddWithValue("@Date", header.BookingDate.Date);
                headerCmd.Parameters.AddWithValue("@Required", (object?)header.RequiredDate?.Date ?? DBNull.Value);
                headerCmd.Parameters.AddWithValue("@Remarks", (object?)header.Remarks ?? DBNull.Value);
                headerCmd.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
                headerCmd.Parameters.AddWithValue("@CreatedBy", (object?)header.CreatedBy ?? DBNull.Value);
                var bookingId = (int)(await headerCmd.ExecuteScalarAsync())!;

                foreach (var line in items)
                {
                    var (ok, message) = line.StockType == OutletStockType.Potted
                        ? await ReservePottedAsync(conn, tx, bookingId, outletAreaId, line, userId, code, header.CustomerName)
                        : line.StockType == OutletStockType.Tray
                            ? await ReserveTrayAsync(conn, tx, bookingId, outletAreaId, line, userId, code, header.CustomerName)
                            : (false, "Each item must be either a Potted Plant or a Ready Tray.");
                    if (!ok) { tx.Rollback(); return (false, message, 0); }
                }

                tx.Commit();
                header.Id = bookingId;
                header.BookingCode = code;
                return (true, null, bookingId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        private async Task<(bool Ok, string? Message)> ReservePottedAsync(
            SqlConnection conn, SqlTransaction tx, int bookingId, int outletAreaId, BookingItemInput line, int? userId, string code, string customerName)
        {
            var lockCmd = new SqlCommand(
                "SELECT SpeciesId, PotSize, AreaId FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", line.StockId);
            int speciesId; string potSize; int? areaId;
            using (var r = await lockCmd.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) { r.Close(); return (false, "One of the items' stock was not found."); }
                speciesId = r.GetInt32(0);
                potSize = r.GetString(1);
                areaId = r.IsDBNull(2) ? null : r.GetInt32(2);
            }
            var (ownOk, ownError) = OutletStockOwnershipRules.ValidateSameArea(areaId ?? -1, outletAreaId);
            if (!ownOk) return (false, ownError);

            var itemCmd = new SqlCommand(@"
INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity)
VALUES (@BookingId, @AreaId, N'Potted', @StockId, @SpeciesId, @PotSize, @Quantity);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            itemCmd.Parameters.AddWithValue("@BookingId", bookingId);
            itemCmd.Parameters.AddWithValue("@AreaId", outletAreaId);
            itemCmd.Parameters.AddWithValue("@StockId", line.StockId);
            itemCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            itemCmd.Parameters.AddWithValue("@PotSize", potSize);
            itemCmd.Parameters.AddWithValue("@Quantity", line.Quantity);
            var itemId = (int)(await itemCmd.ExecuteScalarAsync())!;

            var (resOk, resMessage) = await _pottedPlantStockRepo.RecordReservationAsync(
                conn, tx, line.StockId, line.Quantity, "Reservation", "OutletBookingItem", itemId, userId, $"Outlet booking {code} for {customerName.Trim()}");
            return resOk ? (true, null) : (false, resMessage);
        }

        private async Task<(bool Ok, string? Message)> ReserveTrayAsync(
            SqlConnection conn, SqlTransaction tx, int bookingId, int outletAreaId, BookingItemInput line, int? userId, string code, string customerName)
        {
            var lockCmd = new SqlCommand(
                "SELECT SpeciesId, CavityType, AreaId FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", line.StockId);
            int speciesId; string cavityType; int? areaId;
            using (var r = await lockCmd.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) { r.Close(); return (false, "One of the items' Ready Stock batches was not found."); }
                speciesId = r.GetInt32(0);
                cavityType = r.GetString(1);
                areaId = r.IsDBNull(2) ? null : r.GetInt32(2);
            }
            var (ownOk, ownError) = OutletStockOwnershipRules.ValidateSameArea(areaId ?? -1, outletAreaId);
            if (!ownOk) return (false, ownError);
            var cavitySize = DirectSowingRules.CavityCount(cavityType);
            if (cavitySize is null or <= 0)
                return (false, "This batch's cavity is not recognised.");

            var itemCmd = new SqlCommand(@"
INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity)
VALUES (@BookingId, @AreaId, N'Tray', @StockId, @SpeciesId, @CavityType, @Quantity);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            itemCmd.Parameters.AddWithValue("@BookingId", bookingId);
            itemCmd.Parameters.AddWithValue("@AreaId", outletAreaId);
            itemCmd.Parameters.AddWithValue("@StockId", line.StockId);
            itemCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            itemCmd.Parameters.AddWithValue("@CavityType", cavityType);
            itemCmd.Parameters.AddWithValue("@Quantity", line.Quantity);
            var itemId = (int)(await itemCmd.ExecuteScalarAsync())!;

            var seedlings = line.Quantity * cavitySize.Value;
            var (resOk, resMessage) = await _readyStockRepo.RecordReservationAsync(
                conn, tx, line.StockId, seedlings, "OutletBookingItem", itemId, userId, $"Outlet booking {code} for {customerName.Trim()}");
            return resOk ? (true, null) : (false, resMessage);
        }

        // Collect (fully or partially) one item of a booking: releases that
        // much of the reservation and deducts physical stock, exactly like
        // a normal dispatch, for whichever stock type the item is. The
        // header's Status is recomputed from every item's own
        // Quantity/Collected total.
        public async Task<(bool Success, string? Message)> CollectItemAsync(int bookingItemId, decimal quantity, int? userId, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var itemCmd = new SqlCommand(@"
SELECT i.BookingId, i.StockType, i.PottedPlantStockId, i.ReadyStockId, i.CavityType, i.Quantity, i.CollectedQuantity, b.Status, b.BookingCode, b.CustomerName
FROM dbo.OutletBookingItems i WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.OutletBookings b ON b.Id = i.BookingId
WHERE i.Id = @Id", conn, tx);
                itemCmd.Parameters.AddWithValue("@Id", bookingItemId);
                int bookingId; string stockType, status, code, customer; int? pottedId, readyId; string? cavityType; decimal itemQuantity, collected;
                using (var r = await itemCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "Booking item not found."); }
                    bookingId = r.GetInt32(0);
                    stockType = r.GetString(1);
                    pottedId = r.IsDBNull(2) ? null : r.GetInt32(2);
                    readyId = r.IsDBNull(3) ? null : r.GetInt32(3);
                    cavityType = r.IsDBNull(4) ? null : r.GetString(4);
                    itemQuantity = r.GetDecimal(5);
                    collected = r.GetDecimal(6);
                    status = r.GetString(7);
                    code = r.GetString(8);
                    customer = r.GetString(9);
                }

                var (ok, error) = OutletBookingRules.ValidateCollect(status, quantity, itemQuantity - collected);
                if (!ok) { tx.Rollback(); return (false, error); }

                if (stockType == OutletStockType.Potted)
                {
                    var (relOk, relMessage) = await _pottedPlantStockRepo.RecordReservationAsync(
                        conn, tx, pottedId!.Value, -quantity, "ReservationRelease", "OutletBookingItem", bookingItemId, userId, $"Collected: booking {code}");
                    if (!relOk) { tx.Rollback(); return (false, relMessage); }
                    var (dedOk, dedMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, pottedId.Value, -quantity, "Dispatch", "OutletBookingItem", bookingItemId, userId, $"Collected: booking {code} ({customer})");
                    if (!dedOk) { tx.Rollback(); return (false, dedMessage); }
                    var soldCmd = new SqlCommand("UPDATE dbo.PottedPlantStock SET SoldDispatchedQuantity = SoldDispatchedQuantity + @Q WHERE Id = @Id", conn, tx);
                    soldCmd.Parameters.AddWithValue("@Q", quantity);
                    soldCmd.Parameters.AddWithValue("@Id", pottedId.Value);
                    await soldCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    var cavitySize = DirectSowingRules.CavityCount(cavityType)!.Value;
                    var (dispOk, dispMessage) = await _readyStockRepo.RecordDispatchAsync(
                        conn, tx, readyId!.Value, quantity * cavitySize, "OutletBookingItem", bookingItemId, userId, $"Collected: booking {code} ({customer})", "Dispatch");
                    if (!dispOk) { tx.Rollback(); return (false, dispMessage); }
                }

                var newCollected = collected + quantity;
                var updItemCmd = new SqlCommand("UPDATE dbo.OutletBookingItems SET CollectedQuantity = @C WHERE Id = @Id", conn, tx);
                updItemCmd.Parameters.AddWithValue("@C", newCollected);
                updItemCmd.Parameters.AddWithValue("@Id", bookingItemId);
                await updItemCmd.ExecuteNonQueryAsync();

                var totalsCmd = new SqlCommand("SELECT SUM(Quantity), SUM(CollectedQuantity) FROM dbo.OutletBookingItems WHERE BookingId = @Id", conn, tx);
                totalsCmd.Parameters.AddWithValue("@Id", bookingId);
                decimal totalQuantity, totalCollected;
                using (var r = await totalsCmd.ExecuteReaderAsync())
                {
                    await r.ReadAsync();
                    totalQuantity = r.GetDecimal(0);
                    totalCollected = r.IsDBNull(1) ? 0 : r.GetDecimal(1);
                }
                var newStatus = OutletBookingRules.StatusAfterCollect(totalQuantity, totalCollected);
                var updHeaderCmd = new SqlCommand("UPDATE dbo.OutletBookings SET Status = @Status, ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
                updHeaderCmd.Parameters.AddWithValue("@Status", newStatus);
                updHeaderCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updHeaderCmd.Parameters.AddWithValue("@Id", bookingId);
                await updHeaderCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        // Cancels a booking that has not yet been (fully or partially)
        // collected -- releases every item's remaining reservation, of
        // whichever stock type it is.
        public async Task<(bool Success, string? Message)> CancelAsync(int bookingId, string reason, int userId, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var headCmd = new SqlCommand("SELECT Status, BookingCode FROM dbo.OutletBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                headCmd.Parameters.AddWithValue("@Id", bookingId);
                string status, code;
                using (var r = await headCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "Booking not found."); }
                    status = r.GetString(0);
                    code = r.GetString(1);
                }
                var (ok, error) = OutletBookingRules.ValidateCancel(status);
                if (!ok) { tx.Rollback(); return (false, error); }
                if (string.IsNullOrWhiteSpace(reason)) { tx.Rollback(); return (false, "A cancellation reason is required."); }

                var itemsCmd = new SqlCommand("SELECT Id, StockType, PottedPlantStockId, ReadyStockId, CavityType, Quantity, CollectedQuantity FROM dbo.OutletBookingItems WHERE BookingId = @Id", conn, tx);
                itemsCmd.Parameters.AddWithValue("@Id", bookingId);
                var open = new List<(int Id, string StockType, int? PottedId, int? ReadyId, string? Cavity, decimal Open)>();
                using (var r = await itemsCmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        var o = r.GetDecimal(5) - r.GetDecimal(6);
                        if (o > 0) open.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4), o));
                    }
                }
                foreach (var (itemId, stockType, pottedId, readyId, cavity, openQty) in open)
                {
                    if (stockType == OutletStockType.Potted)
                    {
                        var (relOk, relMessage) = await _pottedPlantStockRepo.RecordReservationAsync(
                            conn, tx, pottedId!.Value, -openQty, "ReservationRelease", "OutletBookingItem", itemId, userId, $"Booking {code} cancelled: {reason.Trim()}");
                        if (!relOk) { tx.Rollback(); return (false, relMessage); }
                    }
                    else
                    {
                        var cavitySize = DirectSowingRules.CavityCount(cavity)!.Value;
                        var (relOk, relMessage) = await _readyStockRepo.RecordReservationAsync(
                            conn, tx, readyId!.Value, -openQty * cavitySize, "OutletBookingItem", itemId, userId, $"Booking {code} cancelled: {reason.Trim()}");
                        if (!relOk) { tx.Rollback(); return (false, relMessage); }
                    }
                }

                var updCmd = new SqlCommand(@"
UPDATE dbo.OutletBookings
SET Status = N'Cancelled', CancelledById = @UserId, CancelledDate = SYSUTCDATETIME(), CancellationReason = @Reason,
    ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
                updCmd.Parameters.AddWithValue("@UserId", userId);
                updCmd.Parameters.AddWithValue("@Reason", reason.Trim());
                updCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updCmd.Parameters.AddWithValue("@Id", bookingId);
                await updCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        private static OutletBooking MapHeader(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            BookingCode = r.GetString(1),
            OutletAreaId = r.GetInt32(2),
            OutletAreaName = r.GetString(3),
            CustomerName = r.GetString(4),
            CustomerContact = r.IsDBNull(5) ? null : r.GetString(5),
            BookingDate = r.GetDateTime(6),
            RequiredDate = r.IsDBNull(7) ? null : r.GetDateTime(7),
            Status = r.GetString(8),
            Remarks = r.IsDBNull(9) ? null : r.GetString(9),
            CreatedById = r.IsDBNull(10) ? null : r.GetInt32(10),
            CreatedBy = r.IsDBNull(11) ? null : r.GetString(11),
            CreatedDate = r.GetDateTime(12),
            CancelledById = r.IsDBNull(13) ? null : r.GetInt32(13),
            CancelledDate = r.IsDBNull(14) ? null : r.GetDateTime(14),
            CancellationReason = r.IsDBNull(15) ? null : r.GetString(15),
            ModifiedBy = r.IsDBNull(16) ? null : r.GetString(16),
            ModifiedDate = r.IsDBNull(17) ? null : r.GetDateTime(17)
        };

        private static OutletBookingItem MapItem(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            BookingId = r.GetInt32(1),
            OutletAreaId = r.GetInt32(2),
            StockType = r.GetString(3),
            PottedPlantStockId = r.IsDBNull(4) ? null : r.GetInt32(4),
            ReadyStockId = r.IsDBNull(5) ? null : r.GetInt32(5),
            SpeciesId = r.GetInt32(6),
            SpeciesName = r.GetString(7),
            PlantTypeName = r.GetString(8),
            SpeciesColor = r.IsDBNull(9) ? null : r.GetString(9),
            PotSize = r.IsDBNull(10) ? null : r.GetString(10),
            CavityType = r.IsDBNull(11) ? null : r.GetString(11),
            Quantity = r.GetDecimal(12),
            CollectedQuantity = r.GetDecimal(13),
            CreatedDate = r.GetDateTime(14)
        };
    }
}
