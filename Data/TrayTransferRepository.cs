using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Empty-tray transfers between two Polyhouses (dbo.TrayTransfers, 2026-10-10_TrayTransfers.sql).
    // Owner decision 2026-10-10: the trays LEAVE the source when the transfer is sent (in transit,
    // counted in neither pool); the destination gains them only on receipt; a rejection or a
    // cancellation returns them to the source. Every change is one ledger row on the pool it
    // touches (TrayStockRepository.RecordTransactionAsync, ReferenceType 'TrayTransfer',
    // ReferenceId = the transfer Id) in the SAME transaction as the status change.
    //   send     Pending    source       -q  TransferOut     dated at the business TransferDate
    //   receive  Received   destination  +q  TransferIn      dated at the business receipt date
    //   reject   Rejected   source       +q  TransferReturn  dated now
    //   cancel   Cancelled  source       +q  TransferReturn  dated now
    // Concurrency: a send takes the source pool's lock (sp_getapplock) before checking its
    // balance; receive / reject / cancel lock the transfer row (UPDLOCK) and re-check that it is
    // still Pending, so two closes of one transfer can never both apply. The send's one-time
    // SubmissionToken is UNIQUE in the table: a repeated submit returns the first transfer.
    // Authorization is the caller's (canSendFrom / canReceiveAt), re-checked here.
    public class TrayTransferRepository
    {
        public const string CodePrefix = "TT";

        private readonly DatabaseHelper _dbHelper;
        private readonly TrayStockRepository _trayStockRepo;
        private readonly BatchNumberRepository _batchNumberRepo;

        public TrayTransferRepository(DatabaseHelper dbHelper, TrayStockRepository trayStockRepo, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _trayStockRepo = trayStockRepo;
            _batchNumberRepo = batchNumberRepo;
        }

        private const string BaseSelect = @"
SELECT t.Id, t.TransferCode, t.SourceTrayStockId, t.SourceAreaId, sa.Name, t.SourcePolyhouseId, sp.Name,
       t.DestinationAreaId, da.Name, t.DestinationPolyhouseId, dp.Name, t.DestinationTrayStockId,
       t.TraySize, t.Quantity, t.TransferDate, t.Status, t.Remarks, t.CreatedBy, t.CreatedAt,
       t.ClosedBy, t.ClosedAt, t.CloseReason
FROM dbo.TrayTransfers t
JOIN dbo.Area sa ON sa.Id = t.SourceAreaId
JOIN dbo.Polyhouses sp ON sp.Id = t.SourcePolyhouseId
JOIN dbo.Area da ON da.Id = t.DestinationAreaId
JOIN dbo.Polyhouses dp ON dp.Id = t.DestinationPolyhouseId";

        // Transfers touching an Area the caller may see (canView(sourceAreaId) || canView(destinationAreaId)).
        public async Task<List<TrayTransfer>> GetAllAsync(Func<int, bool> canView, string? status = null)
        {
            var list = new List<TrayTransfer>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var sql = BaseSelect + (status != null ? " WHERE t.Status = @Status" : "")
                + " ORDER BY CASE WHEN t.Status = N'Pending' THEN 0 ELSE 1 END, t.TransferDate DESC, t.Id DESC";
            using var cmd = new SqlCommand(sql, conn);
            if (status != null)
                cmd.Parameters.AddWithValue("@Status", status);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var t = Map(reader);
                if (canView(t.SourceAreaId) || canView(t.DestinationAreaId))
                    list.Add(t);
            }
            return list;
        }

        public async Task<TrayTransfer?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE t.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? Map(reader) : null;
        }

        // False until 2026-10-10_TrayTransfers.sql is applied -- pages then explain instead of failing.
        public async Task<bool> SchemaReadyAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.TrayTransfers', N'U') IS NULL THEN 0 ELSE 1 END", conn);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        // Trays sent but not yet received, rejected or cancelled (counted in neither pool), for the
        // transfers the caller may see. 0 when the table does not exist yet.
        public async Task<(decimal Trays, int Transfers)> GetInTransitAsync(Func<int, bool> canView)
        {
            if (!await SchemaReadyAsync())
                return (0, 0);
            var pending = await GetAllAsync(canView, TrayTransferRules.StatusPending);
            return (pending.Sum(t => t.Quantity), pending.Count);
        }

        // Destination rule (owner decisions 2026-10-10/11): a non-administrator may send only to an Area
        // where someone can actually receive -- an ACTIVE user assigned to that ACTIVE Area
        // (UserRoles.AreaId) whose roles hold the receive permission for that Area's TYPE
        // (TrayTransferRules.ReceivePermissionFor: Mother Plant -> ReceiveTransfer, Main Office ->
        // ReceiveTransferMainArea) -- so trays never sit in transit for want of a receiver. anyValidDestination (full-access users only): exceptional transfers to any active,
        // non-Outlet Polyhouse; they can also receive / reject those themselves. Outlets are never a
        // destination (TrayTransferRules.ValidateTransfer).
        public const string NoReceiverMessage = "No one is authorized to receive trays in the destination Area. Choose a destination with a receiving supervisor (an administrator can handle exceptional transfers).";

        private const string ReceivableAreasSql = @"
