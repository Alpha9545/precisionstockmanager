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

        public ReadyConfirmationRepository(
            DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo, ReadyStockRepository readyStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _readyStockRepo = readyStockRepo;
        }

        // Phase 5: LEFT JOINs both dbo.SeedSowings and dbo.CuttingSowings and
        // COALESCEs every sowing-derived column, mirroring
        // ReadyStockRepository.BaseSelect exactly -- exactly one of the two
        // ever matches a given row (CK_ReadyConfirmations_SourceType).
        private const string BaseSelect = @"
SELECT
    rc.Id, rc.ConfirmationCode, rc.SeedSowingId, rc.CuttingSowingId, COALESCE(sw.SowingCode, cw.SowingCode) AS SowingCode, rc.ReadyStockId,
    ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    a.Name AS AreaName, COALESCE(sph.Name, cph.Name) AS PolyhouseName,   -- only the Polyhouse actually recorded (optional)
    COALESCE(sw.BatchNo, cw.SowingCode) AS BatchNo,
    COALESCE(sw.CavityType, cw.CavityType) AS CavityType,
    COALESCE(sw.NumberOfTrays, cw.NumberOfTrays) AS SowingTrays,
    COALESCE(sw.SowingDate, cw.SowingDate) AS SowingDate,
    COALESCE(sw.ExpectedReadyDate, cw.ExpectedReadyDate) AS ExpectedReadyDate,
    COALESCE(sw.ReadyStockDays, cw.ReadyStockDays) AS ReadyStockDays,
    COALESCE(sw.QuantitySown, cw.QuantitySown) AS QuantitySown,
    COALESCE(sw.ConfirmedReadyQuantity, cw.ConfirmedReadyQuantity) AS ConfirmedReadyQuantity,
    COALESCE(sw.WastageQuantity, cw.WastageQuantity) AS SowingWastageQuantity,
    rc.ActualTrayQuantity, rc.ConfirmedQuantity, rc.WastageQuantity, rc.WastageReason, rc.ApprovedById, ab.Name AS ApprovedByName,
    rc.ConfirmationDate, rc.Status,
    rc.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    rc.SupervisorId, sup.Name AS SupervisorName,
    rc.Remarks, rc.CreatedDate, rc.CreatedBy, rc.ModifiedDate, rc.ModifiedBy
