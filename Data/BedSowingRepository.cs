using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // 2026-10-12: Bed Sowing (Database/Migrations/2026-10-12_BedSowing.sql; rules in
    // Services/BedSowingRules.cs). ONE transaction per operation; every check is repeated on
    // values re-read under lock, and either everything commits or nothing does:
    //   create      seed lot -Quantity ('Sown', ReferenceType 'BedSowing'); bed row. No trays,
    //               no material, no Ready Stock.
    //   transplant  bed Transplanted +Quantity; NEW growing tray batch (dbo.SeedSowings,
    //               SourceType 'BedSowing', Sowing Date / Ready Days / Ready Date of the BED);
    //               Tray Stock of the target Polyhouse -trays (dated at the transplant date);
    //               growing material -trays x kg per tray (existing Main Office rule). The batch then
    //               goes through the EXISTING Sowing Approval -> Ready Stock; nothing here creates
    //               Ready Stock.
    //   loss        bed Lost +Quantity (reason required).
    //   reverse     a transplant (only while its tray batch has no approval: batch cancelled, trays
    //               and material returned, seedlings back in the bed) or a loss. The operation row
    //               stays, marked Reversed with who / when / why.
    //   cancel      a bed with nothing transplanted or lost: seed returned to the lot.
    // Who: creating / cancelling a bed -- the sowing page permission + the bed's Area (page and
    // canAccessArea). Transplant / loss / reversal -- ONLY a user holding BedSowing.Transplant FOR
    // the bed's Area (read from dbo.UserRoles inside the transaction), or a System Administrator.
    public class BedSowingRepository
    {
        public record Actor(int? UserId, string? UserName, bool IsFullAccess);

        public const string BedPrefix = "BS";
        public const string TransplantPrefix = "BT";
        public const string LossPrefix = "BL";
        public const string LedgerReference = "BedSowing";

        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly TrayStockRepository _trayStockRepo;

        public BedSowingRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo, SeedStockRepository seedStockRepo,
            PlantSpeciesRepository plantSpeciesRepo, UserRoleRepository userRoleRepo, TrayStockRepository trayStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _seedStockRepo = seedStockRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _userRoleRepo = userRoleRepo;
            _trayStockRepo = trayStockRepo;
        }

        public async Task<bool> SchemaReadyAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await BedSowingSchema.ReadyAsync(conn);
        }

        // ---- Location ----------------------------------------------------------------------

        // The Bed Sowing Polyhouses that are usable NOW: listed and active in
        // dbo.BedSowingLocations, still in the Area recorded there, and that Area an active
        // MainOffice Area. The Area always comes from the database, never from a request.
        private const string LocationSql = @"
