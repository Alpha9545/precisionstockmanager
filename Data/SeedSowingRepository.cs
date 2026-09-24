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
            PlantSpeciesRepository plantSpeciesRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _seedStockRepo = seedStockRepo;
            _areaRepo = areaRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
        }

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
    sw.Id, sw.SowingCode, sw.SourceSeedStockId, sw.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    sw.AreaId, a.Name AS AreaName, gp.Name AS GrowingPartnerName,
    sw.PolyhouseId, COALESCE(sph.Name, ph.Name) AS PolyhouseName,
    ss.AreaId AS SourceAreaId, ssa.Name AS SourceAreaName, sw.WastageQuantity,
    sw.BatchNo, sw.SeedSourceId, src.Name AS SeedSourceName,
    sw.CavityType, sw.NumberOfTrays, sw.QuantitySown, sw.SowingDate, sw.ReadyStockDays, sw.ExpectedReadyDate, sw.Status,
    sw.ConfirmedReadyQuantity,
    sw.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    sw.SupervisorId, sup.Name AS SupervisorName,
    sw.Remarks, sw.CreatedDate, sw.CreatedBy, sw.ModifiedDate, sw.ModifiedBy,
    ss.AvailableQuantity AS SourceAvailableQuantity
FROM dbo.SeedSowings sw
INNER JOIN dbo.PlantSpecies ps ON sw.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON sw.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
LEFT JOIN dbo.Polyhouses ph ON a.PolyhouseId = ph.Id
LEFT JOIN dbo.Polyhouses sph ON sw.PolyhouseId = sph.Id
INNER JOIN dbo.SeedStock ss ON sw.SourceSeedStockId = ss.Id
LEFT JOIN dbo.Area ssa ON ss.AreaId = ssa.Id
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
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(SeedSowing entry, int? userId)
        {
            if (entry.SourceSeedStockId <= 0)
                return (false, "Main Office seed lot is required.", 0);
            if (entry.AreaId <= 0)
                return (false, "Area is required.", 0);
            if (!entry.PolyhouseId.HasValue || entry.PolyhouseId.Value <= 0)
                return (false, "Polyhouse is required.", 0);
            if (entry.QuantitySown <= 0)
                return (false, "Seed Quantity must be greater than zero.", 0);
            if (!DirectSowingRules.IsValidCavityType(entry.CavityType))
                return (false, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.", 0);
            if (entry.NumberOfTrays.HasValue && entry.NumberOfTrays.Value <= 0)
                return (false, "Tray Count must be greater than zero when provided.", 0);
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
                int speciesId;
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
                var (enough, _, stockError) = DirectSowingRules.CheckSeedAvailability(physical, inTransit, entry.QuantitySown);
                if (!enough)
                {
                    tx.Rollback();
                    return (false, stockError, 0);
                }

                // 3) Growing Area (active) and a Polyhouse inside it.
                var locCmd = new SqlCommand(@"
SELECT a.IsActive, p.AreaId AS PolyhouseAreaId
FROM dbo.Area a
LEFT JOIN dbo.Polyhouses p ON p.Id = @PolyhouseId
WHERE a.Id = @AreaId", conn, tx);
                locCmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                locCmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId!.Value);
                using (var reader = await locCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync() || !reader.GetBoolean(0))
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Selected Area does not exist or is inactive.", 0);
                    }
                    var polyhouseAreaId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                    if (polyhouseAreaId != entry.AreaId)
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Selected Polyhouse does not belong to the selected Area.", 0);
                    }
                }

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
(SowingCode, SourceSeedStockId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType, NumberOfTrays, QuantitySown,
 SowingDate, ReadyStockDays, ExpectedReadyDate, Status, ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@SowingCode, @SourceSeedStockId, @SpeciesId, @AreaId, @PolyhouseId, @BatchNo, @SeedSourceId, @CavityType, @NumberOfTrays, @QuantitySown,
 @SowingDate, @ReadyStockDays, @ExpectedReadyDate, 'Sown', @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@SowingCode", sowingCode);
                cmd.Parameters.AddWithValue("@SourceSeedStockId", entry.SourceSeedStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId!.Value);
                cmd.Parameters.AddWithValue("@BatchNo", entry.BatchNo ?? string.Empty);
                cmd.Parameters.AddWithValue("@SeedSourceId", (object?)entry.SeedSourceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CavityType", entry.CavityType);
                cmd.Parameters.AddWithValue("@NumberOfTrays", (object?)entry.NumberOfTrays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@QuantitySown", entry.QuantitySown);
                cmd.Parameters.AddWithValue("@SowingDate", entry.SowingDate.Date);
                cmd.Parameters.AddWithValue("@ReadyStockDays", (object?)entry.ReadyStockDays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ExpectedReadyDate", (object?)entry.ExpectedReadyDate?.Date ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)(await cmd.ExecuteScalarAsync())!;

                // 6) Consume the Main Office seed ('Sown' ledger entry; the
                // ledger/CHECK constraints also refuse a negative balance).
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

        // Updates non-stock-affecting, non-historical fields only
        // (ResponsiblePersonId, SupervisorId, Remarks). Source/Area/
        // Species/CavityType/QuantitySown are immutable after creation
        // -- use CancelAsync to reverse a Sowing entirely. Mirrors
        // PotProductionRepository.UpdateDetailsAsync exactly.
        //
        // PHASE J CORRECTION: ExpectedReadyDate and ReadyStockDays are
        // now ALSO immutable after creation, and the guarantee lives
        // here, not just on the Edit page. This UPDATE statement
        // structurally never references either column -- there is no
        // @ExpectedReadyDate/@ReadyStockDays parameter at all, so no
        // value bound onto the passed-in `entry` (however it got
        // there -- a normal edit, a crafted POST, or a future caller
        // that forgets to guard this) can ever reach either column via
        // this method. The two together represent the historical
        // "SowingDate + ReadyStockDays (as it was at sowing time) =
        // ExpectedReadyDate" calculation from InsertAsync -- rewriting
        // just one of them after the fact would create an inconsistent
        // historical record (see Decision 21's correction paragraph in
        // PROJECT_DOCUMENTATION.md).
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(SeedSowing entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.SeedSowings
SET ResponsiblePersonId = @ResponsiblePersonId,
    SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status <> 'Cancelled'";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Seed Sowing record not found, or it is already Cancelled.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

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
                    "SELECT SourceSeedStockId, QuantitySown, Status, ConfirmedReadyQuantity, WastageQuantity FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Seed Sowing record not found.");
                }
                var sourceSeedStockId = reader.GetInt32(reader.GetOrdinal("SourceSeedStockId"));
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

                var (success, message) = await _seedStockRepo.RecordTransactionAsync(
                    conn, tx, sourceSeedStockId, quantitySown, "ReversalReturn", "SeedSowing", id, userId, "Reversal of cancelled Sowing");
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
                SourceSeedStockId = reader.GetInt32(reader.GetOrdinal("SourceSeedStockId")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                PolyhouseId = reader.IsDBNull(reader.GetOrdinal("PolyhouseId")) ? null : reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                SourceAreaId = reader.GetInt32(reader.GetOrdinal("SourceAreaId")),
                SourceAreaName = reader.IsDBNull(reader.GetOrdinal("SourceAreaName")) ? null : reader.GetString(reader.GetOrdinal("SourceAreaName")),
                WastageQuantity = reader.GetDecimal(reader.GetOrdinal("WastageQuantity")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                SeedSourceId = reader.IsDBNull(reader.GetOrdinal("SeedSourceId")) ? null : reader.GetInt32(reader.GetOrdinal("SeedSourceId")),
                SeedSourceName = reader.IsDBNull(reader.GetOrdinal("SeedSourceName")) ? null : reader.GetString(reader.GetOrdinal("SeedSourceName")),
                CavityType = reader.GetString(reader.GetOrdinal("CavityType")),
                NumberOfTrays = reader.IsDBNull(reader.GetOrdinal("NumberOfTrays")) ? null : reader.GetInt32(reader.GetOrdinal("NumberOfTrays")),
                QuantitySown = reader.GetDecimal(reader.GetOrdinal("QuantitySown")),
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
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                SourceAvailableQuantity = reader.IsDBNull(reader.GetOrdinal("SourceAvailableQuantity")) ? null : reader.GetDecimal(reader.GetOrdinal("SourceAvailableQuantity"))
            };
        }
    }
}
