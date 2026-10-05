using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 25 (Phase K): Ready Confirmation -> Ready Stock. The
    // auditable confirmation EVENT (dbo.ReadyConfirmations) that both
    // increments the dedicated dbo.ReadyStock pool (via
    // ReadyStockRepository) and maintains a running total on the
    // parent dbo.SeedSowings row (ConfirmedReadyQuantity) -- mirrors
    // DispatchRepository's own role against dbo.PottedPlantBookings
    // (Phase 21/Phase G's partial-fulfillment pattern) exactly, chosen
    // deliberately over a one-confirmation-per-batch model because
    // this app's own closest precedent (Booking/Dispatch) already
    // supports, and the Phase K spec's own worked examples require,
    // more than one partial confirmation against the same source
    // record (500 sown, 300 then 200 confirmed = fully confirmed
    // *allowed*; 300 then 300 = *rejected*, only 200 remains). See
    // Decision 22 in PROJECT_DOCUMENTATION.md.
    //
    // Deliberately does NOT add a new dbo.SeedSowings.Status value (no
    // 'PartiallyReady'/'ReadyConfirmed') -- ConfirmedReadyQuantity vs
    // QuantitySown alone expresses the partial/full confirmation
    // state, exactly as the spec's own "prefer a confirmed-quantity
    // column over inventing statuses when the existing design already
    // supports it" instruction asks. dbo.SeedSowings.Status therefore
    // stays 'Sown'/'Cancelled' only, completely untouched by this
    // phase -- CK_SeedSowings_Status is never widened.
    //
    // Phase B: a Ready Confirmation is now the SUPERVISOR APPROVAL that
    // closes a sowing batch -- Actual Ready + Wastage (with reason) must
    // equal the sown quantity, and SeedSowings.Status becomes 'Completed'
    // (the Phase K "no new status" decision is superseded; see
    // Database/PhaseB_DirectSowing.sql).
    public class ReadyConfirmationRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly ReadyStockRepository _readyStockRepo;
        // Cutting Tray Sowing: extra cuttings taken at approval / returned on cancel.
        private readonly CuttingStockRepository _cuttingStockRepo;
        // The same extra-overage approval also consumes the corresponding
        // PHYSICAL trays (CEILING-based) from the sowing's Area + Polyhouse + Cavity pool
        // -- a completely separate ledger from the CuttingStock one above,
        // never a replacement for it.
        private readonly TrayStockRepository _trayStockRepo;

        public ReadyConfirmationRepository(
            DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo, ReadyStockRepository readyStockRepo,
            CuttingStockRepository cuttingStockRepo, TrayStockRepository trayStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _readyStockRepo = readyStockRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _trayStockRepo = trayStockRepo;
        }

        private const string BaseSelect = @"
SELECT
    rc.Id, rc.ConfirmationCode, rc.SeedSowingId, sw.SowingCode, rc.ReadyStockId,
    ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    a.Name AS AreaName, sph.Name AS PolyhouseName,   -- only the Polyhouse actually recorded (optional)
    sw.BatchNo, sw.CavityType, sw.NumberOfTrays AS SowingTrays, sw.SowingDate, sw.ExpectedReadyDate, sw.ReadyStockDays,
    sw.QuantitySown, sw.ConfirmedReadyQuantity, sw.WastageQuantity AS SowingWastageQuantity,
    rc.ActualTrayQuantity, rc.ConfirmedQuantity, rc.WastageQuantity, rc.WastageReason, rc.ApprovedById, ab.Name AS ApprovedByName,
    rc.ConfirmationDate, rc.Status,
    rc.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    rc.SupervisorId, sup.Name AS SupervisorName,
    rc.Remarks, rc.CreatedDate, rc.CreatedBy, rc.ModifiedDate, rc.ModifiedBy