SELECT l.PolyhouseId, p.Name, a.Id, a.Name
FROM dbo.BedSowingLocations l
JOIN dbo.Polyhouses p ON p.Id = l.PolyhouseId AND p.AreaId = l.AreaId
JOIN dbo.Area a ON a.Id = l.AreaId
WHERE l.IsActive = 1 AND a.IsActive = 1 AND a.AreaType = N'MainOffice'";

        public async Task<List<BedSowingLocation>> GetLocationsAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return new();
            return await ReadLocationsAsync(conn, null, null);
        }

        private static async Task<List<BedSowingLocation>> ReadLocationsAsync(SqlConnection conn, SqlTransaction? tx, int? polyhouseId)
        {
            var list = new List<BedSowingLocation>();
            using var cmd = new SqlCommand(LocationSql + (polyhouseId.HasValue ? " AND l.PolyhouseId = @P" : "") + " ORDER BY a.Name, p.Name", conn, tx);
            if (polyhouseId.HasValue)
                cmd.Parameters.AddWithValue("@P", polyhouseId.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new BedSowingLocation { PolyhouseId = r.GetInt32(0), PolyhouseName = r.GetString(1), AreaId = r.GetInt32(2), AreaName = r.GetString(3) });
            return list;
        }

        public const string TraysMigrationMessage =
            "Transplanting into trays needs the database update 2026-10-13_TraysToSowingApprovals.sql (Ready Date from the transplant date, no supervisor selection).";

        // Transplant targets of a bed: the Bed Sowing Polyhouse(s) of the bed's own Area, and every
        // Polyhouse of an active Mother Plant Area. polyhouseId: only that one (validation).
        private static async Task<List<BedSowingLocation>> ReadTransplantTargetsAsync(SqlConnection conn, SqlTransaction? tx, int bedAreaId, int? polyhouseId)
        {
            var list = new List<BedSowingLocation>();
            using var cmd = new SqlCommand(@"
SELECT p.Id, p.Name, a.Id, a.Name, a.AreaType
FROM dbo.Polyhouses p
JOIN dbo.Area a ON a.Id = p.AreaId AND a.IsActive = 1
WHERE (@P IS NULL OR p.Id = @P)
  AND (   (a.AreaType = N'MotherPlant')
       OR (a.Id = @BedArea AND a.AreaType = N'MainOffice'
           AND EXISTS (SELECT 1 FROM dbo.BedSowingLocations l WHERE l.PolyhouseId = p.Id AND l.AreaId = a.Id AND l.IsActive = 1)))
ORDER BY CASE WHEN a.Id = @BedArea THEN 0 ELSE 1 END, a.Name, p.Name", conn, tx);
            cmd.Parameters.AddWithValue("@P", (object?)polyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@BedArea", bedAreaId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new BedSowingLocation { PolyhouseId = r.GetInt32(0), PolyhouseName = r.GetString(1), AreaId = r.GetInt32(2), AreaName = r.GetString(3), AreaType = r.GetString(4) });
            return list;
        }

        public async Task<List<BedSowingLocation>> GetTransplantTargetsAsync(int bedAreaId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return new();
            return await ReadTransplantTargetsAsync(conn, null, bedAreaId, null);
        }

        // Does this user hold BedSowing.Transplant FOR this Area (an active user, a role holding
        // the permission, assigned for exactly that Area)? Read inside the caller's transaction.
        public static async Task<bool> IsTransplanterAsync(SqlConnection conn, SqlTransaction? tx, int? userId, int areaId)
        {
            if (userId is not > 0)
                return false;
            using var cmd = new SqlCommand(@"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM dbo.UserRoles ur
    JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND ISNULL(u.IsActive, 0) = 1
    JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = @Code
    WHERE ur.UserId = @U AND ur.AreaId = @A) THEN 1 ELSE 0 END", conn, tx);
            cmd.Parameters.AddWithValue("@Code", BedSowingRules.TransplantPermission);
            cmd.Parameters.AddWithValue("@U", userId.Value);
            cmd.Parameters.AddWithValue("@A", areaId);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        // The Areas in which this user may transplant (null = all: System Administrator).
        public async Task<IReadOnlyCollection<int>?> TransplantAreaIdsAsync(int? userId, bool isFullAccess)
        {
            if (isFullAccess)
                return null;
            var list = new List<int>();
            if (userId is not > 0)
                return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT DISTINCT ur.AreaId FROM dbo.UserRoles ur
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND ISNULL(u.IsActive, 0) = 1
JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = @Code
WHERE ur.UserId = @U AND ur.AreaId IS NOT NULL", conn);
            cmd.Parameters.AddWithValue("@Code", BedSowingRules.TransplantPermission);
            cmd.Parameters.AddWithValue("@U", userId.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(r.GetInt32(0));
            return list;
        }

        // ---- Create ------------------------------------------------------------------------

        public async Task<(bool Success, string? Message, int Id)> CreateAsync(BedSowing entry, Actor actor, Func<int, bool> canAccessArea)
        {
            var (qtyOk, qtyError) = BedSowingRules.ValidateBedQuantity(entry.Quantity);
            if (!qtyOk)
                return (false, qtyError, 0);
            var (dateOk, dateError) = BedSowingRules.ValidateBusinessDate(entry.SowingDate, AppClock.Today, "Sowing Date");
            if (!dateOk)
                return (false, dateError, 0);
            if (entry.SeedStockId <= 0)
                return (false, "Main Office seed lot is required.", 0);
            if (entry.PolyhouseId <= 0)
                return (false, "Polyhouse is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return (false, "Bed Sowing is not set up in this database yet.", 0);
            using var tx = conn.BeginTransaction();
            try
            {
                // A repeat of an already-saved form finds that bed and creates nothing.
                if (entry.SubmissionToken is { } token && token != Guid.Empty)
                {
                    using var dup = new SqlCommand("SELECT Id, BedSowingCode FROM dbo.BedSowings WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T", conn, tx);
                    dup.Parameters.AddWithValue("@T", token);
                    using var dr = await dup.ExecuteReaderAsync();
                    if (await dr.ReadAsync())
                    {
                        entry.Id = dr.GetInt32(0);
                        entry.BedSowingCode = dr.GetString(1);
                        entry.IsDuplicateSubmission = true;
                        dr.Close();
                        tx.Commit();
                        return (true, null, entry.Id);
                    }
                }

                // Location: a Bed Sowing Polyhouse; the Area is the database's.
                var location = (await ReadLocationsAsync(conn, tx, entry.PolyhouseId)).FirstOrDefault();
                if (location == null)
                {
                    tx.Rollback();
                    return (false, BedSowingRules.NotBedLocationMessage, 0);
                }
                if (!canAccessArea(location.AreaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to sow in this Area.", 0);
                }

                // Lock the seed lot: an active Main Office lot of the chosen variety, enough available.
                var lockCmd = new SqlCommand(@"
SELECT ss.SpeciesId, ss.BatchNo, ss.SeedSourceId, ss.PhysicalQuantity, ss.InTransitQuantity, a.AreaType, a.IsActive
FROM dbo.SeedStock ss WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Area a ON a.Id = ss.AreaId
WHERE ss.Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", entry.SeedStockId);
                int speciesId; string lotNo; int? seedSourceId; decimal physical, inTransit; string? lotAreaType; bool lotAreaActive;
                using (var r = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Selected Main Office seed lot not found.", 0);
                    }
                    speciesId = r.GetInt32(0);
                    lotNo = r.GetString(1);
                    seedSourceId = r.IsDBNull(2) ? null : r.GetInt32(2);
                    physical = r.GetDecimal(3);
                    inTransit = r.GetDecimal(4);
                    lotAreaType = r.IsDBNull(5) ? null : r.GetString(5);
                    lotAreaActive = r.GetBoolean(6);
                }
                if (!DirectSowingRules.IsMainOfficeSeedLocation(lotAreaType, lotAreaActive))
                {
                    tx.Rollback();
                    return (false, "Bed Sowing must use seed from an active Main Office Seed Stock lot.", 0);
                }
                if (entry.SpeciesId > 0 && entry.SpeciesId != speciesId)
                {
                    tx.Rollback();
                    return (false, "The selected seed lot does not belong to the selected Variety.", 0);
                }
                var (enough, _, stockError) = DirectSowingRules.CheckSeedAvailability(physical, inTransit, entry.Quantity, "Quantity");
                if (!enough)
                {
                    tx.Rollback();
                    return (false, stockError, 0);
                }

                // 2026-10-13: no Ready Date at bed stage -- it starts at the transplant into trays. Only a
                // database without 2026-10-13_TraysToSowingApprovals.sql still requires the old pair
                // (bed date + Ready Days; NOT NULL there) -- stored, never shown.
                var traysReady = await BedSowingSchema.TraysToApprovalsAsync(conn, tx);
                int? legacyDays = null;
                DateTime? legacyReadyDate = null;
                if (!traysReady)
                {
                    var (days, varietyName) = await TrayBatchWriter.ReadyDaysAsync(conn, tx, speciesId);
                    legacyReadyDate = TrayBatchRules.ReadyDate(entry.SowingDate, days);
                    if (!legacyReadyDate.HasValue)
                    {
                        tx.Rollback();
                        return (false, string.Format(BedSowingRules.NoReadyDaysMessageFormat, varietyName), 0);
                    }
                    legacyDays = days;
                }

                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, BedPrefix, AppClock.Today.Year);
                using var insert = new SqlCommand(@"
INSERT INTO dbo.BedSowings
(BedSowingCode, SpeciesId, SeedStockId, SeedLotNo, SeedSourceId, AreaId, PolyhouseId, SowingDate, Quantity, ReadyStockDays, ExpectedReadyDate,
 Status, Remarks, SubmissionToken, CreatedBy, CreatedById)
VALUES
(@Code, @SpeciesId, @SeedStockId, @LotNo, @SeedSourceId, @AreaId, @PolyhouseId, @SowingDate, @Quantity, @Days, @ReadyDate,
 N'PendingTransplant', @Remarks, @Token, @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@SpeciesId", speciesId);
                insert.Parameters.AddWithValue("@SeedStockId", entry.SeedStockId);
                insert.Parameters.AddWithValue("@LotNo", lotNo);
                insert.Parameters.AddWithValue("@SeedSourceId", (object?)seedSourceId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@AreaId", location.AreaId);
                insert.Parameters.AddWithValue("@PolyhouseId", location.PolyhouseId);
                insert.Parameters.Add("@SowingDate", System.Data.SqlDbType.Date).Value = entry.SowingDate.Date;
                insert.Parameters.AddWithValue("@Quantity", entry.Quantity);
                insert.Parameters.AddWithValue("@Days", (object?)legacyDays ?? DBNull.Value);
                insert.Parameters.Add("@ReadyDate", System.Data.SqlDbType.Date).Value = (object?)legacyReadyDate ?? DBNull.Value;
                insert.Parameters.AddWithValue("@Remarks", string.IsNullOrWhiteSpace(entry.Remarks) ? DBNull.Value : entry.Remarks.Trim());
                insert.Parameters.AddWithValue("@Token", entry.SubmissionToken is { } t2 && t2 != Guid.Empty ? t2 : DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)actor.UserName ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedById", (object?)actor.UserId ?? DBNull.Value);
                var id = (int)(await insert.ExecuteScalarAsync())!;

                // Seed leaves the lot on the bed's BUSINESS date (CreatedAt keeps the recorded time); a
                // back-dated sowing must fit the lot's history (SeedStockRepository.AvailableFromAsync).
                var (seedOk, seedMessage) = await _seedStockRepo.RecordTransactionAsync(
                    conn, tx, entry.SeedStockId, -entry.Quantity, "Sown", LedgerReference, id, actor.UserId, $"Bed Sowing {code}",
                    LedgerDates.Instant(entry.SowingDate).TransactionUtc);
                if (!seedOk)
                {
                    tx.Rollback();
                    return (false, seedMessage, 0);
                }

                tx.Commit();
                entry.Id = id;
                entry.BedSowingCode = code;
                entry.SpeciesId = speciesId;
                entry.AreaId = location.AreaId;
                entry.ReadyStockDays = legacyDays;
                entry.ExpectedReadyDate = legacyReadyDate;
                entry.Status = BedSowing.StatusPending;
                return (true, null, id);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        // ---- Transplant --------------------------------------------------------------------

        public async Task<(bool Success, string? Message, int OperationId, int SeedSowingId, bool Duplicate)> TransplantAsync(BedTransplantRequest req, Actor actor)
        {
            if (req.SubmissionToken is not { } token || token == Guid.Empty)
                return (false, "This form has expired. Reload the page and enter the transplant again.", 0, 0, false);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return (false, "Bed Sowing is not set up in this database yet.", 0, 0, false);
            if (!await BedSowingSchema.TraysToApprovalsAsync(conn))
                return (false, TraysMigrationMessage, 0, 0, false);
            using var tx = conn.BeginTransaction();
            try
            {
                var existing = await FindOperationByTokenAsync(conn, tx, token);
                if (existing.HasValue)
                {
                    tx.Commit();
                    return (true, null, existing.Value.Id, existing.Value.SeedSowingId ?? 0, true);
                }

                var bed = await LockBedAsync(conn, tx, req.BedSowingId);
                if (bed == null)
                {
                    tx.Rollback();
                    return (false, "Bed Sowing not found.", 0, 0, false);
                }
                var (authorized, authError) = await AuthorizeOperationAsync(conn, tx, actor, bed.AreaId);
                if (!authorized)
                {
                    tx.Rollback();
                    return (false, authError, 0, 0, false);
                }
                if (!bed.IsOpen)
                {
                    tx.Rollback();
                    return (false, $"Bed Sowing {bed.BedSowingCode} is {BedSowingRules.StatusLabel(bed.Status).ToLowerInvariant()}; nothing is left to transplant.", 0, 0, false);
                }

                // Target: the Bed Sowing Polyhouse of the bed's own Area, or a Polyhouse of an active
                // Mother Plant Area (Areas from the database, never from the request).
                var target = (await ReadTransplantTargetsAsync(conn, tx, bed.AreaId, req.TargetPolyhouseId)).FirstOrDefault();
                if (target == null)
                {
                    tx.Rollback();
                    return (false, "Transplant into the Bed Sowing Polyhouse of this bed's Area or into a Polyhouse of an active Mother Plant Area.", 0, 0, false);
                }

                // Transplanting ALL the remaining seedlings: complete trays + the remainder as a system-generated
                // bed loss, in this one transaction (BedSowingRules.PlanTransplant). Part of a bed: complete trays only.
                var (ok, plan, error) = BedSowingRules.PlanTransplant(
                    bed.RemainingQuantity, req.Quantity, req.CavityType, req.OutputTrays, req.TransplantDate, bed.SowingDate, AppClock.Today);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error, 0, 0, false);
                }
                var trays = plan!.Trays;
                var (instantOk, transplantUtc, instantError) = LedgerDates.Instant(req.TransplantDate);
                if (!instantOk)
                {
                    tx.Rollback();
                    return (false, instantError, 0, 0, false);
                }

                int transplantOpId = 0, sowingId = 0;
                string? transplantCode = null, sowingCode = null;
                int? readyDays = null;
                DateTime? readyDate = null;
                int? materialId = null;
                if (plan.MakesTrayBatch)
                {
                    // Ready Date = ACTUAL TRANSPLANT DATE + the variety's configured Ready Days (snapshot on
                    // the tray batch). None configured -> refused, nothing assumed. No supervisor is chosen:
                    // the batch is approved by any user who may approve sowings, never its recorder.
                    string? varietyName;
                    (readyDays, varietyName) = await TrayBatchWriter.ReadyDaysAsync(conn, tx, bed.SpeciesId);
                    readyDate = TrayBatchRules.ReadyDate(req.TransplantDate, readyDays);
                    if (!readyDate.HasValue)
                    {
                        tx.Rollback();
                        return (false, string.Format(TrayBatchRules.NoReadyDaysMessageFormat, varietyName), 0, 0, false);
                    }

                    // Growing material: required in a Main Office Polyhouse (existing rule).
                    var (materialOk, materialError, materialRequired) = await MaterialStockRepository.ValidateSowingMaterialAsync(conn, tx, target.AreaId, req.GrowingMaterialTypeId);
                    if (!materialOk)
                    {
                        tx.Rollback();
                        return (false, materialError, 0, 0, false);
                    }
                    materialId = materialRequired ? req.GrowingMaterialTypeId : null;

                    // The tray batch: the bed sowing date kept as its SowingDate (origin); Ready Days / Ready
                    // Date from the transplant (TrayBatchWriter -- shared with Mother Plant transplanting).
                    transplantCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, TransplantPrefix, AppClock.Today.Year);
                    var remarks = $"Transplanted from bed {bed.BedSowingCode} on {req.TransplantDate:dd/MM/yyyy} ({transplantCode})"
                        + (string.IsNullOrWhiteSpace(req.Remarks) ? "" : ": " + req.Remarks.Trim());
                    (sowingId, sowingCode) = await TrayBatchWriter.InsertAsync(conn, tx, _batchNumberRepo, new TrayBatchWriter.Spec(
                        SeedSowing.SourceBedSowing, bed.Id, bed.SpeciesId, target.AreaId, target.PolyhouseId, bed.SeedLotNo, bed.SeedSourceId,
                        req.CavityType!, trays, plan.Transplanted, bed.SowingDate, readyDays!.Value, readyDate.Value, materialId, remarks,
                        actor.UserName, actor.UserId));

                    using var opCmd = new SqlCommand(@"
INSERT INTO dbo.BedSowingOperations
(OperationCode, BedSowingId, OperationType, OperationDate, Quantity, CavityType, OutputTrays, TargetAreaId, TargetPolyhouseId, SeedSowingId,
 Remarks, SubmissionToken, CreatedBy, CreatedById)
VALUES
(@Code, @BedId, N'Transplant', @Date, @Qty, @Cavity, @Trays, @AreaId, @PolyhouseId, @SowingId, @Remarks, @Token, @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                    opCmd.Parameters.AddWithValue("@Code", transplantCode);
                    opCmd.Parameters.AddWithValue("@BedId", bed.Id);
                    opCmd.Parameters.Add("@Date", System.Data.SqlDbType.Date).Value = req.TransplantDate.Date;
                    opCmd.Parameters.AddWithValue("@Qty", plan.Transplanted);
                    opCmd.Parameters.AddWithValue("@Cavity", req.CavityType!);
                    opCmd.Parameters.AddWithValue("@Trays", trays);
                    opCmd.Parameters.AddWithValue("@AreaId", target.AreaId);
                    opCmd.Parameters.AddWithValue("@PolyhouseId", target.PolyhouseId);
                    opCmd.Parameters.AddWithValue("@SowingId", sowingId);
                    opCmd.Parameters.AddWithValue("@Remarks", string.IsNullOrWhiteSpace(req.Remarks) ? DBNull.Value : req.Remarks.Trim());
                    opCmd.Parameters.AddWithValue("@Token", token);
                    opCmd.Parameters.AddWithValue("@CreatedBy", (object?)actor.UserName ?? DBNull.Value);
                    opCmd.Parameters.AddWithValue("@CreatedById", (object?)actor.UserId ?? DBNull.Value);
                    transplantOpId = (int)(await opCmd.ExecuteScalarAsync())!;
                }

                // The remainder: an ordinary bed loss (same ledger, history, reports and reversal), dated at the
                // transplant's business date, recorded by the same Transplanter. It carries the form's token only
                // when it is the whole operation (no tray batch) -- otherwise the transplant row holds it.
                int lossOpId = 0;
                if (plan.Waste > 0)
                {
                    var lossCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, LossPrefix, AppClock.Today.Year);
                    using var lossCmd = new SqlCommand(@"
INSERT INTO dbo.BedSowingOperations
(OperationCode, BedSowingId, OperationType, OperationDate, Quantity, Reason, Remarks, SubmissionToken, CreatedBy, CreatedById)
VALUES (@Code, @BedId, N'Loss', @Date, @Qty, @Reason, @Remarks, @Token, @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                    lossCmd.Parameters.AddWithValue("@Code", lossCode);
                    lossCmd.Parameters.AddWithValue("@BedId", bed.Id);
                    lossCmd.Parameters.Add("@Date", System.Data.SqlDbType.Date).Value = req.TransplantDate.Date;
                    lossCmd.Parameters.AddWithValue("@Qty", plan.Waste);
                    lossCmd.Parameters.AddWithValue("@Reason", BedSowingRules.RemainderLossReason);
                    lossCmd.Parameters.AddWithValue("@Remarks", plan.MakesTrayBatch
                        ? $"System-generated with transplant {transplantCode}: {QuantityFormat.Qty(plan.Transplanted)} of {QuantityFormat.Qty(req.Quantity)} seedlings filled {trays} complete {req.CavityType} trays."
                        : $"System-generated: {QuantityFormat.Qty(req.Quantity)} seedlings do not fill one {req.CavityType} tray; no tray batch was created.");
                    lossCmd.Parameters.AddWithValue("@Token", plan.MakesTrayBatch ? DBNull.Value : token);
                    lossCmd.Parameters.AddWithValue("@CreatedBy", (object?)actor.UserName ?? DBNull.Value);
                    lossCmd.Parameters.AddWithValue("@CreatedById", (object?)actor.UserId ?? DBNull.Value);
                    lossOpId = (int)(await lossCmd.ExecuteScalarAsync())!;
                }

                // Original = Remaining + Transplanted + Lost (TR_BedSowings_Guard checks the totals against the
                // active operations); the bed closes when nothing remains.
                await UpdateBedTotalsAsync(conn, tx, bed, bed.TransplantedQuantity + plan.Transplanted, bed.LostQuantity + plan.Waste, actor.UserName);

                if (plan.MakesTrayBatch)
                {
                    // Empty trays and the growing material for them, BOTH dated at the transplant's business
                    // date (CreatedAt keeps the recorded time); a back-dated transplant must fit each pool's history.
                    var (trayOk, trayMessage) = await _trayStockRepo.ConsumeForSowingAsync(
                        conn, tx, target.AreaId, target.PolyhouseId, req.CavityType!, trays, "SeedSowing", sowingId, actor.UserId,
                        $"Bed transplant {transplantCode} from {bed.BedSowingCode} (batch {sowingCode})", transplantUtc);
                    if (!trayOk)
                    {
                        tx.Rollback();
                        return (false, trayMessage, 0, 0, false);
                    }
                    var (matOk, matMessage) = await MaterialStockRepository.ConsumeForSowingAsync(
                        conn, tx, target.AreaId, target.PolyhouseId, materialId, req.CavityType, trays, sowingId, sowingCode!, actor.UserId, transplantUtc);
                    if (!matOk)
                    {
                        tx.Rollback();
                        return (false, matMessage, 0, 0, false);
                    }
                }

                tx.Commit();
                var summary = plan.Summary(req.CavityType, done: true);
                return (true, summary + (readyDate.HasValue ? $" Ready Date {readyDate.Value:dd-MM-yyyy} (transplant date + {readyDays} days)." : ""),
                    plan.MakesTrayBatch ? transplantOpId : lossOpId, sowingId, false);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0, 0, false);
            }
        }

        // ---- Bed loss ----------------------------------------------------------------------

        public async Task<(bool Success, string? Message, int OperationId, bool Duplicate)> RecordLossAsync(
            int bedSowingId, decimal quantity, DateTime lossDate, string? reason, string? remarks, Guid? submissionToken, Actor actor)
        {
            if (submissionToken is not { } token || token == Guid.Empty)
                return (false, "This form has expired. Reload the page and enter the loss again.", 0, false);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return (false, "Bed Sowing is not set up in this database yet.", 0, false);
            using var tx = conn.BeginTransaction();
            try
            {
                var existing = await FindOperationByTokenAsync(conn, tx, token);
                if (existing.HasValue)
                {
                    tx.Commit();
                    return (true, null, existing.Value.Id, true);
                }
                var bed = await LockBedAsync(conn, tx, bedSowingId);
                if (bed == null)
                {
                    tx.Rollback();
                    return (false, "Bed Sowing not found.", 0, false);
                }
                var (authorized, authError) = await AuthorizeOperationAsync(conn, tx, actor, bed.AreaId);
                if (!authorized)
                {
                    tx.Rollback();
                    return (false, authError, 0, false);
                }
                if (!bed.IsOpen)
                {
                    tx.Rollback();
                    return (false, $"Bed Sowing {bed.BedSowingCode} is {BedSowingRules.StatusLabel(bed.Status).ToLowerInvariant()}.", 0, false);
                }
                var (ok, error) = BedSowingRules.ValidateLoss(bed.RemainingQuantity, quantity, reason, lossDate, bed.SowingDate, AppClock.Today);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error, 0, false);
                }

                var opCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, LossPrefix, AppClock.Today.Year);
                using var opCmd = new SqlCommand(@"
INSERT INTO dbo.BedSowingOperations
(OperationCode, BedSowingId, OperationType, OperationDate, Quantity, Reason, Remarks, SubmissionToken, CreatedBy, CreatedById)
VALUES (@Code, @BedId, N'Loss', @Date, @Qty, @Reason, @Remarks, @Token, @CreatedBy, @CreatedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                opCmd.Parameters.AddWithValue("@Code", opCode);
                opCmd.Parameters.AddWithValue("@BedId", bed.Id);
                opCmd.Parameters.Add("@Date", System.Data.SqlDbType.Date).Value = lossDate.Date;
                opCmd.Parameters.AddWithValue("@Qty", quantity);
                opCmd.Parameters.AddWithValue("@Reason", reason!.Trim());
                opCmd.Parameters.AddWithValue("@Remarks", string.IsNullOrWhiteSpace(remarks) ? DBNull.Value : remarks.Trim());
                opCmd.Parameters.AddWithValue("@Token", token);
                opCmd.Parameters.AddWithValue("@CreatedBy", (object?)actor.UserName ?? DBNull.Value);
                opCmd.Parameters.AddWithValue("@CreatedById", (object?)actor.UserId ?? DBNull.Value);
                var opId = (int)(await opCmd.ExecuteScalarAsync())!;

                await UpdateBedTotalsAsync(conn, tx, bed, bed.TransplantedQuantity, bed.LostQuantity + quantity, actor.UserName);
                tx.Commit();
                return (true, null, opId, false);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0, false);
            }
        }

        // ---- Reversal ----------------------------------------------------------------------

        public async Task<(bool Success, string? Message)> ReverseOperationAsync(int operationId, string? reason, Actor actor)
        {
            var (reasonOk, reasonError) = BedSowingRules.ValidateReason(reason, "reverse a transplant or loss");
            if (!reasonOk)
                return (false, reasonError);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return (false, "Bed Sowing is not set up in this database yet.");
            using var tx = conn.BeginTransaction();
            try
            {
                int bedId; int? sowingId; string type, status, code; decimal quantity;
                using (var cmd = new SqlCommand(
                    "SELECT BedSowingId, SeedSowingId, OperationType, Status, OperationCode, Quantity FROM dbo.BedSowingOperations WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@Id", operationId);
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Bed Sowing operation not found.");
                    }
                    bedId = r.GetInt32(0);
                    sowingId = r.IsDBNull(1) ? null : r.GetInt32(1);
                    type = r.GetString(2);
                    status = r.GetString(3);
                    code = r.GetString(4);
                    quantity = r.GetDecimal(5);
                }
                var bed = await LockBedAsync(conn, tx, bedId);
                if (bed == null)
                {
                    tx.Rollback();
                    return (false, "Bed Sowing not found.");
                }
                // Reversal is a correction: the Bed Sowing Transplanter of the Area, or a System
                // Administrator (management). An administrator's reversal is written to the audit log.
                var isTransplanter = await IsTransplanterAsync(conn, tx, actor.UserId, bed.AreaId);
                if (actor.UserId is not > 0 || (!isTransplanter && !actor.IsFullAccess))
                {
                    tx.Rollback();
                    return (false, BedSowingRules.NotTransplanterMessage);
                }
                if (status != BedSowingOperation.StatusActive)
                {
                    tx.Rollback();
                    return (false, $"{code} is already reversed.");
                }

                if (type == BedSowingOperation.TypeTransplant)
                {
                    // The tray batch must still be untouched by the Sowing Approval.
                    string sowingStatus; decimal approved, wastage;
                    using (var cmd = new SqlCommand(
                        "SELECT Status, ConfirmedReadyQuantity, WastageQuantity FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@Id", sowingId!.Value);
                        using var r = await cmd.ExecuteReaderAsync();
                        if (!await r.ReadAsync())
                        {
                            r.Close();
                            tx.Rollback();
                            return (false, "The tray batch of this transplant was not found.");
                        }
                        sowingStatus = r.GetString(0);
                        approved = r.GetDecimal(1);
                        wastage = r.GetDecimal(2);
                    }
                    var (mayReverse, reverseError) = BedSowingRules.CanReverseTransplant(sowingStatus, approved, wastage);
                    if (!mayReverse)
                    {
                        tx.Rollback();
                        return (false, reverseError);
                    }
                    var note = $"Bed transplant {code} reversed: {reason!.Trim()}";
                    var (trayOk, trayMessage) = await _trayStockRepo.ReverseConsumptionAsync(conn, tx, "SeedSowing", sowingId.Value, actor.UserId, note.Length > 500 ? note[..500] : note);
                    if (!trayOk)
                    {
                        tx.Rollback();
                        return (false, trayMessage);
                    }
                    var (matOk, matMessage) = await MaterialStockRepository.ReverseForSowingAsync(conn, tx, sowingId.Value, actor.UserId, note.Length > 500 ? note[..500] : note);
                    if (!matOk)
                    {
                        tx.Rollback();
                        return (false, matMessage);
                    }
                    using var cancel = new SqlCommand(
                        "UPDATE dbo.SeedSowings SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @By WHERE Id = @Id", conn, tx);
                    cancel.Parameters.AddWithValue("@By", (object?)actor.UserName ?? DBNull.Value);
                    cancel.Parameters.AddWithValue("@Id", sowingId.Value);
                    await cancel.ExecuteNonQueryAsync();
                }

                using (var mark = new SqlCommand(@"
UPDATE dbo.BedSowingOperations
SET Status = N'Reversed', ReversedDate = SYSUTCDATETIME(), ReversedBy = @By, ReversedById = @ById, ReversalReason = @Reason
WHERE Id = @Id AND Status = N'Active'", conn, tx))
                {
                    mark.Parameters.AddWithValue("@By", (object?)actor.UserName ?? DBNull.Value);
                    mark.Parameters.AddWithValue("@ById", (object?)actor.UserId ?? DBNull.Value);
                    mark.Parameters.AddWithValue("@Reason", reason!.Trim());
                    mark.Parameters.AddWithValue("@Id", operationId);
                    if (await mark.ExecuteNonQueryAsync() != 1)
                    {
                        tx.Rollback();
                        return (false, $"{code} is already reversed.");
                    }
                }

                if (type == BedSowingOperation.TypeTransplant)
                    await UpdateBedTotalsAsync(conn, tx, bed, bed.TransplantedQuantity - quantity, bed.LostQuantity, actor.UserName);
                else
                    await UpdateBedTotalsAsync(conn, tx, bed, bed.TransplantedQuantity, bed.LostQuantity - quantity, actor.UserName);
                if (!isTransplanter)
                    await AuditAsync(conn, tx, "AdminReversal", bed.Id, operationId, actor, reason,
                        $"{code} reversed by a System Administrator, not the Bed Sowing Transplanter");

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        // ---- Cancel a bed ------------------------------------------------------------------

        public async Task<(bool Success, string? Message)> CancelAsync(int bedSowingId, string? reason, Actor actor, Func<int, bool> canAccessArea)
        {
            var (reasonOk, reasonError) = BedSowingRules.ValidateReason(reason, "cancel a bed sowing");
            if (!reasonOk)
                return (false, reasonError);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return (false, "Bed Sowing is not set up in this database yet.");
            using var tx = conn.BeginTransaction();
            try
            {
                var bed = await LockBedAsync(conn, tx, bedSowingId);
                if (bed == null)
                {
                    tx.Rollback();
                    return (false, "Bed Sowing not found.");
                }
                if (!canAccessArea(bed.AreaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to work in this bed's Area.");
                }
                if (bed.Status == BedSowing.StatusCancelled)
                {
                    tx.Rollback();
                    return (false, $"Bed Sowing {bed.BedSowingCode} is already cancelled.");
                }
                if (bed.TransplantedQuantity > 0 || bed.LostQuantity > 0)
                {
                    tx.Rollback();
                    return (false, "Seedlings of this bed were already transplanted or recorded as lost. Reverse those first; only an untouched bed can be cancelled.");
                }

                var (seedOk, seedMessage) = await _seedStockRepo.RecordTransactionAsync(
                    conn, tx, bed.SeedStockId, bed.Quantity, "ReversalReturn", LedgerReference, bed.Id, actor.UserId,
                    $"Reversal of cancelled Bed Sowing {bed.BedSowingCode}");
                if (!seedOk)
                {
                    tx.Rollback();
                    return (false, seedMessage);
                }
                using var cmd = new SqlCommand(@"
UPDATE dbo.BedSowings
SET Status = N'Cancelled', CancelledDate = SYSUTCDATETIME(), CancelledById = @ById, CancelReason = @Reason,
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @By
WHERE Id = @Id", conn, tx);
                cmd.Parameters.AddWithValue("@ById", (object?)actor.UserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", reason!.Trim());
                cmd.Parameters.AddWithValue("@By", (object?)actor.UserName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", bed.Id);
                await cmd.ExecuteNonQueryAsync();
                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        // ---- Reads -------------------------------------------------------------------------

        private const string BedSelect = @"
SELECT b.Id, b.BedSowingCode, b.SpeciesId, RTRIM(ps.Name), pt.Name, b.SeedStockId, b.SeedLotNo, b.SeedSourceId, src.Name,
       b.AreaId, a.Name, b.PolyhouseId, p.Name, b.SowingDate, b.Quantity, b.ReadyStockDays, b.ExpectedReadyDate,
       b.TransplantedQuantity, b.LostQuantity, b.Status, b.Remarks, b.CreatedDate, b.CreatedBy, b.CreatedById,
       b.CancelledDate, cu.Name, b.CancelReason
FROM dbo.BedSowings b
JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
LEFT JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
JOIN dbo.Area a ON a.Id = b.AreaId
JOIN dbo.Polyhouses p ON p.Id = b.PolyhouseId
LEFT JOIN dbo.SeedSources src ON src.Id = b.SeedSourceId
LEFT JOIN dbo.IMSUsers cu ON cu.Id = b.CancelledById";

        private static BedSowing MapBed(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            BedSowingCode = r.GetString(1),
            SpeciesId = r.GetInt32(2),
            SpeciesName = r.GetString(3),
            PlantTypeName = r.IsDBNull(4) ? null : r.GetString(4),
            SeedStockId = r.GetInt32(5),
            SeedLotNo = r.GetString(6),
            SeedSourceId = r.IsDBNull(7) ? null : r.GetInt32(7),
            SeedSourceName = r.IsDBNull(8) ? null : r.GetString(8),
            AreaId = r.GetInt32(9),
            AreaName = r.GetString(10),
            PolyhouseId = r.GetInt32(11),
            PolyhouseName = r.GetString(12),
            SowingDate = r.GetDateTime(13),
            Quantity = r.GetDecimal(14),
            ReadyStockDays = r.IsDBNull(15) ? null : r.GetInt32(15),
            ExpectedReadyDate = r.IsDBNull(16) ? null : r.GetDateTime(16),
            TransplantedQuantity = r.GetDecimal(17),
            LostQuantity = r.GetDecimal(18),
            Status = r.GetString(19),
            Remarks = r.IsDBNull(20) ? null : r.GetString(20),
            CreatedDate = r.GetDateTime(21),
            CreatedBy = r.IsDBNull(22) ? null : r.GetString(22),
            CreatedById = r.IsDBNull(23) ? null : r.GetInt32(23),
            CancelledDate = r.IsDBNull(24) ? null : r.GetDateTime(24),
            CancelledByName = r.IsDBNull(25) ? null : r.GetString(25),
            CancelReason = r.IsDBNull(26) ? null : r.GetString(26),
        };

        // areaIds null = every Area. openOnly: pending / partially transplanted only.
        public async Task<List<BedSowing>> ListAsync(IReadOnlyCollection<int>? areaIds, bool openOnly = false, int max = 500)
        {
            var list = new List<BedSowing>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return list;
            using var cmd = new SqlCommand(Top(BedSelect) + @"
WHERE (@Areas IS NULL OR b.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ',')))
  AND (@Open = 0 OR b.Status IN (N'PendingTransplant', N'PartiallyTransplanted'))
ORDER BY b.SowingDate DESC, b.Id DESC", conn);
            cmd.Parameters.AddWithValue("@Max", max);
            cmd.Parameters.AddWithValue("@Areas", areaIds == null ? DBNull.Value : string.Join(",", areaIds.DefaultIfEmpty(-1)));
            cmd.Parameters.AddWithValue("@Open", openOnly ? 1 : 0);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapBed(r));
            return list;
        }

        public async Task<BedSowing?> GetAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return null;
            using var cmd = new SqlCommand(BedSelect + " WHERE b.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapBed(r) : null;
        }

        private const string OperationSelect = @"
SELECT o.Id, o.OperationCode, o.BedSowingId, b.BedSowingCode, o.OperationType, o.OperationDate, o.Quantity, o.CavityType, o.OutputTrays,
       o.TargetAreaId, ta.Name, o.TargetPolyhouseId, tp.Name, o.SeedSowingId, sw.SowingCode, sw.Status, sw.ConfirmedReadyQuantity, sw.WastageQuantity,
       su.Name, gm.Name, o.Reason, o.Remarks, o.Status, o.CreatedDate, o.CreatedBy, o.CreatedById, o.ReversedDate, o.ReversedBy, o.ReversalReason,
       sw.ExpectedReadyDate
FROM dbo.BedSowingOperations o
JOIN dbo.BedSowings b ON b.Id = o.BedSowingId
LEFT JOIN dbo.Area ta ON ta.Id = o.TargetAreaId
LEFT JOIN dbo.Polyhouses tp ON tp.Id = o.TargetPolyhouseId
LEFT JOIN dbo.SeedSowings sw ON sw.Id = o.SeedSowingId
LEFT JOIN dbo.IMSUsers su ON su.Id = sw.SupervisorId
LEFT JOIN dbo.MaterialTypes gm ON gm.Id = sw.GrowingMaterialTypeId";

        private static BedSowingOperation MapOperation(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            OperationCode = r.GetString(1),
            BedSowingId = r.GetInt32(2),
            BedSowingCode = r.GetString(3),
            OperationType = r.GetString(4),
            OperationDate = r.GetDateTime(5),
            Quantity = r.GetDecimal(6),
            CavityType = r.IsDBNull(7) ? null : r.GetString(7),
            OutputTrays = r.IsDBNull(8) ? null : r.GetInt32(8),
            TargetAreaId = r.IsDBNull(9) ? null : r.GetInt32(9),
            TargetAreaName = r.IsDBNull(10) ? null : r.GetString(10),
            TargetPolyhouseId = r.IsDBNull(11) ? null : r.GetInt32(11),
            TargetPolyhouseName = r.IsDBNull(12) ? null : r.GetString(12),
            SeedSowingId = r.IsDBNull(13) ? null : r.GetInt32(13),
            SowingCode = r.IsDBNull(14) ? null : r.GetString(14),
            SowingStatus = r.IsDBNull(15) ? null : r.GetString(15),
            SowingApprovedQuantity = r.IsDBNull(16) ? 0 : r.GetDecimal(16),
            SowingWastageQuantity = r.IsDBNull(17) ? 0 : r.GetDecimal(17),
            SupervisorName = r.IsDBNull(18) ? null : r.GetString(18),
            GrowingMaterialName = r.IsDBNull(19) ? null : r.GetString(19),
            Reason = r.IsDBNull(20) ? null : r.GetString(20),
            Remarks = r.IsDBNull(21) ? null : r.GetString(21),
            Status = r.GetString(22),
            CreatedDate = r.GetDateTime(23),
            CreatedBy = r.IsDBNull(24) ? null : r.GetString(24),
            CreatedById = r.IsDBNull(25) ? null : r.GetInt32(25),
            ReversedDate = r.IsDBNull(26) ? null : r.GetDateTime(26),
            ReversedBy = r.IsDBNull(27) ? null : r.GetString(27),
            ReversalReason = r.IsDBNull(28) ? null : r.GetString(28),
            SowingReadyDate = r.IsDBNull(29) ? null : r.GetDateTime(29),
        };

        public async Task<List<BedSowingOperation>> GetOperationsAsync(int bedSowingId)
        {
            var list = new List<BedSowingOperation>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return list;
            using var cmd = new SqlCommand(OperationSelect + " WHERE o.BedSowingId = @Id ORDER BY o.OperationDate, o.Id", conn);
            cmd.Parameters.AddWithValue("@Id", bedSowingId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapOperation(r));
            return list;
        }

        // Every transplant / loss (newest first) of the given Areas (null = all): the history page.
        public async Task<List<BedSowingOperation>> GetHistoryAsync(IReadOnlyCollection<int>? areaIds, DateTime? fromDate, DateTime? toDate, int max = 1000)
        {
            var list = new List<BedSowingOperation>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn))
                return list;
            using var cmd = new SqlCommand(Top(OperationSelect) + @"
WHERE (@Areas IS NULL OR b.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ',')))
  AND (@From IS NULL OR o.OperationDate >= @From) AND (@To IS NULL OR o.OperationDate <= @To)
ORDER BY o.OperationDate DESC, o.Id DESC", conn);
            cmd.Parameters.AddWithValue("@Max", max);
            cmd.Parameters.AddWithValue("@Areas", areaIds == null ? DBNull.Value : string.Join(",", areaIds.DefaultIfEmpty(-1)));
            cmd.Parameters.Add("@From", System.Data.SqlDbType.Date).Value = (object?)fromDate?.Date ?? DBNull.Value;
            cmd.Parameters.Add("@To", System.Data.SqlDbType.Date).Value = (object?)toDate?.Date ?? DBNull.Value;
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapOperation(r));
            return list;
        }

        // ---- Helpers -----------------------------------------------------------------------

        // "SELECT ..." -> "SELECT TOP (@Max) ..." (the select lists have unnamed columns, so they are
        // never wrapped in a derived table).
        private static string Top(string select)
        {
            var at = select.IndexOf("SELECT ", StringComparison.Ordinal);
            return select[..at] + "SELECT TOP (@Max) " + select[(at + "SELECT ".Length)..];
        }

        private sealed class LockedBed
        {
            public int Id, SpeciesId, SeedStockId, AreaId, PolyhouseId;
            public int? SeedSourceId;
            public string BedSowingCode = "", SeedLotNo = "", Status = "";
            public DateTime SowingDate;
            public decimal Quantity, TransplantedQuantity, LostQuantity;
            public decimal RemainingQuantity => Quantity - TransplantedQuantity - LostQuantity;
            public bool IsOpen => Status is BedSowing.StatusPending or BedSowing.StatusPartial;
        }

        private static async Task<LockedBed?> LockBedAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            using var cmd = new SqlCommand(@"
SELECT Id, BedSowingCode, SpeciesId, SeedStockId, SeedLotNo, SeedSourceId, AreaId, PolyhouseId, SowingDate, Quantity,
       TransplantedQuantity, LostQuantity, Status
FROM dbo.BedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return new LockedBed
            {
                Id = r.GetInt32(0), BedSowingCode = r.GetString(1), SpeciesId = r.GetInt32(2), SeedStockId = r.GetInt32(3),
                SeedLotNo = r.GetString(4), SeedSourceId = r.IsDBNull(5) ? null : r.GetInt32(5), AreaId = r.GetInt32(6),
                PolyhouseId = r.GetInt32(7), SowingDate = r.GetDateTime(8), Quantity = r.GetDecimal(9),
                TransplantedQuantity = r.GetDecimal(10), LostQuantity = r.GetDecimal(11), Status = r.GetString(12),
            };
        }

        private static async Task<(int Id, int? SeedSowingId)?> FindOperationByTokenAsync(SqlConnection conn, SqlTransaction tx, Guid token)
        {
            using var cmd = new SqlCommand("SELECT Id, SeedSowingId FROM dbo.BedSowingOperations WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T", conn, tx);
            cmd.Parameters.AddWithValue("@T", token);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return (r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1));
        }

        // Transplant / bed loss: ONLY the Bed Sowing Transplanter of the bed's Area (Maya Shinde) --
        // nobody else, a System Administrator included, and there is no exception path. Administrators
        // manage and reverse (ReverseOperationAsync, audited). TR_BedSowingOperations_Transplanter repeats it.
        private static async Task<(bool Ok, string? Error)> AuthorizeOperationAsync(SqlConnection conn, SqlTransaction tx, Actor actor, int areaId)
        {
            if (actor.UserId is not > 0)
                return (false, "Your user could not be identified.");
            return await IsTransplanterAsync(conn, tx, actor.UserId, areaId)
                ? (true, null)
                : (false, BedSowingRules.NotTransplanterMessage);
        }

        // dbo.BedSowingAuditLog: administrator reversals (role / permission changes are logged by the
        // database triggers of the same migration).
        private static async Task AuditAsync(SqlConnection conn, SqlTransaction tx, string action, int bedSowingId, int? operationId,
            Actor actor, string? reason, string? details)
        {
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.BedSowingAuditLog (Action, BedSowingId, OperationId, UserId, UserName, Reason, Details)
VALUES (@Action, @Bed, @Op, @U, @UN, @Reason, @Details);", conn, tx);
            cmd.Parameters.AddWithValue("@Action", action);
            cmd.Parameters.AddWithValue("@Bed", bedSowingId);
            cmd.Parameters.AddWithValue("@Op", (object?)operationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@U", (object?)actor.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UN", (object?)actor.UserName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason.Trim());
            cmd.Parameters.AddWithValue("@Details", (object?)details ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // New totals + the status they mean; TR_BedSowings_Guard re-checks them against the
        // active operations, CK_BedSowings_Quantities / _Status against the bed quantity.
        private static async Task UpdateBedTotalsAsync(SqlConnection conn, SqlTransaction tx, LockedBed bed, decimal transplanted, decimal lost, string? by)
        {
            using var cmd = new SqlCommand(@"
UPDATE dbo.BedSowings
SET TransplantedQuantity = @T, LostQuantity = @L, Status = @S, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @By
WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@T", transplanted);
            cmd.Parameters.AddWithValue("@L", lost);
            cmd.Parameters.AddWithValue("@S", BedSowingRules.StatusFor(bed.Quantity, transplanted, lost));
            cmd.Parameters.AddWithValue("@By", (object?)by ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", bed.Id);
            await cmd.ExecuteNonQueryAsync();
            bed.TransplantedQuantity = transplanted;
            bed.LostQuantity = lost;
        }
    }
}
