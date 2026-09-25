using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 5: Cutting Stock -> Cutting Sowing. Mirrors SeedSowingRepository's
    // shape (InsertAsync/CancelAsync/UpdateDetailsAsync, single-actor
    // production event, no confirm/reject workflow) but consumes
    // dbo.CuttingStock instead of dbo.SeedStock -- a deliberately separate
    // table, never mixed with dbo.SeedSowings (see Database/
    // Phase30_CuttingSowing.sql). Simpler than SeedSowingRepository in one
    // respect: there is no separate "growing Area" to resolve -- a Cutting
    // Sowing is always sown IN PLACE at its source Cutting Stock pool's own
    // Area (same rule PotProduction/CreateFromCutting.cshtml.cs already
    // uses), so Area is preserved automatically, never re-chosen.
    public class CuttingSowingRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly UserRoleRepository _userRoleRepo;

        public CuttingSowingRepository(
            DatabaseHelper dbHelper,
            BatchNumberRepository batchNumberRepo,
            CuttingStockRepository cuttingStockRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            UserRoleRepository userRoleRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _userRoleRepo = userRoleRepo;
        }

        public static IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;

        private const string BaseSelect = @"
SELECT
    cw.Id, cw.SowingCode, cw.SourceCuttingStockId, cw.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    cw.AreaId, a.Name AS AreaName, gp.Name AS GrowingPartnerName, ph.Name AS PolyhouseName,
    cw.CavityType, cw.NumberOfTrays, cw.QuantitySown, cw.CuttingQuantityEntered, cw.WastageQuantity,
    cw.SowingDate, cw.ReadyStockDays, cw.ExpectedReadyDate, cw.Status, cw.ConfirmedReadyQuantity,
    cw.SupervisorId, sup.Name AS SupervisorName,
    cw.Remarks, cw.CreatedDate, cw.CreatedBy, cw.CreatedById, cw.ModifiedDate, cw.ModifiedBy,
    cs.AvailableQuantity AS SourceAvailableQuantity
