using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Main Area Ready Stock -> Mother Plant Area (dbo.InternalTransfers, StockType
    // 'MotherPlantTransfer'); rules in Services/MotherPlantTransferRules.cs.
    // Every action is ONE transaction that locks the transfer row (UPDLOCK, HOLDLOCK) and then
    // the source batch, re-reads everything it decides on, and either commits all of it or
    // nothing. Lock order: transfer -> source batch -> (new) destination batch.
    //   create   InTransit += q on the source ('TransferHold'); a repeated submit of the same
    //            form (SubmissionToken, unique) returns the first transfer instead of a second one
    //   receive  InTransit -= q ('TransferHoldRelease'); Quantity -= received ('TransferOut') and
    //            -= shortfall ('TransferLoss') in ONE balance change; a NEW batch at the
    //            destination ('TransferIn') -- UX_ReadyStock_Origin allows only one per transfer
    //   reject / cancel   InTransit -= q ('TransferHoldRelease'); nothing else moves
    // 2026-10-14 (MotherPlantAreaSchema): sent to the Mother Plant AREA -- no Polyhouse to send, receive or
    // transplant -- with a recorded purpose (Pot Production / Transplant into a different cavity tray).
    public class MotherPlantTransferRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly BatchNumberRepository _batchNumberRepo;

        public record Actor(int? UserId, string? UserName);

        public MotherPlantTransferRepository(DatabaseHelper dbHelper, ReadyStockRepository readyStockRepo, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _readyStockRepo = readyStockRepo;
            _batchNumberRepo = batchNumberRepo;
        }

        // Purpose (2026-10-14): MotherPlantTransferRules.PurposePotProduction / PurposeTrayTransplant -- required, and
        // DestinationPolyhouseId optional, once 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql is applied.
        public record CreateRequest(int SourceReadyStockId, decimal Quantity, int? DestinationAreaId, int? DestinationPolyhouseId,
            DateTime TransferDate, string? Remarks, Guid SubmissionToken, string? Purpose = null);

        // canAccessArea: may the user work with stock of this Area (AreaAccessService / SeedlingAreaScope).
        public async Task<(bool Success, string? Message, int Id, bool Duplicate)> CreateAsync(CreateRequest req, Actor actor, Func<int, bool> canAccessArea)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                // A repeated submit of the same form: the first transfer, never a second one.
                var dupCmd = new SqlCommand("SELECT Id FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T", conn, tx);
                dupCmd.Parameters.AddWithValue("@T", req.SubmissionToken);
                if (await dupCmd.ExecuteScalarAsync() is int existing)
                {
                    tx.Rollback();
                    return (true, "This transfer was already saved.", existing, true);
                }

                var src = await LockSourceAsync(conn, tx, req.SourceReadyStockId);
                if (src == null) { tx.Rollback(); return (false, "Source Ready Stock batch not found.", 0, false); }
                if (!canAccessArea(src.AreaId)) { tx.Rollback(); return (false, "You are not allowed to send stock from this Area.", 0, false); }

                // 2026-10-14: to the Mother Plant AREA (no Polyhouse needed) with a recorded purpose, once migrated.
                var areaOnly = await MotherPlantAreaSchema.ReadyAsync(conn, tx);

                MotherPlantTransferRules.Destination dest = new(req.DestinationAreaId, null, false, req.DestinationPolyhouseId, null);
                if (req.DestinationAreaId.HasValue)
                {
                    var dCmd = new SqlCommand(@"
SELECT a.AreaType, a.IsActive, (SELECT p.AreaId FROM dbo.Polyhouses p WHERE p.Id = @P)
FROM dbo.Area a WHERE a.Id = @A", conn, tx);
                    dCmd.Parameters.AddWithValue("@A", req.DestinationAreaId.Value);
                    dCmd.Parameters.AddWithValue("@P", (object?)req.DestinationPolyhouseId ?? DBNull.Value);
                    using var r = await dCmd.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                        dest = dest with { AreaType = r.IsDBNull(0) ? null : r.GetString(0), AreaActive = r.GetBoolean(1), PolyhouseAreaId = r.IsDBNull(2) ? null : r.GetInt32(2) };
                }

                var (ok, trays, error) = MotherPlantTransferRules.ValidateCreate(
                    new MotherPlantTransferRules.SourceBatch(src.OriginType, src.AreaType, src.AreaActive, src.AreaId, src.CavityType, src.Available, src.Quantity),
                    dest, req.Quantity, req.TransferDate, AppClock.Today, src.SowingDate, areaOnly, req.Purpose);
                if (!ok) { tx.Rollback(); return (false, error, 0, false); }
                // 2026-10-16: a posted destination Polyhouse is never stored on a transfer to the Area (one of another
                // Area was refused above) -- the transfer, its receipt and its transplanting use the Area only.
                if (areaOnly)
                    req = req with { DestinationPolyhouseId = null };

                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "TR", req.TransferDate.Year);
                // TransferPurpose exists only after the migration: the column is named only then.
                var insert = new SqlCommand(@"
INSERT INTO dbo.InternalTransfers
(TransferCode, StockType, SourceReadyStockId, SourceAreaId, DestinationAreaId, DestinationPolyhouseId, PendingConfirmationAreaId,
 Quantity, Status, Remarks, TransferDate, SubmissionToken, CreatedDate, CreatedBy" + (areaOnly ? ", TransferPurpose" : "") + @")
VALUES
(@Code, N'MotherPlantTransfer', @Source, @SourceArea, @DestArea, @DestPolyhouse, @DestArea,
 @Quantity, N'PendingConfirmation', @Remarks, @Date, @Token, SYSUTCDATETIME(), @CreatedBy" + (areaOnly ? ", @Purpose" : "") + @");
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@Source", req.SourceReadyStockId);
                insert.Parameters.AddWithValue("@SourceArea", src.AreaId);
                insert.Parameters.AddWithValue("@DestArea", req.DestinationAreaId!.Value);
                insert.Parameters.AddWithValue("@DestPolyhouse", (object?)req.DestinationPolyhouseId ?? DBNull.Value);
                if (areaOnly)
                    insert.Parameters.AddWithValue("@Purpose", req.Purpose!);
                insert.Parameters.AddWithValue("@Quantity", req.Quantity);
                insert.Parameters.AddWithValue("@Remarks", string.IsNullOrWhiteSpace(req.Remarks) ? DBNull.Value : req.Remarks.Trim());
                insert.Parameters.Add("@Date", System.Data.SqlDbType.Date).Value = req.TransferDate.Date;
                insert.Parameters.AddWithValue("@Token", req.SubmissionToken);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)actor.UserName ?? DBNull.Value);
                var id = (int)(await insert.ExecuteScalarAsync())!;

                // Dated on the transfer date (business date): the plants left the Main Area then.
                var (holdOk, holdError) = await _readyStockRepo.RecordTransferHoldAsync(conn, tx, req.SourceReadyStockId, req.Quantity,
                    MotherPlantTransferRules.ReferenceType, id, actor.UserId,
                    $"{code}: {QuantityFormat.Qty(req.Quantity)} plants ({QuantityFormat.Qty(trays)} trays) in transit to a Mother Plant Area",
                    req.TransferDate.Date);
                if (!holdOk) { tx.Rollback(); return (false, holdError, 0, false); }

                tx.Commit();
                return (true, $"Transfer {code} created: {QuantityFormat.Qty(req.Quantity)} plants ({QuantityFormat.Qty(trays)} trays) are in transit"
                    + (areaOnly ? $" -- purpose: {MotherPlantTransferRules.PurposeLabel(req.Purpose)}." : "."), id, false);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Same SubmissionToken committed by a concurrent request: that one wins.
                SafeRollback(tx);
                var existing = await FindByTokenAsync(req.SubmissionToken);
                return existing.HasValue ? (true, "This transfer was already saved.", existing.Value, true) : (false, DbErrorMessage.For(ex), 0, false);
            }
            catch (Exception ex)
            {
                SafeRollback(tx);
                return (false, DbErrorMessage.For(ex), 0, false);
            }
        }

        // receivedDateIndia (2026-10-11): the business date the plants arrived -- null = today (India).
        // Not in the future, not before the send; an earlier date needs receiptDateReason. The receipt
        // rows (hold release, source out / loss, destination in) are dated at the later of that date and
        // the transfer's hold row, so the plants are never counted at both places or at neither.
        // Stored on InternalTransfers.ReceivedDate / ReceiptDateReason (2026-10-11_ReadyStockReceiptDate.sql);
        // ConfirmedDate stays the recorded time. Without that migration only today's date is accepted.
        public async Task<(bool Success, string? Message, int? ReceivedReadyStockId)> ReceiveAsync(
            int transferId, decimal receivedQuantity, string? shortfallReason, Actor actor, Func<int, bool> canAccessArea,
            DateTime? receivedDateIndia = null, string? receiptDateReason = null)
        {
            var receivedDate = (receivedDateIndia ?? AppClock.Today).Date;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var t = await LockTransferAsync(conn, tx, transferId);
                if (t == null) { tx.Rollback(); return (false, "Transfer not found.", null); }
                if (!canAccessArea(t.DestinationAreaId)) { tx.Rollback(); return (false, "You are not allowed to receive stock in this Mother Plant Area.", null); }
                // Received already (repeated click): report the batch it created, add nothing.
                if (t.Status == MotherPlantTransferRules.StatusCompleted)
                {
                    tx.Rollback();
                    return (true, "This transfer was already received.", await FindReceivedBatchAsync(transferId));
                }
                var (ok, loss, error) = MotherPlantTransferRules.ValidateReceive(t.Status, t.Quantity, receivedQuantity, shortfallReason);
                if (!ok) { tx.Rollback(); return (false, error, null); }
                var (dateOk, dateError) = MotherPlantTransferRules.ValidateReceiptDate(receivedDate, t.TransferDate, AppClock.Today, receiptDateReason);
                if (!dateOk) { tx.Rollback(); return (false, dateError, null); }
                var receiptColumns = await ScalarAsync<int?>(conn, tx, "SELECT CASE WHEN COL_LENGTH('dbo.InternalTransfers', 'ReceivedDate') IS NULL THEN 0 ELSE 1 END", 0) == 1;
                if (!receiptColumns && receivedDate < AppClock.Today)
                {
                    tx.Rollback();
                    return (false, "Earlier receipt dates need migration 2026-10-11_ReadyStockReceiptDate.sql; until then only today can be recorded.", null);
                }
                // The instant of every receipt row: the business date (null = now), never before the hold.
                var (_, receiptUtc, _) = LedgerDates.Instant(receivedDate);
                DateTime? atUtc = null;
                if (receiptUtc.HasValue)
                {
                    var holdUtc = await ScalarAsync<DateTime?>(conn, tx, @"SELECT MAX(TransactionDate) FROM dbo.ReadyStockTransactions
WHERE ReferenceType = N'InternalTransfer' AND ReferenceId = @Id AND TransactionType = N'TransferHold'", transferId);
                    atUtc = LedgerDates.ReleaseInstant(receiptUtc, holdUtc) ?? receiptUtc;
                }
                var lateNote = receivedDate < AppClock.Today ? $" [received {receivedDate:dd/MM/yyyy}, recorded later: {receiptDateReason!.Trim()}]" : "";

                var (relOk, relError) = await _readyStockRepo.RecordTransferHoldAsync(conn, tx, t.SourceReadyStockId, -t.Quantity,
                    MotherPlantTransferRules.ReferenceType, transferId, actor.UserId, $"{t.TransferCode}: received{lateNote}", atUtc: atUtc);
                if (!relOk) { tx.Rollback(); return (false, relError, null); }

                var movements = new List<(string, decimal, string?)>
                {
                    ("TransferOut", -receivedQuantity, $"{t.TransferCode}: received in Mother Plant Area"),
                    ("TransferLoss", -loss, loss > 0 ? $"{t.TransferCode}: short on arrival -- {shortfallReason!.Trim()}" : null),
                };
                var (outOk, outError) = await _readyStockRepo.RecordMovementsAsync(conn, tx, t.SourceReadyStockId, movements,
                    MotherPlantTransferRules.ReferenceType, transferId, actor.UserId, atUtc: atUtc);
                if (!outOk) { tx.Rollback(); return (false, outError, null); }

                var cavity = await ScalarAsync<string>(conn, tx, "SELECT CavityType FROM dbo.ReadyStock WHERE Id = @Id", t.SourceReadyStockId);
                var newBatchId = await _readyStockRepo.CreateDerivedBatchAsync(conn, tx, t.SourceReadyStockId, ReadyStock.OriginTransfer, transferId,
                    t.DestinationAreaId, t.DestinationPolyhouseId, cavity, actor.UserName, atUtc);
                var (inOk, inError) = await _readyStockRepo.RecordTransactionAsync(conn, tx, newBatchId, receivedQuantity, "TransferIn",
                    MotherPlantTransferRules.ReferenceType, transferId, actor.UserId, $"{t.TransferCode}: received from Main Area{lateNote}", atUtc: atUtc);
                if (!inOk) { tx.Rollback(); return (false, inError, null); }

                var upd = new SqlCommand(@"
UPDATE dbo.InternalTransfers
SET Status = N'Completed', ConfirmedQuantity = @Received, ConfirmedBy = @User, ConfirmedDate = SYSUTCDATETIME(),
    DiscrepancyReason = @Reason, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @UserName
WHERE Id = @Id AND Status = N'PendingConfirmation'", conn, tx);
                upd.Parameters.AddWithValue("@Received", receivedQuantity);
                upd.Parameters.AddWithValue("@User", (object?)actor.UserId ?? DBNull.Value);
                upd.Parameters.AddWithValue("@Reason", loss > 0 ? shortfallReason!.Trim() : DBNull.Value);
                upd.Parameters.AddWithValue("@UserName", (object?)actor.UserName ?? DBNull.Value);
                upd.Parameters.AddWithValue("@Id", transferId);
                if (await upd.ExecuteNonQueryAsync() != 1) { tx.Rollback(); return (false, "This transfer is no longer pending.", null); }
                if (receiptColumns)
                {
                    // dynamic SQL: the columns exist only after 2026-10-11_ReadyStockReceiptDate.sql
                    var dated = new SqlCommand(@"EXEC sp_executesql N'UPDATE dbo.InternalTransfers SET ReceivedDate = @D, ReceiptDateReason = @R WHERE Id = @Id',
    N'@D DATE, @R NVARCHAR(500), @Id INT', @D = @D, @R = @R, @Id = @Id", conn, tx);
                    dated.Parameters.Add("@D", System.Data.SqlDbType.Date).Value = receivedDate;
                    dated.Parameters.AddWithValue("@R", receivedDate < AppClock.Today ? receiptDateReason!.Trim() : DBNull.Value);
                    dated.Parameters.AddWithValue("@Id", transferId);
                    await dated.ExecuteNonQueryAsync();
                }

                tx.Commit();
                return (true, loss > 0
                    ? $"{t.TransferCode} received: {QuantityFormat.Qty(receivedQuantity)} plants added here; {QuantityFormat.Qty(loss)} recorded as lost in transit."
                    : $"{t.TransferCode} received: {QuantityFormat.Qty(receivedQuantity)} plants are now Ready Stock here.", newBatchId);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                SafeRollback(tx);   // a concurrent receive created the batch first
                var batch = await FindReceivedBatchAsync(transferId);
                return batch.HasValue ? (true, "This transfer was already received.", batch) : (false, DbErrorMessage.For(ex), null);
            }
            catch (Exception ex)
            {
                SafeRollback(tx);
                return (false, DbErrorMessage.For(ex), null);
            }
        }

        // reject: by the receiving Mother Plant Area. cancel: by the sender. Both only while pending.
        public Task<(bool Success, string? Message)> RejectAsync(int transferId, string? reason, Actor actor, Func<int, bool> canAccessArea)
            => CloseAsync(transferId, reason, actor, canAccessArea, reject: true);

        public Task<(bool Success, string? Message)> CancelAsync(int transferId, string? reason, Actor actor, Func<int, bool> canAccessArea)
            => CloseAsync(transferId, reason, actor, canAccessArea, reject: false);

        private async Task<(bool Success, string? Message)> CloseAsync(int transferId, string? reason, Actor actor, Func<int, bool> canAccessArea, bool reject)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var t = await LockTransferAsync(conn, tx, transferId);
                if (t == null) { tx.Rollback(); return (false, "Transfer not found."); }
                if (!canAccessArea(reject ? t.DestinationAreaId : t.SourceAreaId))
                {
                    tx.Rollback();
                    return (false, reject ? "You are not allowed to reject stock for this Mother Plant Area." : "You are not allowed to cancel transfers of this Area.");
                }
                var (ok, error) = MotherPlantTransferRules.ValidateClose(t.Status, reason, reject ? "rejected" : "cancelled");
                if (!ok) { tx.Rollback(); return (false, error); }

                var (relOk, relError) = await _readyStockRepo.RecordTransferHoldAsync(conn, tx, t.SourceReadyStockId, -t.Quantity,
                    MotherPlantTransferRules.ReferenceType, transferId, actor.UserId,
                    $"{t.TransferCode}: {(reject ? "rejected" : "cancelled")} -- {reason!.Trim()}");
                if (!relOk) { tx.Rollback(); return (false, relError); }

                var upd = new SqlCommand(reject
                    ? @"UPDATE dbo.InternalTransfers SET Status = N'Rejected', ConfirmedBy = @User, ConfirmedDate = SYSUTCDATETIME(), DiscrepancyReason = @Reason,
                          ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @UserName WHERE Id = @Id AND Status = N'PendingConfirmation'"
                    : @"UPDATE dbo.InternalTransfers SET Status = N'Cancelled', CancelledById = @User, CancelledDate = SYSUTCDATETIME(), CancelReason = @Reason,
                          ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @UserName WHERE Id = @Id AND Status = N'PendingConfirmation'", conn, tx);
                upd.Parameters.AddWithValue("@User", (object?)actor.UserId ?? DBNull.Value);
                upd.Parameters.AddWithValue("@Reason", reason.Trim());
                upd.Parameters.AddWithValue("@UserName", (object?)actor.UserName ?? DBNull.Value);
                upd.Parameters.AddWithValue("@Id", transferId);
                if (await upd.ExecuteNonQueryAsync() != 1) { tx.Rollback(); return (false, "This transfer is no longer pending."); }

                tx.Commit();
                return (true, $"{t.TransferCode} {(reject ? "rejected" : "cancelled")}: {QuantityFormat.Qty(t.Quantity)} plants are available again at the Main Area batch.");
            }
            catch (Exception ex)
            {
                SafeRollback(tx);
                return (false, DbErrorMessage.For(ex));
            }
        }

        // ---- reads ------------------------------------------------------------------------------------

        // receiptColumns: InternalTransfers.ReceivedDate / ReceiptDateReason exist (2026-10-11_ReadyStockReceiptDate.sql);
        // on a database without them the list still works and shows no business receipt date.
        // purposeColumn: InternalTransfers.TransferPurpose exists (2026-10-14 migration); otherwise NULL.
        private static string ListSelect(bool receiptColumns, bool purposeColumn) => @"
SELECT t.Id, t.TransferCode, t.Status, t.TransferDate, t.SourceReadyStockId, t.SourceAreaId, sa.Name, sph.Name,
       t.DestinationAreaId, da.Name, t.DestinationPolyhouseId, dph.Name, ps.Name, pt.Name, rs.BatchNo, sw.SowingCode, rs.CavityType,
       t.Quantity, t.ConfirmedQuantity, t.Remarks, t.DiscrepancyReason, t.CancelReason, t.CreatedBy, t.CreatedDate,
       cu.Name, t.ConfirmedDate, xu.Name, t.CancelledDate,
       (SELECT r2.Id FROM dbo.ReadyStock r2 WHERE r2.OriginType = N'Transfer' AND r2.OriginReferenceId = t.Id),
       " + (receiptColumns ? "t.ReceivedDate, t.ReceiptDateReason" : "CAST(NULL AS date), CAST(NULL AS nvarchar(500))") + @",
       " + (purposeColumn ? "t.TransferPurpose" : "CAST(NULL AS nvarchar(30))") + @"
FROM dbo.InternalTransfers t
JOIN dbo.ReadyStock rs ON rs.Id = t.SourceReadyStockId
JOIN dbo.SeedSowings sw ON sw.Id = rs.SeedSowingId
JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
JOIN dbo.Area sa ON sa.Id = t.SourceAreaId
LEFT JOIN dbo.Polyhouses sph ON sph.Id = rs.PolyhouseId
JOIN dbo.Area da ON da.Id = t.DestinationAreaId
LEFT JOIN dbo.Polyhouses dph ON dph.Id = t.DestinationPolyhouseId
LEFT JOIN dbo.IMSUsers cu ON cu.Id = t.ConfirmedBy
LEFT JOIN dbo.IMSUsers xu ON xu.Id = t.CancelledById
WHERE t.StockType = N'MotherPlantTransfer'";

        private static async Task<bool> ReceiptColumnsAsync(SqlConnection conn)
        {
            using var cmd = new SqlCommand("SELECT CASE WHEN COL_LENGTH('dbo.InternalTransfers', 'ReceivedDate') IS NULL THEN 0 ELSE 1 END", conn);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        private static async Task<string> ListSelectAsync(SqlConnection conn)
            => ListSelect(await ReceiptColumnsAsync(conn), await MotherPlantAreaSchema.ReadyAsync(conn));

        // 2026-10-14: the purpose recorded on the Mother Plant transfer this batch came through -- the batch itself or ANY
        // ancestor in its lineage (ParentReadyStockId), so stock received for Pot Production stays out of the tray-transplant
        // workflow whatever later derives from it. PotProduction wins if the chain holds both. null: no Mother Plant
        // transfer in the lineage, older transfers without a recorded purpose, or a database without the migration.
        // Read inside the caller's transaction.
        private const string LineagePurposeSql = @"
WITH chain AS (
    SELECT Id, ParentReadyStockId, OriginType, OriginReferenceId, 0 AS Depth FROM dbo.ReadyStock WHERE Id = @R
    UNION ALL
    SELECT p.Id, p.ParentReadyStockId, p.OriginType, p.OriginReferenceId, c.Depth + 1
    FROM dbo.ReadyStock p JOIN chain c ON p.Id = c.ParentReadyStockId
    WHERE c.Depth < 20)
SELECT MAX(CASE WHEN t.TransferPurpose = N'PotProduction' THEN N'PotProduction' END),
       MAX(t.TransferPurpose)
FROM chain c
JOIN dbo.InternalTransfers t ON t.Id = c.OriginReferenceId AND t.StockType = N'MotherPlantTransfer'
WHERE c.OriginType = N'Transfer'";

        public static async Task<string?> ReceivedBatchPurposeAsync(SqlConnection conn, SqlTransaction? tx, int readyStockId)
        {
            if (!await MotherPlantAreaSchema.ReadyAsync(conn, tx))
                return null;
            using var cmd = new SqlCommand(LineagePurposeSql, conn, tx);
            cmd.Parameters.AddWithValue("@R", readyStockId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return r.IsDBNull(0) ? (r.IsDBNull(1) ? null : r.GetString(1)) : r.GetString(0);
        }

        // Every batch that came through a Pot Production transfer -- the received batch and anything derived from it --
        // never offered for Transplant into Trays (TransplantRepository refuses them too, under lock).
        public async Task<HashSet<int>> PotProductionBatchIdsAsync()
        {
            var ids = new HashSet<int>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await MotherPlantAreaSchema.ReadyAsync(conn))
                return ids;
            using var cmd = new SqlCommand(@"
WITH pot AS (
    SELECT rs.Id, 0 AS Depth FROM dbo.ReadyStock rs
    JOIN dbo.InternalTransfers t ON t.Id = rs.OriginReferenceId AND t.StockType = N'MotherPlantTransfer'
    WHERE rs.OriginType = N'Transfer' AND t.TransferPurpose = N'PotProduction'
    UNION ALL
    SELECT c.Id, p.Depth + 1 FROM dbo.ReadyStock c JOIN pot p ON c.ParentReadyStockId = p.Id WHERE p.Depth < 20)
SELECT DISTINCT Id FROM pot", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
            return ids;
        }

        // areaIds: null = all Areas; otherwise transfers whose source OR destination is one of them.
        public async Task<List<MotherPlantTransfer>> ListAsync(IReadOnlyCollection<int>? areaIds, string? status = null, int max = 500)
        {
            var list = new List<MotherPlantTransfer>();
            if (areaIds != null && areaIds.Count == 0) return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(await ListSelectAsync(conn) + @"
  AND (@Areas IS NULL OR t.SourceAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ','))
                      OR t.DestinationAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ',')))
  AND (@Status IS NULL OR t.Status = @Status)
ORDER BY CASE WHEN t.Status = N'PendingConfirmation' THEN 0 ELSE 1 END, t.TransferDate DESC, t.Id DESC
OFFSET 0 ROWS FETCH NEXT @Max ROWS ONLY", conn);
            cmd.Parameters.AddWithValue("@Areas", areaIds == null ? DBNull.Value : string.Join(",", areaIds));
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Max", max);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(Map(r));
            return list;
        }

        public async Task<MotherPlantTransfer?> GetAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(await ListSelectAsync(conn) + " AND t.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Map(r) : null;
        }

        // The transfer that created a received batch (lineage display).
        public async Task<MotherPlantTransfer?> GetByReceivedBatchAsync(int readyStockId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(await ListSelectAsync(conn) + " AND t.Id = (SELECT OriginReferenceId FROM dbo.ReadyStock WHERE Id = @R AND OriginType = N'Transfer')", conn);
            cmd.Parameters.AddWithValue("@R", readyStockId);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Map(r) : null;
        }

        private static MotherPlantTransfer Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), TransferCode = r.GetString(1), Status = r.GetString(2), TransferDate = r.GetDateTime(3),
            SourceReadyStockId = r.GetInt32(4), SourceAreaId = r.GetInt32(5), SourceAreaName = r.GetString(6), SourcePolyhouseName = r.IsDBNull(7) ? null : r.GetString(7),
            DestinationAreaId = r.GetInt32(8), DestinationAreaName = r.GetString(9), DestinationPolyhouseId = r.IsDBNull(10) ? null : r.GetInt32(10), DestinationPolyhouseName = r.IsDBNull(11) ? null : r.GetString(11),
            SpeciesName = r.GetString(12).Trim(), PlantTypeName = r.GetString(13), BatchNo = r.GetString(14), SowingCode = r.GetString(15), CavityType = r.GetString(16),
            Quantity = r.GetDecimal(17), ReceivedQuantity = r.IsDBNull(18) ? null : r.GetDecimal(18),
            Remarks = r.IsDBNull(19) ? null : r.GetString(19), DiscrepancyReason = r.IsDBNull(20) ? null : r.GetString(20), CancelReason = r.IsDBNull(21) ? null : r.GetString(21),
            CreatedBy = r.IsDBNull(22) ? null : r.GetString(22), CreatedDate = r.GetDateTime(23),
            ConfirmedByName = r.IsDBNull(24) ? null : r.GetString(24), ConfirmedDate = r.IsDBNull(25) ? null : r.GetDateTime(25),
            CancelledByName = r.IsDBNull(26) ? null : r.GetString(26), CancelledDate = r.IsDBNull(27) ? null : r.GetDateTime(27),
            ReceivedReadyStockId = r.IsDBNull(28) ? null : r.GetInt32(28),
            ReceivedDate = r.IsDBNull(29) ? null : r.GetDateTime(29), ReceiptDateReason = r.IsDBNull(30) ? null : r.GetString(30),
            TransferPurpose = r.IsDBNull(31) ? null : r.GetString(31),
        };

        // ---- helpers -----------------------------------------------------------------------------------

        private sealed record LockedSource(string OriginType, int AreaId, string? AreaType, bool AreaActive, string CavityType, DateTime SowingDate, decimal Available,
            decimal Quantity);

        private static async Task<LockedSource?> LockSourceAsync(SqlConnection conn, SqlTransaction tx, int readyStockId)
        {
            var cmd = new SqlCommand(@"
SELECT rs.OriginType, rs.AreaId, a.AreaType, a.IsActive, rs.CavityType, rs.SowingDate,
       rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity - rs.InTransitQuantity, rs.Quantity
FROM dbo.ReadyStock rs WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.Area a ON a.Id = rs.AreaId
WHERE rs.Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", readyStockId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new LockedSource(r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3), r.GetString(4), r.GetDateTime(5), r.GetDecimal(6),
                r.GetDecimal(7));
        }

        private sealed record LockedTransfer(string TransferCode, string Status, int SourceReadyStockId, int SourceAreaId, int DestinationAreaId, int? DestinationPolyhouseId, decimal Quantity, DateTime TransferDate);

        private static async Task<LockedTransfer?> LockTransferAsync(SqlConnection conn, SqlTransaction tx, int transferId)
        {
            var cmd = new SqlCommand(@"
SELECT TransferCode, Status, SourceReadyStockId, SourceAreaId, DestinationAreaId, DestinationPolyhouseId, Quantity, ISNULL(TransferDate, CAST(CreatedDate AS date))
FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @Id AND StockType = N'MotherPlantTransfer'", conn, tx);
            cmd.Parameters.AddWithValue("@Id", transferId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new LockedTransfer(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetDecimal(6), r.GetDateTime(7));
        }

        private async Task<int?> FindByTokenAsync(Guid token)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id FROM dbo.InternalTransfers WHERE SubmissionToken = @T", conn);
            cmd.Parameters.AddWithValue("@T", token);
            return await cmd.ExecuteScalarAsync() as int?;
        }

        private async Task<int?> FindReceivedBatchAsync(int transferId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id FROM dbo.ReadyStock WHERE OriginType = N'Transfer' AND OriginReferenceId = @T", conn);
            cmd.Parameters.AddWithValue("@T", transferId);
            return await cmd.ExecuteScalarAsync() as int?;
        }

        private static async Task<T> ScalarAsync<T>(SqlConnection conn, SqlTransaction tx, string sql, int id)
        {
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            var value = await cmd.ExecuteScalarAsync();
            return value is null or DBNull ? default! : (T)value;
        }

        private static void SafeRollback(SqlTransaction tx)
        {
            try { tx.Rollback(); } catch (InvalidOperationException) { /* already completed */ }
        }
    }
}
