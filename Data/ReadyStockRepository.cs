using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 25 (Phase K): dbo.ReadyStock + dbo.ReadyStockTransactions --
    // the confirmed-ready stock pool and its dedicated ledger, mirroring
    // SeedStockRepository/PottedPlantStockRepository's established
    // "GetOrCreateLockedAsync + RecordTransactionAsync, one dedicated
    // ledger per stock entity" shape exactly. The one deliberate
    // difference: this pool's business key is SeedSowingId ALONE
    // (UNIQUE), not a Species+Area+Lot composite -- see Decision 22 in
    // PROJECT_DOCUMENTATION.md for why the SeedSowing-scoped grain is
    // required here (batch-identity/traceability preservation).
    public class ReadyStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public ReadyStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT
    rs.Id, rs.SeedSowingId, sw.SowingCode, rs.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    rs.AreaId, a.Name AS AreaName, a.AreaType, rs.PolyhouseId, rph.Name AS PolyhouseName,   -- only the Polyhouse actually recorded (optional)
    rs.BatchNo, rs.CavityType, sw.NumberOfTrays AS SowingTrays, appr.ReadyTrays, rs.SowingDate, rs.Quantity, rs.FirstConfirmationDate,
    rs.ReservedQuantity, rs.DispatchedQuantity, rs.InTransitQuantity, rs.OriginType, rs.ParentReadyStockId, rs.OriginReferenceId,
    sw.QuantitySown, sw.ConfirmedReadyQuantity, sw.WastageQuantity, sw.Status AS SowingStatus,
    appr.ApprovedByName, appr.ApprovalDate,
    rs.CreatedDate, rs.CreatedBy, rs.ModifiedDate, rs.ModifiedBy
