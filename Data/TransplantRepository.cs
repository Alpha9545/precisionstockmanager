using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Transplanting Ready Stock in a Mother Plant Area (dbo.ReadyStockTransplants); rules in
    // Services/TransplantRules.cs. ONE transaction per operation -- every check is repeated on
    // values re-read under lock, and either everything commits or nothing does:
    //   record   source batch: -transplanted ('TransplantOut') -lost ('TransplantLoss') in ONE balance
    //            change; NEW output batch in the target cavity: +transplanted ('TransplantIn');
    //            Tray Stock (target Area + Polyhouse + cavity): -output trays ('Transplant')
    //   reverse  only while the output batch is untouched: output -transplanted ('ReversalRemoval'),
    //            source +transplanted +lost ('ReversalReturn'), Tray Stock +output trays
    //            ('ReversalReturn'). Ledger rows are never deleted.
    // Lock order: (transplant row) -> source batch -> tray pool (sp_getapplock) -> output batch.
    // SubmissionToken (unique) makes a repeated save return the first transplanting.
    //
    // 2026-10-13 (2026-10-13_TraysToSowingApprovals.sql applied): the output is NOT Ready Stock any more
    // but a GROWING TRAY BATCH (dbo.SeedSowings, SourceType 'Transplant', TrayBatchWriter -- shared with
    // Bed Sowing) that goes through the existing Sowing Approvals; its Ready Date = transplant date +
    // the variety's Ready Days. Complete trays only on that path (approved Ready Stock is whole trays);
    // the rest stays in the received batch. Without the migration the earlier behaviour is unchanged.
    //
    // 2026-10-14 (2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql, MotherPlantAreaSchema): no Polyhouse is
    // chosen -- the trays come from the Mother Plant Area's own tray pool and the tray batch is the Area's; and a
    // batch received through a transfer for POT PRODUCTION is never transplanted into trays (refused under lock).
    public class TransplantRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly TrayStockRepository _trayStockRepo;
        private readonly BatchNumberRepository _batchNumberRepo;

        public TransplantRepository(DatabaseHelper dbHelper, ReadyStockRepository readyStockRepo, TrayStockRepository trayStockRepo, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _readyStockRepo = readyStockRepo;
            _trayStockRepo = trayStockRepo;
            _batchNumberRepo = batchNumberRepo;
        }

        // 2026-10-16: on the tray-batch path SelectedQuantity is the seedlings PROCESSED; the server derives the complete
        // trays and the waste (TransplantRules.PlanProcessing) and refuses posted Transplanted / Lost that differ.
        // WasteReason: why the remainder is wasted; DiscardAll: the user confirmed all processed seedlings are discarded.
        public record CreateRequest(int SourceReadyStockId, int? TargetAreaId, int? TargetPolyhouseId, string? TargetCavityType,
            decimal SelectedQuantity, decimal TransplantedQuantity, decimal LostQuantity, DateTime TransplantDate, string? Notes, Guid SubmissionToken,
            string? WasteReason = null, bool DiscardAll = false);

        public async Task<(bool Success, string? Message, int Id, bool Duplicate)> CreateAsync(
            CreateRequest req, MotherPlantTransferRepository.Actor actor, Func<int, bool> canAccessArea)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var dupCmd = new SqlCommand("SELECT Id FROM dbo.ReadyStockTransplants WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T", conn, tx);
                dupCmd.Parameters.AddWithValue("@T", req.SubmissionToken);
                if (await dupCmd.ExecuteScalarAsync() is int existing)
                {
                    tx.Rollback();
                    return (true, "This transplanting was already saved.", existing, true);
                }

                // Source batch, locked.
                var srcCmd = new SqlCommand(@"
SELECT rs.OriginType, rs.AreaId, a.AreaType, a.IsActive, rs.CavityType, rs.BatchNo,
       rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity - rs.InTransitQuantity,
       ISNULL(rs.FirstConfirmationDate, rs.CreatedDate), rs.SpeciesId, rs.SowingDate
FROM dbo.ReadyStock rs WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.Area a ON a.Id = rs.AreaId
WHERE rs.Id = @Id", conn, tx);
                srcCmd.Parameters.AddWithValue("@Id", req.SourceReadyStockId);
                string originType, sourceCavity, batchNo; int sourceAreaId, speciesId; string? sourceAreaType; bool sourceActive; decimal available; DateTime availableFromUtc, sourceSowingDate;
                using (var r = await srcCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "Source Ready Stock batch not found.", 0, false); }
                    originType = r.GetString(0); sourceAreaId = r.GetInt32(1); sourceAreaType = r.IsDBNull(2) ? null : r.GetString(2);
                    sourceActive = r.GetBoolean(3); sourceCavity = r.GetString(4); batchNo = r.GetString(5); available = r.GetDecimal(6);
                    availableFromUtc = r.GetDateTime(7); speciesId = r.GetInt32(8); sourceSowingDate = r.GetDateTime(9);
                }
                if (!canAccessArea(sourceAreaId)) { tx.Rollback(); return (false, "You are not allowed to transplant stock of this Area.", 0, false); }
                // 2026-10-14: the Mother Plant Area only (its tray pool); a Polyhouse is optional and, if named, must be the Area's.
                var areaOnly = await MotherPlantAreaSchema.ReadyAsync(conn, tx);
                if (req.TargetAreaId is not > 0 || (req.TargetPolyhouseId is not > 0 && !areaOnly)) { tx.Rollback(); return (false, "Choose the target Mother Plant Area and Polyhouse.", 0, false); }
                if (!canAccessArea(req.TargetAreaId.Value)) { tx.Rollback(); return (false, "You are not allowed to transplant into this Mother Plant Area.", 0, false); }
                // A batch received for Pot Production is not transplanted into trays.
                var (purposeOk, purposeError) = MotherPlantTransferRules.ValidateTransplantPurpose(
                    await MotherPlantTransferRepository.ReceivedBatchPurposeAsync(conn, tx, req.SourceReadyStockId));
                if (!purposeOk) { tx.Rollback(); return (false, purposeError, 0, false); }

                var tgtCmd = new SqlCommand("SELECT a.AreaType, a.IsActive, ISNULL((SELECT p.AreaId FROM dbo.Polyhouses p WHERE p.Id = @P), CASE WHEN @P IS NULL THEN a.Id END) FROM dbo.Area a WHERE a.Id = @A", conn, tx);
                tgtCmd.Parameters.AddWithValue("@A", req.TargetAreaId.Value);
                tgtCmd.Parameters.AddWithValue("@P", (object?)req.TargetPolyhouseId ?? DBNull.Value);
                string? targetAreaType = null; bool targetActive = false; int? polyhouseAreaId = null;
                using (var r = await tgtCmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        targetAreaType = r.IsDBNull(0) ? null : r.GetString(0); targetActive = r.GetBoolean(1); polyhouseAreaId = r.IsDBNull(2) ? null : r.GetInt32(2);
                    }
                }
                var (locOk, locError) = TransplantRules.ValidateLocations(
                    new TransplantRules.Location(sourceAreaType, sourceActive, sourceAreaId),
                    new TransplantRules.Location(targetAreaType, targetActive, req.TargetAreaId.Value), polyhouseAreaId);
                if (!locOk) { tx.Rollback(); return (false, locError, 0, false); }
                // 2026-10-16: the Area's trays -- a posted Polyhouse (one of another Area was refused above) is never used or stored
                if (areaOnly)
                    req = req with { TargetPolyhouseId = null };

                var (dateOk, dateError) = TransplantRules.ValidateDate(req.TransplantDate, AppClock.Today, AppClock.ToIndiaTime(availableFromUtc));
                if (!dateOk) { tx.Rollback(); return (false, dateError, 0, false); }

                var wholeTrayCavity = originType == ReadyStock.OriginSowing ? DirectSowingRules.CavityCount(sourceCavity) : null;
                if (!await BedSowingSchema.TraysToApprovalsAsync(conn, tx))
                    return await CreateLegacyAsync(conn, tx, req, actor, available, wholeTrayCavity, batchNo, sourceCavity);

                // 2026-10-16: the seedlings PROCESSED -> complete trays + waste (the untransplanted remainder), re-planned
                // here under the source lock from the batch's own available quantity -- never the browser's numbers.
                var wasteSchema = await WorkflowCorrectionsSchema.TransplantWasteAsync(conn, tx);
                var (planOk, plan, planError) = TransplantRules.PlanProcessing(available, req.SelectedQuantity, req.TargetCavityType,
                    req.DiscardAll, req.WasteReason, wholeTrayCavity);
                if (!planOk) { tx.Rollback(); return (false, planError, 0, false); }
                if (req.TransplantedQuantity != plan!.Transplanted || req.LostQuantity != plan.Waste)
                {
                    tx.Rollback();
                    return (false, $"The quantities do not match: {QuantityFormat.Qty(plan.Processed)} seedlings make {plan.Trays} complete trays "
                        + $"({QuantityFormat.Qty(plan.Transplanted)} transplanted) and {QuantityFormat.Qty(plan.Waste)} waste. Check the form and save again.", 0, false);
                }
                if (!plan.MakesTrayBatch && !wasteSchema) { tx.Rollback(); return (false, WorkflowCorrectionsSchema.TransplantWasteMigrationMessage, 0, false); }
                var wasteReason = plan.Waste > 0 ? req.WasteReason!.Trim() : null;
                // a waste-only row keeps a valid cavity label (CK_RsTransplants_Cavity): the chosen one, else the batch's own
                var cavityLabel = DirectSowingRules.IsValidCavityType(req.TargetCavityType) ? req.TargetCavityType! : sourceCavity;

                // Tray batch in Sowing Approvals -- complete trays only, Ready Date = transplant date + configured Ready Days.
                int? readyDays = null; DateTime? readyDate = null;
                if (plan.MakesTrayBatch)
                {
                    string? varietyName;
                    (readyDays, varietyName) = await TrayBatchWriter.ReadyDaysAsync(conn, tx, speciesId);
                    readyDate = TrayBatchRules.ReadyDate(req.TransplantDate, readyDays);
                    if (!readyDate.HasValue) { tx.Rollback(); return (false, string.Format(TrayBatchRules.NoReadyDaysMessageFormat, varietyName), 0, false); }
                }

                // Empty target trays: only the trays the complete trays need, from the Mother Plant Area's own pool.
                int? trayStockId = null;
                var (_, trayAtUtc, _) = LedgerDates.Instant(req.TransplantDate.Date);
                if (plan.MakesTrayBatch)
                {
                    var (trayLocOk, trayLocError) = await TrayStockRepository.ValidateLocationAsync(conn, tx, req.TargetAreaId.Value, req.TargetPolyhouseId);
                    if (!trayLocOk) { tx.Rollback(); return (false, trayLocError, 0, false); }
                    trayStockId = await _trayStockRepo.GetOrCreateLockedAsync(conn, tx, req.TargetAreaId.Value, req.TargetPolyhouseId, req.TargetCavityType!, actor.UserName);
                    // 2026-10-10: trays available on the TRANSPLANTING date and at every later point; today -> the current balance.
                    var backDated = TrayStockRepository.IsBackDated(trayAtUtc);
                    var trayBalance = await TrayStockRepository.AvailableFromAsync(conn, tx, trayStockId.Value, backDated ? trayAtUtc : null);
                    var (traysOk, traysError) = TransplantRules.ValidateTrays(plan.Trays, trayBalance, req.TargetCavityType!,
                        backDated ? req.TransplantDate.Date : null, req.TargetPolyhouseId is > 0 ? "the target Polyhouse" : "this Mother Plant Area");
                    if (!traysOk) { tx.Rollback(); return (false, traysError, 0, false); }
                }

                var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
                if (wasteReason != null && !wasteSchema)
                    notes = $"Waste reason: {wasteReason}" + (notes == null ? "" : " | " + notes);   // no WasteReason column yet
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "TP", req.TransplantDate.Year);
                var insert = new SqlCommand(@"
INSERT INTO dbo.ReadyStockTransplants
(TransplantCode, SourceReadyStockId, AreaId, PolyhouseId, TargetCavityType, SelectedQuantity, TransplantedQuantity, LostQuantity,
 OutputTrays, TrayStockId, TransplantDate, Notes, Status, SubmissionToken, CreatedById, CreatedBy" + (wasteSchema ? ", WasteReason" : "") + @")
VALUES
(@Code, @Source, @Area, @Polyhouse, @Cavity, @Selected, @Transplanted, @Lost, @Trays, @TrayStock, @Date, @Notes, N'Completed', @Token, @UserId, @UserName" + (wasteSchema ? ", @WasteReason" : "") + @");
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@Source", req.SourceReadyStockId);
                insert.Parameters.AddWithValue("@Area", req.TargetAreaId.Value);
                insert.Parameters.AddWithValue("@Polyhouse", (object?)req.TargetPolyhouseId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Cavity", cavityLabel);
                insert.Parameters.AddWithValue("@Selected", plan.Processed);
                insert.Parameters.AddWithValue("@Transplanted", plan.Transplanted);
                insert.Parameters.AddWithValue("@Lost", plan.Waste);
                insert.Parameters.AddWithValue("@Trays", plan.Trays);
                insert.Parameters.AddWithValue("@TrayStock", (object?)trayStockId ?? DBNull.Value);
                insert.Parameters.Add("@Date", System.Data.SqlDbType.Date).Value = req.TransplantDate.Date;
                insert.Parameters.AddWithValue("@Notes", (object?)notes ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Token", req.SubmissionToken);
                insert.Parameters.AddWithValue("@UserId", (object?)actor.UserId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@UserName", (object?)actor.UserName ?? DBNull.Value);
                insert.Parameters.AddWithValue("@WasteReason", (object?)wasteReason ?? DBNull.Value);
                var id = (int)(await insert.ExecuteScalarAsync())!;

                // 1) the source loses everything processed -- transplanted + waste -- as one change, on the transplant date.
                var (srcOk, srcError) = await _readyStockRepo.RecordMovementsAsync(conn, tx, req.SourceReadyStockId,
                    new List<(string, decimal, string?)>
                    {
                        ("TransplantOut", -plan.Transplanted, $"{code}: transplanted into {req.TargetCavityType} trays"),
                        ("TransplantLoss", -plan.Waste, plan.Waste > 0 ? $"{code}: waste -- {wasteReason}" : null),
                    }, TransplantRules.ReferenceType, id, actor.UserId, req.TransplantDate.Date);
                if (!srcOk) { tx.Rollback(); return (false, srcError, 0, false); }

                // 2) the output: a growing tray batch in Sowing Approvals (no Ready Stock until approved) -- only when at
                //    least one complete tray was made. No growing material: Mother Plant transplanting never consumes
                //    Cocopeat / Pitmos (owner 2026-10-12); bed transplanting keeps its material rule (BedSowingRepository).
                int? outputSowingId = null; string? outputSowingCode = null;
                if (plan.MakesTrayBatch)
                {
                    var (sowId, sowCode) = await TrayBatchWriter.InsertAsync(conn, tx, _batchNumberRepo, new TrayBatchWriter.Spec(
                        SeedSowing.SourceTransplant, null, speciesId, req.TargetAreaId.Value, req.TargetPolyhouseId, batchNo, null,
                        req.TargetCavityType!, plan.Trays, plan.Transplanted, sourceSowingDate, readyDays!.Value, readyDate!.Value, null,
                        $"Transplanted from Ready Stock batch {batchNo} ({sourceCavity}) on {req.TransplantDate:dd/MM/yyyy} ({code})"
                            + (notes == null ? "" : ": " + notes),
                        actor.UserName, actor.UserId));
                    var linkSowing = new SqlCommand("UPDATE dbo.ReadyStockTransplants SET OutputSeedSowingId = @O WHERE Id = @Id", conn, tx);
                    linkSowing.Parameters.AddWithValue("@O", sowId);
                    linkSowing.Parameters.AddWithValue("@Id", id);
                    await linkSowing.ExecuteNonQueryAsync();
                    outputSowingId = sowId; outputSowingCode = sowCode;

                    // 3) only the empty trays the complete trays need.
                    var (trayOk, trayError) = await _trayStockRepo.RecordTransactionAsync(conn, tx, trayStockId!.Value, -plan.Trays, "Transplant",
                        TransplantRules.ReferenceType, id, actor.UserId, $"{code}: {QuantityFormat.Qty(plan.Transplanted)} plants transplanted", trayAtUtc);
                    if (!trayOk) { tx.Rollback(); return (false, trayError, 0, false); }
                }

                tx.Commit();
                return (true, $"{code} saved: " + plan.Summary(req.TargetCavityType, done: true)
                    + (outputSowingId.HasValue ? $" Tray batch {outputSowingCode}, Ready Date {readyDate:dd-MM-yyyy} (transplant date + {readyDays} days), now waiting in Sowing Approvals." : ""),
                    id, false);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                SafeRollback(tx);
                var existing = await ScalarOrNullAsync("SELECT Id FROM dbo.ReadyStockTransplants WHERE SubmissionToken = @V", req.SubmissionToken);
                return existing.HasValue ? (true, "This transplanting was already saved.", existing.Value, true) : (false, DbErrorMessage.For(ex), 0, false);
            }
            catch (Exception ex)
            {
                SafeRollback(tx);
                return (false, DbErrorMessage.For(ex), 0, false);
            }
        }

        // Before 2026-10-13_TraysToSowingApprovals.sql: the output is a NEW Ready Stock batch with exact plant counts
        // (the last tray may be partly filled) and the user enters transplanted + lost -- unchanged behaviour.
        // Runs inside CreateAsync's transaction (source batch locked); commits or rolls back that transaction.
        private async Task<(bool Success, string? Message, int Id, bool Duplicate)> CreateLegacyAsync(SqlConnection conn, SqlTransaction tx,
            CreateRequest req, MotherPlantTransferRepository.Actor actor, decimal available, int? wholeTrayCavity, string batchNo, string sourceCavity)
        {
            var (planOk, plan, planError) = TransplantRules.PlanTransplant(req.SelectedQuantity, req.TransplantedQuantity, req.LostQuantity,
                available, req.TargetCavityType, wholeTrayCavity);
            if (!planOk) { tx.Rollback(); return (false, planError, 0, false); }

            var (trayLocOk, trayLocError) = await TrayStockRepository.ValidateLocationAsync(conn, tx, req.TargetAreaId!.Value, req.TargetPolyhouseId);
            if (!trayLocOk) { tx.Rollback(); return (false, trayLocError, 0, false); }
            var trayStockId = await _trayStockRepo.GetOrCreateLockedAsync(conn, tx, req.TargetAreaId.Value, req.TargetPolyhouseId, req.TargetCavityType!, actor.UserName);
            var (_, trayAtUtc, _) = LedgerDates.Instant(req.TransplantDate.Date);
            var backDated = TrayStockRepository.IsBackDated(trayAtUtc);
            var trayBalance = await TrayStockRepository.AvailableFromAsync(conn, tx, trayStockId, backDated ? trayAtUtc : null);
            var (traysOk, traysError) = TransplantRules.ValidateTrays(plan!.OutputTrays, trayBalance, req.TargetCavityType!,
                backDated ? req.TransplantDate.Date : null, req.TargetPolyhouseId is > 0 ? "the target Polyhouse" : "this Mother Plant Area");
            if (!traysOk) { tx.Rollback(); return (false, traysError, 0, false); }

            var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "TP", req.TransplantDate.Year);
            var insert = new SqlCommand(@"
INSERT INTO dbo.ReadyStockTransplants
(TransplantCode, SourceReadyStockId, AreaId, PolyhouseId, TargetCavityType, SelectedQuantity, TransplantedQuantity, LostQuantity,
 OutputTrays, TrayStockId, TransplantDate, Notes, Status, SubmissionToken, CreatedById, CreatedBy)
VALUES
(@Code, @Source, @Area, @Polyhouse, @Cavity, @Selected, @Transplanted, @Lost, @Trays, @TrayStock, @Date, @Notes, N'Completed', @Token, @UserId, @UserName);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            insert.Parameters.AddWithValue("@Code", code);
            insert.Parameters.AddWithValue("@Source", req.SourceReadyStockId);
            insert.Parameters.AddWithValue("@Area", req.TargetAreaId.Value);
            insert.Parameters.AddWithValue("@Polyhouse", (object?)req.TargetPolyhouseId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@Cavity", req.TargetCavityType!);
            insert.Parameters.AddWithValue("@Selected", req.SelectedQuantity);
            insert.Parameters.AddWithValue("@Transplanted", req.TransplantedQuantity);
            insert.Parameters.AddWithValue("@Lost", req.LostQuantity);
            insert.Parameters.AddWithValue("@Trays", plan.OutputTrays);
            insert.Parameters.AddWithValue("@TrayStock", trayStockId);
            insert.Parameters.Add("@Date", System.Data.SqlDbType.Date).Value = req.TransplantDate.Date;
            insert.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(req.Notes) ? DBNull.Value : req.Notes.Trim());
            insert.Parameters.AddWithValue("@Token", req.SubmissionToken);
            insert.Parameters.AddWithValue("@UserId", (object?)actor.UserId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@UserName", (object?)actor.UserName ?? DBNull.Value);
            var id = (int)(await insert.ExecuteScalarAsync())!;

            var (srcOk, srcError) = await _readyStockRepo.RecordMovementsAsync(conn, tx, req.SourceReadyStockId,
                new List<(string, decimal, string?)>
                {
                    ("TransplantOut", -req.TransplantedQuantity, $"{code}: transplanted into {req.TargetCavityType} trays"),
                    ("TransplantLoss", -req.LostQuantity, req.LostQuantity > 0 ? $"{code}: lost / damaged while transplanting" : null),
                }, TransplantRules.ReferenceType, id, actor.UserId, req.TransplantDate.Date);
            if (!srcOk) { tx.Rollback(); return (false, srcError, 0, false); }

            var outputId = await _readyStockRepo.CreateDerivedBatchAsync(conn, tx, req.SourceReadyStockId, ReadyStock.OriginTransplant, id,
                req.TargetAreaId.Value, req.TargetPolyhouseId, req.TargetCavityType!, actor.UserName);
            var (outOk, outError) = await _readyStockRepo.RecordTransactionAsync(conn, tx, outputId, req.TransplantedQuantity, "TransplantIn",
                TransplantRules.ReferenceType, id, actor.UserId, $"{code}: from batch {batchNo} ({sourceCavity})", req.TransplantDate.Date);
            if (!outOk) { tx.Rollback(); return (false, outError, 0, false); }
            var link = new SqlCommand("UPDATE dbo.ReadyStockTransplants SET OutputReadyStockId = @O WHERE Id = @Id", conn, tx);
            link.Parameters.AddWithValue("@O", outputId);
            link.Parameters.AddWithValue("@Id", id);
            await link.ExecuteNonQueryAsync();

            var (trayOk, trayError) = await _trayStockRepo.RecordTransactionAsync(conn, tx, trayStockId, -plan.OutputTrays, "Transplant",
                TransplantRules.ReferenceType, id, actor.UserId, $"{code}: {QuantityFormat.Qty(req.TransplantedQuantity)} plants transplanted", trayAtUtc);
            if (!trayOk) { tx.Rollback(); return (false, trayError, 0, false); }

            tx.Commit();
            var last = plan.PlantsInLastTray > 0 && plan.PlantsInLastTray < plan.Cavity
                ? $" ({plan.FullTrays} full + 1 tray with {QuantityFormat.Qty(plan.PlantsInLastTray)})" : "";
            return (true, $"{code} saved: {QuantityFormat.Qty(req.TransplantedQuantity)} plants in {plan.OutputTrays} x {req.TargetCavityType} trays{last}"
                + (req.LostQuantity > 0 ? $"; {QuantityFormat.Qty(req.LostQuantity)} recorded as lost / damaged." : "."), id, false);
        }

        public async Task<(bool Success, string? Message)> ReverseAsync(int transplantId, string? reason, MotherPlantTransferRepository.Actor actor, Func<int, bool> canAccessArea)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var traysToApprovals = await BedSowingSchema.TraysToApprovalsAsync(conn, tx);
                var tCmd = new SqlCommand(@"
SELECT TransplantCode, Status, SourceReadyStockId, OutputReadyStockId, AreaId, TransplantedQuantity, LostQuantity, OutputTrays, TrayStockId, "
                    + (traysToApprovals ? "OutputSeedSowingId" : "CAST(NULL AS INT)") + @"
FROM dbo.ReadyStockTransplants WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                tCmd.Parameters.AddWithValue("@Id", transplantId);
                string code, status; int sourceId, outputId = 0, areaId, trays, trayStockId = 0; int? outputSowingId; decimal transplanted, lost;
                using (var r = await tCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "Transplanting not found."); }
                    code = r.GetString(0); status = r.GetString(1); sourceId = r.GetInt32(2);
                    outputSowingId = r.IsDBNull(9) ? null : r.GetInt32(9);
                    areaId = r.GetInt32(4); transplanted = r.GetDecimal(5); lost = r.GetDecimal(6);
                    // 2026-10-16: a waste-only closure has no output batch and no tray pool (TrayStockId NULL).
                    if (r.IsDBNull(3) && outputSowingId == null && transplanted > 0) { r.Close(); tx.Rollback(); return (false, "This transplanting has no output batch."); }
                    if (!r.IsDBNull(3)) outputId = r.GetInt32(3);
                    trays = r.GetInt32(7);
                    if (!r.IsDBNull(8)) trayStockId = r.GetInt32(8);
                }
                if (!canAccessArea(areaId)) { tx.Rollback(); return (false, "You are not allowed to reverse transplanting in this Area."); }
                if (transplanted == 0)
                    return await ReverseWasteOnlyAsync(conn, tx, transplantId, code, status, sourceId, lost, reason, actor);
                if (outputSowingId.HasValue)
                    return await ReverseTrayBatchAsync(conn, tx, transplantId, code, status, sourceId, outputSowingId.Value, transplanted, lost, trays, trayStockId, reason, actor);

                // Lock order as in CreateAsync: source batch first, then the output batch.
                var srcLock = new SqlCommand("SELECT Id FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                srcLock.Parameters.AddWithValue("@Id", sourceId);
                await srcLock.ExecuteScalarAsync();
                var oCmd = new SqlCommand(@"
SELECT Quantity, ReservedQuantity, DispatchedQuantity, InTransitQuantity,
       (SELECT COUNT(*) FROM dbo.ReadyStock c WHERE c.ParentReadyStockId = rs.Id)
FROM dbo.ReadyStock rs WITH (UPDLOCK, HOLDLOCK) WHERE rs.Id = @Id", conn, tx);
                oCmd.Parameters.AddWithValue("@Id", outputId);
                TransplantRules.Output output;
                using (var r = await oCmd.ExecuteReaderAsync())
                {
                    await r.ReadAsync();
                    output = new TransplantRules.Output(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4));
                }
                var (ok, error) = TransplantRules.ValidateReverse(status, transplanted, output, reason);
                if (!ok) { tx.Rollback(); return (false, error); }
                var why = reason!.Trim();

                var (outOk, outError) = await _readyStockRepo.RecordTransactionAsync(conn, tx, outputId, -transplanted, "ReversalRemoval",
                    TransplantRules.ReferenceType, transplantId, actor.UserId, $"{code} reversed: {why}");
                if (!outOk) { tx.Rollback(); return (false, outError); }
                var (srcOk, srcError) = await _readyStockRepo.RecordMovementsAsync(conn, tx, sourceId,
                    new List<(string, decimal, string?)>
                    {
                        ("ReversalReturn", transplanted, $"{code} reversed: transplanted plants returned -- {why}"),
                        ("ReversalReturn", lost, lost > 0 ? $"{code} reversed: plants recorded as lost returned -- {why}" : null),
                    }, TransplantRules.ReferenceType, transplantId, actor.UserId);
                if (!srcOk) { tx.Rollback(); return (false, srcError); }
                var (trayOk, trayError) = await _trayStockRepo.RecordTransactionAsync(conn, tx, await ReturnPoolAsync(conn, tx, trayStockId), trays, "ReversalReturn",
                    TransplantRules.ReferenceType, transplantId, actor.UserId, $"{code} reversed: {why}");
                if (!trayOk) { tx.Rollback(); return (false, trayError); }

                var upd = new SqlCommand(@"
UPDATE dbo.ReadyStockTransplants SET Status = N'Reversed', ReversedById = @User, ReversedAt = SYSUTCDATETIME(), ReversalReason = @Reason
WHERE Id = @Id AND Status = N'Completed'", conn, tx);
                upd.Parameters.AddWithValue("@User", (object?)actor.UserId ?? DBNull.Value);
                upd.Parameters.AddWithValue("@Reason", why);
                upd.Parameters.AddWithValue("@Id", transplantId);
                if (await upd.ExecuteNonQueryAsync() != 1) { tx.Rollback(); return (false, "This transplanting is already reversed."); }

                tx.Commit();
                return (true, $"{code} reversed: {QuantityFormat.Qty(transplanted + lost)} plants returned to the source batch and {trays} trays to Tray Stock.");
            }
            catch (Exception ex)
            {
                SafeRollback(tx);
                return (false, DbErrorMessage.For(ex));
            }
        }

        // 2026-10-13: reversing a transplanting whose output is a tray batch -- only while that batch has
        // no Sowing Approval (no Ready Stock exists yet): the batch is cancelled, the plants (transplanted +
        // lost) return to the source batch and the trays to Tray Stock. Ledger rows are never deleted.
        private async Task<(bool Success, string? Message)> ReverseTrayBatchAsync(SqlConnection conn, SqlTransaction tx, int transplantId, string code,
            string status, int sourceId, int sowingId, decimal transplanted, decimal lost, int trays, int trayStockId, string? reason,
            MotherPlantTransferRepository.Actor actor)
        {
            if (status != TransplantRules.StatusCompleted) { tx.Rollback(); return (false, "This transplanting is already reversed."); }
            if (string.IsNullOrWhiteSpace(reason)) { tx.Rollback(); return (false, "Enter the reason for the reversal."); }
            if (reason.Trim().Length > 500) { tx.Rollback(); return (false, "The reason is too long (500 characters at most)."); }
            var why = reason.Trim();
            var srcLock = new SqlCommand("SELECT Id FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            srcLock.Parameters.AddWithValue("@Id", sourceId);
            await srcLock.ExecuteScalarAsync();
            var sCmd = new SqlCommand("SELECT Status, ConfirmedReadyQuantity, WastageQuantity FROM dbo.SeedSowings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            sCmd.Parameters.AddWithValue("@Id", sowingId);
            string sowingStatus; decimal approved, wastage;
            using (var r = await sCmd.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "The tray batch of this transplanting was not found."); }
                sowingStatus = r.GetString(0); approved = r.GetDecimal(1); wastage = r.GetDecimal(2);
            }
            var (mayReverse, reverseError) = BedSowingRules.CanReverseTransplant(sowingStatus, approved, wastage);
            if (!mayReverse) { tx.Rollback(); return (false, reverseError); }

            var cancel = new SqlCommand("UPDATE dbo.SeedSowings SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @By WHERE Id = @Id", conn, tx);
            cancel.Parameters.AddWithValue("@By", (object?)actor.UserName ?? DBNull.Value);
            cancel.Parameters.AddWithValue("@Id", sowingId);
            await cancel.ExecuteNonQueryAsync();
            var (srcOk, srcError) = await _readyStockRepo.RecordMovementsAsync(conn, tx, sourceId,
                new List<(string, decimal, string?)>
                {
                    ("ReversalReturn", transplanted, $"{code} reversed: transplanted plants returned -- {why}"),
                    ("ReversalReturn", lost, lost > 0 ? $"{code} reversed: plants recorded as lost returned -- {why}" : null),
                }, TransplantRules.ReferenceType, transplantId, actor.UserId);
            if (!srcOk) { tx.Rollback(); return (false, srcError); }
            var (trayOk, trayError) = await _trayStockRepo.RecordTransactionAsync(conn, tx, await ReturnPoolAsync(conn, tx, trayStockId), trays, "ReversalReturn",
                TransplantRules.ReferenceType, transplantId, actor.UserId, $"{code} reversed: {why}");
            if (!trayOk) { tx.Rollback(); return (false, trayError); }
            var upd = new SqlCommand(@"
UPDATE dbo.ReadyStockTransplants SET Status = N'Reversed', ReversedById = @User, ReversedAt = SYSUTCDATETIME(), ReversalReason = @Reason
WHERE Id = @Id AND Status = N'Completed'", conn, tx);
            upd.Parameters.AddWithValue("@User", (object?)actor.UserId ?? DBNull.Value);
            upd.Parameters.AddWithValue("@Reason", why);
            upd.Parameters.AddWithValue("@Id", transplantId);
            if (await upd.ExecuteNonQueryAsync() != 1) { tx.Rollback(); return (false, "This transplanting is already reversed."); }
            tx.Commit();
            return (true, $"{code} reversed: the tray batch is cancelled, {QuantityFormat.Qty(transplanted + lost)} plants returned to the source batch and {trays} trays to Tray Stock.");
        }

        // 2026-10-16: reversing a waste-only closure (seedlings discarded, no tray batch, no trays): the discarded seedlings
        // return to the source batch. Ledger rows are never deleted.
        private async Task<(bool Success, string? Message)> ReverseWasteOnlyAsync(SqlConnection conn, SqlTransaction tx, int transplantId, string code,
            string status, int sourceId, decimal lost, string? reason, MotherPlantTransferRepository.Actor actor)
        {
            if (status != TransplantRules.StatusCompleted) { tx.Rollback(); return (false, "This transplanting is already reversed."); }
            if (string.IsNullOrWhiteSpace(reason)) { tx.Rollback(); return (false, "Enter the reason for the reversal."); }
            if (reason.Trim().Length > 500) { tx.Rollback(); return (false, "The reason is too long (500 characters at most)."); }
            var why = reason.Trim();
            var (srcOk, srcError) = await _readyStockRepo.RecordMovementsAsync(conn, tx, sourceId,
                new List<(string, decimal, string?)> { ("ReversalReturn", lost, $"{code} reversed: seedlings recorded as waste returned -- {why}") },
                TransplantRules.ReferenceType, transplantId, actor.UserId);
            if (!srcOk) { tx.Rollback(); return (false, srcError); }
            var upd = new SqlCommand(@"
UPDATE dbo.ReadyStockTransplants SET Status = N'Reversed', ReversedById = @User, ReversedAt = SYSUTCDATETIME(), ReversalReason = @Reason
WHERE Id = @Id AND Status = N'Completed'", conn, tx);
            upd.Parameters.AddWithValue("@User", (object?)actor.UserId ?? DBNull.Value);
            upd.Parameters.AddWithValue("@Reason", why);
            upd.Parameters.AddWithValue("@Id", transplantId);
            if (await upd.ExecuteNonQueryAsync() != 1) { tx.Rollback(); return (false, "This transplanting is already reversed."); }
            tx.Commit();
            return (true, $"{code} reversed: {QuantityFormat.Qty(lost)} seedlings recorded as waste returned to the source batch.");
        }

        // 2026-10-14: the pool a reversal gives the trays back to -- the transplant's own pool, unless that was a
        // Mother Plant Area Polyhouse pool since merged into (and retired for) the Area pool: then the Area pool.
        private async Task<int> ReturnPoolAsync(SqlConnection conn, SqlTransaction tx, int trayStockId)
        {
            int areaId; int? polyhouseId; string size; bool active;
            using (var cmd = new SqlCommand("SELECT AreaId, PolyhouseId, TraySize, IsActive FROM dbo.TrayStock WHERE Id = @Id", conn, tx))
            {
                cmd.Parameters.AddWithValue("@Id", trayStockId);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return trayStockId;
                areaId = r.GetInt32(0); polyhouseId = r.IsDBNull(1) ? null : r.GetInt32(1); size = r.GetString(2); active = r.GetBoolean(3);
            }
            if (active || !await TrayStockRepository.UsesAreaPoolAsync(conn, tx, areaId))
                return trayStockId;
            return await _trayStockRepo.GetOrCreateLockedAsync(conn, tx, areaId, polyhouseId, size, null);
        }

        // ---- reads ------------------------------------------------------------------------------------

        // traysReady: also the tray-batch output (2026-10-13); wasteReady: the waste reason (2026-10-16); otherwise NULLs
        // in the same positions.
        private static string ListSelect(bool traysReady, bool wasteReady = false) => @"
SELECT t.Id, t.TransplantCode, t.Status, t.TransplantDate, t.SourceReadyStockId, t.OutputReadyStockId,
       src.OriginType, sa.Name, sph.Name, src.CavityType, t.AreaId, ta.Name, t.PolyhouseId, tph.Name, t.TargetCavityType,
       src.SpeciesId, ps.Name, pt.Name, src.BatchNo, sw.SowingCode,
       t.SelectedQuantity, t.TransplantedQuantity, t.LostQuantity, t.OutputTrays, t.Notes, t.CreatedBy, t.CreatedAt,
       ru.Name, t.ReversedAt, t.ReversalReason, "
            + (traysReady ? "t.OutputSeedSowingId, os.SowingCode, os.Status, os.ExpectedReadyDate, os.ReadyStockDays, os.ConfirmedReadyQuantity"
                          : "CAST(NULL AS INT), CAST(NULL AS NVARCHAR(40)), CAST(NULL AS NVARCHAR(60)), CAST(NULL AS DATE), CAST(NULL AS INT), CAST(NULL AS DECIMAL(18,2))")
            + (wasteReady ? ", t.WasteReason" : ", CAST(NULL AS NVARCHAR(500))") + @"
FROM dbo.ReadyStockTransplants t
JOIN dbo.ReadyStock src ON src.Id = t.SourceReadyStockId
JOIN dbo.SeedSowings sw ON sw.Id = src.SeedSowingId
JOIN dbo.PlantSpecies ps ON ps.Id = src.SpeciesId
JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
JOIN dbo.Area sa ON sa.Id = src.AreaId
LEFT JOIN dbo.Polyhouses sph ON sph.Id = src.PolyhouseId
JOIN dbo.Area ta ON ta.Id = t.AreaId
LEFT JOIN dbo.Polyhouses tph ON tph.Id = t.PolyhouseId
LEFT JOIN dbo.IMSUsers ru ON ru.Id = t.ReversedById" + (traysReady ? @"
LEFT JOIN dbo.SeedSowings os ON os.Id = t.OutputSeedSowingId" : "");

        private static async Task<string> ListSelectAsync(SqlConnection conn)
            => ListSelect(await BedSowingSchema.TraysToApprovalsAsync(conn), await WorkflowCorrectionsSchema.TransplantWasteAsync(conn));

        // areaIds: null = every Area; otherwise transplantings whose target OR source Area is one of them.
        public async Task<List<ReadyStockTransplant>> ListAsync(TransplantFilter f, IReadOnlyCollection<int>? areaIds, int max = 1000)
        {
            var list = new List<ReadyStockTransplant>();
            if (areaIds != null && areaIds.Count == 0) return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(await ListSelectAsync(conn) + @"
WHERE (@Areas IS NULL OR t.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ','))
                      OR src.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ',')))
  AND (@From IS NULL OR t.TransplantDate >= @From) AND (@To IS NULL OR t.TransplantDate <= @To)
  AND (@AreaId IS NULL OR t.AreaId = @AreaId) AND (@PolyhouseId IS NULL OR t.PolyhouseId = @PolyhouseId)
  AND (@SpeciesId IS NULL OR src.SpeciesId = @SpeciesId) AND (@Cavity IS NULL OR t.TargetCavityType = @Cavity)
  AND (@BatchNo IS NULL OR src.BatchNo LIKE @BatchNo OR sw.SowingCode LIKE @BatchNo)
  AND (@Status IS NULL OR t.Status = @Status)
ORDER BY t.TransplantDate DESC, t.Id DESC
OFFSET 0 ROWS FETCH NEXT @Max ROWS ONLY", conn);
            cmd.Parameters.AddWithValue("@Areas", areaIds == null ? DBNull.Value : string.Join(",", areaIds));
            cmd.Parameters.Add("@From", System.Data.SqlDbType.Date).Value = (object?)f.From?.Date ?? DBNull.Value;
            cmd.Parameters.Add("@To", System.Data.SqlDbType.Date).Value = (object?)f.To?.Date ?? DBNull.Value;
            cmd.Parameters.AddWithValue("@AreaId", (object?)f.AreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)f.PolyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", (object?)f.SpeciesId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Cavity", string.IsNullOrWhiteSpace(f.CavityType) ? DBNull.Value : f.CavityType);
            cmd.Parameters.AddWithValue("@BatchNo", string.IsNullOrWhiteSpace(f.BatchNo) ? DBNull.Value : "%" + f.BatchNo.Trim() + "%");
            cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(f.Status) ? DBNull.Value : f.Status);
            cmd.Parameters.AddWithValue("@Max", max);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(Map(r));
            return list;
        }

        public async Task<ReadyStockTransplant?> GetAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(await ListSelectAsync(conn) + " WHERE t.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Map(r) : null;
        }

        // Transplantings that used / produced a given batch (lineage display).
        public async Task<List<ReadyStockTransplant>> GetByBatchAsync(int readyStockId)
        {
            var list = new List<ReadyStockTransplant>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(await ListSelectAsync(conn) + " WHERE t.SourceReadyStockId = @R OR t.OutputReadyStockId = @R ORDER BY t.Id", conn);
            cmd.Parameters.AddWithValue("@R", readyStockId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(Map(r));
            return list;
        }

        // Management Dashboard: one India day (transplant dates are India business dates).
        public async Task<TransplantDaySummary> GetDaySummaryAsync(DateTime dayIndia)
        {
            var s = new TransplantDaySummary();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var fromUtc = AppClock.IndiaToUtc(dayIndia.Date);
            var toUtc = AppClock.IndiaToUtc(dayIndia.Date.AddDays(1));
            using var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(rs.InTransitQuantity), 0) FROM dbo.ReadyStock rs;
SELECT COUNT(*) FROM dbo.InternalTransfers WHERE StockType = N'MotherPlantTransfer' AND Status = N'PendingConfirmation';
SELECT ISNULL(SUM(rs.Quantity - rs.DispatchedQuantity - rs.InTransitQuantity), 0)
FROM dbo.ReadyStock rs JOIN dbo.Area a ON a.Id = rs.AreaId WHERE a.AreaType = N'MotherPlant';
SELECT ISNULL(SUM(TransplantedQuantity), 0), ISNULL(SUM(LostQuantity), 0)
FROM dbo.ReadyStockTransplants WHERE Status = N'Completed' AND TransplantDate = @Day;
SELECT ISNULL(-SUM(t.Quantity), 0) FROM dbo.ReadyStockTransactions t
WHERE t.TransactionType = N'TransferLoss' AND t.TransactionDate >= @FromUtc AND t.TransactionDate < @ToUtc;
SELECT TargetCavityType, SUM(TransplantedQuantity), SUM(OutputTrays)
FROM dbo.ReadyStockTransplants WHERE Status = N'Completed' AND TransplantDate = @Day
GROUP BY TargetCavityType ORDER BY TargetCavityType;", conn);
            cmd.Parameters.Add("@Day", System.Data.SqlDbType.Date).Value = dayIndia.Date;
            cmd.Parameters.Add("@FromUtc", System.Data.SqlDbType.DateTime2).Value = fromUtc;
            cmd.Parameters.Add("@ToUtc", System.Data.SqlDbType.DateTime2).Value = toUtc;
            using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync(); s.InTransitPlants = r.GetDecimal(0);
            await r.NextResultAsync(); await r.ReadAsync(); s.PendingTransfers = r.GetInt32(0);
            await r.NextResultAsync(); await r.ReadAsync(); s.MotherPlantAreaReadyStock = r.GetDecimal(0);
            await r.NextResultAsync(); await r.ReadAsync(); s.TransplantedToday = r.GetDecimal(0); s.LostToday = r.GetDecimal(1);
            await r.NextResultAsync(); await r.ReadAsync(); s.TransferLossToday = r.GetDecimal(0);
            await r.NextResultAsync();
            while (await r.ReadAsync()) s.OutputByCavityToday.Add((r.GetString(0), r.GetDecimal(1), r.GetInt32(2)));
            return s;
        }

        private static ReadyStockTransplant Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), TransplantCode = r.GetString(1), Status = r.GetString(2), TransplantDate = r.GetDateTime(3),
            SourceReadyStockId = r.GetInt32(4), OutputReadyStockId = r.IsDBNull(5) ? null : r.GetInt32(5),
            SourceOriginType = r.GetString(6), SourceAreaName = r.GetString(7), SourcePolyhouseName = r.IsDBNull(8) ? null : r.GetString(8),
            SourceCavityType = r.GetString(9), AreaId = r.GetInt32(10), AreaName = r.GetString(11), PolyhouseId = r.IsDBNull(12) ? null : r.GetInt32(12),
            PolyhouseName = r.IsDBNull(13) ? null : r.GetString(13), TargetCavityType = r.GetString(14), SpeciesId = r.GetInt32(15), SpeciesName = r.GetString(16).Trim(),
            PlantTypeName = r.GetString(17), BatchNo = r.GetString(18), SowingCode = r.GetString(19),
            SelectedQuantity = r.GetDecimal(20), TransplantedQuantity = r.GetDecimal(21), LostQuantity = r.GetDecimal(22), OutputTrays = r.GetInt32(23),
            Notes = r.IsDBNull(24) ? null : r.GetString(24), CreatedBy = r.IsDBNull(25) ? null : r.GetString(25), CreatedAt = r.GetDateTime(26),
            ReversedByName = r.IsDBNull(27) ? null : r.GetString(27), ReversedAt = r.IsDBNull(28) ? null : r.GetDateTime(28),
            ReversalReason = r.IsDBNull(29) ? null : r.GetString(29),
            OutputSeedSowingId = r.IsDBNull(30) ? null : r.GetInt32(30),
            OutputSowingCode = r.IsDBNull(31) ? null : r.GetString(31),
            OutputSowingStatus = r.IsDBNull(32) ? null : r.GetString(32),
            OutputReadyDate = r.IsDBNull(33) ? null : r.GetDateTime(33),
            OutputReadyDays = r.IsDBNull(34) ? null : r.GetInt32(34),
            OutputApprovedQuantity = r.IsDBNull(35) ? 0 : r.GetDecimal(35),
            WasteReason = r.IsDBNull(36) ? null : r.GetString(36),
        };

        private async Task<int?> ScalarOrNullAsync(string sql, Guid value)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@V", value);
            return await cmd.ExecuteScalarAsync() as int?;
        }

        private static void SafeRollback(SqlTransaction tx)
        {
            try { tx.Rollback(); } catch (InvalidOperationException) { /* already completed */ }
        }
    }
}
