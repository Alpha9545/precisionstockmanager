using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 23 (Phase I): Seed Stock -> Sowing. A single-actor production
    // event -- mirrors PotProductionRepository's shape (InsertAsync,
    // CancelAsync, UpdateDetailsAsync) rather than SeedIssueRepository's
    // two-party PendingConfirmation shape, since a Supervisor sows their
    // OWN already-received Seed Stock with no counter-party to confirm
    // receipt from. Only ever consumes dbo.SeedStock; never touches
    // dbo.SeedIssues or dbo.InternalTransfers.
    //
    // Phase 25 (Phase K): this class now also carries
    // ConfirmedReadyQuantity (BaseSelect/Map) and
    // GetReadyForConfirmationAsync() for the new Ready Confirmation ->
    // Ready Stock workflow (Data/ReadyConfirmationRepository.cs,
    // Data/ReadyStockRepository.cs). SeedSowingRepository itself gains
    // NO new stock-mutating method for Phase K -- the actual
    // ConfirmedReadyQuantity UPDATE happens inside
    // ReadyConfirmationRepository's own atomic transaction, under a
    // lock it takes directly against dbo.SeedSowings (mirroring how
    // DispatchRepository updates dbo.PottedPlantBookings.
    // DispatchedQuantity directly rather than routing through a
    // PottedPlantBookingRepository method) -- this keeps SeedSowings'
    // own repository focused on the Sowing lifecycle itself.
    public class SeedSowingRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly AreaRepository _areaRepo;
        // Phase 24 (Phase J): read-only master lookup for the sown
        // species' ReadyStockDays, at the moment of Sowing -- same
        // "own connection, outside the SeedStock transaction" pattern
        // InsertAsync already uses for _areaRepo.GetAreaById.
        private readonly PlantSpeciesRepository _plantSpeciesRepo;

        // Phase B: the former Kunjir/Kiran-only rule (seed had to be ISSUED
        // to a growing Area first) is retired. Sowing now consumes Main
        // Office Seed Stock directly and records the growing Area +
        // Polyhouse chosen on the form (DirectSowingRules).

        // Post-review correction: the business's exact, closed cavity
        // list (never free text). Exact-string match, case- and
        // whitespace-sensitive by design -- '102 cavity'/'102-cell'/
        // '102 cell'/'102c' are all rejected, never silently folded into
        // '102 Cavity', so a typo surfaces as a validation error rather
        // than a second, invisible reporting bucket. Mirrors this exact
        // array as CK_SeedSowings_CavityType in
        // Database/Phase23_SeedSowing.sql -- the two must be kept in
        // sync if this list is ever revised.
        public static IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;

        // Phase 24 (Phase J): the default "Ready Soon" lookahead window,
        // in days. The business's own spec only ever gave 3 as an
        // illustrative example ("Ready Soon: within 3 days") and asked
        // that any existing alert-window configuration be reused instead
        // of hardcoding a number -- inspection found no such
        // configuration anywhere in this app, so 3 is used as the
        // documented default. Exposed publicly so Pages/Production/
        // ReadyAlerts/Index.cshtml.cs and SeedSowing/Details.cshtml.cs
        // both classify alerts against the exact same value -- never two
        // independently-hardcoded copies.
        public const int DefaultReadySoonWindowDays = 3;

        public SeedSowingRepository(
            DatabaseHelper dbHelper,
            BatchNumberRepository batchNumberRepo,
            SeedStockRepository seedStockRepo,
            AreaRepository areaRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            UserRoleRepository userRoleRepo,
            CuttingStockRepository cuttingStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _seedStockRepo = seedStockRepo;
            _areaRepo = areaRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _userRoleRepo = userRoleRepo;
            _cuttingStockRepo = cuttingStockRepo;
        }

        private readonly UserRoleRepository _userRoleRepo;
        // Phase D: cutting tray sowing consumes Cutting Stock instead of a seed lot.
        private readonly CuttingStockRepository _cuttingStockRepo;

        // Phase 24: added ph.Name AS PolyhouseName (LEFT JOIN dbo.Polyhouses
        // via the Area's own PolyhouseId -- never a second Polyhouse
        // relationship, per Decision 19/Phase H's "AreaType = role,
        // PolyhouseId = physical location" split) and sw.ReadyStockDays.
        // Purely additive to the existing SELECT column list -- every
        // existing caller of BaseSelect/Map() is unaffected.
        // Phase 25 (Phase K): added sw.ConfirmedReadyQuantity -- the
        // running total maintained by ReadyConfirmationRepository.
        // ConfirmAsync/CancelAsync (mirrors PottedPlantBookings.
        // DispatchedQuantity). Also purely additive.
        private const string BaseSelect = @"
