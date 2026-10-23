using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Cocopeat / Pitmos transfers between Polyhouses (dbo.MaterialTransfers, 2026-10-25_MaterialStockTransfers.sql;
    // rules in Services/MaterialTransferRules.cs). ONE transaction per transfer:
    //   1. the one-time SubmissionToken is looked up (a repeated submit returns the first transfer, moves nothing);
    //   2. both locations are re-read and validated (never trusted from the form); the source Area must pass the caller's
    //      canSendFrom, and -- for a non-administrator -- the destination Area must have someone who can see material;
    //   3. BOTH balances are locked (MaterialStockRepository.GetOrCreateLockedAsync: sp_getapplock + UPDLOCK) in a fixed
    //      order, so concurrent transfers touching the same balances run one after the other and A->B / B->A never deadlock;
    //   4. the token is checked again under the locks, the transfer row is written, then TRANSFER_OUT on the source
    //      (MaterialStockRepository.RecordAsync refuses -- changing nothing -- if the source would go below zero, also on
    //      its business date when back-dated) and TRANSFER_IN on the destination.
    // Any refusal or error rolls the whole transaction back: stock is never taken from one side without reaching the other.
    // The database repeats the rules (TR_MaterialTransfers_Rules, TR_MaterialTransactions_Ledger 51937 / 51938,
    // UQ_MaterialTransfers_Token, UX_MaterialTransactions_TransferLink).
    public class MaterialTransferRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;

        public MaterialTransferRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
        }

        public sealed record Actor(int? UserId, string? UserName, bool IsFullAccess);

        public sealed record Result(bool Success, string? Message, int TransferId, string? Code, bool Duplicate,
            string MaterialName = "", string Unit = "KG", decimal Quantity = 0, decimal SourceBalance = 0, decimal DestinationBalance = 0);

        public Task<bool> SchemaReadyAsync() => MaterialTransferSchema.ReadyAsync(_dbHelper);

        // ---- locations -----------------------------------------------------------------------------------------------

        // Every Polyhouse of an active Main Office or Mother Plant Area (the form's dropdowns; the save re-validates).
        public async Task<List<MaterialTransferLocation>> GetLocationsAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT a.Id, a.Name, a.AreaType, p.Id, p.Name
FROM dbo.Area a JOIN dbo.Polyhouses p ON p.AreaId = a.Id
WHERE a.IsActive = 1 AND a.AreaType IN (N'" + MaterialStockRules.MainOfficeAreaType + "', N'" + TrayTransferRules.MotherPlantAreaType + @"')
ORDER BY CASE WHEN a.AreaType = N'" + MaterialStockRules.MainOfficeAreaType + @"' THEN 0 ELSE 1 END, a.Name, p.Name", conn);
            var list = new List<MaterialTransferLocation>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialTransferLocation(r.GetInt32(0), r.GetString(1).Trim(), r.GetString(2), r.GetInt32(3), r.GetString(4).Trim()));
            return list;
        }

        // Areas where an ACTIVE user assigned to that ACTIVE Area (UserRoles.AreaId) holds MaterialStock.View through any of
        // their roles -- someone there can see what arrives. (The tray-transfer "someone can receive" rule, for a move that
        // has no receive step.)
        private const string ViewableAreasSql = @"