FROM dbo.ReadyConfirmations rc
LEFT JOIN dbo.SeedSowings sw ON rc.SeedSowingId = sw.Id
LEFT JOIN dbo.CuttingSowings cw ON rc.CuttingSowingId = cw.Id
INNER JOIN dbo.PlantSpecies ps ON COALESCE(sw.SpeciesId, cw.SpeciesId) = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON COALESCE(sw.AreaId, cw.AreaId) = a.Id
LEFT JOIN dbo.Polyhouses ph ON a.PolyhouseId = ph.Id
LEFT JOIN dbo.Polyhouses sph ON sw.PolyhouseId = sph.Id
LEFT JOIN dbo.Polyhouses cph ON a.PolyhouseId = cph.Id
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

        public async Task<List<ReadyConfirmation>> GetByCuttingSowingIdAsync(int cuttingSowingId)
        {
            var list = new List<ReadyConfirmation>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE rc.CuttingSowingId = @CuttingSowingId ORDER BY rc.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@CuttingSowingId", cuttingSowingId);
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
        public async Task<(bool Success, string? Message, int Id)> ConfirmAsync(
            int seedSowingId, decimal actualReadyTrays, string? wastageReason, int? responsiblePersonId,
            string? remarks, string? createdBy, int? userId)
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
                    "ConfirmedReadyQuantity, WastageQuantity, Status, CreatedById, CreatedBy, SupervisorId " +
                    "FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", seedSowingId);
                int speciesId, areaId;
                int? polyhouseId;
                string sowingCode, seedLotNo, cavityType, status;
                DateTime sowingDate;
                decimal quantitySown, confirmedSoFar, wastedSoFar;
                int? sowingCreatedById, assignedSupervisorId, sowingTrays;
                string? sowingCreatedBy;
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
                }

                // 2) Eligibility.
                if (status != "Sown")
                {
                    tx.Rollback();
                    return (false, $"This sowing batch is '{status}' and cannot be approved.", 0);
                }
                // Approval authority (under the row lock): ONLY the supervisor
                // assigned to this sowing, and never the person who recorded it.
                var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                    assignedSupervisorId, sowingCreatedById, sowingCreatedBy, userId, createdBy);
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
                var newStatus = newReady + newWastage == quantitySown ? "Completed" : "Sown";
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

        // Phase 5: the Cutting Sowing twin of ConfirmAsync above -- IDENTICAL
        // logic (lock the source sowing, check eligibility/authority, tray
        // arithmetic via DirectSowingRules.ComputeTrayApproval, get-or-create
        // the batch's Ready Stock row, record the approval, close the
        // batch), just reading/writing dbo.CuttingSowings and
        // ReadyConfirmations.CuttingSowingId instead of dbo.SeedSowings/
        // SeedSowingId. No database trigger backstops this path (see
        // Database/Phase30_CuttingSowing.sql's header comment) -- every
        // rule here is enforced in this method, under the sowing's own
        // row lock, exactly like every other Phase 1-4 approval rule in
        // this codebase.
        public async Task<(bool Success, string? Message, int Id)> ConfirmCuttingSowingAsync(
            int cuttingSowingId, decimal actualReadyTrays, string? wastageReason, string? remarks, string? createdBy, int? userId)
        {
            if (actualReadyTrays <= 0 || !DirectSowingRules.IsWholeNumber(actualReadyTrays))
                return (false, "Actual Ready Trays must be a whole number of at least 1.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SpeciesId, AreaId, SowingCode, CavityType, NumberOfTrays, SowingDate, QuantitySown, " +
                    "ConfirmedReadyQuantity, WastageQuantity, Status, CreatedById, CreatedBy, SupervisorId " +
                    "FROM dbo.CuttingSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", cuttingSowingId);
                int speciesId, areaId;
                string sowingCode, cavityType, status;
                DateTime sowingDate;
                decimal quantitySown, confirmedSoFar, wastedSoFar;
                int? sowingCreatedById, assignedSupervisorId, sowingTrays;
                string? sowingCreatedBy;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Cutting Sowing record not found.", 0);
                    }
                    speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
                    areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
                    sowingCode = reader.GetString(reader.GetOrdinal("SowingCode"));
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
                }

                if (status != "Sown")
                {
                    tx.Rollback();
                    return (false, $"This Cutting Sowing batch is '{status}' and cannot be approved.", 0);
                }
                // Approval authority: the EXISTING assigned-supervisor rule,
                // unchanged -- only the supervisor assigned to this sowing,
                // never the person who recorded it.
                var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                    assignedSupervisorId, sowingCreatedById, sowingCreatedBy, userId, createdBy);
                if (!mayApprove)
                {
                    tx.Rollback();
                    return (false, authorityError, 0);
                }

                // Tray arithmetic (under the lock), always with the SOWING's
                // own cavity and tray count -- never a value from the browser.
                var (ok, actualTrays, readyQuantity, wastage, _, error) = DirectSowingRules.ComputeTrayApproval(
                    quantitySown, sowingTrays, cavityType, confirmedSoFar, wastedSoFar, actualReadyTrays, wastageReason);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error, 0);
                }
                var reasonToStore = wastage > 0 ? wastageReason : null;

                var confirmationCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "RDY", DateTime.UtcNow.Year);

                var readyStockId = await _readyStockRepo.GetOrCreateLockedFromCuttingSowingAsync(
                    conn, tx, cuttingSowingId, speciesId, areaId, polyhouseId: null, sowingCode, cavityType, sowingDate, createdBy);

                const string insertSql = @"