FROM dbo.ReadyConfirmations rc
INNER JOIN dbo.SeedSowings sw ON rc.SeedSowingId = sw.Id
INNER JOIN dbo.PlantSpecies ps ON sw.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON sw.AreaId = a.Id
LEFT JOIN dbo.Polyhouses sph ON sw.PolyhouseId = sph.Id
LEFT JOIN dbo.IMSUsers ab ON rc.ApprovedById = ab.Id
LEFT JOIN dbo.IMSUsers r ON rc.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON rc.SupervisorId = sup.Id";

        public async Task<List<ReadyConfirmation>> GetAllAsync()
        {
            var list = new List<ReadyConfirmation>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY rc.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<ReadyConfirmation?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE rc.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<List<ReadyConfirmation>> GetBySeedSowingIdAsync(int seedSowingId)
        {
            var list = new List<ReadyConfirmation>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE rc.SeedSowingId = @SeedSowingId ORDER BY rc.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Phase B -- SUPERVISOR APPROVAL (closes the sowing batch).
        // One atomic transaction:
        //   1) lock the Sowing (UPDLOCK/HOLDLOCK) and re-derive every field
        //      from it -- nothing about the batch is trusted from the form;
        //   2) only a 'Sown' (growing) batch can be approved;
        //   3) DirectSowingRules.ComputeApproval: Wastage = remaining - Ready;
        //      Ready + Wastage must equal what is still to be accounted for,
        //      nothing negative, a Wastage Reason whenever Wastage > 0;
        //   (Area authorization is checked by the page against the AreaId
        //    re-read from the Sowing, before this call.)
        //   4) create/reuse the batch's ONE Ready Stock row (traceable to the
        //      batch, variety, Area, Polyhouse and sowing date) and add the
        //      approved Ready quantity through its ledger ('Confirmed');
        //   5) record the approval (Actual Ready, Wastage, Reason, Approved
        //      By = the logged-in user, Approval Date = now);
        //   6) advance the Sowing's ConfirmedReadyQuantity/WastageQuantity and
        //      set Status = 'Completed' (Ready + Wastage = Sown -- enforced
        //      again by CK_SeedSowings_CompletedAccounted);
        //   7) commit, or roll everything back.
        // The approver may be a different user from the one who sowed.
        // Tray-based approval: the supervisor supplies ONLY the Actual Ready
        // Trays. Cavity, sowing trays and Seeds Used are read from the LOCKED
        // sowing; Actual Ready Seedlings (= trays x sowing cavity) and Wastage
        // (= Seeds Used - seedlings) are calculated here. No seedling,
        // wastage or cavity value is accepted from the caller.
        //
        // CUTTING TRAY SOWING ONLY: a supervisor may report more ready TRAYS than
        // were sown (actualReadyTrays is always TRAYS). The sowing's own cuttings
        // (SownTrays x cavity) were taken from its Cutting Stock pool when it was
        // recorded; only the EXTRA -- (actualReadyTrays - SownTrays) x cavity
        // CUTTINGS (DirectSowingRules.ExtraCuttingsNeeded) -- is taken from that
        // same pool now, under the pool's row lock, from AVAILABLE stock only
        // (Physical - In-Transit, in cuttings). It is refused if the TOTAL ready
        // cuttings (actualReadyTrays x cavity) or the extra cuttings are not covered.
        // It is written to the Cutting Stock ledger as a 'Sown' row (a negative
        // number of CUTTINGS) referencing this approval.
        // canUseSourceArea: may this user use cuttings held in the pool's Area
        // (same rule as recording the sowing); only consulted when there IS an extra.
        public async Task<(bool Success, string? Message, int Id)> ConfirmAsync(
            int seedSowingId, decimal actualReadyTrays, string? wastageReason, int? responsiblePersonId,
            string? remarks, string? createdBy, int? userId, Func<int, bool>? canUseSourceArea = null)
        {
            if (actualReadyTrays <= 0 || !DirectSowingRules.IsWholeNumber(actualReadyTrays))
                return (false, "Actual Ready Trays must be a whole number of at least 1.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the source Sowing.
                var lockCmd = new SqlCommand(
                    "SELECT SpeciesId, AreaId, PolyhouseId, SowingCode, BatchNo, CavityType, NumberOfTrays, SowingDate, QuantitySown, " +
                    "ConfirmedReadyQuantity, WastageQuantity, Status, CreatedById, CreatedBy, SupervisorId, SourceType, SourceCuttingStockId " +
                    "FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", seedSowingId);
                int speciesId, areaId;
                int? polyhouseId;
                string sowingCode, seedLotNo, cavityType, status;
                DateTime sowingDate;
                decimal quantitySown, confirmedSoFar, wastedSoFar;
                int? sowingCreatedById, assignedSupervisorId, sowingTrays, sourceCuttingStockId;
                string? sowingCreatedBy, sowingSourceType;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Seed Sowing record not found.", 0);
                    }
                    speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
                    areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
                    polyhouseId = reader.IsDBNull(reader.GetOrdinal("PolyhouseId")) ? null : reader.GetInt32(reader.GetOrdinal("PolyhouseId"));
                    sowingCode = reader.GetString(reader.GetOrdinal("SowingCode"));
                    seedLotNo = reader.GetString(reader.GetOrdinal("BatchNo"));
                    cavityType = reader.GetString(reader.GetOrdinal("CavityType"));
                    sowingTrays = reader.IsDBNull(reader.GetOrdinal("NumberOfTrays")) ? null : reader.GetInt32(reader.GetOrdinal("NumberOfTrays"));
                    sowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate"));
                    quantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown"));
                    confirmedSoFar = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity"));
                    wastedSoFar = reader.GetDecimal(reader.GetOrdinal("WastageQuantity"));
                    status = reader.GetString(reader.GetOrdinal("Status"));
                    sowingCreatedById = reader.IsDBNull(reader.GetOrdinal("CreatedById")) ? null : reader.GetInt32(reader.GetOrdinal("CreatedById"));
                    sowingCreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy"));
                    assignedSupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId"));
                    sowingSourceType = reader.GetString(reader.GetOrdinal("SourceType"));
                    sourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId"));
                }

                // 2) Eligibility.
                if (status != "Sown")
                {
                    tx.Rollback();
                    return (false, $"This sowing batch is '{status}' and cannot be approved.", 0);
                }
                // Approval authority (under the row lock): ONLY the supervisor
                // assigned to this sowing. A seed sowing's recorder can never
                // approve it; a cutting tray sowing's recorder can, when they
                // are its assigned supervisor.
                var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                    assignedSupervisorId, sowingCreatedById, sowingCreatedBy, userId, createdBy, sowingSourceType);
                if (!mayApprove)
                {
                    tx.Rollback();
                    return (false, authorityError, 0);
                }

                // 3) Tray arithmetic (under the lock), always with the SOWING's
                //    cavity and tray count -- never a value from the browser.
                var (ok, actualTrays, readyQuantity, wastage, _, error) = DirectSowingRules.ComputeTrayApproval(
                    quantitySown, sowingTrays, cavityType, confirmedSoFar, wastedSoFar, actualReadyTrays, wastageReason);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error, 0);
                }
                var reasonToStore = wastage > 0 ? wastageReason : null;

                // 3b) Cutting sowing with MORE ready cuttings than it still expects:
                //     lock the sowing's own Cutting Stock pool and check the extra
                //     against its AVAILABLE quantity, before anything is written.
                var extraCuttings = DirectSowingRules.ExtraCuttingsNeeded(
                    sowingSourceType, actualTrays, cavityType, quantitySown - confirmedSoFar - wastedSoFar);
                if (extraCuttings > 0)
                {
                    if (!sourceCuttingStockId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "This cutting sowing has no Cutting Stock source, so more trays than sown cannot be approved.", 0);
                    }
                    var poolCmd = new SqlCommand(
                        "SELECT AreaId, PhysicalQuantity, InTransitQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                    poolCmd.Parameters.AddWithValue("@Id", sourceCuttingStockId.Value);
                    int poolAreaId; decimal poolPhysical, poolInTransit;
                    using (var poolReader = await poolCmd.ExecuteReaderAsync())
                    {
                        if (!await poolReader.ReadAsync())
                        {
                            poolReader.Close();
                            tx.Rollback();
                            return (false, "The sowing's Cutting Stock record was not found.", 0);
                        }
                        poolAreaId = poolReader.GetInt32(0);
                        poolPhysical = poolReader.GetDecimal(1);
                        poolInTransit = poolReader.GetDecimal(2);
                    }
                    if (canUseSourceArea != null && !canUseSourceArea(poolAreaId))
                    {
                        tx.Rollback();
                        return (false, "You are not authorized to use cuttings from this Area.", 0);
                    }
                    // readyQuantity = ActualReadyTrays x cavity, in cuttings; the pool is in cuttings too
                    var (stockOk, _, stockError) = DirectSowingRules.CheckCuttingOverage(
                        readyQuantity, extraCuttings, poolPhysical, poolInTransit, actualTrays, DirectSowingRules.CavityCount(cavityType));
                    if (!stockOk)
                    {
                        tx.Rollback();
                        return (false, stockError, 0);
                    }
                }

                var confirmationCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "RDY", DateTime.UtcNow.Year);

                // 4) The batch's ONE Ready Stock row (UNIQUE SeedSowingId ->
                // sowing batch number; BatchNo keeps its existing meaning,
                // the seed lot number).
                var readyStockId = await _readyStockRepo.GetOrCreateLockedAsync(
                    conn, tx, seedSowingId, speciesId, areaId, polyhouseId, seedLotNo, cavityType, sowingDate, createdBy);

                // 5) The approval record.
                const string insertSql = @"