FROM dbo.CuttingSowings cw
INNER JOIN dbo.PlantSpecies ps ON cw.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON cw.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
LEFT JOIN dbo.Polyhouses ph ON a.PolyhouseId = ph.Id
INNER JOIN dbo.CuttingStock cs ON cw.SourceCuttingStockId = cs.Id
LEFT JOIN dbo.IMSUsers sup ON cw.SupervisorId = sup.Id";

        public async Task<List<CuttingSowing>> GetAllAsync(string? status = null)
        {
            var list = new List<CuttingSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE (@Status IS NULL OR cw.Status = @Status) ORDER BY cw.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<CuttingSowing?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE cw.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<List<CuttingSowing>> GetByAreaAsync(int areaId)
        {
            var list = new List<CuttingSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE cw.AreaId = @AreaId ORDER BY cw.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Ready Alerts / Management Dashboard candidate list -- mirrors
        // SeedSowingRepository.GetAlertCandidatesAsync exactly (same SQL
        // shape, same DirectSowingRules-independent classification via
        // the shared static SeedSowingRepository.ClassifyReadyAlert,
        // which takes only primitive status/date/window arguments and is
        // reused unchanged for either sowing type).
        public async Task<List<CuttingSowing>> GetAlertCandidatesAsync(DateTime horizon)
        {
            var list = new List<CuttingSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE cw.Status = 'Sown' AND cw.ExpectedReadyDate IS NOT NULL AND cw.ExpectedReadyDate <= @Horizon
      AND cw.ConfirmedReadyQuantity + cw.WastageQuantity < cw.QuantitySown
ORDER BY cw.ExpectedReadyDate";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Horizon", horizon.Date);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Active ('Sown') Cutting Sowings that still have remaining
        // un-confirmed quantity -- the Cutting Sowing Approval queue.
        // Mirrors SeedSowingRepository.GetReadyForConfirmationAsync.
        public async Task<List<CuttingSowing>> GetReadyForConfirmationAsync()
        {
            var list = new List<CuttingSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE cw.Status = 'Sown' AND cw.QuantitySown - cw.ConfirmedReadyQuantity - cw.WastageQuantity > 0
ORDER BY cw.SowingDate";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Sows cuttings from an existing Cutting Stock pool, IN PLACE at
        // that pool's own Area (never a separately-chosen Area). Only the
        // cuttings that fill COMPLETE trays (entry.CuttingQuantityEntered
        // -> DirectSowingRules.CalculateTrays) are actually sown/deducted;
        // the rest stay in the Cutting Stock pool -- same rule as Phase 4's
        // Cutting Delivery, no "remaining" concept surfaced anywhere.
        // Nothing about quantity/cavity/trays is ever trusted from the
        // caller -- the server recalculates from scratch under the source
        // pool's own row lock.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(CuttingSowing entry, int? userId, Func<int, bool>? canAccessArea = null)
        {
            if (entry.SourceCuttingStockId <= 0)
                return (false, "Source Cutting Stock is required.", 0);
            if (entry.CuttingQuantityEntered <= 0)
                return (false, "Cutting Quantity must be greater than zero.", 0);
            if (!DirectSowingRules.IsWholeNumber(entry.CuttingQuantityEntered))
                return (false, "Cutting Quantity must be a whole number.", 0);
            if (entry.SowingDate == default)
                return (false, "Sowing Date is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the source Cutting Stock pool.
                var lockCmd = new SqlCommand(@"
SELECT cs.SpeciesId, cs.AreaId, cs.PhysicalQuantity, cs.InTransitQuantity, a.IsActive
FROM dbo.CuttingStock cs WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Area a ON a.Id = cs.AreaId
WHERE cs.Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.SourceCuttingStockId);
                int speciesId, areaId;
                decimal physical, inTransit;
                bool areaActive;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Selected Cutting Stock pool not found.", 0);
                    }
                    speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
                    areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
                    physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
                    inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
                    areaActive = reader.GetBoolean(reader.GetOrdinal("IsActive"));
                }

                if (!areaActive)
                {
                    tx.Rollback();
                    return (false, "The selected Cutting Stock pool's Area is inactive.", 0);
                }
                if (canAccessArea != null && !canAccessArea(areaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to sow cuttings from the selected Area.", 0);
                }

                // Under the lock: whole number, complete trays, available quantity.
                var (traysOk, trays, cuttingsUsed, _, trayError) = DirectSowingRules.CalculateTrays(entry.CuttingQuantityEntered, entry.CavityType);
                if (!traysOk)
                {
                    tx.Rollback();
                    return (false, trayError, 0);
                }
                var available = physical - inTransit;
                if (cuttingsUsed > available)
                {
                    tx.Rollback();
                    return (false, $"Insufficient Cutting Stock (available {available:N2}, requested {cuttingsUsed:N2}).", 0);
                }

                entry.SpeciesId = speciesId;
                entry.AreaId = areaId;
                entry.NumberOfTrays = trays;
                entry.QuantitySown = cuttingsUsed;

                // Assigned supervisor: reuses the EXACT same "who may
                // approve Ready Stock" eligibility as Direct Sowing
                // (SupervisorKind.Sowing / ReadyStock.Confirm) -- the
                // existing assigned-supervisor rule, not a new one.
                var approvers = (await _userRoleRepo.GetSowingApproversAsync(conn, tx)).Select(a => a.EmployeeID).ToList();
                var (supervisorOk, supervisorError) = DirectSowingRules.ValidateSupervisorAssignment(
                    entry.SupervisorId, entry.CreatedById ?? userId, approvers);
                if (!supervisorOk)
                {
                    tx.Rollback();
                    return (false, supervisorError, 0);
                }

                // Expected Ready Date from the variety's growing days --
                // same rule as Direct Sowing (Decision 21).
                var species = await _plantSpeciesRepo.GetByIdAsync(speciesId);
                var expectedReadyDate = DirectSowingRules.ExpectedReadyDate(entry.SowingDate, species?.ReadyStockDays);
                if (!expectedReadyDate.HasValue)
                {
                    tx.Rollback();
                    return (false, $"Growing days are not configured for variety '{species?.Name?.Trim()}'. Set them in Administration > Plant Master before sowing.", 0);
                }
                entry.ReadyStockDays = species!.ReadyStockDays;
                entry.ExpectedReadyDate = expectedReadyDate;

                var sowingCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "CSOW", entry.SowingDate.Year);

                const string insertSql = @"
INSERT INTO dbo.CuttingSowings
(SowingCode, SourceCuttingStockId, SpeciesId, AreaId, CavityType, NumberOfTrays, QuantitySown, CuttingQuantityEntered,
 SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, Remarks, CreatedDate, CreatedBy, CreatedById)
VALUES
(@SowingCode, @SourceCuttingStockId, @SpeciesId, @AreaId, @CavityType, @NumberOfTrays, @QuantitySown, @CuttingQuantityEntered,
 @SowingDate, @ReadyStockDays, @ExpectedReadyDate, 'Sown', @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@SowingCode", sowingCode);
                cmd.Parameters.AddWithValue("@SourceCuttingStockId", entry.SourceCuttingStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@CavityType", entry.CavityType);
                cmd.Parameters.AddWithValue("@NumberOfTrays", entry.NumberOfTrays!.Value);
                cmd.Parameters.AddWithValue("@QuantitySown", entry.QuantitySown);
                cmd.Parameters.AddWithValue("@CuttingQuantityEntered", entry.CuttingQuantityEntered);
                cmd.Parameters.AddWithValue("@SowingDate", entry.SowingDate.Date);
                cmd.Parameters.AddWithValue("@ReadyStockDays", (object?)entry.ReadyStockDays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ExpectedReadyDate", (object?)entry.ExpectedReadyDate?.Date ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedById", (object?)(entry.CreatedById ?? userId) ?? DBNull.Value);

                var newId = (int)(await cmd.ExecuteScalarAsync())!;

                // Consume ONLY the cuttings sown in complete trays
                // ('Sown' ledger entry = -QuantitySown); the remainder
                // stays in the pool. The ledger/CHECK constraints refuse
                // a negative balance.
                var (success, message) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, entry.SourceCuttingStockId, -entry.QuantitySown, "Sown", "CuttingSowing", newId, userId, entry.Remarks);
                if (!success)
                {
                    tx.Rollback();
                    return (false, message, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.SowingCode = sowingCode;
                entry.Status = "Sown";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Updates non-stock-affecting fields only (SupervisorId, Remarks).
        // Source/Area/Species/Cavity/Quantities are immutable after
        // creation -- use CancelAsync to reverse a Sowing entirely.
        // Mirrors SeedSowingRepository.UpdateDetailsAsync: editorId can
        // never become the supervisor themselves (F1).
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(CuttingSowing entry, int? editorId = null)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SupervisorId, CreatedById, Status FROM dbo.CuttingSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.Id);
                int? currentSupervisorId, createdById;
                string status;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Cutting Sowing record not found.");
                    }
                    currentSupervisorId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                    createdById = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    status = reader.GetString(2);
                }

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Cutting Sowing record is Cancelled and cannot be edited.");
                }

                if (entry.SupervisorId != currentSupervisorId)
                {
                    var approvers = (await _userRoleRepo.GetSowingApproversAsync(conn, tx)).Select(a => a.EmployeeID).ToList();
                    var (ok, error) = DirectSowingRules.ValidateSupervisorChange(entry.SupervisorId, createdById, editorId, approvers);
                    if (!ok)
                    {
                        tx.Rollback();
                        return (false, error);
                    }
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.CuttingSowings SET SupervisorId = @SupervisorId, Remarks = @Remarks, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateCmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@Id", entry.Id);
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

        // Reverses an un-approved Cutting Sowing entirely, returning the
        // cuttings it consumed. Refused once any Ready Confirmation exists
        // (cancel the approval first -- Cutting Sowing Approval History).
        // Mirrors SeedSowingRepository.CancelAsync.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SourceCuttingStockId, QuantitySown, Status, ConfirmedReadyQuantity, WastageQuantity FROM dbo.CuttingSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                int sourceCuttingStockId;
                decimal quantitySown, approvedReady, approvedWastage;
                string status;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Cutting Sowing record not found.");
                    }
                    sourceCuttingStockId = reader.GetInt32(0);
                    quantitySown = reader.GetDecimal(1);
                    status = reader.GetString(2);
                    approvedReady = reader.GetDecimal(3);
                    approvedWastage = reader.GetDecimal(4);
                }

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Cutting Sowing record is already Cancelled.");
                }
                if (status != "Sown" || approvedReady > 0 || approvedWastage > 0)
                {
                    tx.Rollback();
                    return (false, "This Cutting Sowing already has a Supervisor Approval. Cancel the approval first.");
                }

                var (success, message) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, sourceCuttingStockId, quantitySown, "ReversalReturn", "CuttingSowing", id, userId, "Reversal of cancelled Cutting Sowing");
                if (!success)
                {
                    tx.Rollback();
                    return (false, message);
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.CuttingSowings SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
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

        private static CuttingSowing Map(SqlDataReader reader)
        {
            return new CuttingSowing
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SowingCode = reader.GetString(reader.GetOrdinal("SowingCode")),
                SourceCuttingStockId = reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                CavityType = reader.GetString(reader.GetOrdinal("CavityType")),
                NumberOfTrays = reader.IsDBNull(reader.GetOrdinal("NumberOfTrays")) ? null : reader.GetInt32(reader.GetOrdinal("NumberOfTrays")),
                QuantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown")),
                CuttingQuantityEntered = reader.GetDecimal(reader.GetOrdinal("CuttingQuantityEntered")),
                WastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity")),
                SowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate")),
                ReadyStockDays = reader.IsDBNull(reader.GetOrdinal("ReadyStockDays")) ? null : reader.GetInt32(reader.GetOrdinal("ReadyStockDays")),
                ExpectedReadyDate = reader.IsDBNull(reader.GetOrdinal("ExpectedReadyDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ExpectedReadyDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ConfirmedReadyQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                CreatedById = reader.IsDBNull(reader.GetOrdinal("CreatedById")) ? null : reader.GetInt32(reader.GetOrdinal("CreatedById")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                SourceAvailableQuantity = reader.IsDBNull(reader.GetOrdinal("SourceAvailableQuantity")) ? null : reader.GetDecimal(reader.GetOrdinal("SourceAvailableQuantity"))
            };
        }
    }
}