SELECT
    sw.Id, sw.SowingCode, sw.SourceType, sw.SourceSeedStockId, sw.SourceCuttingStockId,
    sw.SpeciesId, ps.Name AS SpeciesName, ps.Color AS SpeciesColor, pt.Name AS PlantTypeName,
    sw.AreaId, a.Name AS AreaName, gp.Name AS GrowingPartnerName,
    sw.PolyhouseId, sph.Name AS PolyhouseName,   -- only the Polyhouse actually recorded (optional)
    COALESCE(ss.AreaId, cs.AreaId) AS SourceAreaId, ssa.Name AS SourceAreaName, sw.WastageQuantity,
    sw.BatchNo, sw.SeedSourceId, src.Name AS SeedSourceName,
    sw.CavityType, sw.NumberOfTrays, sw.QuantitySown, sw.SeedQuantity, sw.SowingDate, sw.ReadyStockDays, sw.ExpectedReadyDate, sw.Status,
    sw.ConfirmedReadyQuantity,
    sw.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    sw.SupervisorId, sup.Name AS SupervisorName,
    sw.Remarks, sw.CreatedDate, sw.CreatedBy, sw.CreatedById, sw.ModifiedDate, sw.ModifiedBy,
    COALESCE(ss.AvailableQuantity, cs.AvailableQuantity) AS SourceAvailableQuantity
