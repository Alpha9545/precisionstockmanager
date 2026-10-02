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
    // Business key: (PolyhouseId, TraySize) -- a Polyhouse, never an Area.
    // Trays only ever exist at Polyhouses whose own Area is AreaType=
    // 'MainOffice' (GetMainOfficePolyhousesAsync enforces this for the
    // allocation screen; IsMainOfficePolyhouseAsync is the same check used
    // by the Seed/Cutting Sowing integration to decide whether tray
    // consumption applies to a given sowing's destination Polyhouse at all).
    public class TrayStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public TrayStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT t.Id, t.PolyhouseId, p.Name AS PolyhouseName, p.AreaId, a.Name AS AreaName, t.TraySize, t.PhysicalQuantity, t.IsActive,
       t.CreatedDate, t.CreatedBy, t.ModifiedDate, t.ModifiedBy
FROM dbo.TrayStock t
INNER JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
LEFT JOIN dbo.Area a ON a.Id = p.AreaId";

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

        // Every active, real Polyhouse that belongs to an active Main
        // Office-type Area -- the ONLY valid tray allocation/consumption
        // locations (requirement 18: Polyhouse, never Area, never global;
        // requirement 4: Main Office Area Polyhouses only, never Outlet,
        // never a growing-site Polyhouse elsewhere, never unassigned).
        public async Task<List<Polyhouse>> GetMainOfficePolyhousesAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT p.Id, p.Name, p.AreaId, a.Name AS AreaName, a.IsActive AS AreaIsActive