SELECT DISTINCT ur.AreaId
FROM dbo.UserRoles ur
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
JOIN dbo.Area a ON a.Id = ur.AreaId AND a.IsActive = 1
WHERE EXISTS (SELECT 1 FROM dbo.UserRoles r2
              JOIN dbo.RolePermissions rp ON rp.RoleId = r2.RoleId
              JOIN dbo.Permissions p ON p.Id = rp.PermissionId
              WHERE r2.UserId = u.Id
                AND p.Code = CASE a.AreaType
                                 WHEN N'" + TrayTransferRules.MotherPlantAreaType + @"' THEN N'" + TrayTransferRules.ReceivePermission + @"'
                                 WHEN N'" + DirectSowingRules.MainOfficeAreaType + @"' THEN N'" + TrayTransferRules.ReceiveMainAreaPermission + @"'
                             END)";

        public async Task<HashSet<int>> GetReceivableAreaIdsAsync()
        {
            var ids = new HashSet<int>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(ReceivableAreasSql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
            return ids;
        }

        private static async Task<bool> HasReceiverAsync(SqlConnection conn, SqlTransaction tx, int areaId)
        {
            using var cmd = new SqlCommand($"SELECT CASE WHEN EXISTS (SELECT 1 FROM ({ReceivableAreasSql}) x WHERE x.AreaId = @A) THEN 1 ELSE 0 END", conn, tx);
            cmd.Parameters.AddWithValue("@A", areaId);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        public async Task<(bool Success, string? Message, int TransferId, string? Code, bool Duplicate)> CreateAsync(
            TrayTransferRequest req, int? userId, string? userName, Func<int, bool> canSendFrom, bool anyValidDestination = false)
        {
            if (req.SubmissionToken == Guid.Empty)
                return (false, "This form has expired. Reload the page and enter the transfer again.", 0, null, false);
            if (!canSendFrom(req.SourceAreaId))
                return (false, "You are not authorized to send trays from the selected Area.", 0, null, false);
            if (!DirectSowingRules.IsValidCavityType(req.TraySize))
                return (false, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.", 0, null, false);
            if (req.Remarks?.Trim().Length > TrayTransferRules.MaxReasonLength)
                return (false, $"The remark is too long ({TrayTransferRules.MaxReasonLength} characters at most).", 0, null, false);
            var (dateOk, transactionUtc, dateError) = LedgerDates.Instant(req.TransferDate.Date);
            if (!dateOk)
                return (false, dateError, 0, null, false);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (await WorkflowCorrectionsSchema.CavityNotSupportedAsync(conn, null, req.TraySize) is string cavityError)
                return (false, cavityError, 0, null, false);
            using var tx = conn.BeginTransaction();
            try
            {
                var existing = await FindByTokenAsync(conn, tx, req.SubmissionToken);
                if (existing != null)
                {
                    tx.Commit();
                    return (true, null, existing.Value.Id, existing.Value.Code, true);
                }

                var source = await ReadLocationAsync(conn, tx, req.SourceAreaId, req.SourcePolyhouseId);
                var destination = await ReadLocationAsync(conn, tx, req.DestinationAreaId, req.DestinationPolyhouseId);
                if (source == null || destination == null)
                {
                    tx.Rollback();
                    return (false, "The selected Polyhouse does not exist.", 0, null, false);
                }

                // A pool row created here (never held trays) has a zero balance and is rolled back with the refusal.
                var (sourceOk, sourceError) = await TrayStockRepository.ValidateLocationAsync(conn, tx, req.SourceAreaId, req.SourcePolyhouseId);
                if (!sourceOk) { tx.Rollback(); return (false, sourceError, 0, null, false); }
                var sourcePoolId = await _trayStockRepo.GetOrCreateLockedAsync(conn, tx, req.SourceAreaId, req.SourcePolyhouseId, req.TraySize!, userName);
                // 2026-10-10: a transfer dated earlier may only send trays the source held from that date on
                var sourceBalance = await TrayStockRepository.AvailableFromAsync(conn, tx, sourcePoolId,
                    TrayStockRepository.IsBackDated(transactionUtc) ? transactionUtc : null);

                var (ok, error) = TrayTransferRules.ValidateTransfer(source, destination, req.TraySize, req.Quantity,
                    sourceBalance, req.TransferDate, AppClock.Now);
                if (!ok) { tx.Rollback(); return (false, error, 0, null, false); }
                if (!anyValidDestination && !await HasReceiverAsync(conn, tx, req.DestinationAreaId))
                {
                    tx.Rollback();
                    return (false, NoReceiverMessage, 0, null, false);
                }

                // Re-check under the source pool lock: a token submitted twice at once (both requests
                // name the same source pool, so they run one after the other here; UQ_TrayTransfers_Token
                // is the backstop). No range locks before the pool lock -- they could deadlock.
                existing = await FindByTokenAsync(conn, tx, req.SubmissionToken);
                if (existing != null)
                {
                    tx.Commit();
                    return (true, null, existing.Value.Id, existing.Value.Code, true);
                }

                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, CodePrefix, req.TransferDate.Year);
                var insert = new SqlCommand(@"
INSERT INTO dbo.TrayTransfers
(TransferCode, SourceTrayStockId, SourceAreaId, SourcePolyhouseId, DestinationAreaId, DestinationPolyhouseId,
 TraySize, Quantity, TransferDate, Status, Remarks, SubmissionToken, CreatedById, CreatedBy, CreatedAt)
VALUES
(@Code, @SourcePool, @SourceArea, @SourcePolyhouse, @DestArea, @DestPolyhouse,
 @TraySize, @Quantity, @TransferDate, N'Pending', @Remarks, @Token, @UserId, @UserName, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@SourcePool", sourcePoolId);
                insert.Parameters.AddWithValue("@SourceArea", req.SourceAreaId);
                insert.Parameters.AddWithValue("@SourcePolyhouse", req.SourcePolyhouseId);
                insert.Parameters.AddWithValue("@DestArea", req.DestinationAreaId);
                insert.Parameters.AddWithValue("@DestPolyhouse", req.DestinationPolyhouseId);
                insert.Parameters.AddWithValue("@TraySize", req.TraySize!);
                insert.Parameters.AddWithValue("@Quantity", req.Quantity);
                insert.Parameters.Add("@TransferDate", System.Data.SqlDbType.Date).Value = req.TransferDate.Date;
                insert.Parameters.AddWithValue("@Remarks", string.IsNullOrWhiteSpace(req.Remarks) ? DBNull.Value : req.Remarks.Trim());
                insert.Parameters.AddWithValue("@Token", req.SubmissionToken);
                insert.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@UserName", (object?)userName ?? DBNull.Value);
                var transferId = (int)(await insert.ExecuteScalarAsync())!;

                var (moved, moveError) = await _trayStockRepo.RecordTransactionAsync(conn, tx, sourcePoolId, -req.Quantity, "TransferOut",
                    TrayTransferRules.TransferReferenceType, transferId, userId,
                    $"Transfer {code} sent to {destination.AreaName} / {destination.PolyhouseName}", transactionUtc);
                if (!moved) { tx.Rollback(); return (false, moveError, 0, null, false); }

                tx.Commit();
                return (true, null, transferId, code, false);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex), 0, null, false);
            }
        }

        // Receipt at the destination: the destination pool (created if needed) gains the trays.
        // receivedDateIndia: the business date of receipt -- not before the send, not in the future.
        public async Task<(bool Success, string? Message)> ReceiveAsync(
            int transferId, DateTime receivedDateIndia, int? userId, string? userName, Func<int, bool> canReceiveAt)
        {
            var (dateOk, transactionUtc, dateError) = LedgerDates.Instant(receivedDateIndia.Date);
            if (!dateOk)
                return (false, dateError);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var t = await LockAsync(conn, tx, transferId);
                if (t == null) { tx.Rollback(); return (false, "Tray transfer not found."); }
                if (!canReceiveAt(t.DestinationAreaId)) { tx.Rollback(); return (false, "You are not authorized to receive trays in the destination Area."); }
                if (t.Status != TrayTransferRules.StatusPending)
                {
                    tx.Rollback();
                    return (false, $"This transfer is already {TrayTransferRules.StatusLabel(t.Status).ToLowerInvariant()} and cannot be received.");
                }
                if (receivedDateIndia.Date < t.TransferDate.Date)
                {
                    tx.Rollback();
                    return (false, $"The receipt date cannot be before the transfer was sent ({t.TransferDate:dd/MM/yyyy}).");
                }

                var (locationOk, locationError) = await TrayStockRepository.ValidateLocationAsync(conn, tx, t.DestinationAreaId, t.DestinationPolyhouseId);
                if (!locationOk) { tx.Rollback(); return (false, "The destination can no longer receive trays: " + locationError + " Reject the transfer instead."); }
                var destPoolId = await _trayStockRepo.GetOrCreateLockedAsync(conn, tx, t.DestinationAreaId, t.DestinationPolyhouseId, t.TraySize, userName);
                var (moved, moveError) = await _trayStockRepo.RecordTransactionAsync(conn, tx, destPoolId, t.Quantity, "TransferIn",
                    TrayTransferRules.TransferReferenceType, t.Id, userId,
                    $"Transfer {t.TransferCode} received from {t.SourceAreaName} / {t.SourcePolyhouseName}", transactionUtc);
                if (!moved) { tx.Rollback(); return (false, moveError); }

                await CloseAsync(conn, tx, t.Id, TrayTransferRules.StatusReceived, destPoolId, null, userId, userName);
                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex));
            }
        }

        // Rejected by the destination (canReceiveAt) or cancelled by the sender (canSendFrom):
        // the trays return to the source pool. reject = true for a rejection.
        public async Task<(bool Success, string? Message)> ReturnAsync(
            int transferId, bool reject, string? reason, int? userId, string? userName, Func<int, bool> canActOnArea)
        {
            var status = reject ? TrayTransferRules.StatusRejected : TrayTransferRules.StatusCancelled;
            var action = reject ? "rejected" : "cancelled";
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var t = await LockAsync(conn, tx, transferId);
                if (t == null) { tx.Rollback(); return (false, "Tray transfer not found."); }
                if (!canActOnArea(reject ? t.DestinationAreaId : t.SourceAreaId))
                {
                    tx.Rollback();
                    return (false, reject ? "You are not authorized to reject trays for the destination Area."
                                          : "You are not authorized to cancel transfers from the source Area.");
                }
                var (ok, error) = TrayTransferRules.ValidateClose(t.Status, reason, action);
                if (!ok) { tx.Rollback(); return (false, error); }

                // The source pool still exists (FK); re-lock it by its key. A retired source pool
                // (its Polyhouse moved Areas) is refused by RecordTransactionAsync -- the trays
                // then stay in transit until an Admin corrects the location.
                var lockCmd = new SqlCommand("SELECT AreaId, PolyhouseId FROM dbo.TrayStock WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", t.SourceTrayStockId);
                int poolArea, poolPolyhouse;
                using (var r = await lockCmd.ExecuteReaderAsync())
                {
                    await r.ReadAsync();
                    poolArea = r.GetInt32(0);
                    poolPolyhouse = r.GetInt32(1);
                }
                var sourcePoolId = await _trayStockRepo.GetOrCreateLockedAsync(conn, tx, poolArea, poolPolyhouse, t.TraySize, userName);
                var (moved, moveError) = await _trayStockRepo.RecordTransactionAsync(conn, tx, sourcePoolId, t.Quantity, "TransferReturn",
                    TrayTransferRules.TransferReferenceType, t.Id, userId,
                    $"Transfer {t.TransferCode} {action}: {reason!.Trim()}");
                if (!moved) { tx.Rollback(); return (false, moveError); }

                await CloseAsync(conn, tx, t.Id, status, null, reason.Trim(), userId, userName);
                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex));
            }
        }

        private static async Task CloseAsync(SqlConnection conn, SqlTransaction tx, int id, string status, int? destPoolId,
            string? reason, int? userId, string? userName)
        {
            var cmd = new SqlCommand(@"
UPDATE dbo.TrayTransfers
SET Status = @Status, DestinationTrayStockId = @DestPool, CloseReason = @Reason,
    ClosedById = @UserId, ClosedBy = @UserName, ClosedAt = SYSUTCDATETIME()
WHERE Id = @Id AND Status = N'Pending';", conn, tx);
            cmd.Parameters.AddWithValue("@Status", status);
            cmd.Parameters.AddWithValue("@DestPool", (object?)destPoolId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserName", (object?)userName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", id);
            if (await cmd.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("This transfer was changed by someone else. Reload and try again.");
        }

        private static async Task<TrayTransfer?> LockAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            var lockCmd = new SqlCommand("SELECT Id FROM dbo.TrayTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", id);
            if (await lockCmd.ExecuteScalarAsync() == null)
                return null;
            var cmd = new SqlCommand(BaseSelect + " WHERE t.Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? Map(reader) : null;
        }

        private static async Task<(int Id, string Code)?> FindByTokenAsync(SqlConnection conn, SqlTransaction tx, Guid token)
        {
            var cmd = new SqlCommand("SELECT Id, TransferCode FROM dbo.TrayTransfers WHERE SubmissionToken = @Token", conn, tx);
            cmd.Parameters.AddWithValue("@Token", token);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetString(1)) : null;
        }

        // The posted Area / Polyhouse pair as it really is in the database.
        private static async Task<LocationInfo?> ReadLocationAsync(SqlConnection conn, SqlTransaction tx, int areaId, int polyhouseId)
        {
            var cmd = new SqlCommand(@"
SELECT p.AreaId, p.Name, a.Name, a.AreaType, a.IsActive
FROM dbo.Polyhouses p
LEFT JOIN dbo.Area a ON a.Id = @AreaId
WHERE p.Id = @PolyhouseId", conn, tx);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.IsDBNull(2))
                return null;
            return new LocationInfo(areaId, reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), polyhouseId,
                reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetString(2), reader.GetString(1));
        }

        private sealed record LocationInfo(int AreaId, string? AreaType, bool AreaActive, int PolyhouseId, int? PolyhouseAreaId,
            string AreaName, string PolyhouseName)
        {
            public static implicit operator TrayTransferRules.Location(LocationInfo l)
                => new(l.AreaId, l.AreaType, l.AreaActive, l.PolyhouseId, l.PolyhouseAreaId);
        }

        private static TrayTransfer Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            TransferCode = r.GetString(1),
            SourceTrayStockId = r.GetInt32(2),
            SourceAreaId = r.GetInt32(3),
            SourceAreaName = r.GetString(4),
            SourcePolyhouseId = r.GetInt32(5),
            SourcePolyhouseName = r.GetString(6),
            DestinationAreaId = r.GetInt32(7),
            DestinationAreaName = r.GetString(8),
            DestinationPolyhouseId = r.GetInt32(9),
            DestinationPolyhouseName = r.GetString(10),
            DestinationTrayStockId = r.IsDBNull(11) ? null : r.GetInt32(11),
            TraySize = r.GetString(12),
            Quantity = r.GetDecimal(13),
            TransferDate = r.GetDateTime(14),
            Status = r.GetString(15),
            Remarks = r.IsDBNull(16) ? null : r.GetString(16),
            CreatedBy = r.IsDBNull(17) ? null : r.GetString(17),
            CreatedAt = r.GetDateTime(18),
            ClosedBy = r.IsDBNull(19) ? null : r.GetString(19),
            ClosedAt = r.IsDBNull(20) ? null : r.GetDateTime(20),
            CloseReason = r.IsDBNull(21) ? null : r.GetString(21)
        };
    }
}