FROM dbo.SeedSowings sw
INNER JOIN dbo.PlantSpecies ps ON sw.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON sw.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
LEFT JOIN dbo.Polyhouses sph ON sw.PolyhouseId = sph.Id
LEFT JOIN dbo.SeedStock ss ON sw.SourceSeedStockId = ss.Id          -- seed sowing
LEFT JOIN dbo.CuttingStock cs ON sw.SourceCuttingStockId = cs.Id    -- cutting tray sowing
LEFT JOIN dbo.Area ssa ON ssa.Id = COALESCE(ss.AreaId, cs.AreaId)
LEFT JOIN dbo.SeedSources src ON sw.SeedSourceId = src.Id
LEFT JOIN dbo.IMSUsers r ON sw.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON sw.SupervisorId = sup.Id";

        public async Task<List<SeedSowing>> GetAllAsync(string? status = null)
        {
            var list = new List<SeedSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE (@Status IS NULL OR sw.Status = @Status) ORDER BY sw.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<SeedSowing?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE sw.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<List<SeedSowing>> GetByAreaAsync(int areaId)
        {
            var list = new List<SeedSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE sw.AreaId = @AreaId ORDER BY sw.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Phase 24 (Phase J): the query-driven backbone of the Ready
        // Alerts page. Filters at the SQL level -- Status = 'Sown' (a
        // Cancelled Sowing must NEVER surface as an alert, see rule 18
        // of the Phase J spec) AND ExpectedReadyDate populated AND not
        // further out than the given horizon -- rather than loading
        // every historical Sowing and filtering in C#, per the Phase J
        // spec's own performance requirement. Reuses BaseSelect's single
        // query (Area/Polyhouse/Species/PlantType/GrowingPartner/
        // SeedSource/ResponsiblePerson/Supervisor all already joined) so
        // there is no N+1 risk. Area-scoping is still applied by the
        // caller afterwards, in C#, via AreaAccessService -- identical to
        // every other list page in this app (Index.cshtml.cs included);
        // this method only narrows by date/status, not by Area.
        // Phase 25 (Phase K) CORRECTION: added
        // "AND sw.ConfirmedReadyQuantity < sw.QuantitySown" -- once a
        // Sowing has been Ready-Confirmed for its FULL QuantitySown, it
        // must stop appearing as an actionable Ready Alert (Phase K
        // spec: "do not let a fully confirmed batch continue appearing
        // as an actionable Ready Alert"). A partially-confirmed Sowing
        // still has genuine remaining quantity outstanding, so it
        // continues to alert -- exactly as the spec requires
        // ("the remaining quantity may continue to appear in Ready
        // Alerts"). ClassifyReadyAlert itself is completely untouched
        // by this change, per the spec's own "do not change the Phase J
        // classification rules unnecessarily" instruction -- this is a
        // pure SQL-level candidate filter, one level "outside" the
        // classification logic.
        public async Task<List<SeedSowing>> GetAlertCandidatesAsync(DateTime horizon)
        {
            var list = new List<SeedSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE sw.Status = 'Sown' AND sw.ExpectedReadyDate IS NOT NULL AND sw.ExpectedReadyDate <= @Horizon
      AND sw.ConfirmedReadyQuantity + sw.WastageQuantity < sw.QuantitySown
ORDER BY sw.ExpectedReadyDate";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Horizon", horizon.Date);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Phase 25 (Phase K): the source list for the Ready Confirmation
        // page -- every ACTIVE Sowing that still has remaining
        // un-confirmed quantity, regardless of whether its
        // ExpectedReadyDate has actually arrived yet. Deliberately NOT
        // restricted to only currently-alerted (Overdue/ReadyToday/
        // ReadySoon) Sowings: the Phase K spec's own workflow diagram
        // has the Supervisor physically verify the crop before
        // confirming, and nothing in the spec says an early (before the
        // expected date) or a not-yet-alerted physical confirmation
        // should be blocked -- Ready Alerts remain a helpful nudge, not
        // a gate, exactly as Phase J already established ("Ready Alerts
        // ... remain alerts only"). A Cancelled Sowing can never appear
        // here (Status = 'Sown' only), and a fully-confirmed Sowing
        // drops off once ConfirmedReadyQuantity reaches QuantitySown.
        public async Task<List<SeedSowing>> GetReadyForConfirmationAsync()
        {
            var list = new List<SeedSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE sw.Status = 'Sown' AND sw.ConfirmedReadyQuantity + sw.WastageQuantity < sw.QuantitySown
ORDER BY sw.ExpectedReadyDate, sw.SowingDate";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Phase 24 (Phase J): the single source of truth for turning a
        // Status + ExpectedReadyDate into an alert category. Called
        // identically from Pages/Production/ReadyAlerts/Index.cshtml.cs
        // (the alert list) and Pages/Production/SeedSowing/Details.cshtml.cs
        // (the individual record's own displayed alert state) so the two
        // pages can never disagree -- the same discipline already
        // established for CavityTypes. Pure and read-only: computing a
        // category here never writes anything, never changes Status,
        // and never creates or touches any stock row (Phase J's own
        // "no automatic readiness" rule).
        public static string ClassifyReadyAlert(string status, DateTime? expectedReadyDate, DateTime today, int readySoonWindowDays)
        {
            if (status != "Sown" || !expectedReadyDate.HasValue)
                return "None";

            var readyDate = expectedReadyDate.Value.Date;
            if (readyDate < today)
                return "Overdue";
            if (readyDate == today)
                return "ReadyToday";
            if (readyDate <= today.AddDays(readySoonWindowDays))
                return "ReadySoon";
            return "None";
        }

        // Phase B -- DIRECT SOWING. One atomic transaction:
        //   1) lock the Main Office Seed Stock pool (UPDLOCK/HOLDLOCK) and
        //      refuse if the quantity exceeds Available (no negative stock);
        //   2) the pool must be at an ACTIVE MainOffice Area, and its
        //      variety must match the variety chosen on the form;
        //   3) the growing Area must be active and the Polyhouse must belong
        //      to it (dbo.Polyhouses.AreaId);
        //   4) Expected Ready Date = Sowing Date + the variety's growing days
        //      (dbo.PlantSpecies.ReadyStockDays) -- a variety without growing
        //      days is refused (no manual date);
        //   5) batch number YYYY-MM-DD-L-NNN from dbo.BatchNumberSequences,
        //      stored in the unique SowingCode column;
        //   6) insert the sowing and write the 'Sown' ledger entry.
        // Area AUTHORIZATION (may this user sow into that Area?) is checked
        // by the page before this call, against the growing Area.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(SeedSowing entry, int? userId, Func<int, bool>? canAccessArea = null)
        {
            entry.SourceType = SeedSowing.SourceSeed;
            entry.SourceCuttingStockId = null;
            if (entry.SourceSeedStockId <= 0)
                return (false, "Main Office seed lot is required.", 0);
            // Growing Area and Polyhouse are optional for now -- resolved
            // below by DirectSowingRules.ResolveGrowingLocation.
            // The operator's Seed Quantity (entered on the form). Only the seeds
            // that fill COMPLETE trays are sown and deducted; the rest stay in
            // the lot (DirectSowingRules.PlanSowing).
            var seedQuantity = entry.SeedQuantity > 0 ? entry.SeedQuantity : entry.QuantitySown;
            if (seedQuantity <= 0)
                return (false, "Seed Quantity must be greater than zero.", 0);
            if (!DirectSowingRules.IsWholeNumber(seedQuantity))
                return (false, "Seed Quantity must be a whole number.", 0);
            // Tray count is ALWAYS calculated here (FLOOR(SeedQuantity / TraySize));
            // any value posted by the browser is overwritten.
            var (traysOk, _, _, _, trayError) = DirectSowingRules.CalculateTrays(seedQuantity, entry.CavityType);
            if (!traysOk)
                return (false, trayError, 0);
            if (entry.SowingDate == default)
                return (false, "Sowing Date is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) + 2) Lock the Main Office seed pool.
                var lockCmd = new SqlCommand(@"
SELECT ss.AreaId, ss.SpeciesId, ss.BatchNo, ss.SeedSourceId, ss.PhysicalQuantity, ss.InTransitQuantity,
       a.AreaType, a.IsActive
FROM dbo.SeedStock ss WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Area a ON a.Id = ss.AreaId
WHERE ss.Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.SourceSeedStockId);
                int speciesId, seedLotAreaId;
                string batchNo;
                int? seedSourceId;
                decimal physical, inTransit;
                string? sourceAreaType;
                bool sourceAreaActive;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Selected Main Office seed lot not found.", 0);
                    }
                    speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
                    seedLotAreaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
                    batchNo = reader.GetString(reader.GetOrdinal("BatchNo"));
                    seedSourceId = reader.IsDBNull(reader.GetOrdinal("SeedSourceId")) ? null : reader.GetInt32(reader.GetOrdinal("SeedSourceId"));
                    physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
                    inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
                    sourceAreaType = reader.IsDBNull(reader.GetOrdinal("AreaType")) ? null : reader.GetString(reader.GetOrdinal("AreaType"));
                    sourceAreaActive = reader.GetBoolean(reader.GetOrdinal("IsActive"));
                }

                if (!DirectSowingRules.IsMainOfficeSeedLocation(sourceAreaType, sourceAreaActive))
                {
                    tx.Rollback();
                    return (false, "Sowing must consume seed from an active Main Office Seed Stock lot.", 0);
                }
                if (entry.SpeciesId > 0 && entry.SpeciesId != speciesId)
                {
                    tx.Rollback();
                    return (false, "The selected seed lot does not belong to the selected Variety.", 0);
                }
                // Under the lot lock: whole number, complete trays, entered
                // quantity available; sown = seeds used in complete trays.
                var (planOk, trays, seedsUsed, _, _, planError) = DirectSowingRules.PlanSowing(seedQuantity, entry.CavityType, physical, inTransit);
                if (!planOk)
                {
                    tx.Rollback();
                    return (false, planError, 0);
                }
                entry.SeedQuantity = seedQuantity;
                entry.NumberOfTrays = trays;
                entry.QuantitySown = seedsUsed;

                // Assigned supervisor: an active Sowing Supervisor, not the recorder.
                var approvers = (await _userRoleRepo.GetSowingApproversAsync(conn, tx)).Select(a => a.EmployeeID).ToList();
                var (supervisorOk, supervisorError) = DirectSowingRules.ValidateSupervisorAssignment(
                    entry.SupervisorId, entry.CreatedById ?? userId, approvers);
                if (!supervisorOk)
                {
                    tx.Rollback();
                    return (false, supervisorError, 0);
                }
                entry.ResponsiblePersonId = null;   // Phase D: the assigned supervisor is the only person recorded

                // 3) Growing location. Polyhouse is optional; the Area
                //    defaults to the Polyhouse's Area (when it has one) or to
                //    the seed lot's Main Office Area. Nothing is invented: an
                //    empty Polyhouse stays NULL.
                int? polyhouseAreaId = null;
                if (entry.PolyhouseId is > 0)
                {
                    var phCmd = new SqlCommand("SELECT AreaId FROM dbo.Polyhouses WHERE Id = @PolyhouseId", conn, tx);
                    phCmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId.Value);
                    var ph = await phCmd.ExecuteScalarAsync();
                    if (ph == null)
                    {
                        tx.Rollback();
                        return (false, "Selected Polyhouse does not exist.", 0);
                    }
                    polyhouseAreaId = ph == DBNull.Value ? null : (int)ph;
                }
                var (locationOk, growingAreaId, growingPolyhouseId, locationError) = DirectSowingRules.ResolveGrowingLocation(
                    seedLotAreaId, entry.AreaId > 0 ? entry.AreaId : null, entry.PolyhouseId, polyhouseAreaId);
                if (!locationOk)
                {
                    tx.Rollback();
                    return (false, locationError, 0);
                }
                var areaCmd = new SqlCommand("SELECT IsActive FROM dbo.Area WHERE Id = @AreaId", conn, tx);
                areaCmd.Parameters.AddWithValue("@AreaId", growingAreaId);
                var areaActive = await areaCmd.ExecuteScalarAsync();
                if (areaActive == null || areaActive == DBNull.Value || !(bool)areaActive)
                {
                    tx.Rollback();
                    return (false, "Selected Area does not exist or is inactive.", 0);
                }
                if (canAccessArea != null && !canAccessArea(growingAreaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to sow in this Area.", 0);
                }
                entry.AreaId = growingAreaId;
                entry.PolyhouseId = growingPolyhouseId;

                // 4) Expected Ready Date from the variety's growing days.
                var species = await _plantSpeciesRepo.GetByIdAsync(speciesId);
                var expectedReadyDate = DirectSowingRules.ExpectedReadyDate(entry.SowingDate, species?.ReadyStockDays);
                if (!expectedReadyDate.HasValue)
                {
                    tx.Rollback();
                    return (false, $"Growing days are not configured for variety '{species?.Name?.Trim()}'. Set them in Administration > Plant Master before sowing.", 0);
                }

                entry.SpeciesId = speciesId;
                entry.BatchNo = batchNo;           // seed lot number (traceability to the seed received)
                entry.SeedSourceId = seedSourceId;
                entry.ReadyStockDays = species!.ReadyStockDays;
                entry.ExpectedReadyDate = expectedReadyDate;

                // 5) Unique sowing batch number (existing counter table).
                var sowingCode = await _batchNumberRepo.GetNextSowingBatchNumberAsync(conn, tx, entry.SowingDate.Date);

                const string insertSql = @"
INSERT INTO dbo.SeedSowings
(SowingCode, SourceType, SourceSeedStockId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType, NumberOfTrays, QuantitySown, SeedQuantity,
 SowingDate, ReadyStockDays, ExpectedReadyDate, Status, ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy, CreatedById)
VALUES
(@SowingCode, N'Seed', @SourceSeedStockId, @SpeciesId, @AreaId, @PolyhouseId, @BatchNo, @SeedSourceId, @CavityType, @NumberOfTrays, @QuantitySown, @SeedQuantity,
 @SowingDate, @ReadyStockDays, @ExpectedReadyDate, 'Sown', @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@SowingCode", sowingCode);
                cmd.Parameters.AddWithValue("@SourceSeedStockId", entry.SourceSeedStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@PolyhouseId", (object?)entry.PolyhouseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BatchNo", entry.BatchNo ?? string.Empty);
                cmd.Parameters.AddWithValue("@SeedSourceId", (object?)entry.SeedSourceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CavityType", entry.CavityType);
                cmd.Parameters.AddWithValue("@NumberOfTrays", (object?)entry.NumberOfTrays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@QuantitySown", entry.QuantitySown);   // Seeds Used = trays x cavity
                cmd.Parameters.AddWithValue("@SeedQuantity", entry.SeedQuantity);   // as entered; Remaining Seeds = SeedQuantity - QuantitySown
                cmd.Parameters.AddWithValue("@SowingDate", entry.SowingDate.Date);
                cmd.Parameters.AddWithValue("@ReadyStockDays", (object?)entry.ReadyStockDays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ExpectedReadyDate", (object?)entry.ExpectedReadyDate?.Date ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedById", (object?)(entry.CreatedById ?? userId) ?? DBNull.Value);

                var newId = (int)(await cmd.ExecuteScalarAsync())!;

                // 6) Consume ONLY the seeds sown in complete trays ('Sown'
                // ledger entry = -QuantitySown); the remaining seeds stay in
                // the lot. The ledger/CHECK constraints refuse a negative balance.
                var (success, message) = await _seedStockRepo.RecordTransactionAsync(
                    conn, tx, entry.SourceSeedStockId, -entry.QuantitySown, "Sown", "SeedSowing", newId, userId, entry.Remarks);
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

        // Phase D: only the Remarks of a sowing can be edited. The source,
        // variety, cavity, trays, quantities, dates, the ASSIGNED SUPERVISOR
        // (the only approver) and the recorder are final once saved --
        // enforced again by TR_SeedSowings_ImmutableTrayData. A wrong sowing
        // is cancelled (stock returned) and recorded again.
        public async Task<(bool Success, string? Message)> UpdateRemarksAsync(int id, string? remarks, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            try
            {
                const string updateSql = @"
UPDATE dbo.SeedSowings
SET Remarks = @Remarks, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status <> 'Cancelled'";
                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Seed Sowing record not found, or it is already Cancelled.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Phase D -- CUTTING TRAY SOWING. Same tray rule and the same
        // Supervisor Approval -> Ready Stock chain as seed sowing, but the
        // material comes from Cutting Stock:
        //   Complete Trays  = FLOOR(Cutting Quantity / cavity)
        //   Used Cutting    = trays x cavity      ('Sown' ledger entry)
        //   Remaining       = the rest -- stays in Cutting Stock (never wastage)
        public async Task<(bool Success, string? Message, int Id)> InsertFromCuttingAsync(SeedSowing entry, int? userId, Func<int, bool>? canAccessArea = null, Func<int, bool>? canUseSourceArea = null)
        {
            entry.SourceType = SeedSowing.SourceCutting;
            entry.SourceSeedStockId = 0;
            if (!entry.SourceCuttingStockId.HasValue || entry.SourceCuttingStockId.Value <= 0)
                return (false, "Choose the Cutting Stock to sow from.", 0);
            var cuttingQuantity = entry.SeedQuantity;
            if (entry.SowingDate == default)
                return (false, "Sowing Date is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var lockCmd = new SqlCommand(@"
SELECT cs.AreaId, cs.SpeciesId, cs.PhysicalQuantity, cs.InTransitQuantity, a.Name AS AreaName
FROM dbo.CuttingStock cs WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Area a ON a.Id = cs.AreaId
WHERE cs.Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.SourceCuttingStockId.Value);
                int speciesId, stockAreaId; decimal physical, inTransit; string stockAreaName;
                using (var reader = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Selected Cutting Stock not found.", 0);
                    }
                    stockAreaId = reader.GetInt32(0);
                    speciesId = reader.GetInt32(1);
                    physical = reader.GetDecimal(2);
                    inTransit = reader.GetDecimal(3);
                    stockAreaName = reader.GetString(4).Trim();
                }
                var canUseSource = canUseSourceArea ?? canAccessArea;
                if (canUseSource != null && !canUseSource(stockAreaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to use cuttings from this Area.", 0);
                }
                if (entry.SpeciesId > 0 && entry.SpeciesId != speciesId)
                {
                    tx.Rollback();
                    return (false, "The selected Cutting Stock does not belong to the selected Variety.", 0);
                }
                // Under the stock lock: whole number, complete trays, quantity
                // available; used = cuttings placed in complete trays.
                var (planOk, trays, used, _, _, planError) = DirectSowingRules.PlanSowing(
                    cuttingQuantity, entry.CavityType, physical, inTransit, DirectSowingRules.CuttingQuantityLabel, "Cutting Stock");
                if (!planOk)
                {
                    tx.Rollback();
                    return (false, planError, 0);
                }
                entry.SeedQuantity = cuttingQuantity;
                entry.NumberOfTrays = trays;
                entry.QuantitySown = used;

                var approvers = (await _userRoleRepo.GetSowingApproversAsync(conn, tx)).Select(a => a.EmployeeID).ToList();
                var (supervisorOk, supervisorError) = DirectSowingRules.ValidateSupervisorAssignment(entry.SupervisorId, entry.CreatedById ?? userId, approvers);
                if (!supervisorOk)
                {
                    tx.Rollback();
                    return (false, supervisorError, 0);
                }

                // Growing location: the chosen Area, else the Cutting Stock's own Area.
                int? polyhouseAreaId = null;
                if (entry.PolyhouseId is > 0)
                {
                    var phCmd = new SqlCommand("SELECT AreaId FROM dbo.Polyhouses WHERE Id = @PolyhouseId", conn, tx);
                    phCmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId.Value);
                    var ph = await phCmd.ExecuteScalarAsync();
                    if (ph == null)
                    {
                        tx.Rollback();
                        return (false, "Selected Polyhouse does not exist.", 0);
                    }
                    polyhouseAreaId = ph == DBNull.Value ? null : (int)ph;
                }
                var (locationOk, growingAreaId, growingPolyhouseId, locationError) = DirectSowingRules.ResolveGrowingLocation(
                    stockAreaId, entry.AreaId > 0 ? entry.AreaId : null, entry.PolyhouseId, polyhouseAreaId);
                if (!locationOk)
                {
                    tx.Rollback();
                    return (false, locationError, 0);
                }
                var areaCmd = new SqlCommand("SELECT IsActive FROM dbo.Area WHERE Id = @AreaId", conn, tx);
                areaCmd.Parameters.AddWithValue("@AreaId", growingAreaId);
                var areaActive = await areaCmd.ExecuteScalarAsync();
                if (areaActive == null || areaActive == DBNull.Value || !(bool)areaActive)
                {
                    tx.Rollback();
                    return (false, "Selected Area does not exist or is inactive.", 0);
                }
                if (canAccessArea != null && !canAccessArea(growingAreaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to sow in this Area.", 0);
                }

                var species = await _plantSpeciesRepo.GetByIdAsync(speciesId);
                var expectedReadyDate = DirectSowingRules.ExpectedReadyDate(entry.SowingDate, species?.ReadyStockDays);
                if (!expectedReadyDate.HasValue)
                {
                    tx.Rollback();
                    return (false, $"Growing days are not configured for variety '{species?.Name?.Trim()}'. Set them in Administration > Plant Master before sowing.", 0);
                }

                var sowingCode = await _batchNumberRepo.GetNextSowingBatchNumberAsync(conn, tx, entry.SowingDate.Date);
                entry.SpeciesId = speciesId;
                entry.AreaId = growingAreaId;
                entry.PolyhouseId = growingPolyhouseId;
                entry.BatchNo = TruncateBatch($"CUT-{stockAreaName}");   // traceability: the cutting pool's Area
                entry.SeedSourceId = null;
                entry.ResponsiblePersonId = null;
                entry.ReadyStockDays = species!.ReadyStockDays;
                entry.ExpectedReadyDate = expectedReadyDate;

                const string insertSql = @"
INSERT INTO dbo.SeedSowings
(SowingCode, SourceType, SourceSeedStockId, SourceCuttingStockId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType, NumberOfTrays, QuantitySown, SeedQuantity,
 SowingDate, ReadyStockDays, ExpectedReadyDate, Status, ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy, CreatedById)
VALUES
(@SowingCode, N'Cutting', NULL, @SourceCuttingStockId, @SpeciesId, @AreaId, @PolyhouseId, @BatchNo, NULL, @CavityType, @NumberOfTrays, @QuantitySown, @SeedQuantity,
 @SowingDate, @ReadyStockDays, @ExpectedReadyDate, 'Sown', NULL, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@SowingCode", sowingCode);
                cmd.Parameters.AddWithValue("@SourceCuttingStockId", entry.SourceCuttingStockId.Value);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@PolyhouseId", (object?)entry.PolyhouseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BatchNo", entry.BatchNo);
                cmd.Parameters.AddWithValue("@CavityType", entry.CavityType);
                cmd.Parameters.AddWithValue("@NumberOfTrays", entry.NumberOfTrays!.Value);
                cmd.Parameters.AddWithValue("@QuantitySown", entry.QuantitySown);   // Used Cutting = trays x cavity
                cmd.Parameters.AddWithValue("@SeedQuantity", entry.SeedQuantity);   // Cutting Quantity as entered
                cmd.Parameters.AddWithValue("@SowingDate", entry.SowingDate.Date);
                cmd.Parameters.AddWithValue("@ReadyStockDays", (object?)entry.ReadyStockDays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ExpectedReadyDate", (object?)entry.ExpectedReadyDate?.Date ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedById", (object?)(entry.CreatedById ?? userId) ?? DBNull.Value);
                var newId = (int)(await cmd.ExecuteScalarAsync())!;

                // Only the cuttings placed in complete trays leave Cutting Stock.
                var (success, message) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, entry.SourceCuttingStockId.Value, -entry.QuantitySown, "Sown", "SeedSowing", newId, userId, entry.Remarks);
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
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        private static string TruncateBatch(string value) => value.Length <= 50 ? value : value[..50];

        // Reverses a Sowing: credits the consumed seed back to its
        // source pool ('ReversalReturn' -- reusing the exact name
        // Decision 15 already established for "credit stock back on
        // reversal," never a new one) and marks the record Cancelled.
        // Never deletes the row. Mirrors PotProductionRepository.CancelAsync.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SourceType, SourceSeedStockId, SourceCuttingStockId, QuantitySown, Status, ConfirmedReadyQuantity, WastageQuantity FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Seed Sowing record not found.");
                }
                var sourceType = reader.GetString(reader.GetOrdinal("SourceType"));
                var sourceSeedStockId = reader.IsDBNull(reader.GetOrdinal("SourceSeedStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceSeedStockId"));
                var sourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId"));
                var quantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                var approvedReady = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity"));
                var approvedWastage = reader.GetDecimal(reader.GetOrdinal("WastageQuantity"));
                reader.Close();

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Seed Sowing record is already Cancelled.");
                }
                // Phase B: once a Supervisor Approval exists (Ready Stock was
                // created / wastage recorded) the sowing can no longer be
                // cancelled -- cancel the approval first (Ready Confirmation
                // History), which re-opens the sowing.
                if (status != "Sown" || approvedReady > 0 || approvedWastage > 0)
                {
                    tx.Rollback();
                    return (false, "This sowing already has a Supervisor Approval. Cancel the approval first.");
                }

                // The seeds / cuttings that were sown go back to their source.
                var (success, message) = sourceType == SeedSowing.SourceCutting && sourceCuttingStockId.HasValue
                    ? await _cuttingStockRepo.RecordTransactionAsync(
                        conn, tx, sourceCuttingStockId.Value, quantitySown, "ReversalReturn", "SeedSowing", id, userId, "Reversal of cancelled Sowing")
                    : await _seedStockRepo.RecordTransactionAsync(
                        conn, tx, sourceSeedStockId!.Value, quantitySown, "ReversalReturn", "SeedSowing", id, userId, "Reversal of cancelled Sowing");
                if (!success)
                {
                    tx.Rollback();
                    return (false, message);
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.SeedSowings SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
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

        private static SeedSowing Map(SqlDataReader reader)
        {
            return new SeedSowing
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SowingCode = reader.GetString(reader.GetOrdinal("SowingCode")),
                SourceType = reader.GetString(reader.GetOrdinal("SourceType")),
                SourceSeedStockId = reader.IsDBNull(reader.GetOrdinal("SourceSeedStockId")) ? 0 : reader.GetInt32(reader.GetOrdinal("SourceSeedStockId")),
                SourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId")),
                SpeciesColor = reader.IsDBNull(reader.GetOrdinal("SpeciesColor")) ? null : reader.GetString(reader.GetOrdinal("SpeciesColor")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                PolyhouseId = reader.IsDBNull(reader.GetOrdinal("PolyhouseId")) ? null : reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                SourceAreaId = reader.IsDBNull(reader.GetOrdinal("SourceAreaId")) ? 0 : reader.GetInt32(reader.GetOrdinal("SourceAreaId")),
                SourceAreaName = reader.IsDBNull(reader.GetOrdinal("SourceAreaName")) ? null : reader.GetString(reader.GetOrdinal("SourceAreaName")),
                WastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                SeedSourceId = reader.IsDBNull(reader.GetOrdinal("SeedSourceId")) ? null : reader.GetInt32(reader.GetOrdinal("SeedSourceId")),
                SeedSourceName = reader.IsDBNull(reader.GetOrdinal("SeedSourceName")) ? null : reader.GetString(reader.GetOrdinal("SeedSourceName")),
                CavityType = reader.GetString(reader.GetOrdinal("CavityType")),
                NumberOfTrays = reader.IsDBNull(reader.GetOrdinal("NumberOfTrays")) ? null : reader.GetInt32(reader.GetOrdinal("NumberOfTrays")),
                QuantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown")),
                SeedQuantity = reader.IsDBNull(reader.GetOrdinal("SeedQuantity")) ? 0 : reader.GetDecimal(reader.GetOrdinal("SeedQuantity")),
                SowingDate = reader.GetDateTime(reader.GetOrdinal("SowingDate")),
                ReadyStockDays = reader.IsDBNull(reader.GetOrdinal("ReadyStockDays")) ? null : reader.GetInt32(reader.GetOrdinal("ReadyStockDays")),
                ExpectedReadyDate = reader.IsDBNull(reader.GetOrdinal("ExpectedReadyDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ExpectedReadyDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ConfirmedReadyQuantity = reader.GetDecimal(reader.GetOrdinal("ConfirmedReadyQuantity")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
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