INSERT INTO dbo.ReadyConfirmations
(ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, WastageReason, ApprovedById,
 ConfirmationDate, Status, ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@ConfirmationCode, @SeedSowingId, @ReadyStockId, @ActualTrayQuantity, @ConfirmedQuantity, @WastageQuantity, @WastageReason, @ApprovedById,
 SYSUTCDATETIME(), 'Confirmed', @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@ConfirmationCode", confirmationCode);
                cmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
                cmd.Parameters.AddWithValue("@ReadyStockId", readyStockId);
                cmd.Parameters.AddWithValue("@ActualTrayQuantity", actualTrays);
                cmd.Parameters.AddWithValue("@ConfirmedQuantity", readyQuantity);   // = actualTrays x sowing cavity
                cmd.Parameters.AddWithValue("@WastageQuantity", wastage);
                cmd.Parameters.AddWithValue("@WastageReason", (object?)reasonToStore ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ApprovedById", (object?)userId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)responsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)assignedSupervisorId ?? DBNull.Value);   // the sowing's assigned supervisor
                cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                var newId = (int)(await cmd.ExecuteScalarAsync())!;

                // The extra cuttings leave the sowing's Cutting Stock pool, in this
                // same transaction (the ledger/CHECK constraints refuse a negative
                // balance). Only the EXTRA -- the sowing's own cuttings were taken
                // when it was recorded and are never taken again.
                if (extraCuttings > 0)
                {
                    var (cutOk, cutMessage) = await _cuttingStockRepo.RecordTransactionAsync(
                        conn, tx, sourceCuttingStockId!.Value, -extraCuttings, "Sown", "ReadyConfirmation", newId, userId,
                        $"Extra {QuantityFormat.Qty(DirectSowingRules.ExtraTrays(extraCuttings, cavityType))} trays ({QuantityFormat.Qty(extraCuttings)} cuttings) beyond the sown trays: approval of batch {sowingCode}");
                    if (!cutOk)
                    {
                        tx.Rollback();
                        return (false, cutMessage, 0);
                    }

                    // The extra cuttings above also need the matching PHYSICAL
                    // trays, from the sowing's own Area + Polyhouse + Cavity
                    // pool (the same pool the sowing itself used). Seed-source overage never reaches here --
                    // extraCuttings is always 0 for SourceType=Seed, per
                    // DirectSowingRules.ExtraCuttingsNeeded. Only the EXTRA
                    // trays: the sowing's own trays were consumed when it was
                    // recorded and are never taken again. Reuses the existing
                    // ExtraTrays formula and tags the ledger to THIS approval's
                    // own Id, so cancelling just this approval returns exactly
                    // this amount.
                    var requiredExtraTrays = Math.Ceiling(DirectSowingRules.ExtraTrays(extraCuttings, cavityType));
                    var (trayOk, trayMessage) = await _trayStockRepo.ConsumeForSowingAsync(
                        conn, tx, areaId, polyhouseId, cavityType, requiredExtraTrays, "ReadyConfirmation", newId, userId,
                        $"Extra {QuantityFormat.Qty(requiredExtraTrays)} trays ({QuantityFormat.Qty(extraCuttings)} cuttings) beyond the sown trays: approval of batch {sowingCode}");
                    if (!trayOk)
                    {
                        tx.Rollback();
                        return (false, trayMessage, 0);
                    }
                }

                // Ready Stock grows by exactly the approved Ready quantity
                // (no ledger row when the whole batch was wastage).
                if (readyQuantity > 0)
                {
                    var (stockSuccess, stockMessage) = await _readyStockRepo.RecordTransactionAsync(
                        conn, tx, readyStockId, readyQuantity, "Confirmed", "ReadyConfirmation", newId, userId,
                        $"Supervisor Approval of batch {sowingCode}");
                    if (!stockSuccess)
                    {
                        tx.Rollback();
                        return (false, stockMessage, 0);
                    }
                }

                // 6) Close the batch.
                var newReady = confirmedSoFar + readyQuantity;
                var newWastage = wastedSoFar + wastage;
                // >= (not ==): an approval can now legitimately push Ready
                // above QuantitySown (Wastage stays 0 in that case), so the
                // batch must still close instead of getting stuck "Sown".
                var newStatus = newReady + newWastage >= quantitySown ? "Completed" : "Sown";
                var updateSowingCmd = new SqlCommand(@"
UPDATE dbo.SeedSowings
SET ConfirmedReadyQuantity = @Ready, WastageQuantity = @Wastage, Status = @Status,
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateSowingCmd.Parameters.AddWithValue("@Ready", newReady);
                updateSowingCmd.Parameters.AddWithValue("@Wastage", newWastage);
                updateSowingCmd.Parameters.AddWithValue("@Status", newStatus);
                updateSowingCmd.Parameters.AddWithValue("@ModifiedBy", (object?)createdBy ?? DBNull.Value);
                updateSowingCmd.Parameters.AddWithValue("@Id", seedSowingId);
                await updateSowingCmd.ExecuteNonQueryAsync();

                // 7) Commit.
                tx.Commit();
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                // A database backstop (trigger) may already have rolled the
                // transaction back on the server.
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        // Reverses ONE approval: removes exactly its Ready quantity from the
        // batch's Ready Stock ('ReversalRemoval'; refused if that stock is no
        // longer there), takes its Ready and Wastage back off the Sowing and
        // RE-OPENS the batch (Completed -> Sown) so it can be approved again.
        // Never deletes the approval row or the Ready Stock row.
        // Phase D: only the sowing's ASSIGNED supervisor may reverse its
        // approval -- the same person who alone may approve it. No other
        // supervisor and no administrator can undo someone else's approval.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT rc.SeedSowingId, rc.ReadyStockId, rc.ConfirmedQuantity, rc.WastageQuantity, rc.Status, sw.SupervisorId AS SowingSupervisorId, " +
                    "sw.SourceType AS SowingSourceType, sw.SourceCuttingStockId AS SowingCuttingStockId " +
                    "FROM dbo.ReadyConfirmations rc WITH (UPDLOCK, HOLDLOCK) INNER JOIN dbo.SeedSowings sw ON sw.Id = rc.SeedSowingId WHERE rc.Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                int seedSowingId, readyStockId;
                decimal confirmedQuantity, wastageQuantity;
                string status;
                int? sowingSupervisorId, sowingCuttingStockId;
                string sowingSourceType;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Ready Confirmation record not found.");
                    }
                    seedSowingId = reader.GetInt32(reader.GetOrdinal("SeedSowingId"));
                    readyStockId = reader.GetInt32(reader.GetOrdinal("ReadyStockId"));
                    confirmedQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity"));
                    wastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity"));
                    status = reader.GetString(reader.GetOrdinal("Status"));
                    sowingSupervisorId = reader.IsDBNull(reader.GetOrdinal("SowingSupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SowingSupervisorId"));
                    sowingSourceType = reader.GetString(reader.GetOrdinal("SowingSourceType"));
                    sowingCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SowingCuttingStockId")) ? null : reader.GetInt32(reader.GetOrdinal("SowingCuttingStockId"));
                }

                if (!userId.HasValue || sowingSupervisorId != userId)
                {
                    tx.Rollback();
                    return (false, "Only the Sowing Supervisor assigned to this sowing can cancel its approval.");
                }
                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Ready Confirmation is already Cancelled.");
                }

                var sowingLockCmd = new SqlCommand(
                    "SELECT ConfirmedReadyQuantity, WastageQuantity, Status FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                sowingLockCmd.Parameters.AddWithValue("@Id", seedSowingId);
                decimal confirmedSoFar, wastedSoFar;
                string sowingStatus;
                using (var reader = await sowingLockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Parent Seed Sowing no longer exists.");
                    }
                    confirmedSoFar = reader.GetDecimal(0);
                    wastedSoFar = reader.GetDecimal(1);
                    sowingStatus = reader.GetString(2);
                }
                if (sowingStatus == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "The parent sowing is Cancelled; this approval cannot be reversed.");
                }
                if (confirmedQuantity > confirmedSoFar || wastageQuantity > wastedSoFar)
                {
                    tx.Rollback();
                    return (false, "The sowing totals are inconsistent with this approval; reversal refused.");
                }

                if (confirmedQuantity > 0)
                {
                    var (reversalSuccess, reversalMessage) = await _readyStockRepo.RecordTransactionAsync(
                        conn, tx, readyStockId, -confirmedQuantity, "ReversalRemoval", "ReadyConfirmation", id, userId, "Supervisor Approval cancelled");
                    if (!reversalSuccess)
                    {
                        tx.Rollback();
                        return (false, reversalMessage);
                    }
                }

                // Cutting Tray Sowing: give back ONLY the extra CUTTINGS (not trays) this
                // approval took at approval time (never the sowing's own cuttings -- those
                // return only if the sowing itself is cancelled). The amount is read
                // from the Cutting Stock ledger under this approval's row lock, as
                // (taken - already returned) for this approval and pool, so it can
                // never be returned twice; nothing at all for seed sowings or for a
                // cutting approval that took no extra.
                if (sowingSourceType == SeedSowing.SourceCutting && sowingCuttingStockId.HasValue)
                {
                    var netCmd = new SqlCommand(@"
SELECT ISNULL(-SUM(Quantity), 0) FROM dbo.CuttingStockTransactions
WHERE CuttingStockId = @Pool AND ReferenceType = N'ReadyConfirmation' AND ReferenceId = @Id
  AND TransactionType IN (N'Sown', N'ReversalReturn')", conn, tx);
                    netCmd.Parameters.AddWithValue("@Pool", sowingCuttingStockId.Value);
                    netCmd.Parameters.AddWithValue("@Id", id);
                    var extraToReturn = Convert.ToDecimal(await netCmd.ExecuteScalarAsync());
                    if (extraToReturn > 0)
                    {
                        var (returnOk, returnMessage) = await _cuttingStockRepo.RecordTransactionAsync(
                            conn, tx, sowingCuttingStockId.Value, extraToReturn, "ReversalReturn", "ReadyConfirmation", id, userId,
                            "Supervisor Approval cancelled: extra cuttings returned");
                        if (!returnOk)
                        {
                            tx.Rollback();
                            return (false, returnMessage);
                        }
                    }
                }

                // Return exactly the physical trays THIS approval consumed (if
                // any) to the same Area + Polyhouse + Cavity pool -- same generic method the
                // sowing cancellation uses, scoped to ReferenceType=
                // "ReadyConfirmation" so it never touches the sowing's own
                // original tray consumption or any other approval's. A clean
                // no-op when this approval never consumed trays (no overage,
                // or a Seed source).
                var (trayReversalOk, trayReversalMessage) = await _trayStockRepo.ReverseConsumptionAsync(
                    conn, tx, "ReadyConfirmation", id, userId, "Supervisor Approval cancelled: extra trays returned");
                if (!trayReversalOk)
                {
                    tx.Rollback();
                    return (false, trayReversalMessage);
                }

                var updateConfirmationCmd = new SqlCommand(
                    "UPDATE dbo.ReadyConfirmations SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateConfirmationCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateConfirmationCmd.Parameters.AddWithValue("@Id", id);
                await updateConfirmationCmd.ExecuteNonQueryAsync();

                var updateSowingCmd = new SqlCommand(@"
UPDATE dbo.SeedSowings
SET ConfirmedReadyQuantity = @Ready, WastageQuantity = @Wastage, Status = 'Sown',
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateSowingCmd.Parameters.AddWithValue("@Ready", confirmedSoFar - confirmedQuantity);
                updateSowingCmd.Parameters.AddWithValue("@Wastage", wastedSoFar - wastageQuantity);
                updateSowingCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateSowingCmd.Parameters.AddWithValue("@Id", seedSowingId);
                await updateSowingCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static ReadyConfirmation Map(SqlDataReader reader)
        {
            return new ReadyConfirmation
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                ConfirmationCode = reader.GetString(reader.GetOrdinal("ConfirmationCode")),
                SeedSowingId = reader.GetInt32(reader.GetOrdinal("SeedSowingId")),
                SowingCode = reader.GetString(reader.GetOrdinal("SowingCode")),
                ReadyStockId = reader.GetInt32(reader.GetOrdinal("ReadyStockId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                CavityType = reader.GetString(reader.GetOrdinal("CavityType")),
                SowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate")),
                ExpectedReadyDate = reader.IsDBNull(reader.GetOrdinal("ExpectedReadyDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ExpectedReadyDate")),
                ReadyStockDays = reader.IsDBNull(reader.GetOrdinal("ReadyStockDays")) ? null : reader.GetInt32(reader.GetOrdinal("ReadyStockDays")),
                QuantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown")),
                ConfirmedReadyQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity")),
                ActualTrayQuantity = reader.IsDBNull(reader.GetOrdinal("ActualTrayQuantity")) ? null : reader.GetInt32(reader.GetOrdinal("ActualTrayQuantity")),
                SowingTrays = reader.IsDBNull(reader.GetOrdinal("SowingTrays")) ? null : reader.GetInt32(reader.GetOrdinal("SowingTrays")),
                ConfirmedQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity")),
                WastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity")),
                WastageReason = reader.IsDBNull(reader.GetOrdinal("WastageReason")) ? null : reader.GetString(reader.GetOrdinal("WastageReason")),
                ApprovedById = reader.IsDBNull(reader.GetOrdinal("ApprovedById")) ? null : reader.GetInt32(reader.GetOrdinal("ApprovedById")),
                ApprovedByName = reader.IsDBNull(reader.GetOrdinal("ApprovedByName")) ? null : reader.GetString(reader.GetOrdinal("ApprovedByName")),
                SowingWastageQuantity = reader.GetDecimal(reader.GetOrdinal("SowingWastageQuantity")),
                ConfirmationDate = reader.GetDateTime(reader.GetOrdinal("ConfirmationDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
