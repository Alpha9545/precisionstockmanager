using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

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

        private const string BaseSelect = @"
SELECT
    rc.Id, rc.ConfirmationCode, rc.SeedSowingId, sw.SowingCode, rc.ReadyStockId,
    ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    a.Name AS AreaName, ph.Name AS PolyhouseName,
    sw.BatchNo, sw.CavityType, sw.SowingDate, sw.ExpectedReadyDate, sw.ReadyStockDays,
    sw.QuantitySown, sw.ConfirmedReadyQuantity,
    rc.ConfirmedQuantity, rc.ConfirmationDate, rc.Status,
    rc.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    rc.SupervisorId, sup.Name AS SupervisorName,
    rc.Remarks, rc.CreatedDate, rc.CreatedBy, rc.ModifiedDate, rc.ModifiedBy
FROM dbo.ReadyConfirmations rc
INNER JOIN dbo.SeedSowings sw ON rc.SeedSowingId = sw.Id
INNER JOIN dbo.PlantSpecies ps ON sw.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON sw.AreaId = a.Id
LEFT JOIN dbo.Polyhouses ph ON a.PolyhouseId = ph.Id
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

        // The atomic Ready Confirmation transaction (Phase K spec,
        // "transaction safety" section):
        //   1) Lock/re-fetch the source Sowing record
        //   2) Validate eligible for Ready Confirmation
        //   3) Validate requested Ready Quantity
        //   (4: Area authorization happens at the PAGE level BEFORE
        //      this call, using AreaAccessService against the AreaId
        //      re-fetched via SeedSowingRepository.GetByIdAsync -- this
        //      method never receives or trusts a posted AreaId at all,
        //      mirroring how SeedSowing/Edit.cshtml.cs splits page-level
        //      Area authorization from repository-level stock
        //      integrity. See Pages/Production/ReadyConfirmation/
        //      Confirm.cshtml.cs.)
        //   5) Create/update the Ready Stock record
        //   6) Create the corresponding stock ledger/transaction record
        //   7) Update the source Sowing's ConfirmedReadyQuantity
        //   8) Commit (or ROLLBACK on any failure -- no partial stock
        //      movement)
        public async Task<(bool Success, string? Message, int Id)> ConfirmAsync(
            int seedSowingId, decimal readyQuantity, int? responsiblePersonId, int? supervisorId,
            string? remarks, string? createdBy, int? userId)
        {
            if (readyQuantity <= 0)
                return (false, "Ready Quantity must be greater than zero.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the source Sowing and re-derive EVERY field
                // from it -- never trust anything posted for these.
                var lockCmd = new SqlCommand(
                    "SELECT SpeciesId, AreaId, BatchNo, CavityType, SowingDate, QuantitySown, ConfirmedReadyQuantity, Status " +
                    "FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", seedSowingId);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Seed Sowing record not found.", 0);
                }
                var speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
                var areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
                var batchNo = reader.GetString(reader.GetOrdinal("BatchNo"));
                var cavityType = reader.GetString(reader.GetOrdinal("CavityType"));
                var sowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate"));
                var quantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown"));
                var confirmedSoFar = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                // 2) Eligibility -- a Cancelled Sowing can never be
                // Ready-Confirmed (reused from every other cancellable
                // production event in this app).
                if (status != "Sown")
                {
                    tx.Rollback();
                    return (false, $"This Seed Sowing is '{status}' and cannot be Ready-Confirmed.", 0);
                }

                // 3) Quantity -- must not exceed what is genuinely still
                // remaining, validated under THIS lock so a concurrent
                // confirmation against the same Sowing cannot race it
                // (mirrors DispatchRepository.InsertAsync's own
                // remaining-quantity check against its Booking lock).
                var remaining = quantitySown - confirmedSoFar;
                if (readyQuantity > remaining)
                {
                    tx.Rollback();
                    return (false, $"Ready Quantity ({readyQuantity:N2}) exceeds this Sowing's remaining un-confirmed quantity ({remaining:N2}).", 0);
                }

                var confirmationCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "RDY", DateTime.UtcNow.Year);

                // 5) The ONE ReadyStock pool row for this Sowing --
                // created on first confirmation, reused on every
                // subsequent partial confirmation.
                var readyStockId = await _readyStockRepo.GetOrCreateLockedAsync(
                    conn, tx, seedSowingId, speciesId, areaId, batchNo, cavityType, sowingDate, createdBy);

                // Insert the confirmation header FIRST so its Id is
                // available as ReferenceId on the ledger entry (mirrors
                // DispatchRepository.InsertAsync's own ordering).
                const string insertSql = @"
