using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Stock repository for dbo.TrayStock, with its own dedicated ledger
    // (dbo.TrayStockTransactions) -- same "separate ledger per stock entity"
    // pattern as EmptyPotInventoryRepository. PhysicalQuantity on the stock
    // row is NEVER updated directly outside of a matching ledger insert in
    // the SAME transaction.
    //
    // Business key (2026-10-04, Database/Migrations/2026-10-04_TrayStockPolyhouse.sql):
    // AREA + POLYHOUSE + CAVITY -- trays are physically held in a Polyhouse.
    // The Sowing Supervisor adds trays for a Polyhouse of an Area they are
    // authorized for (AddStockAsync); every Seed/Cutting Sowing consumes from
    // the pool of its own Area + Polyhouse + Cavity (ConsumeForSowingAsync).
    // The short-lived Area-level pools (PolyhouseId NULL) are retired at 0
    // and kept only for history; CK_TrayStock_ActivePoolHasPolyhouse makes
    // sure no Area-level pool can ever be active again.
    public class TrayStockRepository
    {
        // ReferenceType of an Add Tray Stock ledger row; its ReferenceId is
        // the form's one-time submission token (duplicate-submit guard).
        public const string AddReferenceType = "TrayStockAdd";

        public const string PolyhouseRequiredMessage = "Please select a Polyhouse for this sowing.";

        private readonly DatabaseHelper _dbHelper;

        public TrayStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT t.Id, t.AreaId, a.Name AS AreaName, t.PolyhouseId, p.Name AS PolyhouseName, t.TraySize, t.PhysicalQuantity, t.IsActive,
       t.CreatedDate, t.CreatedBy, t.ModifiedDate, t.ModifiedBy
FROM dbo.TrayStock t
INNER JOIN dbo.Area a ON a.Id = t.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId";

        public async Task<List<TrayStock>> GetAllAsync(bool activeOnly = false)
        {
            var list = new List<TrayStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + (activeOnly ? " WHERE t.IsActive = 1" : "") + " ORDER BY a.Name, p.Name, t.TraySize";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        public async Task<TrayStock?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE t.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? Map(reader) : null;
        }

        // Trays currently held by a Polyhouse across all its pools -- used by
        // Admin > Polyhouse to refuse moving a Polyhouse that still holds trays.
        public async Task<decimal> GetPolyhouseBalanceAsync(int polyhouseId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE PolyhouseId = @P", conn);
            cmd.Parameters.AddWithValue("@P", polyhouseId);
            return (decimal)(await cmd.ExecuteScalarAsync())!;
        }

        // Server-side check of a tray location, under the caller's transaction:
        // the Polyhouse exists and belongs to THIS Area, and the Area is
        // active and not an Outlet (Outlets sell; they never sow). Polyhouses
        // have no active flag of their own -- a Polyhouse is usable when its
        // Area is. Never trusts a posted AreaId/PolyhouseId pair.
        public static async Task<(bool Ok, string? Error)> ValidateLocationAsync(SqlConnection conn, SqlTransaction tx, int areaId, int? polyhouseId)
        {
            if (polyhouseId is not > 0)
                return (false, PolyhouseRequiredMessage);
            var cmd = new SqlCommand(@"
SELECT p.AreaId, a.IsActive, a.AreaType
FROM dbo.Polyhouses p
LEFT JOIN dbo.Area a ON a.Id = p.AreaId
WHERE p.Id = @PolyhouseId", conn, tx);
            cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return (false, "Selected Polyhouse does not exist.");
            var polyhouseAreaId = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
            if (polyhouseAreaId != areaId)
                return (false, "Selected Polyhouse does not belong to the selected Area.");
            var areaActive = !reader.IsDBNull(1) && reader.GetBoolean(1);
            var areaType = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (!areaActive || areaType == OutletRules.AreaType)
                return (false, "Tray Stock cannot be used in this Area (inactive or an Outlet).");
            return (true, null);
        }

        // Gets the (AreaId, PolyhouseId, TraySize) pool row, locked for the
        // duration of the caller's transaction, or creates a new zeroed row
        // and locks that instead. Must be called from inside an existing
        // transaction, AFTER ValidateLocationAsync.
        //
        // sp_getapplock takes an exclusive, transaction-scoped mutex on this
        // exact key FIRST -- a true key lock regardless of whether the row
        // exists yet, so two first-time callers can never both reach the
        // INSERT, and two sowings against the same pool run strictly one after
        // the other (the second sees the first one's deduction). Released
        // automatically when the transaction ends. UX_TrayStock_Area_Polyhouse_Size
        // is the database-level backstop.
        //
        // A matching row that is inactive (a Polyhouse that moved Areas and
        // came back -- Admin > Polyhouse retires its empty pools on a move) is
        // re-activated: the location has just been validated as current.
        public async Task<int> GetOrCreateLockedAsync(SqlConnection conn, SqlTransaction tx, int areaId, int polyhouseId, string traySize, string? createdBy)
        {
            var lockKey = $"TrayStock:{areaId}:{polyhouseId}:{traySize}";
            var appLockCmd = new SqlCommand("sp_getapplock", conn, tx) { CommandType = System.Data.CommandType.StoredProcedure };
            appLockCmd.Parameters.AddWithValue("@Resource", lockKey);
            appLockCmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            appLockCmd.Parameters.AddWithValue("@LockOwner", "Transaction");
            appLockCmd.Parameters.AddWithValue("@LockTimeout", 15000);
            var returnValue = appLockCmd.Parameters.Add("@ReturnValue", System.Data.SqlDbType.Int);
            returnValue.Direction = System.Data.ParameterDirection.ReturnValue;
            await appLockCmd.ExecuteNonQueryAsync();
            if ((int)returnValue.Value < 0)
                throw new InvalidOperationException($"Could not reserve {traySize} tray stock for this Polyhouse right now -- another request is in progress. Please try again.");

            var lockCmd = new SqlCommand(
                "SELECT Id, IsActive FROM dbo.TrayStock WITH (UPDLOCK, HOLDLOCK) WHERE AreaId = @AreaId AND PolyhouseId = @PolyhouseId AND TraySize = @TraySize",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@AreaId", areaId);
            lockCmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
            lockCmd.Parameters.AddWithValue("@TraySize", traySize);
            int? existingId = null; bool existingActive = false;
            using (var reader = await lockCmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    existingId = reader.GetInt32(0);
                    existingActive = reader.GetBoolean(1);
                }
            }
            if (existingId.HasValue)
            {
                if (!existingActive)
                {
                    var reactivate = new SqlCommand("UPDATE dbo.TrayStock SET IsActive = 1, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
                    reactivate.Parameters.AddWithValue("@Id", existingId.Value);
                    await reactivate.ExecuteNonQueryAsync();
                }
                return existingId.Value;
            }

            const string insertSql = @"
INSERT INTO dbo.TrayStock (AreaId, PolyhouseId, TraySize, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
VALUES (@AreaId, @PolyhouseId, @TraySize, 0, 1, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@AreaId", areaId);
            insertCmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
            insertCmd.Parameters.AddWithValue("@TraySize", traySize);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)(await insertCmd.ExecuteScalarAsync())!;
        }

        // Records a stock movement against an existing, ACTIVE Tray Stock pool
        // and writes the matching ledger row in the SAME transaction --
        // PhysicalQuantity is never changed any other way. quantityDelta:
        // positive for Add Tray Stock / a reversal, negative for Sowing
        // consumption. Refuses (and changes nothing) if the result would go
        // negative. transactionDateUtc: the business time of an addition
        // (already converted to UTC); defaults to now.
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int trayStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks,
            DateTime? transactionDateUtc = null)
        {
            var lockCmd = new SqlCommand(@"
SELECT t.PhysicalQuantity, a.Name, p.Name, t.TraySize, t.IsActive
FROM dbo.TrayStock t WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Area a ON a.Id = t.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
WHERE t.Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", trayStockId);
            decimal before; string areaName, traySize; string? polyhouseName; bool isActive;
            using (var reader = await lockCmd.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                    return (false, "Tray Stock record not found.");
                before = reader.GetDecimal(0);
                areaName = reader.GetString(1);
                polyhouseName = reader.IsDBNull(2) ? null : reader.GetString(2);
                traySize = reader.GetString(3);
                isActive = reader.GetBoolean(4);
            }
            if (!isActive)
                return (false, "This Tray Stock pool is retired and cannot be changed.");

            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"Insufficient tray stock for this Area and Polyhouse ({areaName}, {polyhouseName}, {traySize}). Available: {QuantityFormat.Qty(before)}, Required: {QuantityFormat.Qty(-quantityDelta)}.");

            var updateCmd = new SqlCommand("UPDATE dbo.TrayStock SET PhysicalQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", trayStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string ledgerSql = @"
INSERT INTO dbo.TrayStockTransactions
(TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@TrayStockId, ISNULL(@TransactionDate, SYSUTCDATETIME()), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";
            var ledgerCmd = new SqlCommand(ledgerSql, conn, tx);
            ledgerCmd.Parameters.AddWithValue("@TrayStockId", trayStockId);
            ledgerCmd.Parameters.Add("@TransactionDate", System.Data.SqlDbType.DateTime2).Value = (object?)transactionDateUtc ?? DBNull.Value;
            ledgerCmd.Parameters.AddWithValue("@TransactionType", transactionType);
            ledgerCmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Quantity", quantityDelta);
            ledgerCmd.Parameters.AddWithValue("@BeforeQuantity", before);
            ledgerCmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await ledgerCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // Sowing / overage integration: validates the location, resolves (and
        // creates if needed) the Area + Polyhouse + Cavity pool, then consumes
        // requiredTrays from it, inside the caller's own transaction -- a
        // failure rolls back the whole sowing or approval. requiredTrays is
        // already CEILING-rounded by the caller. referenceType/referenceId:
        // "SeedSowing"/sowing id for a sowing, "ReadyConfirmation"/approval id
        // for an overage -- so each cancellation path returns exactly its own
        // consumption. A missing Polyhouse is refused: trays are never taken
        // from a generic Area-level pool.
        public async Task<(bool Success, string? Message)> ConsumeForSowingAsync(
            SqlConnection conn, SqlTransaction tx, int areaId, int? polyhouseId, string traySize, decimal requiredTrays,
            string referenceType, int referenceId, int? userId, string? remarks)
        {
            if (requiredTrays <= 0)
                return (true, null);
            var (locationOk, locationError) = await ValidateLocationAsync(conn, tx, areaId, polyhouseId);
            if (!locationOk)
                return (false, locationError);
            var trayStockId = await GetOrCreateLockedAsync(conn, tx, areaId, polyhouseId!.Value, traySize, null);
            return await RecordTransactionAsync(conn, tx, trayStockId, -requiredTrays, "Sowing", referenceType, referenceId, userId, remarks);
        }

        // Sowing Supervisor's own action (Pages/Production/TrayStock/Add).
        // Self-contained transaction. Never trusts the posted values: whole-
        // number quantity, a cavity from the sowing system's own closed set,
        // the caller's Area authorization (canAccessArea), and a Polyhouse
        // that belongs to that active, non-Outlet Area -- all re-checked here,
        // server-side. submissionToken: the form's one-time token; a repeated
        // submit of the same form (double click, refresh, retry) finds the
        // first submit's ledger row under the same pool lock and adds nothing.
        public async Task<(bool Success, string? Message, bool Duplicate, decimal Balance)> AddStockAsync(
            int areaId, int polyhouseId, string traySize, decimal quantity, DateTime transactionDateUtc, int submissionToken,
            int? userId, string? createdBy, string? remarks, Func<int, bool> canAccessArea)
        {
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Tray Quantity must be a whole number greater than zero.", false, 0);
            if (!DirectSowingRules.IsValidCavityType(traySize))
                return (false, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.", false, 0);
            if (submissionToken <= 0)
                return (false, "This form has expired. Reload the page and enter the trays again.", false, 0);
            if (!canAccessArea(areaId))
                return (false, "You are not authorized to add Tray Stock for the selected Area.", false, 0);
            if (polyhouseId <= 0)
                return (false, "Polyhouse is required.", false, 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var (locationOk, locationError) = await ValidateLocationAsync(conn, tx, areaId, polyhouseId);
                if (!locationOk)
                {
                    tx.Rollback();
                    return (false, locationError, false, 0);
                }
                var trayStockId = await GetOrCreateLockedAsync(conn, tx, areaId, polyhouseId, traySize, createdBy);

                var dupCmd = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.TrayStockTransactions
WHERE TrayStockId = @TrayStockId AND ReferenceType = @RefType AND ReferenceId = @Token", conn, tx);
                dupCmd.Parameters.AddWithValue("@TrayStockId", trayStockId);
                dupCmd.Parameters.AddWithValue("@RefType", AddReferenceType);
                dupCmd.Parameters.AddWithValue("@Token", submissionToken);
                var duplicate = (int)(await dupCmd.ExecuteScalarAsync())! > 0;

                if (!duplicate)
                {
                    var (success, message) = await RecordTransactionAsync(
                        conn, tx, trayStockId, quantity, "Allocation", AddReferenceType, submissionToken, userId, remarks, transactionDateUtc);
                    if (!success)
                    {
                        tx.Rollback();
                        return (false, message, false, 0);
                    }
                }

                var balCmd = new SqlCommand("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE Id = @Id", conn, tx);
                balCmd.Parameters.AddWithValue("@Id", trayStockId);
                var balance = (decimal)(await balCmd.ExecuteScalarAsync())!;
                tx.Commit();
                return (true, null, duplicate, balance);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, false, 0);
            }
        }

        // Generic reversal, reused by both cancellation paths that can undo a
        // tray consumption: SeedSowingRepository.CancelAsync
        // (ReferenceType="SeedSowing") and ReadyConfirmationRepository.CancelAsync
        // (ReferenceType="ReadyConfirmation"). Never recalculates from
        // quantity/cavity -- reads the NET outstanding amount straight from the
        // ledger (consumption minus any prior reversal under the same
        // reference), and returns it to the EXACT Area + Polyhouse + Cavity
        // pool it was consumed from. A consumption taken from a retired
        // Area-level pool (only possible between the two 2026-10-04
        // migrations) goes back to the sowing's own Polyhouse in that Area.
        // Never returns twice; a clean no-op when nothing is outstanding.
        public async Task<(bool Success, string? Message)> ReverseConsumptionAsync(
            SqlConnection conn, SqlTransaction tx, string referenceType, int referenceId, int? userId, string? remarks)
        {
            var netCmd = new SqlCommand(@"
SELECT t.AreaId, COALESCE(t.PolyhouseId, sw.PolyhouseId) AS TargetPolyhouseId, t.TraySize, ISNULL(-SUM(tr.Quantity), 0) AS NetToReturn
FROM dbo.TrayStockTransactions tr
INNER JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId
LEFT JOIN dbo.ReadyConfirmations rc ON tr.ReferenceType = N'ReadyConfirmation' AND rc.Id = tr.ReferenceId
LEFT JOIN dbo.SeedSowings sw ON sw.Id = CASE WHEN tr.ReferenceType = N'SeedSowing' THEN tr.ReferenceId ELSE rc.SeedSowingId END
WHERE tr.ReferenceType = @ReferenceType AND tr.ReferenceId = @ReferenceId
  AND tr.TransactionType IN (N'Sowing', N'ReversalReturn')
GROUP BY t.AreaId, COALESCE(t.PolyhouseId, sw.PolyhouseId), t.TraySize
HAVING ISNULL(-SUM(tr.Quantity), 0) > 0", conn, tx);
            netCmd.Parameters.AddWithValue("@ReferenceType", referenceType);
            netCmd.Parameters.AddWithValue("@ReferenceId", referenceId);
            var toReturn = new List<(int AreaId, int? PolyhouseId, string TraySize, decimal Net)>();
            using (var reader = await netCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    toReturn.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetString(2), reader.GetDecimal(3)));
            }

            foreach (var (areaId, polyhouseId, traySize, net) in toReturn)
            {
                if (!polyhouseId.HasValue)
                    return (false, "The trays used by this record cannot be matched to a Polyhouse -- nothing was changed.");
                var trayStockId = await GetOrCreateLockedAsync(conn, tx, areaId, polyhouseId.Value, traySize, null);
                var (success, message) = await RecordTransactionAsync(
                    conn, tx, trayStockId, net, "ReversalReturn", referenceType, referenceId, userId, remarks);
                if (!success)
                    return (false, message);
            }
            return (true, null);
        }

        // Ledger history. allowedAreaIds: null = no Area restriction (full
        // access); otherwise only rows of those Areas are returned -- the
        // Area scope is applied in SQL, never only in the view.
        public async Task<List<TrayStockTransaction>> GetTransactionsAsync(
            int? areaId = null, int? polyhouseId = null, string? traySize = null, IReadOnlyCollection<int>? allowedAreaIds = null)
        {
            var list = new List<TrayStockTransaction>();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0)
                return list;

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = @"
SELECT tr.Id, tr.TrayStockId, t.AreaId, a.Name AS AreaName, t.PolyhouseId, p.Name AS PolyhouseName, t.TraySize, tr.TransactionDate, tr.TransactionType,
       tr.ReferenceType, tr.ReferenceId, tr.Quantity, tr.BeforeQuantity, tr.UserId, u.Name AS UserName, tr.Remarks, tr.CreatedAt
FROM dbo.TrayStockTransactions tr
INNER JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId
INNER JOIN dbo.Area a ON a.Id = t.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
LEFT JOIN dbo.IMSUsers u ON u.Id = tr.UserId
WHERE (@AreaId IS NULL OR t.AreaId = @AreaId)
  AND (@PolyhouseId IS NULL OR t.PolyhouseId = @PolyhouseId)
  AND (@TraySize IS NULL OR t.TraySize = @TraySize)
  AND (@Allowed IS NULL OR t.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ',')))
ORDER BY tr.CreatedAt DESC, tr.Id DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TraySize", (object?)traySize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Allowed", allowedAreaIds == null ? DBNull.Value : string.Join(",", allowedAreaIds));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new TrayStockTransaction
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    TrayStockId = reader.GetInt32(reader.GetOrdinal("TrayStockId")),
                    AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                    AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                    PolyhouseId = reader.IsDBNull(reader.GetOrdinal("PolyhouseId")) ? null : reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                    PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                    TraySize = reader.GetString(reader.GetOrdinal("TraySize")),
                    TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                    TransactionType = reader.GetString(reader.GetOrdinal("TransactionType")),
                    ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                    ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                    Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                    BeforeQuantity = reader.GetDecimal(reader.GetOrdinal("BeforeQuantity")),
                    UserId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? null : reader.GetInt32(reader.GetOrdinal("UserId")),
                    UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                    Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
                });
            }
            return list;
        }

        private static TrayStock Map(SqlDataReader reader)
        {
            return new TrayStock
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                PolyhouseId = reader.IsDBNull(reader.GetOrdinal("PolyhouseId")) ? null : reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                TraySize = reader.GetString(reader.GetOrdinal("TraySize")),
                PhysicalQuantity = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity")),
                IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
