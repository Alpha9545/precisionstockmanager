using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // ========================================================================
    // Phase C: Ready Stock -> seedling Booking -> Reservation -> Batch
    // Allocation -> (Substitution) -> Dispatch.
    //
    //   Booking   dbo.Bookings (the existing seedling booking table). Creating a
    //             booking never touches stock (pre-booking is allowed).
    //   Reserve   plants of specific Ready Stock batches are reserved for the
    //             booking (dbo.BookingBatchAllocations, ReadyStock.Reserved).
    //   Dispatch  reserved plants leave the nursery (dbo.SeedlingDispatches +
    //             Lines, ReadyStock.Dispatched). Only now does physical stock
    //             (ReadyStock.Quantity - Dispatched) go down.
    //
    // CONCURRENCY: every write runs in ONE SQL transaction and takes locks in a
    // fixed order -- the Bookings row, then the booking's allocation rows, then
    // the ReadyStock batch rows -- all WITH (UPDLOCK, HOLDLOCK). The FIFO read
    // of a variety's batches holds a key-range lock, so two users reserving the
    // last plants of the same variety are serialised: the second one re-reads
    // availability after the first commits and is refused. DB CHECK
    // constraints (Reserved + Dispatched <= Quantity, etc.) are the backstop.
    // A deadlock victim (SQL error 1205) is retried up to twice.
    //
    // Old pipeline: bookings that were fulfilled through the REMOVED legacy
    // Inventory page (FulfillBooking; InventoryTransactions 'Allocation' rows)
    // are recognised as legacy (IsLegacy) and never mixed with Ready Stock.
    // ========================================================================
    public class SeedlingFulfilmentRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly ReadyStockRepository _readyStockRepo;

        public const string DispatchCodePrefix = "SD";

        public SeedlingFulfilmentRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo, ReadyStockRepository readyStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _readyStockRepo = readyStockRepo;
        }

        public sealed record Actor(string? Name, int? UserId);

        // ------------------------------------------------------------------
        // Transaction helper (commit on success, rollback otherwise, retry a
        // deadlock victim).
        // ------------------------------------------------------------------
        private async Task<(bool Success, string? Message)> RunAsync(Func<SqlConnection, SqlTransaction, Task<(bool Success, string? Message)>> body)
        {
            for (var attempt = 1; ; attempt++)
            {
                using var conn = _dbHelper.GetConnection();
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                try
                {
                    var result = await body(conn, tx);
                    if (result.Success) tx.Commit(); else tx.Rollback();
                    return result;
                }
                catch (SqlException ex) when (ex.Number == 1205 && attempt < 3)
                {
                    try { tx.Rollback(); } catch { /* already rolled back by the server */ }
                    await Task.Delay(40 * attempt);
                }
                catch (SqlException ex) when (ex.Number == 547)
                {
                    try { tx.Rollback(); } catch { }
                    return (false, "The database refused the change because it would break a stock rule (" + ex.Message + ")");
                }
                catch (Exception ex)
                {
                    try { tx.Rollback(); } catch { }
                    return (false, ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // Locked reads
        // ------------------------------------------------------------------
        private sealed class LockedBooking
        {
            public int Id; public int PlantId; public int? SpeciesId; public int Quantity; public string Status = "";
            public decimal Reserved; public decimal Dispatched; public string? Source; public string? CustomerName;
            public DateTime? DeliveryDate; public decimal LegacyQuantity;
            public bool IsLegacy => LegacyQuantity > 0 || Source == SeedlingBookingRules.SourceLegacy;
            public decimal Unreserved => Quantity - Reserved - Dispatched;
        }

        private static async Task<LockedBooking?> LockBookingAsync(SqlConnection conn, SqlTransaction tx, int bookingId)
        {
            var cmd = new SqlCommand(@"
SELECT b.Id, b.PlantId, b.SpeciesId, b.Quantity, ISNULL(b.Status, 'Pending'), b.ReservedQuantity, b.DispatchedQuantity,
       b.FulfilmentSource, b.CustomerName, b.DeliveryDate,
       (SELECT ISNULL(SUM(CAST(it.QuantityUtilized AS DECIMAL(18,2))), 0) FROM dbo.InventoryTransactions it
        WHERE it.BookingId = b.Id AND it.TransactionType = 'Allocation')
FROM dbo.Bookings b WITH (UPDLOCK, HOLDLOCK)
WHERE b.Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", bookingId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new LockedBooking
            {
                Id = r.GetInt32(0), PlantId = r.GetInt32(1), SpeciesId = r.IsDBNull(2) ? null : r.GetInt32(2),
                Quantity = r.GetInt32(3), Status = r.GetString(4), Reserved = r.GetDecimal(5), Dispatched = r.GetDecimal(6),
                Source = r.IsDBNull(7) ? null : r.GetString(7), CustomerName = r.IsDBNull(8) ? null : r.GetString(8),
                DeliveryDate = r.IsDBNull(9) ? null : r.GetDateTime(9), LegacyQuantity = r.GetDecimal(10)
            };
        }

        private sealed class LockedAllocation
        {
            public int Id; public int ReadyStockId; public int? BookedSpeciesId; public int ActualSpeciesId;
            public bool IsSubstitution; public string? SubstitutionReason; public decimal Open; public int AreaId;
        }

        private static async Task<Dictionary<int, LockedAllocation>> LockActiveAllocationsAsync(SqlConnection conn, SqlTransaction tx, int bookingId)
        {
            var cmd = new SqlCommand(@"
SELECT a.Id, a.ReadyStockId, a.BookedSpeciesId, a.ActualSpeciesId, a.IsSubstitution, a.SubstitutionReason,
       a.Quantity - a.DispatchedQuantity - a.ReleasedQuantity, rs.AreaId
FROM dbo.BookingBatchAllocations a WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.ReadyStock rs ON rs.Id = a.ReadyStockId
WHERE a.BookingId = @B AND a.Status = 'Active'
ORDER BY a.Id", conn, tx);
            cmd.Parameters.AddWithValue("@B", bookingId);
            var result = new Dictionary<int, LockedAllocation>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var a = new LockedAllocation
                {
                    Id = r.GetInt32(0), ReadyStockId = r.GetInt32(1), BookedSpeciesId = r.IsDBNull(2) ? null : r.GetInt32(2),
                    ActualSpeciesId = r.GetInt32(3), IsSubstitution = r.GetBoolean(4), SubstitutionReason = r.IsDBNull(5) ? null : r.GetString(5),
                    Open = r.GetDecimal(6), AreaId = r.GetInt32(7)
                };
                result[a.Id] = a;
            }
            return result;
        }

        private static string? CheckReservable(LockedBooking? b)
        {
            if (b == null) return "Booking not found.";
            if (b.Status != "Pending") return $"This booking is {b.Status}; only Pending bookings can be reserved or allocated.";
            if (b.IsLegacy) return "This booking is fulfilled from the legacy Inventory pipeline and cannot also use Ready Stock.";
            if (!b.SpeciesId.HasValue) return "This booking has no variety.";
            return null;
        }

        // ------------------------------------------------------------------
        // Allocation line + booking helpers (inside a transaction)
        // ------------------------------------------------------------------
        private static async Task<int> AddToAllocationAsync(SqlConnection conn, SqlTransaction tx, int bookingId, int readyStockId,
            int? bookedSpeciesId, int actualSpeciesId, bool isSubstitution, string? reason, decimal quantity, Actor actor)
        {
            var find = new SqlCommand(@"
SELECT Id FROM dbo.BookingBatchAllocations WITH (UPDLOCK, HOLDLOCK)
WHERE BookingId = @B AND ReadyStockId = @R AND Status = 'Active'", conn, tx);
            find.Parameters.AddWithValue("@B", bookingId);
            find.Parameters.AddWithValue("@R", readyStockId);
            var existing = await find.ExecuteScalarAsync();
            if (existing != null && existing != DBNull.Value)
            {
                var id = (int)existing;
                var upd = new SqlCommand(@"
UPDATE dbo.BookingBatchAllocations
SET Quantity = Quantity + @Q, ModifiedBy = @By, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
                upd.Parameters.AddWithValue("@Q", quantity);
                upd.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
                upd.Parameters.AddWithValue("@Id", id);
                await upd.ExecuteNonQueryAsync();
                return id;
            }

            var ins = new SqlCommand(@"
INSERT INTO dbo.BookingBatchAllocations
(BookingId, ReadyStockId, BookedSpeciesId, ActualSpeciesId, IsSubstitution, SubstitutionReason, Quantity, Status, CreatedById, CreatedBy, CreatedDate)
VALUES (@B, @R, @BS, @AS, @Sub, @Reason, @Q, 'Active', @UserId, @By, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            ins.Parameters.AddWithValue("@B", bookingId);
            ins.Parameters.AddWithValue("@R", readyStockId);
            ins.Parameters.AddWithValue("@BS", (object?)bookedSpeciesId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@AS", actualSpeciesId);
            ins.Parameters.AddWithValue("@Sub", isSubstitution);
            ins.Parameters.AddWithValue("@Reason", (object?)(isSubstitution ? reason?.Trim() : null) ?? DBNull.Value);
            ins.Parameters.AddWithValue("@Q", quantity);
            ins.Parameters.AddWithValue("@UserId", (object?)actor.UserId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
            return (int)(await ins.ExecuteScalarAsync())!;
        }

        // Releases open quantity from allocation lines (plan = newest first).
        private async Task<(bool Success, string? Message, decimal Released)> ReleaseLinesAsync(
            SqlConnection conn, SqlTransaction tx, Dictionary<int, LockedAllocation> lines,
            IEnumerable<(int AllocationId, decimal Quantity)> plan, Actor actor, string remarks)
        {
            decimal total = 0;
            // ReadyStock rows are locked in Id order.
            foreach (var (allocationId, qty) in plan.OrderBy(p => lines[p.AllocationId].ReadyStockId))
            {
                var line = lines[allocationId];
                var (ok, msg) = await _readyStockRepo.RecordReservationAsync(conn, tx, line.ReadyStockId, -qty,
                    "BookingBatchAllocation", allocationId, actor.UserId, remarks);
                if (!ok) return (false, msg, 0);

                var upd = new SqlCommand(@"
UPDATE dbo.BookingBatchAllocations
SET ReleasedQuantity = ReleasedQuantity + @Q,
    Status = CASE WHEN Quantity - DispatchedQuantity - (ReleasedQuantity + @Q) <= 0 THEN 'Closed' ELSE 'Active' END,
    ModifiedBy = @By, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
                upd.Parameters.AddWithValue("@Q", qty);
                upd.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
                upd.Parameters.AddWithValue("@Id", allocationId);
                await upd.ExecuteNonQueryAsync();
                line.Open -= qty;
                total += qty;
            }
            return (true, null, total);
        }

        private static async Task UpdateBookingQuantitiesAsync(SqlConnection conn, SqlTransaction tx, int bookingId,
            decimal reservedDelta, decimal dispatchedDelta, string? source, bool setSource, Actor actor)
        {
            var cmd = new SqlCommand(@"
UPDATE dbo.Bookings
SET ReservedQuantity = ReservedQuantity + @RD,
    DispatchedQuantity = DispatchedQuantity + @DD,
    FulfilmentSource = CASE WHEN @SetSource = 1 THEN @Source ELSE FulfilmentSource END,
    ModifiedBy = @By, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@RD", reservedDelta);
            cmd.Parameters.AddWithValue("@DD", dispatchedDelta);
            cmd.Parameters.AddWithValue("@SetSource", setSource ? 1 : 0);
            cmd.Parameters.AddWithValue("@Source", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", bookingId);
            await cmd.ExecuteNonQueryAsync();
        }

        // After a release: a booking with nothing reserved and nothing dispatched
        // is free again to be fulfilled from either pipeline.
        private static (string? Source, bool Set) SourceAfterRelease(LockedBooking b, decimal released)
            => (b.Reserved - released <= 0 && b.Dispatched <= 0) ? (null, true) : (null, false);

        // ------------------------------------------------------------------
        // 1) RESERVE (Booking Executive): booked variety only, oldest batch
        //    first, all-or-nothing.
        // ------------------------------------------------------------------
        public Task<(bool Success, string? Message)> ReserveAsync(int bookingId, decimal quantity, Actor actor)
            => RunAsync(async (conn, tx) =>
            {
                var b = await LockBookingAsync(conn, tx, bookingId);
                var error = CheckReservable(b);
                if (error != null) return (false, error);
                if (!SeedlingBookingRules.IsWholePositive(quantity))
                    return (false, "Quantity to reserve must be a whole number greater than zero.");
                if (quantity > b!.Unreserved)
                    return (false, $"Only {b.Unreserved:N0} plants of this booking still need reserving.");

                // Lock every batch of the booked variety (key-range lock).
                var batchCmd = new SqlCommand(@"
SELECT rs.Id, rs.SpeciesId, rs.SowingDate, rs.FirstConfirmationDate, rs.Quantity, rs.ReservedQuantity, rs.DispatchedQuantity
FROM dbo.ReadyStock rs WITH (UPDLOCK, HOLDLOCK)
WHERE rs.SpeciesId = @S
ORDER BY rs.Id", conn, tx);
                batchCmd.Parameters.AddWithValue("@S", b.SpeciesId!.Value);
                var batches = new List<ReadyBatchOption>();
                using (var r = await batchCmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        var q = r.GetDecimal(4); var res = r.GetDecimal(5); var dis = r.GetDecimal(6);
                        batches.Add(new ReadyBatchOption
                        {
                            ReadyStockId = r.GetInt32(0), SpeciesId = r.GetInt32(1), SowingDate = r.GetDateTime(2),
                            FirstConfirmationDate = r.IsDBNull(3) ? null : r.GetDateTime(3),
                            Quantity = q, ReservedQuantity = res, DispatchedQuantity = dis, AvailableQuantity = q - res - dis
                        });
                    }
                }

                var (ok, plan, planError) = SeedlingBookingRules.PlanFifo(batches, b.SpeciesId.Value, quantity);
                if (!ok) return (false, planError);

                foreach (var (readyStockId, qty) in plan.OrderBy(p => p.ReadyStockId))
                {
                    var allocationId = await AddToAllocationAsync(conn, tx, b.Id, readyStockId, b.SpeciesId, b.SpeciesId.Value, false, null, qty, actor);
                    var (resOk, resMsg) = await _readyStockRepo.RecordReservationAsync(conn, tx, readyStockId, qty,
                        "BookingBatchAllocation", allocationId, actor.UserId, $"Reserved for booking {b.Id}");
                    if (!resOk) return (false, resMsg);
                }

                await UpdateBookingQuantitiesAsync(conn, tx, b.Id, quantity, 0, SeedlingBookingRules.SourceReadyStock, true, actor);
                return (true, $"Reserved {quantity:N0} plants from {plan.Count} batch(es), oldest first.");
            });

        // ------------------------------------------------------------------
        // 2) ALLOCATE a specific batch (Dispatch Executive): the booked
        //    variety, or -- with a reason -- another variety of the same
        //    species (substitution). The batch must be in an Area the user
        //    may work in.
        // ------------------------------------------------------------------
        public Task<(bool Success, string? Message)> AllocateBatchAsync(int bookingId, int readyStockId, decimal quantity,
            string? substitutionReason, Actor actor, Func<int, bool> canAccessArea)
            => RunAsync(async (conn, tx) =>
            {
                var b = await LockBookingAsync(conn, tx, bookingId);
                var error = CheckReservable(b);
                if (error != null) return (false, error);
                if (!SeedlingBookingRules.IsWholePositive(quantity))
                    return (false, "Quantity must be a whole number greater than zero.");
                if (quantity > b!.Unreserved)
                    return (false, $"Only {b.Unreserved:N0} plants of this booking are not yet reserved. Release a reservation first to replace it.");

                var batchCmd = new SqlCommand(@"
SELECT rs.SpeciesId, ps.PlantTypeId, rs.AreaId, rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity
FROM dbo.ReadyStock rs WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
WHERE rs.Id = @R", conn, tx);
                batchCmd.Parameters.AddWithValue("@R", readyStockId);
                int batchSpecies, batchPlantType, areaId; decimal available;
                using (var r = await batchCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync()) return (false, "Ready Stock batch not found.");
                    batchSpecies = r.GetInt32(0); batchPlantType = r.GetInt32(1); areaId = r.GetInt32(2); available = r.GetDecimal(3);
                }
                if (!canAccessArea(areaId))
                    return (false, "You are not authorized to allocate stock from this batch's Area.");

                var (valid, isSubstitution, choiceError) = SeedlingBookingRules.ValidateBatchChoice(
                    b.SpeciesId, b.PlantId, batchSpecies, batchPlantType, substitutionReason);
                if (!valid) return (false, choiceError);
                if (quantity > available)
                    return (false, $"{SeedlingBookingRules.InsufficientStockMessage} Batch available {available:N0}, requested {quantity:N0}.");

                var allocationId = await AddToAllocationAsync(conn, tx, b.Id, readyStockId, b.SpeciesId, batchSpecies,
                    isSubstitution, substitutionReason, quantity, actor);
                var (resOk, resMsg) = await _readyStockRepo.RecordReservationAsync(conn, tx, readyStockId, quantity,
                    "BookingBatchAllocation", allocationId, actor.UserId,
                    isSubstitution ? $"Substitution for booking {b.Id}: {substitutionReason?.Trim()}" : $"Allocated to booking {b.Id}");
                if (!resOk) return (false, resMsg);

                await UpdateBookingQuantitiesAsync(conn, tx, b.Id, quantity, 0, SeedlingBookingRules.SourceReadyStock, true, actor);
                return (true, isSubstitution
                    ? $"Substituted {quantity:N0} plants of another variety (recorded as a substitution)."
                    : $"Allocated {quantity:N0} plants from the selected batch.");
            });

        // ------------------------------------------------------------------
        // 3) RELEASE one allocation line (Dispatch Executive, re-allocation)
        //    or everything (Booking Executive).
        // ------------------------------------------------------------------
        public Task<(bool Success, string? Message)> ReleaseAllocationAsync(int bookingId, int allocationId, decimal quantity,
            Actor actor, Func<int, bool> canAccessArea)
            => RunAsync(async (conn, tx) =>
            {
                var b = await LockBookingAsync(conn, tx, bookingId);
                if (b == null) return (false, "Booking not found.");
                if (b.Status != "Pending") return (false, $"This booking is {b.Status}.");
                var lines = await LockActiveAllocationsAsync(conn, tx, bookingId);
                if (!lines.TryGetValue(allocationId, out var line)) return (false, "Allocation not found on this booking.");
                if (!canAccessArea(line.AreaId)) return (false, "You are not authorized to change allocations in this batch's Area.");
                if (!SeedlingBookingRules.IsWholePositive(quantity) || quantity > line.Open)
                    return (false, $"Release quantity must be a whole number between 1 and {line.Open:N0}.");

                var (ok, msg, released) = await ReleaseLinesAsync(conn, tx, lines, new[] { (allocationId, quantity) }, actor, $"Released from booking {b.Id}");
                if (!ok) return (false, msg);
                var (source, set) = SourceAfterRelease(b, released);
                await UpdateBookingQuantitiesAsync(conn, tx, b.Id, -released, 0, source, set, actor);
                return (true, $"Released {released:N0} plants back to Ready Stock.");
            });

        public Task<(bool Success, string? Message)> ReleaseAllAsync(int bookingId, Actor actor)
            => RunAsync(async (conn, tx) =>
            {
                var b = await LockBookingAsync(conn, tx, bookingId);
                if (b == null) return (false, "Booking not found.");
                if (b.Status != "Pending") return (false, $"This booking is {b.Status}.");
                var lines = await LockActiveAllocationsAsync(conn, tx, bookingId);
                var plan = lines.Values.Where(l => l.Open > 0).Select(l => (l.Id, l.Open)).ToList();
                if (plan.Count == 0) return (false, "Nothing is reserved on this booking.");
                var (ok, msg, released) = await ReleaseLinesAsync(conn, tx, lines, plan, actor, $"Reservation released on booking {b.Id}");
                if (!ok) return (false, msg);
                var (source, set) = SourceAfterRelease(b, released);
                await UpdateBookingQuantitiesAsync(conn, tx, b.Id, -released, 0, source, set, actor);
                return (true, $"Released {released:N0} reserved plants back to Ready Stock.");
            });

        // ------------------------------------------------------------------
        // 4) CANCEL (replaces the old hard delete). Releases every open
        //    reservation in the same transaction. Not allowed once plants have
        //    been dispatched (revise the quantity instead).
        // ------------------------------------------------------------------
        public Task<(bool Success, string? Message)> CancelAsync(int bookingId, string? reason, Actor actor)
            => RunAsync(async (conn, tx) =>
            {
                if (string.IsNullOrWhiteSpace(reason)) return (false, "A cancellation reason is required.");
                var b = await LockBookingAsync(conn, tx, bookingId);
                if (b == null) return (false, "Booking not found.");
                if (b.Status != "Pending") return (false, $"Only Pending bookings can be cancelled (this one is {b.Status}).");
                if (b.Dispatched > 0)
                    return (false, $"{b.Dispatched:N0} plants of this booking have already been dispatched. Revise the quantity down instead of cancelling.");

                decimal released = 0;
                var lines = await LockActiveAllocationsAsync(conn, tx, bookingId);
                var plan = lines.Values.Where(l => l.Open > 0).Select(l => (l.Id, l.Open)).ToList();
                if (plan.Count > 0)
                {
                    var (ok, msg, rel) = await ReleaseLinesAsync(conn, tx, lines, plan, actor, $"Booking {b.Id} cancelled");
                    if (!ok) return (false, msg);
                    released = rel;
                }

                var cmd = new SqlCommand(@"
UPDATE dbo.Bookings
SET Status = 'Cancelled', ReservedQuantity = ReservedQuantity - @Released,
    CancelledById = @UserId, CancelledDate = SYSUTCDATETIME(), CancellationReason = @Reason,
    ModifiedBy = @By, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id AND Status = 'Pending'", conn, tx);
                cmd.Parameters.AddWithValue("@Released", released);
                cmd.Parameters.AddWithValue("@UserId", (object?)actor.UserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", reason.Trim().Length > 500 ? reason.Trim()[..500] : reason.Trim());
                cmd.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", bookingId);
                if (await cmd.ExecuteNonQueryAsync() != 1) return (false, "Booking could not be cancelled.");
                return (true, released > 0 ? $"Booking cancelled; {released:N0} reserved plants released back to Ready Stock." : "Booking cancelled.");
            });

        // ------------------------------------------------------------------
        // 5) REVISION with history. The booking row keeps the latest values;
        //    dbo.BookingRevisions keeps every previous value. Optionally a new
        //    variety is split off into a linked new booking.
        // ------------------------------------------------------------------
        public sealed class RevisionRequest
        {
            public int NewPlantId { get; set; }
            public int NewSpeciesId { get; set; }
            public int NewQuantity { get; set; }
            public DateTime? NewDeliveryDate { get; set; }
            public string Reason { get; set; } = string.Empty;
            public string? OtherChanges { get; set; }
            // Optional split: an additional variety for the same customer.
            public int? SplitPlantId { get; set; }
            public int? SplitSpeciesId { get; set; }
            public int? SplitQuantity { get; set; }
        }

        public Task<(bool Success, string? Message)> ReviseAsync(int bookingId, RevisionRequest request, Actor actor)
            => RunAsync((conn, tx) => ApplyRevisionAsync(conn, tx, bookingId, request, actor));

        // Also used by BookingRepository.UpdateBooking (old edit screen) inside
        // its own transaction.
        public async Task<(bool Success, string? Message)> ApplyRevisionAsync(SqlConnection conn, SqlTransaction tx,
            int bookingId, RevisionRequest req, Actor actor)
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) return (false, "A reason is required for a booking revision.");
            if (req.Reason.Trim().Length > 500) return (false, "The revision reason is too long (max 500 characters).");
            var b = await LockBookingAsync(conn, tx, bookingId);
            if (b == null) return (false, "Booking not found.");
            if (b.Status != "Pending") return (false, $"Only Pending bookings can be revised (this one is {b.Status}).");

            // The new variety must belong to the new species.
            if (!await SpeciesBelongsToPlantTypeAsync(conn, tx, req.NewSpeciesId, req.NewPlantId))
                return (false, "The selected variety does not belong to the selected species.");

            var varietyChanged = req.NewSpeciesId != b.SpeciesId || req.NewPlantId != b.PlantId;
            if (varietyChanged && b.IsLegacy)
                return (false, "The variety of a legacy-fulfilled booking cannot be changed.");
            var (ok, releaseQty, error) = SeedlingBookingRules.ValidateRevision(b.Dispatched + b.LegacyQuantity, b.Reserved, req.NewQuantity, varietyChanged);
            if (!ok) return (false, error);

            decimal released = 0;
            if (releaseQty > 0)
            {
                var lines = await LockActiveAllocationsAsync(conn, tx, bookingId);
                var plan = SeedlingBookingRules.PlanRelease(lines.Values.Select(l => (l.Id, l.Open)), releaseQty);
                var (relOk, relMsg, rel) = await ReleaseLinesAsync(conn, tx, lines, plan, actor, $"Booking {b.Id} revised");
                if (!relOk) return (false, relMsg);
                released = rel;
            }

            int? splitId = null;
            if (req.SplitSpeciesId.HasValue || req.SplitQuantity.HasValue)
            {
                if (!req.SplitPlantId.HasValue || !req.SplitSpeciesId.HasValue || !req.SplitQuantity.HasValue || req.SplitQuantity < 1)
                    return (false, "To add another variety, choose its species, variety and a quantity of at least 1.");
                if (!await SpeciesBelongsToPlantTypeAsync(conn, tx, req.SplitSpeciesId.Value, req.SplitPlantId.Value))
                    return (false, "The added variety does not belong to the selected species.");
                splitId = await InsertSplitBookingAsync(conn, tx, b.Id, req, actor);
            }

            var nextRevision = await NextRevisionNoAsync(conn, tx, b.Id);
            var hist = new SqlCommand(@"
INSERT INTO dbo.BookingRevisions
(BookingId, RevisionNo, PreviousPlantId, NewPlantId, PreviousSpeciesId, NewSpeciesId, PreviousQuantity, NewQuantity,
 PreviousDeliveryDate, NewDeliveryDate, ReleasedQuantity, SplitBookingId, OtherChanges, Reason, ChangedById, ChangedBy, ChangedDate)
VALUES
(@B, @Rev, @PP, @NP, @PS, @NS, @PQ, @NQ, @PD, @ND, @Rel, @Split, @Other, @Reason, @UserId, @By, SYSUTCDATETIME());", conn, tx);
            hist.Parameters.AddWithValue("@B", b.Id);
            hist.Parameters.AddWithValue("@Rev", nextRevision);
            hist.Parameters.AddWithValue("@PP", b.PlantId);
            hist.Parameters.AddWithValue("@NP", req.NewPlantId);
            hist.Parameters.AddWithValue("@PS", (object?)b.SpeciesId ?? DBNull.Value);
            hist.Parameters.AddWithValue("@NS", req.NewSpeciesId);
            hist.Parameters.AddWithValue("@PQ", b.Quantity);
            hist.Parameters.AddWithValue("@NQ", req.NewQuantity);
            hist.Parameters.AddWithValue("@PD", (object?)b.DeliveryDate?.Date ?? DBNull.Value);
            hist.Parameters.AddWithValue("@ND", (object?)(req.NewDeliveryDate ?? b.DeliveryDate)?.Date ?? DBNull.Value);
            hist.Parameters.AddWithValue("@Rel", released);
            hist.Parameters.AddWithValue("@Split", (object?)splitId ?? DBNull.Value);
            hist.Parameters.AddWithValue("@Other", (object?)Truncate(req.OtherChanges, 1000) ?? DBNull.Value);
            hist.Parameters.AddWithValue("@Reason", req.Reason.Trim());
            hist.Parameters.AddWithValue("@UserId", (object?)actor.UserId ?? DBNull.Value);
            hist.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
            await hist.ExecuteNonQueryAsync();

            var fulfilled = b.Dispatched + b.LegacyQuantity;
            var completes = fulfilled >= req.NewQuantity;
            var (source, setSource) = SourceAfterRelease(b, released);
            var upd = new SqlCommand(@"
UPDATE dbo.Bookings
SET PlantId = @NP, SpeciesId = @NS, Quantity = @NQ, DeliveryDate = @ND,
    ReservedQuantity = ReservedQuantity - @Rel,
    FulfilmentSource = CASE WHEN @SetSource = 1 THEN NULL ELSE FulfilmentSource END,
    Status = CASE WHEN @Completes = 1 THEN 'Completed' ELSE Status END,
    ActualDeliveryDate = CASE WHEN @Completes = 1 AND ActualDeliveryDate IS NULL THEN CAST(GETDATE() AS DATE) ELSE ActualDeliveryDate END,
    RevisionNo = @Rev, ModifiedBy = @By, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
            upd.Parameters.AddWithValue("@NP", req.NewPlantId);
            upd.Parameters.AddWithValue("@NS", req.NewSpeciesId);
            upd.Parameters.AddWithValue("@NQ", req.NewQuantity);
            upd.Parameters.AddWithValue("@ND", (object?)(req.NewDeliveryDate ?? b.DeliveryDate)?.Date ?? DBNull.Value);
            upd.Parameters.AddWithValue("@Rel", released);
            upd.Parameters.AddWithValue("@SetSource", setSource && source == null && b.Source == SeedlingBookingRules.SourceReadyStock ? 1 : 0);
            upd.Parameters.AddWithValue("@Completes", completes ? 1 : 0);
            upd.Parameters.AddWithValue("@Rev", nextRevision);
            upd.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
            upd.Parameters.AddWithValue("@Id", b.Id);
            await upd.ExecuteNonQueryAsync();

            var msg = $"Revision {nextRevision} saved.";
            if (released > 0) msg += $" {released:N0} reserved plants released.";
            if (splitId.HasValue) msg += $" Added variety booked as booking #{splitId}.";
            if (completes) msg += " The booking is now Completed (fully dispatched).";
            return (true, msg);
        }

        private static async Task<bool> SpeciesBelongsToPlantTypeAsync(SqlConnection conn, SqlTransaction tx, int speciesId, int plantTypeId)
        {
            var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Id = @S AND PlantTypeId = @P", conn, tx);
            cmd.Parameters.AddWithValue("@S", speciesId);
            cmd.Parameters.AddWithValue("@P", plantTypeId);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        private static async Task<int> NextRevisionNoAsync(SqlConnection conn, SqlTransaction tx, int bookingId)
        {
            var cmd = new SqlCommand(@"
SELECT ISNULL(MAX(RevisionNo), 0) + 1 FROM dbo.BookingRevisions WITH (UPDLOCK, HOLDLOCK) WHERE BookingId = @B", conn, tx);
            cmd.Parameters.AddWithValue("@B", bookingId);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        // The added variety becomes a new Pending booking for the same
        // customer, linked through ParentBookingId (same shape as the existing
        // BookingRepository.InsertBookingAsync, incl. its 'Booking' log row).
        private static async Task<int> InsertSplitBookingAsync(SqlConnection conn, SqlTransaction tx, int parentId, RevisionRequest req, Actor actor)
        {
            var cmd = new SqlCommand(@"
INSERT INTO dbo.Bookings
(PlantId, SpeciesId, CustomerName, DeliveryDate, Quantity, Status, AddedBy, BookingDate,
 Address, Contact, AdvanceTaken, AdvanceTakenAmount, AdvanceTakenDetails, BookedById, BookedByOther, StateId, DistrictId, ParentBookingId)
SELECT @PlantId, @SpeciesId, p.CustomerName, COALESCE(@DeliveryDate, p.DeliveryDate), @Quantity, 'Pending', @AddedBy, GETDATE(),
       p.Address, p.Contact, 0, NULL, NULL, p.BookedById, p.BookedByOther, p.StateId, p.DistrictId, p.Id
FROM dbo.Bookings p WHERE p.Id = @ParentId;
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            cmd.Parameters.AddWithValue("@PlantId", req.SplitPlantId!.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", req.SplitSpeciesId!.Value);
            cmd.Parameters.AddWithValue("@DeliveryDate", (object?)req.NewDeliveryDate?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Quantity", req.SplitQuantity!.Value);
            cmd.Parameters.AddWithValue("@AddedBy", actor.Name ?? "System");
            cmd.Parameters.AddWithValue("@ParentId", parentId);
            var newId = (int)(await cmd.ExecuteScalarAsync())!;

            var log = new SqlCommand(@"
INSERT INTO Transactions (BookingId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
VALUES (@BookingId, 'Booking', @Quantity, GETDATE(), @UpdatedBy, GETDATE());", conn, tx);
            log.Parameters.AddWithValue("@BookingId", newId);
            log.Parameters.AddWithValue("@Quantity", req.SplitQuantity.Value);
            log.Parameters.AddWithValue("@UpdatedBy", actor.Name ?? "System");
            await log.ExecuteNonQueryAsync();
            return newId;
        }

        private static string? Truncate(string? s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);

        // ------------------------------------------------------------------
        // 6) DISPATCH (Dispatch Executive): partial or full, from the
        //    booking's allocation lines only.
        // ------------------------------------------------------------------
        public Task<(bool Success, string? Message)> DispatchAsync(int bookingId, IReadOnlyList<(int AllocationId, decimal Quantity)> lines,
            DateTime dispatchDate, int? responsiblePersonId, string? remarks, Actor actor, Func<int, bool> canAccessArea)
            => RunAsync(async (conn, tx) =>
            {
                var requested = lines.Where(l => l.Quantity != 0).ToList();
                if (requested.Count == 0) return (false, "Enter a dispatch quantity for at least one batch.");
                if (requested.Select(l => l.AllocationId).Distinct().Count() != requested.Count) return (false, "Each batch line can only appear once.");
                if (dispatchDate == default) return (false, "Dispatch date is required.");

                var b = await LockBookingAsync(conn, tx, bookingId);
                if (b == null) return (false, "Booking not found.");
                if (b.Status != "Pending") return (false, $"This booking is {b.Status}; it cannot be dispatched.");
                if (b.IsLegacy) return (false, "This booking is fulfilled from the legacy Inventory pipeline.");

                var active = await LockActiveAllocationsAsync(conn, tx, bookingId);
                foreach (var (allocationId, qty) in requested)
                {
                    if (!active.TryGetValue(allocationId, out var line))
                        return (false, "A dispatch line does not match an active allocation of this booking.");
                    if (!canAccessArea(line.AreaId))
                        return (false, "You are not authorized to dispatch from one of the selected batches' Area.");
                    var lineError = SeedlingBookingRules.ValidateDispatchLine(line.Open, qty);
                    if (lineError != null) return (false, lineError);
                }

                var total = requested.Sum(l => l.Quantity);
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, DispatchCodePrefix, dispatchDate.Year);
                var header = new SqlCommand(@"
INSERT INTO dbo.SeedlingDispatches
(DispatchCode, BookingId, DispatchDate, CustomerName, TotalQuantity, Status, ResponsiblePersonId, Remarks, CreatedById, CreatedBy, CreatedDate)
VALUES (@Code, @B, @Date, @Customer, @Total, 'Completed', @Resp, @Remarks, @UserId, @By, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                header.Parameters.AddWithValue("@Code", code);
                header.Parameters.AddWithValue("@B", b.Id);
                header.Parameters.AddWithValue("@Date", dispatchDate.Date);
                header.Parameters.AddWithValue("@Customer", (object?)b.CustomerName ?? DBNull.Value);
                header.Parameters.AddWithValue("@Total", total);
                header.Parameters.AddWithValue("@Resp", (object?)responsiblePersonId ?? DBNull.Value);
                header.Parameters.AddWithValue("@Remarks", (object?)Truncate(remarks, 500) ?? DBNull.Value);
                header.Parameters.AddWithValue("@UserId", (object?)actor.UserId ?? DBNull.Value);
                header.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
                var dispatchId = (int)(await header.ExecuteScalarAsync())!;

                foreach (var (allocationId, qty) in requested.OrderBy(l => active[l.AllocationId].ReadyStockId))
                {
                    var line = active[allocationId];
                    var (ok, msg) = await _readyStockRepo.RecordDispatchAsync(conn, tx, line.ReadyStockId, qty,
                        "SeedlingDispatch", dispatchId, actor.UserId, $"{code} booking {b.Id}");
                    if (!ok) return (false, msg);

                    var ins = new SqlCommand(@"
INSERT INTO dbo.SeedlingDispatchLines
(SeedlingDispatchId, BookingBatchAllocationId, ReadyStockId, BookedSpeciesId, ActualSpeciesId, IsSubstitution, SubstitutionReason, Quantity)
VALUES (@D, @A, @R, @BS, @AS, @Sub, @Reason, @Q);", conn, tx);
                    ins.Parameters.AddWithValue("@D", dispatchId);
                    ins.Parameters.AddWithValue("@A", allocationId);
                    ins.Parameters.AddWithValue("@R", line.ReadyStockId);
                    ins.Parameters.AddWithValue("@BS", (object?)line.BookedSpeciesId ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@AS", line.ActualSpeciesId);
                    ins.Parameters.AddWithValue("@Sub", line.IsSubstitution);
                    ins.Parameters.AddWithValue("@Reason", (object?)line.SubstitutionReason ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@Q", qty);
                    await ins.ExecuteNonQueryAsync();

                    var upd = new SqlCommand(@"
UPDATE dbo.BookingBatchAllocations
SET DispatchedQuantity = DispatchedQuantity + @Q,
    Status = CASE WHEN Quantity - (DispatchedQuantity + @Q) - ReleasedQuantity <= 0 THEN 'Closed' ELSE 'Active' END,
    ModifiedBy = @By, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
                    upd.Parameters.AddWithValue("@Q", qty);
                    upd.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
                    upd.Parameters.AddWithValue("@Id", allocationId);
                    await upd.ExecuteNonQueryAsync();
                }

                var newDispatched = b.Dispatched + total;
                var status = SeedlingBookingRules.StatusAfterDispatch(b.Quantity, newDispatched);
                var bk = new SqlCommand(@"
UPDATE dbo.Bookings
SET ReservedQuantity = ReservedQuantity - @T, DispatchedQuantity = DispatchedQuantity + @T,
    Status = @Status,
    ActualDeliveryDate = CASE WHEN @Status = 'Completed' THEN @Date ELSE ActualDeliveryDate END,
    ModifiedBy = @By, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
                bk.Parameters.AddWithValue("@T", total);
                bk.Parameters.AddWithValue("@Status", status);
                bk.Parameters.AddWithValue("@Date", dispatchDate.Date);
                bk.Parameters.AddWithValue("@By", (object?)actor.Name ?? DBNull.Value);
                bk.Parameters.AddWithValue("@Id", b.Id);
                await bk.ExecuteNonQueryAsync();

                return (true, status == "Completed"
                    ? $"Dispatch {code}: {total:N0} plants. The booking is fully dispatched and Completed."
                    : $"Dispatch {code}: {total:N0} plants. {b.Quantity - newDispatched:N0} still to dispatch.");
            });

        // ------------------------------------------------------------------
        // Reads
        // ------------------------------------------------------------------
        private const string SummarySelect = @"
SELECT b.Id, b.PlantId, b.SpeciesId, pt.Name AS PlantTypeName, ps.Name AS SpeciesName, b.Quantity, ISNULL(b.Status, 'Pending') AS Status,
       b.CustomerName, b.Contact, b.Address, b.BookingDate, b.DeliveryDate, b.ActualDeliveryDate, b.AddedBy,
       b.ReservedQuantity, b.DispatchedQuantity, b.FulfilmentSource, b.RevisionNo, b.ParentBookingId,
       b.CancelledDate, cu.Name AS CancelledByName, b.CancellationReason, b.ModifiedDate, b.ModifiedBy,
       ISNULL(leg.Qty, 0) AS LegacyQty,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.BookingBatchAllocations a WHERE a.BookingId = b.Id) THEN 1 ELSE 0 END AS BIT) AS HadReservations
FROM dbo.Bookings b
LEFT JOIN dbo.PlantTypes pt ON pt.Id = b.PlantId
LEFT JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
LEFT JOIN dbo.IMSUsers cu ON cu.Id = b.CancelledById
OUTER APPLY (SELECT SUM(CAST(it.QuantityUtilized AS DECIMAL(18,2))) AS Qty FROM dbo.InventoryTransactions it
             WHERE it.BookingId = b.Id AND it.TransactionType = 'Allocation') leg";

        private static SeedlingBookingSummary MapSummary(SqlDataReader r)
        {
            string? S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
            DateTime? D(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetDateTime(r.GetOrdinal(c));
            return new SeedlingBookingSummary
            {
                Id = r.GetInt32(r.GetOrdinal("Id")),
                PlantId = r.GetInt32(r.GetOrdinal("PlantId")),
                SpeciesId = r.IsDBNull(r.GetOrdinal("SpeciesId")) ? null : r.GetInt32(r.GetOrdinal("SpeciesId")),
                PlantTypeName = S("PlantTypeName")?.Trim(),
                SpeciesName = S("SpeciesName")?.Trim(),
                Quantity = r.GetInt32(r.GetOrdinal("Quantity")),
                Status = r.GetString(r.GetOrdinal("Status")),
                CustomerName = S("CustomerName"), Contact = S("Contact"), Address = S("Address"),
                BookingDate = D("BookingDate"), DeliveryDate = D("DeliveryDate"), ActualDeliveryDate = D("ActualDeliveryDate"),
                AddedBy = S("AddedBy"),
                ReservedQuantity = r.GetDecimal(r.GetOrdinal("ReservedQuantity")),
                DispatchedQuantity = r.GetDecimal(r.GetOrdinal("DispatchedQuantity")),
                FulfilmentSource = S("FulfilmentSource"),
                RevisionNo = r.GetInt32(r.GetOrdinal("RevisionNo")),
                ParentBookingId = r.IsDBNull(r.GetOrdinal("ParentBookingId")) ? null : r.GetInt32(r.GetOrdinal("ParentBookingId")),
                CancelledDate = D("CancelledDate"), CancelledByName = S("CancelledByName"), CancellationReason = S("CancellationReason"),
                ModifiedDate = D("ModifiedDate"), ModifiedBy = S("ModifiedBy"),
                LegacyFulfilledQuantity = r.GetDecimal(r.GetOrdinal("LegacyQty")),
                HadReservations = r.GetBoolean(r.GetOrdinal("HadReservations"))
            };
        }

        public async Task<SeedlingBookingSummary?> GetBookingAsync(int bookingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand(SummarySelect + " WHERE b.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", bookingId);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapSummary(r) : null;
        }

        // Booking fulfilment report. Filters are optional; at most 1000 rows.
        public async Task<List<SeedlingBookingSummary>> ListBookingsAsync(string? status, string? search, DateTime? deliveryFrom, DateTime? deliveryTo, int? speciesId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand(SummarySelect.Replace("SELECT b.Id,", "SELECT TOP (1000) b.Id,") + @"
WHERE (@Status IS NULL OR b.Status = @Status)
  AND (@Search IS NULL OR b.CustomerName LIKE @Search OR CAST(b.Id AS NVARCHAR(20)) = @SearchExact)
  AND (@From IS NULL OR b.DeliveryDate >= @From)
  AND (@To IS NULL OR b.DeliveryDate <= @To)
  AND (@Species IS NULL OR b.SpeciesId = @Species)
ORDER BY b.DeliveryDate DESC, b.Id DESC", conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : "%" + search.Trim() + "%");
            cmd.Parameters.AddWithValue("@SearchExact", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim());
            cmd.Parameters.AddWithValue("@From", (object?)deliveryFrom?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)deliveryTo?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Species", (object?)speciesId ?? DBNull.Value);
            var list = new List<SeedlingBookingSummary>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(MapSummary(r));
            return list;
        }

        private const string AllocationSelect = @"
SELECT a.Id, a.BookingId, a.ReadyStockId, a.BookedSpeciesId, a.ActualSpeciesId, a.IsSubstitution, a.SubstitutionReason,
       a.Quantity, a.DispatchedQuantity, a.ReleasedQuantity, a.Status, a.CreatedBy, a.CreatedDate, a.ModifiedBy, a.ModifiedDate,
       bps.Name AS BookedSpeciesName, aps.Name AS ActualSpeciesName,
       COALESCE(sw.Id, cw.Id) AS SeedSowingId, COALESCE(sw.SowingCode, cw.SowingCode) AS SowingCode,
       COALESCE(sw.SowingDate, cw.SowingDate) AS SowingDate, rs.CavityType, rs.BatchNo AS SeedLot, rs.AreaId, ar.Name AS AreaName,
       COALESCE(rph.Name, sph.Name, cph.Name) AS PolyhouseName, COALESCE(sup.Name, csup.Name) AS SupervisorName, appr.ApprovedByName
FROM dbo.BookingBatchAllocations a
INNER JOIN dbo.ReadyStock rs ON rs.Id = a.ReadyStockId
-- Phase 5: exactly one of rs.SeedSowingId/rs.CuttingSowingId is ever set
-- (CK_ReadyStock_SourceType) -- LEFT JOIN both and COALESCE every
-- sowing-derived column, same pattern as ReadyStockRepository/
-- ReadyConfirmationRepository.
LEFT JOIN dbo.SeedSowings sw ON sw.Id = rs.SeedSowingId
LEFT JOIN dbo.CuttingSowings cw ON cw.Id = rs.CuttingSowingId
INNER JOIN dbo.PlantSpecies aps ON aps.Id = a.ActualSpeciesId
LEFT JOIN dbo.PlantSpecies bps ON bps.Id = a.BookedSpeciesId
INNER JOIN dbo.Area ar ON ar.Id = rs.AreaId
LEFT JOIN dbo.Polyhouses rph ON rph.Id = rs.PolyhouseId
LEFT JOIN dbo.Polyhouses sph ON sph.Id = sw.PolyhouseId
LEFT JOIN dbo.Polyhouses cph ON cph.Id = ar.PolyhouseId
LEFT JOIN dbo.IMSUsers sup ON sup.Id = sw.SupervisorId
LEFT JOIN dbo.IMSUsers csup ON csup.Id = cw.SupervisorId
OUTER APPLY (SELECT TOP 1 u.Name AS ApprovedByName FROM dbo.ReadyConfirmations rc
             LEFT JOIN dbo.IMSUsers u ON u.Id = rc.ApprovedById
             WHERE rc.ReadyStockId = rs.Id AND rc.Status = 'Confirmed' ORDER BY rc.ConfirmationDate DESC) appr";

        public async Task<List<BookingBatchAllocation>> GetAllocationsAsync(int bookingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand(AllocationSelect + " WHERE a.BookingId = @B ORDER BY a.Id", conn);
            cmd.Parameters.AddWithValue("@B", bookingId);
            var list = new List<BookingBatchAllocation>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string? S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
                list.Add(new BookingBatchAllocation
                {
                    Id = r.GetInt32(0), BookingId = r.GetInt32(1), ReadyStockId = r.GetInt32(2),
                    BookedSpeciesId = r.IsDBNull(3) ? null : r.GetInt32(3), ActualSpeciesId = r.GetInt32(4),
                    IsSubstitution = r.GetBoolean(5), SubstitutionReason = S("SubstitutionReason"),
                    Quantity = r.GetDecimal(7), DispatchedQuantity = r.GetDecimal(8), ReleasedQuantity = r.GetDecimal(9),
                    Status = r.GetString(10), CreatedBy = S("CreatedBy"), CreatedDate = r.GetDateTime(12),
                    ModifiedBy = S("ModifiedBy"), ModifiedDate = r.IsDBNull(14) ? null : r.GetDateTime(14),
                    BookedSpeciesName = S("BookedSpeciesName")?.Trim(), ActualSpeciesName = S("ActualSpeciesName")?.Trim(),
                    SeedSowingId = r.GetInt32(r.GetOrdinal("SeedSowingId")), BatchCode = S("SowingCode"),
                    SowingDate = r.GetDateTime(r.GetOrdinal("SowingDate")), CavityType = S("CavityType"), SeedLot = S("SeedLot"),
                    AreaId = r.GetInt32(r.GetOrdinal("AreaId")), AreaName = S("AreaName"), PolyhouseName = S("PolyhouseName"),
                    SupervisorName = S("SupervisorName"), ApprovedByName = S("ApprovedByName")
                });
            }
            return list;
        }

        public async Task<List<BookingRevision>> GetRevisionsAsync(int bookingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand(@"
SELECT r.Id, r.BookingId, r.RevisionNo, r.PreviousPlantId, r.NewPlantId, r.PreviousSpeciesId, r.NewSpeciesId,
       pps.Name, nps.Name, r.PreviousQuantity, r.NewQuantity, r.PreviousDeliveryDate, r.NewDeliveryDate,
       r.ReleasedQuantity, r.SplitBookingId, r.OtherChanges, r.Reason, r.ChangedBy, r.ChangedDate
FROM dbo.BookingRevisions r
LEFT JOIN dbo.PlantSpecies pps ON pps.Id = r.PreviousSpeciesId
LEFT JOIN dbo.PlantSpecies nps ON nps.Id = r.NewSpeciesId
WHERE r.BookingId = @B ORDER BY r.RevisionNo", conn);
            cmd.Parameters.AddWithValue("@B", bookingId);
            var list = new List<BookingRevision>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new BookingRevision
                {
                    Id = r.GetInt32(0), BookingId = r.GetInt32(1), RevisionNo = r.GetInt32(2),
                    PreviousPlantId = r.IsDBNull(3) ? null : r.GetInt32(3), NewPlantId = r.IsDBNull(4) ? null : r.GetInt32(4),
                    PreviousSpeciesId = r.IsDBNull(5) ? null : r.GetInt32(5), NewSpeciesId = r.IsDBNull(6) ? null : r.GetInt32(6),
                    PreviousSpeciesName = r.IsDBNull(7) ? null : r.GetString(7).Trim(), NewSpeciesName = r.IsDBNull(8) ? null : r.GetString(8).Trim(),
                    PreviousQuantity = r.GetInt32(9), NewQuantity = r.GetInt32(10),
                    PreviousDeliveryDate = r.IsDBNull(11) ? null : r.GetDateTime(11), NewDeliveryDate = r.IsDBNull(12) ? null : r.GetDateTime(12),
                    ReleasedQuantity = r.GetDecimal(13), SplitBookingId = r.IsDBNull(14) ? null : r.GetInt32(14),
                    OtherChanges = r.IsDBNull(15) ? null : r.GetString(15), Reason = r.GetString(16),
                    ChangedBy = r.IsDBNull(17) ? null : r.GetString(17), ChangedDate = r.GetDateTime(18)
                });
            }
            return list;
        }

        private const string DispatchLineSelect = @"
SELECT l.Id, l.SeedlingDispatchId, l.BookingBatchAllocationId, l.ReadyStockId, l.BookedSpeciesId, l.ActualSpeciesId,
       l.IsSubstitution, l.SubstitutionReason, l.Quantity, bps.Name AS BookedSpeciesName, aps.Name AS ActualSpeciesName,
       COALESCE(sw.SowingCode, cw.SowingCode) AS SowingCode, ar.Name AS AreaName,
       COALESCE(rph.Name, sph.Name, cph.Name) AS PolyhouseName, COALESCE(sw.SowingDate, cw.SowingDate) AS SowingDate,
       d.DispatchCode, d.DispatchDate, d.BookingId, d.CustomerName, d.CreatedBy
FROM dbo.SeedlingDispatchLines l
INNER JOIN dbo.SeedlingDispatches d ON d.Id = l.SeedlingDispatchId
INNER JOIN dbo.ReadyStock rs ON rs.Id = l.ReadyStockId
LEFT JOIN dbo.SeedSowings sw ON sw.Id = rs.SeedSowingId
LEFT JOIN dbo.CuttingSowings cw ON cw.Id = rs.CuttingSowingId
INNER JOIN dbo.PlantSpecies aps ON aps.Id = l.ActualSpeciesId
LEFT JOIN dbo.PlantSpecies bps ON bps.Id = l.BookedSpeciesId
INNER JOIN dbo.Area ar ON ar.Id = rs.AreaId
LEFT JOIN dbo.Polyhouses rph ON rph.Id = rs.PolyhouseId
LEFT JOIN dbo.Polyhouses sph ON sph.Id = sw.PolyhouseId
LEFT JOIN dbo.Polyhouses cph ON cph.Id = ar.PolyhouseId";

        private static SeedlingDispatchLine MapLine(SqlDataReader r)
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            return new SeedlingDispatchLine
            {
                Id = r.GetInt32(0), SeedlingDispatchId = r.GetInt32(1), BookingBatchAllocationId = r.GetInt32(2), ReadyStockId = r.GetInt32(3),
                BookedSpeciesId = r.IsDBNull(4) ? null : r.GetInt32(4), ActualSpeciesId = r.GetInt32(5),
                IsSubstitution = r.GetBoolean(6), SubstitutionReason = S(7), Quantity = r.GetDecimal(8),
                BookedSpeciesName = S(9)?.Trim(), ActualSpeciesName = S(10)?.Trim(), BatchCode = S(11), AreaName = S(12),
                PolyhouseName = S(13), SowingDate = r.GetDateTime(14), DispatchCode = S(15), DispatchDate = r.GetDateTime(16),
                BookingId = r.GetInt32(17), CustomerName = S(18), DispatchedBy = S(19)
            };
        }

        public async Task<List<SeedlingDispatch>> GetDispatchesAsync(int bookingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var dispatches = new List<SeedlingDispatch>();
            var cmd = new SqlCommand(@"
SELECT d.Id, d.DispatchCode, d.BookingId, d.DispatchDate, d.CustomerName, d.TotalQuantity, d.Status, d.ResponsiblePersonId,
       rp.Name, d.Remarks, d.CreatedBy, d.CreatedDate
FROM dbo.SeedlingDispatches d
LEFT JOIN dbo.IMSUsers rp ON rp.Id = d.ResponsiblePersonId
WHERE d.BookingId = @B ORDER BY d.Id", conn);
            cmd.Parameters.AddWithValue("@B", bookingId);
            using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    dispatches.Add(new SeedlingDispatch
                    {
                        Id = r.GetInt32(0), DispatchCode = r.GetString(1), BookingId = r.GetInt32(2), DispatchDate = r.GetDateTime(3),
                        CustomerName = r.IsDBNull(4) ? null : r.GetString(4), TotalQuantity = r.GetDecimal(5), Status = r.GetString(6),
                        ResponsiblePersonId = r.IsDBNull(7) ? null : r.GetInt32(7), ResponsiblePersonName = r.IsDBNull(8) ? null : r.GetString(8),
                        Remarks = r.IsDBNull(9) ? null : r.GetString(9), CreatedBy = r.IsDBNull(10) ? null : r.GetString(10), CreatedDate = r.GetDateTime(11)
                    });
                }
            }
            if (dispatches.Count == 0) return dispatches;

            var lineCmd = new SqlCommand(DispatchLineSelect + " WHERE d.BookingId = @B ORDER BY l.Id", conn);
            lineCmd.Parameters.AddWithValue("@B", bookingId);
            var byId = dispatches.ToDictionary(d => d.Id);
            using (var r = await lineCmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    var line = MapLine(r);
                    if (byId.TryGetValue(line.SeedlingDispatchId, out var d)) d.Lines.Add(line);
                }
            }
            return dispatches;
        }

        // Dispatch register (report): one row per batch line.
        public async Task<List<SeedlingDispatchLine>> GetDispatchRegisterAsync(DateTime from, DateTime to, string? search)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand(DispatchLineSelect + @"
WHERE d.DispatchDate >= @From AND d.DispatchDate <= @To
  AND (@Search IS NULL OR d.CustomerName LIKE @Search OR d.DispatchCode LIKE @Search OR sw.SowingCode LIKE @Search)
ORDER BY d.DispatchDate DESC, d.Id DESC, l.Id", conn);
            cmd.Parameters.AddWithValue("@From", from.Date);
            cmd.Parameters.AddWithValue("@To", to.Date);
            cmd.Parameters.AddWithValue("@Search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : "%" + search.Trim() + "%");
            var list = new List<SeedlingDispatchLine>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(MapLine(r));
            return list;
        }

        // Variety totals for the booking screen (company-wide).
        public async Task<ReadyStockAvailability> GetAvailabilityAsync(int speciesId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand(@"
SELECT COUNT(*), ISNULL(SUM(Quantity), 0), ISNULL(SUM(Quantity - DispatchedQuantity), 0), ISNULL(SUM(ReservedQuantity), 0),
       ISNULL(SUM(DispatchedQuantity), 0), ISNULL(SUM(Quantity - ReservedQuantity - DispatchedQuantity), 0)
FROM dbo.ReadyStock WHERE SpeciesId = @S", conn);
            cmd.Parameters.AddWithValue("@S", speciesId);
            using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return new ReadyStockAvailability
            {
                SpeciesId = speciesId, Batches = r.GetInt32(0), ReadyQuantity = r.GetDecimal(1), PhysicalQuantity = r.GetDecimal(2),
                ReservedQuantity = r.GetDecimal(3), DispatchedQuantity = r.GetDecimal(4), AvailableQuantity = r.GetDecimal(5)
            };
        }

        // Batches with available plants for a species (plant type), oldest
        // first -- the Dispatch page's allocation / substitution choices.
        public async Task<List<ReadyBatchOption>> GetBatchOptionsAsync(int plantTypeId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand(@"
SELECT rs.Id, rs.SpeciesId, ps.Name, ps.PlantTypeId, COALESCE(sw.SowingCode, cw.SowingCode), rs.SowingDate, rs.FirstConfirmationDate, rs.CavityType,
       rs.AreaId, ar.Name, COALESCE(rph.Name, sph.Name, cph.Name), rs.Quantity, rs.ReservedQuantity, rs.DispatchedQuantity
FROM dbo.ReadyStock rs
INNER JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
LEFT JOIN dbo.SeedSowings sw ON sw.Id = rs.SeedSowingId
LEFT JOIN dbo.CuttingSowings cw ON cw.Id = rs.CuttingSowingId
INNER JOIN dbo.Area ar ON ar.Id = rs.AreaId
LEFT JOIN dbo.Polyhouses rph ON rph.Id = rs.PolyhouseId
LEFT JOIN dbo.Polyhouses sph ON sph.Id = sw.PolyhouseId
LEFT JOIN dbo.Polyhouses cph ON cph.Id = ar.PolyhouseId
WHERE ps.PlantTypeId = @P AND rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity > 0
ORDER BY rs.SowingDate, rs.FirstConfirmationDate, rs.Id", conn);
            cmd.Parameters.AddWithValue("@P", plantTypeId);
            var list = new List<ReadyBatchOption>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var q = r.GetDecimal(11); var res = r.GetDecimal(12); var dis = r.GetDecimal(13);
                list.Add(new ReadyBatchOption
                {
                    ReadyStockId = r.GetInt32(0), SpeciesId = r.GetInt32(1), SpeciesName = r.GetString(2).Trim(), PlantTypeId = r.GetInt32(3),
                    BatchCode = r.GetString(4), SowingDate = r.GetDateTime(5), FirstConfirmationDate = r.IsDBNull(6) ? null : r.GetDateTime(6),
                    CavityType = r.GetString(7), AreaId = r.GetInt32(8), AreaName = r.GetString(9), PolyhouseName = r.IsDBNull(10) ? null : r.GetString(10),
                    Quantity = q, ReservedQuantity = res, DispatchedQuantity = dis, AvailableQuantity = q - res - dis
                });
            }
            return list;
        }
    }
}