FROM dbo.Polyhouses p
INNER JOIN dbo.Area a ON a.Id = p.AreaId
WHERE a.AreaType = N'MainOffice' AND a.IsActive = 1
ORDER BY a.Name, p.Name";
            using var cmd = new SqlCommand(sql, conn);
            var list = new List<Polyhouse>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new Polyhouse
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    AreaId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    AreaName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    AreaIsActive = !reader.IsDBNull(4) && reader.GetBoolean(4)
                });
            }
            return list;
        }

        // Whether a given Polyhouse is a valid tray location (active, real,
        // under an active Main Office-type Area). Used by the Seed/Cutting
        // Sowing integration to decide whether tray validation/consumption
        // applies to THIS sowing's destination Polyhouse at all -- sowing
        // into any other (non-Main-Office) Polyhouse is completely
        // unaffected by this feature, exactly as scoped.
        public async Task<bool> IsMainOfficePolyhouseAsync(SqlConnection conn, SqlTransaction tx, int polyhouseId)
        {
            var cmd = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.Polyhouses p
INNER JOIN dbo.Area a ON a.Id = p.AreaId
WHERE p.Id = @PolyhouseId AND a.AreaType = N'MainOffice' AND a.IsActive = 1", conn, tx);
            cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
            return (int)(await cmd.ExecuteScalarAsync())! > 0;
        }

        // Gets the existing (PolyhouseId, TraySize) pool row, locked for the
        // duration of the caller's transaction, or creates a new zeroed row
        // and locks that instead. Must be called from inside an existing
        // transaction.
        //
        // Race note: "SELECT ... WITH (UPDLOCK, HOLDLOCK)" below only locks
        // rows it actually finds. When this is the FIRST-EVER allocation for
        // a (PolyhouseId, TraySize) pair, there is no row yet, so that hint
        // locks nothing -- two concurrent callers could both see "not
        // found" and both reach the INSERT, racing on it. The UNIQUE
        // constraint (UQ_TrayStock_Polyhouse_Size) stops the duplicate ROW,
        // but the loser gets a raw constraint-violation exception rather
        // than a clean, predictable outcome.
        //
        // sp_getapplock takes an exclusive, transaction-scoped mutex on this
        // exact (PolyhouseId, TraySize) key FIRST -- an app lock is a true
        // key lock regardless of whether the underlying row exists, so the
        // second caller simply waits here, then finds the first caller's
        // row already committed (or rolled back) and proceeds cleanly. The
        // lock is released automatically when the transaction ends
        // (@LockOwner = 'Transaction'), so no manual release is needed on
        // either the success or the rollback path. Every other
        // (PolyhouseId, TraySize) pair uses a different key and is
        // completely unaffected -- this never blocks unrelated allocations.
        public async Task<int> GetOrCreateLockedAsync(SqlConnection conn, SqlTransaction tx, int polyhouseId, string traySize, string? createdBy)
        {
            var lockKey = $"TrayStock:{polyhouseId}:{traySize}";
            var appLockCmd = new SqlCommand("sp_getapplock", conn, tx) { CommandType = System.Data.CommandType.StoredProcedure };
            appLockCmd.Parameters.AddWithValue("@Resource", lockKey);
            appLockCmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            appLockCmd.Parameters.AddWithValue("@LockOwner", "Transaction");
            appLockCmd.Parameters.AddWithValue("@LockTimeout", 15000);
            var returnValue = appLockCmd.Parameters.Add("@ReturnValue", System.Data.SqlDbType.Int);
            returnValue.Direction = System.Data.ParameterDirection.ReturnValue;
            await appLockCmd.ExecuteNonQueryAsync();
            var lockResult = (int)returnValue.Value;
            if (lockResult < 0)
                throw new InvalidOperationException($"Could not reserve {traySize} tray stock for this Polyhouse right now -- another request is in progress. Please try again.");

            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.TrayStock WITH (UPDLOCK, HOLDLOCK) WHERE PolyhouseId = @PolyhouseId AND TraySize = @TraySize",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
            lockCmd.Parameters.AddWithValue("@TraySize", traySize);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
                return (int)existingId;

            const string insertSql = @"
INSERT INTO dbo.TrayStock (PolyhouseId, TraySize, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
VALUES (@PolyhouseId, @TraySize, 0, 1, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
            insertCmd.Parameters.AddWithValue("@TraySize", traySize);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)(await insertCmd.ExecuteScalarAsync())!;
        }

        // Records a stock movement against an existing Tray Stock pool and
        // writes the matching ledger row in the SAME transaction --
        // PhysicalQuantity is never changed any other way. quantityDelta:
        // positive for an Officer's Allocation, negative for Sowing
        // consumption/Adjustment. Refuses (and changes nothing) if the
        // result would go negative.
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int trayStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var lockCmd = new SqlCommand(@"
SELECT t.PhysicalQuantity, p.Name, t.TraySize
FROM dbo.TrayStock t WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
WHERE t.Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", trayStockId);
            decimal before; string polyhouseName, traySize;
            using (var reader = await lockCmd.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                    return (false, "Tray Stock record not found.");
                before = reader.GetDecimal(0);
                polyhouseName = reader.GetString(1);
                traySize = reader.GetString(2);
            }

            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"Insufficient {traySize} tray stock in {polyhouseName}. Available: {before:N0}, Required: {-quantityDelta:N0}.");

            var updateCmd = new SqlCommand("UPDATE dbo.TrayStock SET PhysicalQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", trayStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string ledgerSql = @"
INSERT INTO dbo.TrayStockTransactions
(TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@TrayStockId, SYSUTCDATETIME(), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";
            var ledgerCmd = new SqlCommand(ledgerSql, conn, tx);
            ledgerCmd.Parameters.AddWithValue("@TrayStockId", trayStockId);
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

        // Convenience wrapper for the Sowing integration: resolves (and
        // creates if needed) the pool for this Polyhouse/TraySize, then
        // consumes requiredTrays from it -- a single call for
        // SeedSowingRepository.InsertAsync/InsertFromCuttingAsync to make
        // from inside their own transaction. requiredTrays must already be
        // CEILING-rounded by the caller (DirectSowingRules-style pure
        // calculation stays the caller's responsibility, matching how every
        // other stock deduction in this codebase is computed by the caller
        // and merely recorded here).
        public async Task<(bool Success, string? Message)> ConsumeForSowingAsync(
            SqlConnection conn, SqlTransaction tx, int polyhouseId, string traySize, decimal requiredTrays,
            int seedSowingId, int? userId, string? remarks)
        {
            var trayStockId = await GetOrCreateLockedAsync(conn, tx, polyhouseId, traySize, null);
            return await RecordTransactionAsync(conn, tx, trayStockId, -requiredTrays, "Sowing", "SeedSowing", seedSowingId, userId, remarks);
        }

        // Main Office Officer's own standalone action (Pages/Production/
        // TrayStock/Allocate): gives a quantity of trays to a Main Office
        // Polyhouse. Self-contained (opens and commits its own transaction)
        // -- unlike ConsumeForSowingAsync, which must run inside the
        // caller's own Sowing transaction. Re-validates polyhouseId is a
        // real, active Main Office Polyhouse under lock -- never trusts a
        // posted value as-is.
        public async Task<(bool Success, string? Message)> AllocateAsync(
            int polyhouseId, string traySize, decimal quantity, int? userId, string? createdBy, string? remarks)
        {
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.");
            if (!DirectSowingRules.IsValidCavityType(traySize))
                return (false, $"Tray size must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                if (!await IsMainOfficePolyhouseAsync(conn, tx, polyhouseId))
                {
                    tx.Rollback();
                    return (false, "Choose an active Polyhouse under a Main Office Area.");
                }
                var trayStockId = await GetOrCreateLockedAsync(conn, tx, polyhouseId, traySize, createdBy);
                var (success, message) = await RecordTransactionAsync(conn, tx, trayStockId, quantity, "Allocation", null, null, userId, remarks);
                if (!success)
                {
                    tx.Rollback();
                    return (false, message);
                }
                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        // Generic reversal, reused by both cancellation paths that can undo
        // a tray consumption: SeedSowingRepository.CancelAsync (cancelling a
        // whole sowing -- ReferenceType="SeedSowing", the ORIGINAL sowing-
        // time consumption) and ReadyConfirmationRepository.CancelAsync
        // (cancelling one approval -- ReferenceType="ReadyConfirmation",
        // only that approval's own overage consumption). Never recalculates
        // from quantity/cavity/polyhouse -- reads the NET outstanding amount
        // straight from the ledger itself (consumption minus any prior
        // reversal under the same reference), exactly the technique
        // ReadyConfirmationRepository.CancelAsync already uses for the
        // identical problem on CuttingStockTransactions. A clean no-op
        // (returns success, touches nothing) when nothing was ever consumed
        // under that reference -- a legacy pre-Tray-Stock sowing, a non-
        // Main-Office destination, or a sowing with no overage.
        public async Task<(bool Success, string? Message)> ReverseConsumptionAsync(
            SqlConnection conn, SqlTransaction tx, string referenceType, int referenceId, int? userId, string? remarks)
        {
            var netCmd = new SqlCommand(@"
SELECT TrayStockId, ISNULL(-SUM(Quantity), 0) AS NetToReturn
FROM dbo.TrayStockTransactions
WHERE ReferenceType = @ReferenceType AND ReferenceId = @ReferenceId
  AND TransactionType IN (N'Sowing', N'ReversalReturn')
GROUP BY TrayStockId
HAVING ISNULL(-SUM(Quantity), 0) > 0", conn, tx);
            netCmd.Parameters.AddWithValue("@ReferenceType", referenceType);
            netCmd.Parameters.AddWithValue("@ReferenceId", referenceId);
            var toReturn = new List<(int TrayStockId, decimal Net)>();
            using (var reader = await netCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    toReturn.Add((reader.GetInt32(0), reader.GetDecimal(1)));
            }

            foreach (var (trayStockId, net) in toReturn)
            {
                var (success, message) = await RecordTransactionAsync(
                    conn, tx, trayStockId, net, "ReversalReturn", referenceType, referenceId, userId, remarks);
                if (!success)
                    return (false, message);
            }
            return (true, null);
        }

        public async Task<List<TrayStockTransaction>> GetTransactionsAsync(int? trayStockId = null, int? polyhouseId = null, string? traySize = null)
        {
            var list = new List<TrayStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = @"
SELECT tr.Id, tr.TrayStockId, p.Name AS PolyhouseName, t.TraySize, tr.TransactionDate, tr.TransactionType, tr.ReferenceType, tr.ReferenceId,
       tr.Quantity, tr.BeforeQuantity, tr.UserId, u.Name AS UserName, tr.Remarks, tr.CreatedAt
FROM dbo.TrayStockTransactions tr
INNER JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId
INNER JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
LEFT JOIN dbo.IMSUsers u ON u.Id = tr.UserId
WHERE (@TrayStockId IS NULL OR tr.TrayStockId = @TrayStockId)
  AND (@PolyhouseId IS NULL OR t.PolyhouseId = @PolyhouseId)
  AND (@TraySize IS NULL OR t.TraySize = @TraySize)
ORDER BY tr.CreatedAt DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TrayStockId", (object?)trayStockId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TraySize", (object?)traySize ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new TrayStockTransaction
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    TrayStockId = reader.GetInt32(reader.GetOrdinal("TrayStockId")),
                    PolyhouseName = reader.GetString(reader.GetOrdinal("PolyhouseName")),
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
                PolyhouseId = reader.GetInt32(reader.GetOrdinal("PolyhouseId")),
                PolyhouseName = reader.GetString(reader.GetOrdinal("PolyhouseName")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
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