INSERT INTO dbo.ReadyConfirmations
(ConfirmationCode, CuttingSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, WastageReason, ApprovedById,
 ConfirmationDate, Status, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@ConfirmationCode, @CuttingSowingId, @ReadyStockId, @ActualTrayQuantity, @ConfirmedQuantity, @WastageQuantity, @WastageReason, @ApprovedById,
 SYSUTCDATETIME(), 'Confirmed', @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@ConfirmationCode", confirmationCode);
                cmd.Parameters.AddWithValue("@CuttingSowingId", cuttingSowingId);
                cmd.Parameters.AddWithValue("@ReadyStockId", readyStockId);
                cmd.Parameters.AddWithValue("@ActualTrayQuantity", actualTrays);
                cmd.Parameters.AddWithValue("@ConfirmedQuantity", readyQuantity);
                cmd.Parameters.AddWithValue("@WastageQuantity", wastage);
                cmd.Parameters.AddWithValue("@WastageReason", (object?)reasonToStore ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ApprovedById", (object?)userId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)assignedSupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                var newId = (int)(await cmd.ExecuteScalarAsync())!;

                if (readyQuantity > 0)
                {
                    var (stockSuccess, stockMessage) = await _readyStockRepo.RecordTransactionAsync(
                        conn, tx, readyStockId, readyQuantity, "Confirmed", "ReadyConfirmation", newId, userId,
                        $"Supervisor Approval of Cutting Sowing batch {sowingCode}");
                    if (!stockSuccess)
                    {
                        tx.Rollback();
                        return (false, stockMessage, 0);
                    }
                }

                var newReady = confirmedSoFar + readyQuantity;
                var newWastage = wastedSoFar + wastage;
                var newStatus = newReady + newWastage == quantitySown ? "Completed" : "Sown";
                var updateSowingCmd = new SqlCommand(@"
UPDATE dbo.CuttingSowings
SET ConfirmedReadyQuantity = @Ready, WastageQuantity = @Wastage, Status = @Status,
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateSowingCmd.Parameters.AddWithValue("@Ready", newReady);
                updateSowingCmd.Parameters.AddWithValue("@Wastage", newWastage);
                updateSowingCmd.Parameters.AddWithValue("@Status", newStatus);
                updateSowingCmd.Parameters.AddWithValue("@ModifiedBy", (object?)createdBy ?? DBNull.Value);
                updateSowingCmd.Parameters.AddWithValue("@Id", cuttingSowingId);
                await updateSowingCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        // Reverses ONE approval: removes exactly its Ready quantity from the
        // batch's Ready Stock ('ReversalRemoval'; refused if that stock is no
        // longer there), takes its Ready and Wastage back off the Sowing and
        // RE-OPENS the batch (Completed -> Sown) so it can be approved again.
        // Never deletes the approval row or the Ready Stock row.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SeedSowingId, CuttingSowingId, ReadyStockId, ConfirmedQuantity, WastageQuantity, Status FROM dbo.ReadyConfirmations WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                int? seedSowingId, cuttingSowingId;
                int readyStockId;
                decimal confirmedQuantity, wastageQuantity;
                string status;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Ready Confirmation record not found.");
                    }
                    seedSowingId = reader.IsDBNull(reader.GetOrdinal("SeedSowingId")) ? null : reader.GetInt32(reader.GetOrdinal("SeedSowingId"));
                    cuttingSowingId = reader.IsDBNull(reader.GetOrdinal("CuttingSowingId")) ? null : reader.GetInt32(reader.GetOrdinal("CuttingSowingId"));
                    readyStockId = reader.GetInt32(reader.GetOrdinal("ReadyStockId"));
                    confirmedQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity"));
                    wastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity"));
                    status = reader.GetString(reader.GetOrdinal("Status"));
                }

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Ready Confirmation is already Cancelled.");
                }

                // Phase 5: exactly one of SeedSowingId/CuttingSowingId is set
                // (CK_ReadyConfirmations_SourceType) -- lock and update
                // whichever parent table actually produced this row.
                var sowingTable = cuttingSowingId.HasValue ? "dbo.CuttingSowings" : "dbo.SeedSowings";
                var parentSowingId = cuttingSowingId ?? seedSowingId!.Value;

                var sowingLockCmd = new SqlCommand(
                    $"SELECT ConfirmedReadyQuantity, WastageQuantity, Status, SupervisorId FROM {sowingTable} WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                sowingLockCmd.Parameters.AddWithValue("@Id", parentSowingId);
                decimal confirmedSoFar, wastedSoFar;
                string sowingStatus;
                int? sowingSupervisorId;
                using (var reader = await sowingLockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, cuttingSowingId.HasValue ? "Parent Cutting Sowing no longer exists." : "Parent Seed Sowing no longer exists.");
                    }
                    confirmedSoFar = reader.GetDecimal(0);
                    wastedSoFar = reader.GetDecimal(1);
                    sowingStatus = reader.GetString(2);
                    sowingSupervisorId = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                }
                // Phase 1 (F2): only the sowing's assigned supervisor may
                // cancel its approval -- checked under the sowing's row lock.
                // Applies identically to both sources -- the existing rule,
                // never a new one.
                var (mayCancel, cancelError) = DirectSowingRules.CanCancelApproval(sowingSupervisorId, userId);
                if (!mayCancel)
                {
                    tx.Rollback();
                    return (false, cancelError);
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

                var updateConfirmationCmd = new SqlCommand(
                    "UPDATE dbo.ReadyConfirmations SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateConfirmationCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateConfirmationCmd.Parameters.AddWithValue("@Id", id);
                await updateConfirmationCmd.ExecuteNonQueryAsync();

                var updateSowingCmd = new SqlCommand($@"
UPDATE {sowingTable}
SET ConfirmedReadyQuantity = @Ready, WastageQuantity = @Wastage, Status = 'Sown',
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateSowingCmd.Parameters.AddWithValue("@Ready", confirmedSoFar - confirmedQuantity);
                updateSowingCmd.Parameters.AddWithValue("@Wastage", wastedSoFar - wastageQuantity);
                updateSowingCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateSowingCmd.Parameters.AddWithValue("@Id", parentSowingId);
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
                SeedSowingId = reader.IsDBNull(reader.GetOrdinal("SeedSowingId")) ? null : reader.GetInt32(reader.GetOrdinal("SeedSowingId")),
                CuttingSowingId = reader.IsDBNull(reader.GetOrdinal("CuttingSowingId")) ? null : reader.GetInt32(reader.GetOrdinal("CuttingSowingId")),
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