SELECT DISTINCT ur.AreaId
FROM dbo.UserRoles ur
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
JOIN dbo.Area a ON a.Id = ur.AreaId AND a.IsActive = 1
WHERE EXISTS (SELECT 1 FROM dbo.UserRoles r2
              JOIN dbo.RolePermissions rp ON rp.RoleId = r2.RoleId
              JOIN dbo.Permissions p ON p.Id = rp.PermissionId
              WHERE r2.UserId = u.Id AND p.Code = N'" + Authorization.MaterialStockAccess.MaterialView + "')";

        public async Task<HashSet<int>> GetViewableAreaIdsAsync()
        {
            var ids = new HashSet<int>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(ViewableAreasSql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
            return ids;
        }

        private static async Task<bool> HasViewerAsync(SqlConnection conn, SqlTransaction tx, int areaId)
        {
            using var cmd = new SqlCommand($"SELECT CASE WHEN EXISTS (SELECT 1 FROM ({ViewableAreasSql}) x WHERE x.AreaId = @A) THEN 1 ELSE 0 END", conn, tx);
            cmd.Parameters.AddWithValue("@A", areaId);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        private sealed record LocationRow(MaterialTransferRules.Location Location, string AreaName, string PolyhouseName);

        private static async Task<LocationRow?> ReadLocationAsync(SqlConnection conn, SqlTransaction tx, int areaId, int polyhouseId)
        {
            using var cmd = new SqlCommand(@"
SELECT p.AreaId, p.Name, a.Name, a.AreaType, a.IsActive
FROM dbo.Polyhouses p LEFT JOIN dbo.Area a ON a.Id = @A
WHERE p.Id = @P", conn, tx);
            cmd.Parameters.AddWithValue("@A", areaId);
            cmd.Parameters.AddWithValue("@P", polyhouseId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return new LocationRow(
                new MaterialTransferRules.Location(areaId, r.IsDBNull(3) ? null : r.GetString(3), !r.IsDBNull(4) && r.GetBoolean(4), polyhouseId, r.IsDBNull(0) ? null : r.GetInt32(0)),
                r.IsDBNull(2) ? "" : r.GetString(2).Trim(), r.GetString(1).Trim());
        }

        // ---- the transfer ----------------------------------------------------------------------------------------------

        public async Task<Result> CreateAsync(MaterialTransferRequest req, Actor actor, Func<int, bool> canSendFrom)
        {
            Result Fail(string? message) => new(false, message ?? DbErrorMessage.Generic, 0, null, false);
            if (req.SubmissionToken == Guid.Empty) return Fail(MaterialTransferRules.ExpiredFormMessage);
            if (actor.UserId is not > 0) return Fail("Sign in again to record a transfer.");
            if (req.SourceAreaId <= 0 || !canSendFrom(req.SourceAreaId)) return Fail(MaterialTransferRules.NotAuthorizedSourceMessage);
            var requestError = MaterialTransferRules.ValidateRequest(req.Quantity, req.Remarks, req.TransferDate, AppClock.Today);
            if (requestError != null) return Fail(requestError);
            var (dateOk, transactionUtc, dateError) = LedgerDates.Instant(req.TransferDate.Date);
            if (!dateOk) return Fail(dateError);
            var remarks = MaterialStockRules.Trimmed(req.Remarks);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await MaterialTransferSchema.ReadyAsync(conn))
                return Fail(MaterialTransferSchema.MigrationMessage);
            using var tx = conn.BeginTransaction();
            try
            {
                var existing = await FindByTokenAsync(conn, tx, req.SubmissionToken);
                if (existing != null) { tx.Commit(); return existing; }

                var source = await ReadLocationAsync(conn, tx, req.SourceAreaId, req.SourcePolyhouseId);
                var destination = await ReadLocationAsync(conn, tx, req.DestinationAreaId, req.DestinationPolyhouseId);
                var locationError = MaterialTransferRules.ValidateLocations(source?.Location, destination?.Location);
                if (locationError != null) { tx.Rollback(); return Fail(locationError); }
                if (!actor.IsFullAccess && !await HasViewerAsync(conn, tx, req.DestinationAreaId)) { tx.Rollback(); return Fail(MaterialTransferRules.NoViewerMessage); }
                var (materialOk, materialError, materialName, unit, _) = await MaterialStockRepository.GetActiveMaterialAsync(conn, tx, req.MaterialTypeId);
                if (!materialOk) { tx.Rollback(); return Fail(materialError); }

                // Both balances, locked in a fixed order (a new destination balance is created at 0 and rolls back with a refusal).
                int sourceStockId, destinationStockId;
                if (MaterialTransferRules.LockSourceFirst(req.SourceAreaId, req.SourcePolyhouseId, req.DestinationAreaId, req.DestinationPolyhouseId))
                {
                    sourceStockId = await MaterialStockRepository.GetOrCreateLockedAsync(conn, tx, req.MaterialTypeId, req.SourceAreaId, req.SourcePolyhouseId);
                    destinationStockId = await MaterialStockRepository.GetOrCreateLockedAsync(conn, tx, req.MaterialTypeId, req.DestinationAreaId, req.DestinationPolyhouseId);
                }
                else
                {
                    destinationStockId = await MaterialStockRepository.GetOrCreateLockedAsync(conn, tx, req.MaterialTypeId, req.DestinationAreaId, req.DestinationPolyhouseId);
                    sourceStockId = await MaterialStockRepository.GetOrCreateLockedAsync(conn, tx, req.MaterialTypeId, req.SourceAreaId, req.SourcePolyhouseId);
                }

                // Under the locks: the same token submitted twice at once names the same balances, so the second request
                // waits above and finds the first one's transfer here (UQ_MaterialTransfers_Token is the backstop).
                existing = await FindByTokenAsync(conn, tx, req.SubmissionToken);
                if (existing != null) { tx.Commit(); return existing; }

                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, MaterialTransferRules.CodePrefix, req.TransferDate.Year);
                int transferId;
                using (var insert = new SqlCommand(@"
INSERT INTO dbo.MaterialTransfers
(TransferCode, MaterialTypeId, SourceStockId, SourceAreaId, SourcePolyhouseId, DestinationStockId, DestinationAreaId, DestinationPolyhouseId,
 Quantity, TransferDate, Remarks, SubmissionToken, CreatedById, CreatedAt)
VALUES
(@Code, @M, @SS, @SA, @SP, @DS, @DA, @DP, @Q, @Date, @Remarks, @Token, @User, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx))
                {
                    insert.Parameters.AddWithValue("@Code", code);
                    insert.Parameters.AddWithValue("@M", req.MaterialTypeId);
                    insert.Parameters.AddWithValue("@SS", sourceStockId);
                    insert.Parameters.AddWithValue("@SA", req.SourceAreaId);
                    insert.Parameters.AddWithValue("@SP", req.SourcePolyhouseId);
                    insert.Parameters.AddWithValue("@DS", destinationStockId);
                    insert.Parameters.AddWithValue("@DA", req.DestinationAreaId);
                    insert.Parameters.AddWithValue("@DP", req.DestinationPolyhouseId);
                    insert.Parameters.Add("@Q", System.Data.SqlDbType.Decimal).Value = req.Quantity;
                    insert.Parameters["@Q"].Precision = 18; insert.Parameters["@Q"].Scale = 3;
                    insert.Parameters.Add("@Date", System.Data.SqlDbType.Date).Value = req.TransferDate.Date;
                    insert.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@Token", req.SubmissionToken);
                    insert.Parameters.AddWithValue("@User", actor.UserId!.Value);
                    transferId = (int)(await insert.ExecuteScalarAsync())!;
                }

                var (outOk, outMessage, sourceAfter) = await MaterialStockRepository.RecordAsync(conn, tx, sourceStockId,
                    new MaterialStockRepository.Movement(MaterialTransferRules.TransferOut, -1, req.Quantity,
                        ReferenceType: MaterialTransferRules.ReferenceType, ReferenceId: transferId,
                        Remarks: MaterialTransferRules.OutRemark(code, destination!.AreaName, destination.PolyhouseName), TransactionDateUtc: transactionUtc), actor.UserId);
                if (!outOk) { tx.Rollback(); return Fail(outMessage); }
                var (inOk, inMessage, destinationAfter) = await MaterialStockRepository.RecordAsync(conn, tx, destinationStockId,
                    new MaterialStockRepository.Movement(MaterialTransferRules.TransferIn, 1, req.Quantity,
                        ReferenceType: MaterialTransferRules.ReferenceType, ReferenceId: transferId,
                        Remarks: MaterialTransferRules.InRemark(code, source!.AreaName, source.PolyhouseName), TransactionDateUtc: transactionUtc), actor.UserId);
                if (!inOk) { tx.Rollback(); return Fail(inMessage); }

                tx.Commit();
                return new Result(true, null, transferId, code, false, materialName, unit, req.Quantity, sourceAfter, destinationAfter);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // A unique key stopped a second copy (the token backstop): report the first transfer, nothing moved twice.
                try { tx.Rollback(); } catch { }
                return await FindByTokenAsync(req.SubmissionToken) ?? Fail(DbErrorMessage.For(ex));
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return Fail(DbErrorMessage.For(ex));
            }
        }

        private async Task<Result?> FindByTokenAsync(Guid token)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            var found = await FindByTokenAsync(conn, tx, token);
            tx.Commit();
            return found;
        }

        private static async Task<Result?> FindByTokenAsync(SqlConnection conn, SqlTransaction tx, Guid token)
        {
            using var cmd = new SqlCommand(@"
SELECT x.Id, x.TransferCode, m.Name, m.Unit, x.Quantity, so.CurrentQuantity, de.CurrentQuantity
FROM dbo.MaterialTransfers x
JOIN dbo.MaterialTypes m ON m.Id = x.MaterialTypeId
JOIN dbo.MaterialStock so ON so.Id = x.SourceStockId
JOIN dbo.MaterialStock de ON de.Id = x.DestinationStockId
WHERE x.SubmissionToken = @T", conn, tx);
            cmd.Parameters.AddWithValue("@T", token);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return new Result(true, null, r.GetInt32(0), r.GetString(1), true, r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6));
        }

        // ---- history ---------------------------------------------------------------------------------------------------

        // Transfers whose source OR destination Area is in scope (allowedAreaIds null = every Area), newest first.
        public async Task<(List<MaterialTransfer> Rows, int Total)> GetTransfersAsync(MaterialTransferFilter f, IReadOnlyCollection<int>? allowedAreaIds)
        {
            var list = new List<MaterialTransfer>();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0) return (list, 0);
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await MaterialTransferSchema.ReadyAsync(conn)) return (list, 0);
            using var cmd = new SqlCommand { Connection = conn };
            var scope = "";
            if (allowedAreaIds != null)
            {
                cmd.Parameters.AddWithValue("@Allowed", string.Join(",", allowedAreaIds));
                scope = " AND (x.SourceAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))"
                      + " OR x.DestinationAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ',')))";
            }
            var where = $@"
WHERE (@M IS NULL OR x.MaterialTypeId = @M) AND (@A IS NULL OR x.SourceAreaId = @A OR x.DestinationAreaId = @A)
  AND (@From IS NULL OR x.TransferDate >= @From) AND (@To IS NULL OR x.TransferDate <= @To){scope}";
            cmd.CommandText = $@"
SELECT COUNT(*) FROM dbo.MaterialTransfers x {where};
SELECT x.Id, x.TransferCode, x.MaterialTypeId, m.Name, m.Unit,
       x.SourceAreaId, sa.Name, x.SourcePolyhouseId, sp.Name, x.DestinationAreaId, da.Name, x.DestinationPolyhouseId, dp.Name,
       x.Quantity, x.TransferDate, x.Remarks, x.CreatedById, u.Name, x.CreatedAt,
       (SELECT t.BalanceAfter FROM dbo.MaterialTransactions t WHERE t.ReferenceType = @RT AND t.ReferenceId = x.Id AND t.TransactionType = @Out),
       (SELECT t.BalanceAfter FROM dbo.MaterialTransactions t WHERE t.ReferenceType = @RT AND t.ReferenceId = x.Id AND t.TransactionType = @In)
FROM dbo.MaterialTransfers x
JOIN dbo.MaterialTypes m ON m.Id = x.MaterialTypeId
JOIN dbo.Area sa ON sa.Id = x.SourceAreaId
JOIN dbo.Polyhouses sp ON sp.Id = x.SourcePolyhouseId
JOIN dbo.Area da ON da.Id = x.DestinationAreaId
JOIN dbo.Polyhouses dp ON dp.Id = x.DestinationPolyhouseId
LEFT JOIN dbo.IMSUsers u ON u.Id = x.CreatedById
{where}
ORDER BY x.TransferDate DESC, x.Id DESC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";
            cmd.Parameters.AddWithValue("@M", (object?)f.MaterialTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@A", (object?)f.AreaId ?? DBNull.Value);
            cmd.Parameters.Add("@From", System.Data.SqlDbType.Date).Value = f.FromDate.HasValue ? f.FromDate.Value.Date : DBNull.Value;
            cmd.Parameters.Add("@To", System.Data.SqlDbType.Date).Value = f.ToDate.HasValue ? f.ToDate.Value.Date : DBNull.Value;
            cmd.Parameters.AddWithValue("@RT", MaterialTransferRules.ReferenceType);
            cmd.Parameters.AddWithValue("@Out", MaterialTransferRules.TransferOut);
            cmd.Parameters.AddWithValue("@In", MaterialTransferRules.TransferIn);
            var pageSize = Math.Clamp(f.PageSize, 10, 500);
            cmd.Parameters.AddWithValue("@Skip", (Math.Max(1, f.Page) - 1) * pageSize);
            cmd.Parameters.AddWithValue("@Take", pageSize);
            using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            var total = r.GetInt32(0);
            await r.NextResultAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialTransfer
                {
                    Id = r.GetInt32(0), TransferCode = r.GetString(1), MaterialTypeId = r.GetInt32(2), MaterialName = r.GetString(3), Unit = r.GetString(4),
                    SourceAreaId = r.GetInt32(5), SourceAreaName = r.GetString(6).Trim(), SourcePolyhouseId = r.GetInt32(7), SourcePolyhouseName = r.GetString(8).Trim(),
                    DestinationAreaId = r.GetInt32(9), DestinationAreaName = r.GetString(10).Trim(), DestinationPolyhouseId = r.GetInt32(11), DestinationPolyhouseName = r.GetString(12).Trim(),
                    Quantity = r.GetDecimal(13), TransferDate = r.GetDateTime(14), Remarks = r.IsDBNull(15) ? null : r.GetString(15),
                    CreatedById = r.GetInt32(16), CreatedByName = r.IsDBNull(17) ? null : r.GetString(17), CreatedAt = r.GetDateTime(18),
                    SourceBalanceAfter = r.IsDBNull(19) ? null : r.GetDecimal(19), DestinationBalanceAfter = r.IsDBNull(20) ? null : r.GetDecimal(20),
                });
            return (list, total);
        }
    }
}