FROM dbo.ReadyStock rs
INNER JOIN dbo.SeedSowings sw ON rs.SeedSowingId = sw.Id
INNER JOIN dbo.PlantSpecies ps ON rs.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON rs.AreaId = a.Id
LEFT JOIN dbo.Polyhouses rph ON rs.PolyhouseId = rph.Id
OUTER APPLY (
    SELECT TOP 1 u.Name AS ApprovedByName, rc.ConfirmationDate AS ApprovalDate, rc.ActualTrayQuantity AS ReadyTrays
    FROM dbo.ReadyConfirmations rc
    LEFT JOIN dbo.IMSUsers u ON u.Id = rc.ApprovedById
    WHERE rc.ReadyStockId = rs.Id AND rc.Status = 'Confirmed'
    ORDER BY rc.ConfirmationDate DESC
) appr";   // one Confirmed approval per sowing (UX_ReadyConfirmations_OneConfirmedPerSowing)

        public async Task<List<ReadyStock>> GetAllAsync()
        {
            var list = new List<ReadyStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY rs.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<ReadyStock?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE rs.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<ReadyStock?> GetBySeedSowingIdAsync(int seedSowingId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            // The SOWING batch of this sowing (received / transplanted batches share the sowing reference).
            var sql = BaseSelect + " WHERE rs.SeedSowingId = @SeedSowingId AND rs.OriginType = N'Sowing'";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Finds-or-creates the ONE ReadyStock row for this Sowing --
        // called under the caller's own transaction
        // (ReadyConfirmationRepository.ConfirmAsync), with the parent
        // SeedSowings row already locked by the caller. Never created
        // eagerly at Sowing time -- only the first time a confirmation
        // actually happens, per the "no automatic Ready Stock creation"
        // rule. Mirrors SeedStockRepository.GetOrCreateLockedAsync.
        public async Task<int> GetOrCreateLockedAsync(
            SqlConnection conn, SqlTransaction tx, int seedSowingId, int speciesId, int areaId, int? polyhouseId,
            string batchNo, string cavityType, DateTime sowingDate, string? createdBy)
        {
            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE SeedSowingId = @SeedSowingId AND OriginType = N'Sowing'",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return (int)existingId;
            }

            const string insertSql = @"
INSERT INTO dbo.ReadyStock (SeedSowingId, SpeciesId, AreaId, PolyhouseId, BatchNo, CavityType, SowingDate, Quantity, CreatedDate, CreatedBy)
VALUES (@SeedSowingId, @SpeciesId, @AreaId, @PolyhouseId, @BatchNo, @CavityType, @SowingDate, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
            insertCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            insertCmd.Parameters.AddWithValue("@AreaId", areaId);
            insertCmd.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@BatchNo", batchNo ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@CavityType", cavityType);
            insertCmd.Parameters.AddWithValue("@SowingDate", sowingDate.Date);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)await insertCmd.ExecuteScalarAsync();
        }

        // Records a signed movement against an existing (locked)
        // ReadyStock row and writes the matching ledger row in the SAME
        // transaction -- Quantity is never changed any other way.
        // Positive quantityDelta = 'Confirmed'; negative =
        // 'ReversalRemoval' (a cancelled confirmation). Mirrors
        // SeedStockRepository.RecordTransactionAsync exactly.
        // businessDateIndia (2026-10-10): the operation's own business date, if it has one
        // (e.g. the transplanting date) -- stored as TransactionDate (Services/LedgerDates.cs);
        // CreatedAt stays the recorded time. null = the recorded time (as before).
        // atUtc (2026-10-11): an exact, already-validated instant instead (a Ready Stock receipt is
        // dated at the later of its business receipt date and the transfer hold).
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int readyStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks,
            DateTime? businessDateIndia = null, DateTime? atUtc = null)
        {
            var (dateOk, transactionUtc, dateError) = atUtc.HasValue ? (true, atUtc, null) : LedgerDates.Instant(businessDateIndia);
            if (!dateOk)
                return (false, dateError);
            var lockCmd = new SqlCommand("SELECT Quantity, ReservedQuantity, DispatchedQuantity, InTransitQuantity FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", readyStockId);
            decimal before, reserved, dispatched, inTransit;
            using (var reader = await lockCmd.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                    return (false, "Ready Stock record not found.");
                before = reader.GetDecimal(0);
                reserved = reader.GetDecimal(1);
                dispatched = reader.GetDecimal(2);
                inTransit = reader.GetDecimal(3);
            }

            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"This movement would take Ready Stock quantity negative (current {QuantityFormat.Qty(before)}, change {QuantityFormat.Qty(quantityDelta)}).");
            // Phase C: plants already reserved for bookings or dispatched to
            // customers cannot be taken back out of the approved Ready quantity
            // (e.g. by cancelling the Supervisor Approval).
            if (after < reserved + dispatched + inTransit)
                return (false, inTransit > 0
                    ? $"This batch has {QuantityFormat.Qty(reserved)} plants reserved, {QuantityFormat.Qty(dispatched)} dispatched and {QuantityFormat.Qty(inTransit)} in transit to a Mother Plant Area; its Ready quantity cannot go below {QuantityFormat.Qty(reserved + dispatched + inTransit)}."
                    : $"This batch has {QuantityFormat.Qty(reserved)} plants reserved for bookings and {QuantityFormat.Qty(dispatched)} dispatched; its Ready quantity cannot go below {QuantityFormat.Qty(reserved + dispatched)}. Release the reservations first.");

            var updateCmd = new SqlCommand(@"
UPDATE dbo.ReadyStock
SET Quantity = @After,
    FirstConfirmationDate = CASE WHEN FirstConfirmationDate IS NULL AND @After > 0 THEN SYSUTCDATETIME() ELSE FirstConfirmationDate END,
    ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", readyStockId);
            await updateCmd.ExecuteNonQueryAsync();

            await InsertLedgerAsync(conn, tx, readyStockId, transactionType, referenceType, referenceId, quantityDelta, before, userId, remarks, transactionUtc);
            return (true, null);
        }

        // 2026-10-09: several movements of ONE batch as one change -- one lock, one Quantity
        // update, one ledger row per movement (BeforeQuantity running from the locked balance).
        // Needed where the parts would be invalid on their own: e.g. a Main Area batch (whole
        // trays only, TR_ReadyStock_WholeTrays) losing 230 received + 10 lost plants of a
        // 240-plant (10 x 24) transfer -- 230 alone is not whole trays, the total is.
        // Refuses (changes nothing) if the result is negative or below Reserved + Dispatched +
        // InTransit.
        public async Task<(bool Success, string? Message)> RecordMovementsAsync(
            SqlConnection conn, SqlTransaction tx, int readyStockId,
            IReadOnlyList<(string TransactionType, decimal Delta, string? Remarks)> movements,
            string? referenceType, int? referenceId, int? userId, DateTime? businessDateIndia = null, DateTime? atUtc = null)
        {
            var parts = movements.Where(m => m.Delta != 0).ToList();
            if (parts.Count == 0)
                return (true, null);
            var (dateOk, transactionUtc, dateError) = atUtc.HasValue ? (true, atUtc, null) : LedgerDates.Instant(businessDateIndia);
            if (!dateOk)
                return (false, dateError);
            var (ok, quantity, reserved, dispatched, inTransit) = await LockQuantitiesAsync(conn, tx, readyStockId);
            if (!ok)
                return (false, "Ready Stock batch not found.");
            var after = quantity + parts.Sum(m => m.Delta);
            if (after < 0)
                return (false, $"This would take the batch below zero (current {QuantityFormat.Qty(quantity)}, change {QuantityFormat.Qty(after - quantity)}).");
            if (after < reserved + dispatched + inTransit)
                return (false, $"Only {QuantityFormat.Qty(quantity - reserved - dispatched - inTransit)} plants of this batch are available (the rest is reserved, dispatched or in transit).");

            var update = new SqlCommand(@"
UPDATE dbo.ReadyStock
SET Quantity = @After,
    FirstConfirmationDate = CASE WHEN FirstConfirmationDate IS NULL AND @After > 0 THEN SYSUTCDATETIME() ELSE FirstConfirmationDate END,
    ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
            update.Parameters.AddWithValue("@After", after);
            update.Parameters.AddWithValue("@Id", readyStockId);
            await update.ExecuteNonQueryAsync();

            var running = quantity;
            foreach (var m in parts)
            {
                await InsertLedgerAsync(conn, tx, readyStockId, m.TransactionType, referenceType, referenceId, m.Delta, running, userId, m.Remarks, transactionUtc);
                running += m.Delta;
            }
            return (true, null);
        }

        // Phase C: reserve (+) or release (-) plants of a batch for a booking.
        // Called inside the caller's transaction (SeedlingFulfilmentRepository),
        // under UPDLOCK/HOLDLOCK on the batch row. Reserved can never go
        // negative nor above what is still physically there
        // (Reserved + Dispatched <= Quantity; also a DB CHECK). Ledger row:
        // 'Reservation' / 'ReservationRelease', BeforeQuantity = Reserved.
        public async Task<(bool Success, string? Message)> RecordReservationAsync(
            SqlConnection conn, SqlTransaction tx, int readyStockId, decimal reservedDelta,
            string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var (ok, quantity, reserved, dispatched, inTransit) = await LockQuantitiesAsync(conn, tx, readyStockId);
            if (!ok)
                return (false, "Ready Stock batch not found.");

            var after = reserved + reservedDelta;
            if (after < 0)
                return (false, $"Cannot release {QuantityFormat.Qty(-reservedDelta)}: only {QuantityFormat.Qty(reserved)} reserved on this batch.");
            if (after + dispatched + inTransit > quantity)
                return (false, $"{PlantStockManager.Services.SeedlingBookingRules.InsufficientStockMessage} Batch available {QuantityFormat.Qty(quantity - reserved - dispatched - inTransit)}, requested {QuantityFormat.Qty(reservedDelta)}.");

            var update = new SqlCommand("UPDATE dbo.ReadyStock SET ReservedQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            update.Parameters.AddWithValue("@After", after);
            update.Parameters.AddWithValue("@Id", readyStockId);
            await update.ExecuteNonQueryAsync();

            await InsertLedgerAsync(conn, tx, readyStockId, reservedDelta >= 0 ? "Reservation" : "ReservationRelease",
                referenceType, referenceId, reservedDelta, reserved, userId, remarks);
            return (true, null);
        }

        // Phase C: plants physically leave the batch. Only reserved plants can
        // be dispatched: Reserved -= q, Dispatched += q (so Physical -= q).
        // Two ledger rows keep both movements auditable: 'ReservationRelease'
        // (BeforeQuantity = Reserved) and 'Dispatch' (BeforeQuantity = Physical).
        // 2026-10-10: businessDateIndia = the operation's own date (dispatch / sale / wastage
        // date) -> the movement row's TransactionDate; reservedAtUtc = when these plants were
        // reserved (null = in this same operation) -> the release row's date
        // (LedgerDates.ReleaseInstant). A business date before the batch was approved Ready is
        // ALLOWED (owner decision 2026-10-10: data entry lags) but flagged in the row's remarks.
        public async Task<(bool Success, string? Message)> RecordDispatchAsync(
            SqlConnection conn, SqlTransaction tx, int readyStockId, decimal quantity,
            string? referenceType, int? referenceId, int? userId, string? remarks, string ledgerType = "Dispatch",
            DateTime? businessDateIndia = null, DateTime? reservedAtUtc = null)
        {
            if (quantity <= 0)
                return (false, "Dispatch quantity must be greater than zero.");
            var (dateOk, dispatchUtc, dateError) = LedgerDates.Instant(businessDateIndia);
            if (!dateOk)
                return (false, dateError);
            var (ok, readyQuantity, reserved, dispatched, inTransit) = await LockQuantitiesAsync(conn, tx, readyStockId);
            if (!ok)
                return (false, "Ready Stock batch not found.");
            if (quantity > reserved)
                return (false, $"Only {QuantityFormat.Qty(reserved)} plants are reserved on this batch; {QuantityFormat.Qty(quantity)} cannot be dispatched.");
            var physical = readyQuantity - dispatched - inTransit;
            if (quantity > physical)
                return (false, $"Only {QuantityFormat.Qty(physical)} plants are physically in this batch.");

            var update = new SqlCommand(@"
UPDATE dbo.ReadyStock
SET ReservedQuantity = ReservedQuantity - @Q, DispatchedQuantity = DispatchedQuantity + @Q, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
            update.Parameters.AddWithValue("@Q", quantity);
            update.Parameters.AddWithValue("@Id", readyStockId);
            await update.ExecuteNonQueryAsync();

            if (dispatchUtc is DateTime when)
            {
                var readyCmd = new SqlCommand("SELECT FirstConfirmationDate FROM dbo.ReadyStock WHERE Id = @Id", conn, tx);
                readyCmd.Parameters.AddWithValue("@Id", readyStockId);
                if (await readyCmd.ExecuteScalarAsync() is DateTime readyUtc && when < readyUtc)
                    remarks = (remarks + " " + BeforeReadyFlag(AppClock.ToIndiaTime(readyUtc))).Trim();
            }

            await InsertLedgerAsync(conn, tx, readyStockId, "ReservationRelease", referenceType, referenceId, -quantity, reserved, userId,
                ledgerType == "Dispatch" ? "Dispatched" : ledgerType, LedgerDates.ReleaseInstant(dispatchUtc, reservedAtUtc));
            await InsertLedgerAsync(conn, tx, readyStockId, ledgerType, referenceType, referenceId, -quantity, physical, userId, remarks, dispatchUtc);
            return (true, null);
        }

        // The ledger remark that marks a movement dated before its batch was approved Ready.
        public static string BeforeReadyFlag(DateTime readyIndia) => $"[dated before this batch was approved Ready on {readyIndia:dd/MM/yyyy}]";

        // Read-only lock-free peek at the current balances, for the
        // caller's own pre-checks (e.g. Outlet Sale/Wastage) before it
        // decides how much to reserve. NOT under a lock -- the actual
        // Reserve/Dispatch calls re-check everything under their own lock.
        public async Task<(decimal Quantity, decimal Reserved, decimal Dispatched)?> PeekQuantitiesAsync(int readyStockId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            // Reserved includes plants in transit to a Mother Plant Area: neither can be used.
            var cmd = new SqlCommand("SELECT Quantity, ReservedQuantity + InTransitQuantity, DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", readyStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
        }

        private static async Task<(bool Ok, decimal Quantity, decimal Reserved, decimal Dispatched, decimal InTransit)> LockQuantitiesAsync(
            SqlConnection conn, SqlTransaction tx, int readyStockId)
        {
            var cmd = new SqlCommand("SELECT Quantity, ReservedQuantity, DispatchedQuantity, InTransitQuantity FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", readyStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return (false, 0, 0, 0, 0);
            return (true, reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3));
        }

        // 2026-10-09: hold (+) or release (-) plants IN TRANSIT to a Mother Plant Area.
        // Under UPDLOCK/HOLDLOCK on the batch, inside the caller's transaction. A hold
        // takes only AVAILABLE plants (Quantity - Reserved - Dispatched - InTransit); the
        // DB CHECK (Reserved + Dispatched + InTransit <= Quantity) is the last line of
        // defence. Ledger: 'TransferHold' / 'TransferHoldRelease', BeforeQuantity = InTransit
        // (like Reservation, these rows do not change Quantity).
        public async Task<(bool Success, string? Message)> RecordTransferHoldAsync(
            SqlConnection conn, SqlTransaction tx, int readyStockId, decimal delta,
            string? referenceType, int? referenceId, int? userId, string? remarks, DateTime? businessDateIndia = null, DateTime? atUtc = null)
        {
            var (dateOk, transactionUtc, dateError) = atUtc.HasValue ? (true, atUtc, null) : LedgerDates.Instant(businessDateIndia);
            if (!dateOk)
                return (false, dateError);
            var (ok, quantity, reserved, dispatched, inTransit) = await LockQuantitiesAsync(conn, tx, readyStockId);
            if (!ok)
                return (false, "Ready Stock batch not found.");
            var after = inTransit + delta;
            if (after < 0)
                return (false, $"Only {QuantityFormat.Qty(inTransit)} plants are in transit from this batch.");
            if (reserved + dispatched + after > quantity)
                return (false, $"Only {QuantityFormat.Qty(quantity - reserved - dispatched - inTransit)} plants of this batch are available; {QuantityFormat.Qty(delta)} cannot be sent.");

            var update = new SqlCommand("UPDATE dbo.ReadyStock SET InTransitQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            update.Parameters.AddWithValue("@After", after);
            update.Parameters.AddWithValue("@Id", readyStockId);
            await update.ExecuteNonQueryAsync();
            await InsertLedgerAsync(conn, tx, readyStockId, delta >= 0 ? "TransferHold" : "TransferHoldRelease",
                referenceType, referenceId, delta, inTransit, userId, remarks, transactionUtc);
            return (true, null);
        }

        // 2026-10-09: a NEW batch derived from a (locked) parent batch -- received in a
        // Mother Plant Area ('Transfer') or the output of a transplanting ('Transplant').
        // Keeps the ORIGINAL sowing's SeedSowingId / Species / BatchNo / SowingDate for
        // traceability. Starts at 0; the caller adds the plants through RecordTransactionAsync
        // in the same transaction. UX_ReadyStock_Origin allows ONE batch per transfer /
        // transplanting, so a repeated receive / save can never create a second one.
        public async Task<int> CreateDerivedBatchAsync(
            SqlConnection conn, SqlTransaction tx, int parentReadyStockId, string originType, int originReferenceId,
            int areaId, int polyhouseId, string cavityType, string? createdBy, DateTime? availableFromUtc = null)
        {
            // availableFromUtc (2026-10-11): a received batch is usable here from its business receipt
            // instant (stored as FirstConfirmationDate); NULL = from CreatedDate (recorded time), as before.
            var cmd = new SqlCommand(@"
INSERT INTO dbo.ReadyStock (SeedSowingId, SpeciesId, AreaId, PolyhouseId, BatchNo, CavityType, SowingDate, Quantity, CreatedDate, CreatedBy,
                            OriginType, ParentReadyStockId, OriginReferenceId, FirstConfirmationDate)
SELECT p.SeedSowingId, p.SpeciesId, @AreaId, @PolyhouseId, p.BatchNo, @CavityType, p.SowingDate, 0, SYSUTCDATETIME(), @CreatedBy,
       @OriginType, p.Id, @OriginReferenceId, @AvailableFrom
FROM dbo.ReadyStock p WHERE p.Id = @ParentId;
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            cmd.Parameters.Add("@AvailableFrom", System.Data.SqlDbType.DateTime2).Value = (object?)availableFromUtc ?? DBNull.Value;
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
            cmd.Parameters.AddWithValue("@CavityType", cavityType);
            cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OriginType", originType);
            cmd.Parameters.AddWithValue("@ParentId", parentReadyStockId);
            cmd.Parameters.AddWithValue("@OriginReferenceId", originReferenceId);
            var id = await cmd.ExecuteScalarAsync();
            if (id == null || id == DBNull.Value)
                throw new InvalidOperationException("The parent Ready Stock batch was not found.");
            return (int)id;
        }

        // The lineage of a batch: itself and every ancestor, oldest (the sowing batch) first.
        public async Task<List<ReadyStock>> GetLineageAsync(int readyStockId)
        {
            var chain = new List<ReadyStock>();
            int? id = readyStockId;
            var guard = 0;
            while (id.HasValue && guard++ < 20)
            {
                var batch = await GetByIdAsync(id.Value);
                if (batch == null) break;
                chain.Insert(0, batch);
                id = batch.ParentReadyStockId ?? await TransplantSourceOfAsync(batch.SeedSowingId);
            }
            return chain;
        }

        // 2026-10-13: a batch approved from a tray batch that was transplanted from received Ready Stock
        // continues its lineage at that source batch (dbo.ReadyStockTransplants.OutputSeedSowingId).
        private async Task<int?> TransplantSourceOfAsync(int? seedSowingId)
        {
            if (seedSowingId is not > 0) return null;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.TraysToApprovalsAsync(conn)) return null;
            using var cmd = new SqlCommand("SELECT TOP 1 SourceReadyStockId FROM dbo.ReadyStockTransplants WHERE OutputSeedSowingId = @S AND Status = N'Completed'", conn);
            cmd.Parameters.AddWithValue("@S", seedSowingId.Value);
            return await cmd.ExecuteScalarAsync() as int?;
        }

        // Batches derived from this one (received elsewhere / transplanted outputs).
        public async Task<List<ReadyStock>> GetChildrenAsync(int readyStockId)
        {
            var list = new List<ReadyStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE rs.ParentReadyStockId = @Id ORDER BY rs.Id", conn);
            cmd.Parameters.AddWithValue("@Id", readyStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        private static async Task InsertLedgerAsync(SqlConnection conn, SqlTransaction tx, int readyStockId, string transactionType,
            string? referenceType, int? referenceId, decimal quantity, decimal beforeQuantity, int? userId, string? remarks,
            DateTime? transactionUtc = null)
        {
            // TransactionDate = business moment (null = now); CreatedAt = recorded time, always now.
            var cmd = new SqlCommand(@"
INSERT INTO dbo.ReadyStockTransactions
(ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@ReadyStockId, ISNULL(@TransactionDate, SYSUTCDATETIME()), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());", conn, tx);
            cmd.Parameters.Add("@TransactionDate", System.Data.SqlDbType.DateTime2).Value = (object?)transactionUtc ?? DBNull.Value;
            cmd.Parameters.AddWithValue("@ReadyStockId", readyStockId);
            cmd.Parameters.AddWithValue("@TransactionType", transactionType);
            cmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Quantity", quantity);
            cmd.Parameters.AddWithValue("@BeforeQuantity", beforeQuantity);
            cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<ReadyStockTransaction>> GetTransactionsAsync(int readyStockId)
        {
            var list = new List<ReadyStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.ReadyStockId, ps.Name AS SpeciesName, rs.BatchNo, t.TransactionDate, t.TransactionType,
       t.ReferenceType, t.ReferenceId, t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName,
       t.Remarks, t.CreatedAt
FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock rs ON t.ReadyStockId = rs.Id
INNER JOIN dbo.PlantSpecies ps ON rs.SpeciesId = ps.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.ReadyStockId = @Id
ORDER BY t.CreatedAt DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", readyStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new ReadyStockTransaction
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    ReadyStockId = reader.GetInt32(reader.GetOrdinal("ReadyStockId")),
                    SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                    BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                    TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                    TransactionType = reader.GetString(reader.GetOrdinal("TransactionType")),
                    ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                    ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                    Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                    BeforeQuantity = reader.GetDecimal(reader.GetOrdinal("BeforeQuantity")),
                    AfterQuantity = reader.GetDecimal(reader.GetOrdinal("AfterQuantity")),
                    UserId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? null : reader.GetInt32(reader.GetOrdinal("UserId")),
                    UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                    Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
                });
            }
            return list;
        }

        private static ReadyStock Map(SqlDataReader reader)
        {
            return new ReadyStock
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SeedSowingId = reader.GetInt32(reader.GetOrdinal("SeedSowingId")),
                SowingCode = reader.GetString(reader.GetOrdinal("SowingCode")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                AreaType = reader.IsDBNull(reader.GetOrdinal("AreaType")) ? null : reader.GetString(reader.GetOrdinal("AreaType")),
                PolyhouseId = reader.IsDBNull(reader.GetOrdinal("PolyhouseId")) ? null : reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                QuantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown")),
                ApprovedReadyQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity")),
                WastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity")),
                SowingStatus = reader.GetString(reader.GetOrdinal("SowingStatus")),
                ApprovedByName = reader.IsDBNull(reader.GetOrdinal("ApprovedByName")) ? null : reader.GetString(reader.GetOrdinal("ApprovedByName")),
                ApprovalDate = reader.IsDBNull(reader.GetOrdinal("ApprovalDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ApprovalDate")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                CavityType = reader.GetString(reader.GetOrdinal("CavityType")),
                SowingTrays = reader.IsDBNull(reader.GetOrdinal("SowingTrays")) ? null : reader.GetInt32(reader.GetOrdinal("SowingTrays")),
                ReadyTrays = reader.IsDBNull(reader.GetOrdinal("ReadyTrays")) ? null : reader.GetInt32(reader.GetOrdinal("ReadyTrays")),
                SowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                ReservedQuantity = reader.GetDecimal(reader.GetOrdinal("ReservedQuantity")),
                DispatchedQuantity = reader.GetDecimal(reader.GetOrdinal("DispatchedQuantity")),
                InTransitQuantity = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity")),
                OriginType = reader.GetString(reader.GetOrdinal("OriginType")),
                ParentReadyStockId = reader.IsDBNull(reader.GetOrdinal("ParentReadyStockId")) ? null : reader.GetInt32(reader.GetOrdinal("ParentReadyStockId")),
                OriginReferenceId = reader.IsDBNull(reader.GetOrdinal("OriginReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("OriginReferenceId")),
                FirstConfirmationDate = reader.IsDBNull(reader.GetOrdinal("FirstConfirmationDate")) ? null : reader.GetDateTime(reader.GetOrdinal("FirstConfirmationDate")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