INSERT INTO dbo.ReadyConfirmations
(ConfirmationCode, SeedSowingId, ReadyStockId, ConfirmedQuantity, ConfirmationDate, Status,
 ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@ConfirmationCode, @SeedSowingId, @ReadyStockId, @ConfirmedQuantity, SYSUTCDATETIME(), 'Confirmed',
 @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@ConfirmationCode", confirmationCode);
                cmd.Parameters.AddWithValue("@SeedSowingId", seedSowingId);
                cmd.Parameters.AddWithValue("@ReadyStockId", readyStockId);
                cmd.Parameters.AddWithValue("@ConfirmedQuantity", readyQuantity);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)responsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)supervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                var newId = (int)await cmd.ExecuteScalarAsync();

                // 6) The Ready Stock pool actually grows by exactly this
                // confirmation's quantity.
                var (stockSuccess, stockMessage) = await _readyStockRepo.RecordTransactionAsync(
                    conn, tx, readyStockId, readyQuantity, "Confirmed", "ReadyConfirmation", newId, userId, remarks);
                if (!stockSuccess)
                {
                    tx.Rollback();
                    return (false, stockMessage, 0);
                }

                // 7) The parent Sowing's running total advances -- never
                // its Status (see class header comment).
                var updateSowingCmd = new SqlCommand(
                    "UPDATE dbo.SeedSowings SET ConfirmedReadyQuantity = @ConfirmedReadyQuantity, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateSowingCmd.Parameters.AddWithValue("@ConfirmedReadyQuantity", confirmedSoFar + readyQuantity);
                updateSowingCmd.Parameters.AddWithValue("@ModifiedBy", (object?)createdBy ?? DBNull.Value);
                updateSowingCmd.Parameters.AddWithValue("@Id", seedSowingId);
                await updateSowingCmd.ExecuteNonQueryAsync();

                // 8) Commit.
                tx.Commit();
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Reverses ONE Ready Confirmation: removes exactly its own
        // quantity from the Ready Stock pool ('ReversalRemoval' --
        // Decision 15's naming convention; there is no source pool to
        // credit back to here, only an addition being undone) and
        // restores the parent Sowing's ConfirmedReadyQuantity by the
        // same amount. Never deletes the row, and never deletes the
        // underlying ReadyStock pool row either (only its Quantity
        // moves). Mirrors DispatchRepository.CancelAsync's own "lock
        // both the child and the parent" discipline.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SeedSowingId, ReadyStockId, ConfirmedQuantity, Status FROM dbo.ReadyConfirmations WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Ready Confirmation record not found.");
                }
                var seedSowingId = reader.GetInt32(reader.GetOrdinal("SeedSowingId"));
                var readyStockId = reader.GetInt32(reader.GetOrdinal("ReadyStockId"));
                var confirmedQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Ready Confirmation is already Cancelled.");
                }

                // Lock the parent Sowing too -- ConfirmedReadyQuantity is
                // about to be read-then-written, and a concurrent NEW
                // confirmation against the same Sowing must not race
                // this reversal.
                var sowingLockCmd = new SqlCommand(
                    "SELECT ConfirmedReadyQuantity FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                sowingLockCmd.Parameters.AddWithValue("@Id", seedSowingId);
                var confirmedSoFarObj = await sowingLockCmd.ExecuteScalarAsync();
                if (confirmedSoFarObj == null || confirmedSoFarObj == DBNull.Value)
                {
                    tx.Rollback();
                    return (false, "Parent Seed Sowing no longer exists.");
                }
                var confirmedSoFar = (decimal)confirmedSoFarObj;

                var (reversalSuccess, reversalMessage) = await _readyStockRepo.RecordTransactionAsync(
                    conn, tx, readyStockId, -confirmedQuantity, "ReversalRemoval", "ReadyConfirmation", id, userId, "Ready Confirmation cancelled");
                if (!reversalSuccess)
                {
                    tx.Rollback();
                    return (false, reversalMessage);
                }

                var updateConfirmationCmd = new SqlCommand(
                    "UPDATE dbo.ReadyConfirmations SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateConfirmationCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateConfirmationCmd.Parameters.AddWithValue("@Id", id);
                await updateConfirmationCmd.ExecuteNonQueryAsync();

                var newConfirmedTotal = confirmedSoFar - confirmedQuantity;
                var updateSowingCmd = new SqlCommand(
                    "UPDATE dbo.SeedSowings SET ConfirmedReadyQuantity = @ConfirmedReadyQuantity, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateSowingCmd.Parameters.AddWithValue("@ConfirmedReadyQuantity", newConfirmedTotal < 0 ? 0 : newConfirmedTotal);
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
                ConfirmedQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity")),
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
