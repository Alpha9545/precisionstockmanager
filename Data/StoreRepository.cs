using System.Text;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Inventory Store (Database/Migrations/2026-10-06_StoreInventory.sql).
    //
    // EVERY change of Main Office stock goes through MoveStockAsync, inside the caller's
    // transaction: the item row is locked (UPDLOCK) BEFORE its balance is read, so a second
    // operation on the same item waits and then sees the new balance (never a lost update);
    // the ledger row is written with BalanceBefore / BalanceAfter, then the item. The database
    // refuses anything else (TR_StoreTransactions_Ledger: balance before = current stock;
    // TR_StoreItems_Balance: stock = SUM(ledger); CK_StoreItems_Available >= 0).
    // Multi-item operations lock their items in ItemId order, so two of them never deadlock.
    //
    // Request workflow steps are conditional UPDATEs ("... WHERE Status = <expected>"), so two
    // people can never perform the same step twice (false = the request moved on).
    // WHO may call a step is decided by the page (StoreAuthorization) BEFORE calling here.
    public class StoreRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public StoreRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public enum StepResult { Done, Changed }

        public sealed record StockMove(int ItemId, decimal Quantity, string Type, DateTime Date, int? RequestId = null, int? RequestLineId = null,
            int? AreaId = null, int? PolyhouseId = null, int? VendorId = null, string? Reference = null, decimal? UnitCost = null, string? Remarks = null);

        // ---- categories and items ----------------------------------------------------------

        public async Task<List<StoreCategory>> GetCategoriesAsync(bool activeOnly = false)
        {
            var list = new List<StoreCategory>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, Name, IsActive FROM dbo.StoreItemCategories WHERE (@A = 0 OR IsActive = 1) ORDER BY Name", conn);
            cmd.Parameters.AddWithValue("@A", activeOnly);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new StoreCategory { Id = r.GetInt32(0), Name = r.GetString(1), IsActive = r.GetBoolean(2) });
            return list;
        }

        // Returns the new id, or null when the name exists already.
        public async Task<int?> CreateCategoryAsync(string name, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.StoreItemCategories (Name, CreatedById) VALUES (@Name, @User);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);
            cmd.Parameters.AddWithValue("@Name", name.Trim());
            cmd.Parameters.AddWithValue("@User", (object?)userId ?? DBNull.Value);
            try { return (int)(await cmd.ExecuteScalarAsync())!; }
            catch (SqlException ex) when (ex.Number is 2601 or 2627) { return null; }
        }

        public async Task<List<(int Id, string Name)>> GetUnitsAsync()
        {
            var list = new List<(int, string)>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT UnitId, LTRIM(RTRIM(UnitName)) FROM dbo.UnitMaster WHERE IsActive = 1 ORDER BY UnitName", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add((r.GetInt32(0), r.GetString(1)));
            return list;
        }

        private const string ItemSelect = @"
SELECT i.Id, i.ItemCode, i.Name, i.CategoryId, c.Name, i.Description, i.UnitId, LTRIM(RTRIM(u.UnitName)), i.ItemType, i.MinimumStock,
       i.AvailableQuantity, i.Location, i.IsActive, i.Remarks, i.CreatedAt, i.UpdatedAt, uu.Name,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.StoreTransactions t WHERE t.ItemId = i.Id) OR EXISTS (SELECT 1 FROM dbo.StoreRequestLines l WHERE l.ItemId = i.Id) THEN 1 ELSE 0 END
FROM dbo.StoreItems i
JOIN dbo.StoreItemCategories c ON c.Id = i.CategoryId
JOIN dbo.UnitMaster u ON u.UnitId = i.UnitId
LEFT JOIN dbo.IMSUsers uu ON uu.Id = i.UpdatedById";

        public async Task<List<StoreItem>> GetItemsAsync(bool activeOnly = false, int? categoryId = null)
        {
            var list = new List<StoreItem>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(ItemSelect + " WHERE (@A = 0 OR i.IsActive = 1) AND (@C IS NULL OR i.CategoryId = @C) ORDER BY i.Name", conn);
            cmd.Parameters.AddWithValue("@A", activeOnly);
            cmd.Parameters.AddWithValue("@C", (object?)categoryId ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapItem(r));
            return list;
        }

        public async Task<StoreItem?> GetItemAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(ItemSelect + " WHERE i.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapItem(r) : null;
        }

        // Returns the new id, or null when the item code exists already. A new item has 0 stock.
        public async Task<int?> CreateItemAsync(StoreItem item, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.StoreItems (ItemCode, Name, CategoryId, Description, UnitId, ItemType, MinimumStock, Location, IsActive, Remarks, CreatedById)
VALUES (@Code, @Name, @Category, @Description, @Unit, @Type, @Minimum, @Location, @Active, @Remarks, @User);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);
            AddItem(cmd, item, userId);
            cmd.Parameters.AddWithValue("@Code", item.ItemCode.Trim().ToUpperInvariant());
            try { return (int)(await cmd.ExecuteScalarAsync())!; }
            catch (SqlException ex) when (ex.Number is 2601 or 2627) { return null; }
        }

        // Item code, stock and history never change here. Unit and type change only while the item
        // has no movements and no request lines (they would change the meaning of past quantities).
        public async Task<bool> UpdateItemAsync(StoreItem item, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
UPDATE i SET Name = @Name, CategoryId = @Category, Description = @Description, MinimumStock = @Minimum, Location = @Location,
       IsActive = @Active, Remarks = @Remarks, UpdatedById = @User, UpdatedAt = SYSUTCDATETIME(),
       UnitId = CASE WHEN x.Used = 1 THEN i.UnitId ELSE @Unit END,
       ItemType = CASE WHEN x.Used = 1 THEN i.ItemType ELSE @Type END
FROM dbo.StoreItems i
CROSS APPLY (SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.StoreTransactions t WHERE t.ItemId = i.Id)
                          OR EXISTS (SELECT 1 FROM dbo.StoreRequestLines l WHERE l.ItemId = i.Id) THEN 1 ELSE 0 END AS Used) x
WHERE i.Id = @Id", conn);
            AddItem(cmd, item, userId);
            cmd.Parameters.AddWithValue("@Id", item.Id);
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        private static void AddItem(SqlCommand cmd, StoreItem item, int? userId)
        {
            cmd.Parameters.AddWithValue("@Name", item.Name.Trim());
            cmd.Parameters.AddWithValue("@Category", item.CategoryId);
            cmd.Parameters.AddWithValue("@Description", (object?)StoreRules.Trimmed(item.Description) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Unit", item.UnitId);
            cmd.Parameters.AddWithValue("@Type", item.ItemType);
            cmd.Parameters.AddWithValue("@Minimum", item.MinimumStock);
            cmd.Parameters.AddWithValue("@Location", (object?)StoreRules.Trimmed(item.Location) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Active", item.IsActive);
            cmd.Parameters.AddWithValue("@Remarks", (object?)StoreRules.Trimmed(item.Remarks) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@User", (object?)userId ?? DBNull.Value);
        }

        // ---- stock movements -----------------------------------------------------------------

        // Purchase, adjustment, or a direct permanent transfer to an Area / Polyhouse.
        public async Task<StoreTransaction> RecordMovementAsync(StockMove move, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var id = await MoveStockAsync(conn, tx, move, userId);
                tx.Commit();
                return (await SearchTransactionsAsync(new StoreTransactionFilter(), null, id)).Single();
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // Takes the items' update locks in ItemId order (two multi-item operations can never wait
        // for each other in opposite orders). MoveStockAsync re-reads under the lock it then holds.
        private static async Task LockItemsAsync(SqlConnection conn, SqlTransaction tx, IEnumerable<int> itemIds)
        {
            foreach (var id in itemIds.Distinct().OrderBy(x => x))
            {
                using var cmd = new SqlCommand("SELECT Id FROM dbo.StoreItems WITH (UPDLOCK, ROWLOCK, FORCESEEK) WHERE Id = @Id", conn, tx);
                cmd.Parameters.AddWithValue("@Id", id);
                await cmd.ExecuteScalarAsync();
            }
        }

        private static async Task<int> MoveStockAsync(SqlConnection conn, SqlTransaction tx, StockMove m, int? userId)
        {
            decimal before;
            string name;
            using (var read = new SqlCommand("SELECT AvailableQuantity, ItemCode + ' ' + Name FROM dbo.StoreItems WITH (UPDLOCK, ROWLOCK, FORCESEEK) WHERE Id = @Id", conn, tx))
            {
                read.Parameters.AddWithValue("@Id", m.ItemId);
                using var r = await read.ExecuteReaderAsync();
                if (!await r.ReadAsync())
                    throw new StoreStockException("The store item does not exist.");
                before = r.GetDecimal(0);
                name = r.GetString(1);
            }
            var after = before + m.Quantity;
            if (after < 0)
                throw new StoreStockException($"Not enough stock of {name}: available {StoreRules.Qty(before)}, needed {StoreRules.Qty(-m.Quantity)}.");

            int id;
            using (var insert = new SqlCommand(@"
INSERT INTO dbo.StoreTransactions (ItemId, TransactionType, Quantity, BalanceBefore, BalanceAfter, TransactionDate, RequestId, RequestLineId,
                                   AreaId, PolyhouseId, VendorId, Reference, UnitCost, Remarks, CreatedById, CreatedAt)
VALUES (@Item, @Type, @Qty, @Before, @After, @Date, @Request, @Line, @Area, @Polyhouse, @Vendor, @Reference, @UnitCost, @Remarks, @User, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx))
            {
                insert.Parameters.AddWithValue("@Item", m.ItemId);
                insert.Parameters.AddWithValue("@Type", m.Type);
                insert.Parameters.AddWithValue("@Qty", m.Quantity);
                insert.Parameters.AddWithValue("@Before", before);
                insert.Parameters.AddWithValue("@After", after);
                insert.Parameters.AddWithValue("@Date", m.Date.Date);
                insert.Parameters.AddWithValue("@Request", (object?)m.RequestId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Line", (object?)m.RequestLineId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Area", (object?)m.AreaId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Polyhouse", (object?)m.PolyhouseId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Vendor", (object?)m.VendorId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Reference", (object?)StoreRules.Trimmed(m.Reference) ?? DBNull.Value);
                insert.Parameters.AddWithValue("@UnitCost", (object?)m.UnitCost ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Remarks", (object?)StoreRules.Trimmed(m.Remarks) ?? DBNull.Value);
                insert.Parameters.AddWithValue("@User", (object?)userId ?? DBNull.Value);
                id = (int)(await insert.ExecuteScalarAsync())!;
            }
            using (var update = new SqlCommand(@"
UPDATE dbo.StoreItems SET AvailableQuantity = @After WHERE Id = @Id AND AvailableQuantity = @Before", conn, tx))
            {
                update.Parameters.AddWithValue("@Id", m.ItemId);
                update.Parameters.AddWithValue("@After", after);
                update.Parameters.AddWithValue("@Before", before);
                if (await update.ExecuteNonQueryAsync() != 1)
                    throw new StoreStockException("Store stock changed meanwhile. Please try again.");
            }
            return id;
        }

        // ---- requests ----------------------------------------------------------------------

        public async Task<int> CreateRequestAsync(int areaId, int? polyhouseId, string purpose, IReadOnlyList<StoreRequestLineInput> lines, int userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.StoreRequests (AreaId, PolyhouseId, Purpose, Status, RequestedById, RequestedAt)
VALUES (@Area, @Polyhouse, @Purpose, 'Pending', @User, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                cmd.Parameters.AddWithValue("@Area", areaId);
                cmd.Parameters.AddWithValue("@Polyhouse", (object?)polyhouseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Purpose", purpose.Trim());
                cmd.Parameters.AddWithValue("@User", userId);
                var id = (int)(await cmd.ExecuteScalarAsync())!;
                foreach (var line in lines)
                {
                    // The item's type is copied onto the line (a Returnable line can never be consumed).
                    using var l = new SqlCommand(@"
INSERT INTO dbo.StoreRequestLines (RequestId, ItemId, ItemType, RequestedQty)
SELECT @Request, i.Id, i.ItemType, @Qty FROM dbo.StoreItems i WHERE i.Id = @Item AND i.IsActive = 1;
SELECT @@ROWCOUNT;", conn, tx);
                    l.Parameters.AddWithValue("@Request", id);
                    l.Parameters.AddWithValue("@Item", line.ItemId);
                    l.Parameters.AddWithValue("@Qty", line.Quantity);
                    if ((int)(await l.ExecuteScalarAsync())! != 1)
                        throw new StoreStockException("One of the items is not available for requests any more.");
                }
                tx.Commit();
                return id;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // Approve / partially approve / reject each line (0 = rejected). The request becomes
        // Approved when anything is approved, otherwise Rejected.
        public async Task<StepResult> ReviewAsync(int requestId, IReadOnlyDictionary<int, (decimal Approved, string? Remarks)> lines, string? remarks, int userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var status = lines.Values.Any(v => v.Approved > 0) ? StoreRules.Approved : StoreRules.Rejected;
                if (!await UpdateRequestAsync(conn, tx, requestId, StoreRules.Pending, status,
                        "ReviewedById = @User, ReviewedAt = SYSUTCDATETIME(), ReviewRemarks = @Remarks", userId,
                        cmd => cmd.Parameters.AddWithValue("@Remarks", (object?)StoreRules.Trimmed(remarks) ?? DBNull.Value)))
                {
                    tx.Rollback();
                    return StepResult.Changed;
                }
                foreach (var (lineId, (approved, lineRemarks)) in lines)
                {
                    using var l = new SqlCommand(@"
UPDATE dbo.StoreRequestLines SET ApprovedQty = @Approved, ReviewRemarks = @Remarks WHERE Id = @Line AND RequestId = @Request AND ApprovedQty IS NULL;
SELECT @@ROWCOUNT;", conn, tx);
                    l.Parameters.AddWithValue("@Approved", approved);
                    l.Parameters.AddWithValue("@Remarks", (object?)StoreRules.Trimmed(lineRemarks) ?? DBNull.Value);
                    l.Parameters.AddWithValue("@Line", lineId);
                    l.Parameters.AddWithValue("@Request", requestId);
                    if ((int)(await l.ExecuteScalarAsync())! != 1)
                    {
                        tx.Rollback();
                        return StepResult.Changed;
                    }
                }
                if (await LineCountAsync(conn, tx, requestId, "ApprovedQty IS NULL") > 0)
                    throw new InvalidOperationException("Every line of the request must be reviewed.");
                tx.Commit();
                return StepResult.Done;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // Issue (0 .. approved) for each line, all or nothing: stock leaves Main Office through ISSUE
        // transactions. A line that cannot be covered by the available stock rolls everything back
        // (StoreStockException). Nothing issued at all closes the request (nothing to settle).
        public async Task<StepResult> IssueAsync(int requestId, IReadOnlyDictionary<int, decimal> issued, int userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var request = await ReadRequestHeaderAsync(conn, tx, requestId);
                var lines = await ReadLinesAsync(conn, tx, requestId);
                // Lock order everywhere: items first (by ItemId), then request / line rows.
                await LockItemsAsync(conn, tx, lines.Where(l => issued.TryGetValue(l.Id, out var q) && q > 0).Select(l => l.ItemId));
                if (request == null || !await UpdateRequestAsync(conn, tx, requestId, StoreRules.Approved, StoreRules.Issued,
                        "IssuedById = @User, IssuedAt = SYSUTCDATETIME()", userId))
                {
                    tx.Rollback();
                    return StepResult.Changed;
                }
                foreach (var line in lines.OrderBy(l => l.ItemId))
                {
                    var qty = issued.TryGetValue(line.Id, out var q) ? q : 0;
                    if (qty < 0 || qty > (line.ApprovedQty ?? 0))
                        throw new StoreStockException($"{line.ItemName}: at most {StoreRules.Qty(line.ApprovedQty ?? 0)} can be issued (approved).");
                    if (qty == 0)
                        continue;
                    using (var l = new SqlCommand("UPDATE dbo.StoreRequestLines SET IssuedQty = @Qty WHERE Id = @Line AND RequestId = @Request", conn, tx))
                    {
                        l.Parameters.AddWithValue("@Qty", qty);
                        l.Parameters.AddWithValue("@Line", line.Id);
                        l.Parameters.AddWithValue("@Request", requestId);
                        await l.ExecuteNonQueryAsync();
                    }
                    await MoveStockAsync(conn, tx, new StockMove(line.ItemId, -qty, StoreRules.Issue, AppClock.Today, requestId, line.Id,
                        request.AreaId, request.PolyhouseId, Remarks: $"Issued for {request.RequestNo}"), userId);
                }
                await TryCloseAsync(conn, tx, requestId);
                tx.Commit();
                return StepResult.Done;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // The Area declares what happened to an issued line: consumed / kept / returned (returned
        // goes back to stock only when the Store Keeper confirms it). Once per line.
        public async Task<StepResult> SettleLineAsync(int requestId, int lineId, decimal consumed, decimal kept, decimal returned, string? remarks, int userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var l = new SqlCommand(@"
UPDATE l SET ConsumedQty = @Consumed, KeptQty = @Kept, ReturnDeclaredQty = @Returned, ReturnRemarks = @Remarks,
             SettledById = @User, SettledAt = SYSUTCDATETIME()
FROM dbo.StoreRequestLines l JOIN dbo.StoreRequests r ON r.Id = l.RequestId
WHERE l.Id = @Line AND l.RequestId = @Request AND r.Status = 'Issued' AND l.IssuedQty > 0 AND l.SettledAt IS NULL;
SELECT @@ROWCOUNT;", conn, tx);
                l.Parameters.AddWithValue("@Consumed", consumed);
                l.Parameters.AddWithValue("@Kept", kept);
                l.Parameters.AddWithValue("@Returned", returned);
                l.Parameters.AddWithValue("@Remarks", (object?)StoreRules.Trimmed(remarks) ?? DBNull.Value);
                l.Parameters.AddWithValue("@User", userId);
                l.Parameters.AddWithValue("@Line", lineId);
                l.Parameters.AddWithValue("@Request", requestId);
                if ((int)(await l.ExecuteScalarAsync())! != 1)
                {
                    tx.Rollback();
                    return StepResult.Changed;
                }
                await TouchRequestAsync(conn, tx, requestId, userId);
                await TryCloseAsync(conn, tx, requestId);
                tx.Commit();
                return StepResult.Done;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // The Store Keeper confirms the physical return: only now the returned quantity goes back
        // to Main Office stock (RETURN transaction). Once per line.
        public async Task<StepResult> ConfirmReturnAsync(int requestId, int lineId, string condition, string? remarks, int userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var request = await ReadRequestHeaderAsync(conn, tx, requestId);
                // Lock order everywhere: the item first, then the line.
                using (var item = new SqlCommand("SELECT ItemId FROM dbo.StoreRequestLines WHERE Id = @Line AND RequestId = @Request", conn, tx))
                {
                    item.Parameters.AddWithValue("@Line", lineId);
                    item.Parameters.AddWithValue("@Request", requestId);
                    if (await item.ExecuteScalarAsync() is int lineItem)
                        await LockItemsAsync(conn, tx, new[] { lineItem });
                }
                using var l = new SqlCommand(@"
DECLARE @Out TABLE (ItemId INT, Qty DECIMAL(18,3));
UPDATE l SET ReturnedQty = l.ReturnDeclaredQty, ReturnDeclaredQty = 0, ReturnCondition = @Condition, ConfirmRemarks = @Remarks,
             ReturnConfirmedById = @User, ReturnConfirmedAt = SYSUTCDATETIME()
OUTPUT inserted.ItemId, inserted.ReturnedQty INTO @Out
FROM dbo.StoreRequestLines l JOIN dbo.StoreRequests r ON r.Id = l.RequestId
WHERE l.Id = @Line AND l.RequestId = @Request AND r.Status = 'Issued' AND l.ReturnDeclaredQty > 0 AND l.ReturnConfirmedAt IS NULL;
SELECT ItemId, Qty FROM @Out;", conn, tx);
                l.Parameters.AddWithValue("@Condition", condition);
                l.Parameters.AddWithValue("@Remarks", (object?)StoreRules.Trimmed(remarks) ?? DBNull.Value);
                l.Parameters.AddWithValue("@User", userId);
                l.Parameters.AddWithValue("@Line", lineId);
                l.Parameters.AddWithValue("@Request", requestId);
                int itemId;
                decimal qty;
                using (var r = await l.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return StepResult.Changed;
                    }
                    itemId = r.GetInt32(0);
                    qty = r.GetDecimal(1);
                }
                await MoveStockAsync(conn, tx, new StockMove(itemId, qty, StoreRules.Return, AppClock.Today, requestId, lineId, request!.AreaId, request.PolyhouseId,
                    Remarks: $"Returned from {request.RequestNo} ({condition})" + (string.IsNullOrWhiteSpace(remarks) ? "" : $" -- {remarks.Trim()}")), userId);
                await TouchRequestAsync(conn, tx, requestId, userId);
                await TryCloseAsync(conn, tx, requestId);
                tx.Commit();
                return StepResult.Done;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        public async Task<StepResult> CancelAsync(int requestId, string reason, int userId, bool isManager)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
UPDATE dbo.StoreRequests SET Status = 'Cancelled', CancelledById = @User, CancelledAt = SYSUTCDATETIME(), CancelReason = @Reason,
       UpdatedById = @User, UpdatedAt = SYSUTCDATETIME()
WHERE Id = @Id AND Status IN ('Pending', 'Approved') AND (RequestedById = @User OR @Manager = 1)", conn);
            cmd.Parameters.AddWithValue("@Id", requestId);
            cmd.Parameters.AddWithValue("@User", userId);
            cmd.Parameters.AddWithValue("@Reason", reason.Trim());
            cmd.Parameters.AddWithValue("@Manager", isManager);
            return await cmd.ExecuteNonQueryAsync() == 1 ? StepResult.Done : StepResult.Changed;
        }

        private static async Task<bool> UpdateRequestAsync(SqlConnection conn, SqlTransaction tx, int id, string from, string to, string set, int userId, Action<SqlCommand>? add = null)
        {
            using var cmd = new SqlCommand($@"
UPDATE dbo.StoreRequests SET Status = @To, {set}, UpdatedById = @User, UpdatedAt = SYSUTCDATETIME()
WHERE Id = @Id AND Status = @From;
SELECT @@ROWCOUNT;", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@From", from);
            cmd.Parameters.AddWithValue("@To", to);
            cmd.Parameters.AddWithValue("@User", userId);
            add?.Invoke(cmd);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        private static async Task TouchRequestAsync(SqlConnection conn, SqlTransaction tx, int id, int userId)
        {
            using var cmd = new SqlCommand("UPDATE dbo.StoreRequests SET UpdatedById = @User, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@User", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Closed once every issued line is settled and no return waits for confirmation.
        private static async Task TryCloseAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            using var cmd = new SqlCommand(@"
UPDATE r SET Status = 'Closed', ClosedAt = SYSUTCDATETIME()
FROM dbo.StoreRequests r
WHERE r.Id = @Id AND r.Status = 'Issued'
  AND NOT EXISTS (SELECT 1 FROM dbo.StoreRequestLines l WHERE l.RequestId = r.Id AND l.IssuedQty > 0 AND (l.SettledAt IS NULL OR l.ReturnDeclaredQty > 0))", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<int> LineCountAsync(SqlConnection conn, SqlTransaction tx, int requestId, string condition)
        {
            using var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.StoreRequestLines WHERE RequestId = @Id AND {condition}", conn, tx);
            cmd.Parameters.AddWithValue("@Id", requestId);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        // ---- reading requests --------------------------------------------------------------

        private const string RequestSelect = @"
SELECT r.Id, r.RequestNo, r.AreaId, a.Name, r.PolyhouseId, p.Name, r.Purpose, r.Status, r.RequestedById, ru.Name, r.RequestedAt,
       rv.Name, r.ReviewedAt, r.ReviewRemarks, iu.Name, r.IssuedAt, r.ClosedAt, cu.Name, r.CancelledAt, r.CancelReason,
       (SELECT COUNT(*) FROM dbo.StoreRequestLines l WHERE l.RequestId = r.Id),
       (SELECT COUNT(*) FROM dbo.StoreRequestLines l WHERE l.RequestId = r.Id AND l.ReturnDeclaredQty > 0),
       (SELECT COUNT(*) FROM dbo.StoreRequestLines l WHERE l.RequestId = r.Id AND l.IssuedQty > 0 AND l.SettledAt IS NULL)
FROM dbo.StoreRequests r
JOIN dbo.Area a ON a.Id = r.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = r.PolyhouseId
LEFT JOIN dbo.IMSUsers ru ON ru.Id = r.RequestedById
LEFT JOIN dbo.IMSUsers rv ON rv.Id = r.ReviewedById
LEFT JOIN dbo.IMSUsers iu ON iu.Id = r.IssuedById
LEFT JOIN dbo.IMSUsers cu ON cu.Id = r.CancelledById";

        public async Task<StoreRequest?> GetRequestAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var request = await ReadRequestHeaderAsync(conn, null, id);
            if (request != null)
                request.Lines = await ReadLinesAsync(conn, null, id);
            return request;
        }

        // areaScope null = every Area. Newest first.
        public async Task<List<StoreRequest>> SearchRequestsAsync(IReadOnlyCollection<int>? areaScope, string? status = null, int? areaId = null, int? requestedById = null,
            bool pendingReturnsOnly = false)
        {
            var list = new List<StoreRequest>();
            if (areaScope != null && areaScope.Count == 0)
                return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var sb = new StringBuilder(" WHERE 1 = 1");
            if (!string.IsNullOrWhiteSpace(status)) { sb.Append(" AND r.Status = @Status"); cmd.Parameters.AddWithValue("@Status", status); }
            if (areaId.HasValue) { sb.Append(" AND r.AreaId = @Area"); cmd.Parameters.AddWithValue("@Area", areaId.Value); }
            if (requestedById.HasValue) { sb.Append(" AND r.RequestedById = @By"); cmd.Parameters.AddWithValue("@By", requestedById.Value); }
            if (pendingReturnsOnly) sb.Append(" AND EXISTS (SELECT 1 FROM dbo.StoreRequestLines l WHERE l.RequestId = r.Id AND l.ReturnDeclaredQty > 0)");
            if (areaScope != null)
                sb.Append(" AND r.AreaId IN (").Append(string.Join(", ", areaScope.Select((id, i) => { cmd.Parameters.AddWithValue("@S" + i, id); return "@S" + i; }))).Append(')');
            cmd.CommandText = RequestSelect + sb + " ORDER BY r.RequestedAt DESC, r.Id DESC";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapRequest(r));
            return list;
        }

        private static async Task<StoreRequest?> ReadRequestHeaderAsync(SqlConnection conn, SqlTransaction? tx, int id)
        {
            using var cmd = new SqlCommand(RequestSelect + " WHERE r.Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapRequest(r) : null;
        }

        private static async Task<List<StoreRequestLine>> ReadLinesAsync(SqlConnection conn, SqlTransaction? tx, int requestId)
        {
            var list = new List<StoreRequestLine>();
            using var cmd = new SqlCommand(@"
SELECT l.Id, l.RequestId, l.ItemId, i.ItemCode, i.Name, LTRIM(RTRIM(u.UnitName)), l.ItemType, i.AvailableQuantity, l.RequestedQty, l.ApprovedQty, l.ReviewRemarks,
       l.IssuedQty, l.ConsumedQty, l.KeptQty, l.ReturnDeclaredQty, l.ReturnedQty, l.ReturnCondition, l.ReturnRemarks, su.Name, l.SettledAt,
       cu.Name, l.ReturnConfirmedAt, l.ConfirmRemarks
FROM dbo.StoreRequestLines l
JOIN dbo.StoreItems i ON i.Id = l.ItemId
JOIN dbo.UnitMaster u ON u.UnitId = i.UnitId
LEFT JOIN dbo.IMSUsers su ON su.Id = l.SettledById
LEFT JOIN dbo.IMSUsers cu ON cu.Id = l.ReturnConfirmedById
WHERE l.RequestId = @Id ORDER BY l.Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", requestId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new StoreRequestLine
                {
                    Id = r.GetInt32(0), RequestId = r.GetInt32(1), ItemId = r.GetInt32(2), ItemCode = r.GetString(3), ItemName = r.GetString(4),
                    UnitName = S(r, 5), ItemType = r.GetString(6), ItemAvailable = r.GetDecimal(7), RequestedQty = r.GetDecimal(8),
                    ApprovedQty = r.IsDBNull(9) ? null : r.GetDecimal(9), ReviewRemarks = S(r, 10), IssuedQty = r.GetDecimal(11),
                    ConsumedQty = r.GetDecimal(12), KeptQty = r.GetDecimal(13), ReturnDeclaredQty = r.GetDecimal(14), ReturnedQty = r.GetDecimal(15),
                    ReturnCondition = S(r, 16), ReturnRemarks = S(r, 17), SettledByName = S(r, 18), SettledAt = Utc(r, 19),
                    ReturnConfirmedByName = S(r, 20), ReturnConfirmedAt = Utc(r, 21), ConfirmRemarks = S(r, 22),
                });
            return list;
        }

        // ---- ledger and holdings (reports) -----------------------------------------------------

        // areaScope null = everything. Otherwise only movements to / from those Areas (Main Office
        // purchases and adjustments have no Area and are shown only to the Store Keeper).
        public async Task<List<StoreTransaction>> SearchTransactionsAsync(StoreTransactionFilter f, IReadOnlyCollection<int>? areaScope, int? id = null)
        {
            var list = new List<StoreTransaction>();
            if (areaScope != null && areaScope.Count == 0)
                return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var sb = new StringBuilder(" WHERE 1 = 1");
            void Eq(string column, string name, object? value)
            {
                if (value == null) return;
                sb.Append($" AND {column} = {name}");
                cmd.Parameters.AddWithValue(name, value);
            }
            Eq("t.Id", "@Id", id);
            Eq("t.TransactionType", "@Type", string.IsNullOrWhiteSpace(f.TransactionType) ? null : f.TransactionType);
            Eq("t.ItemId", "@Item", f.ItemId);
            Eq("t.AreaId", "@Area", f.AreaId);
            Eq("t.PolyhouseId", "@Polyhouse", f.PolyhouseId);
            Eq("t.RequestId", "@Request", f.RequestId);
            if (f.FromDate.HasValue) { sb.Append(" AND t.TransactionDate >= @From"); cmd.Parameters.AddWithValue("@From", f.FromDate.Value.Date); }
            if (f.ToDate.HasValue) { sb.Append(" AND t.TransactionDate <= @To"); cmd.Parameters.AddWithValue("@To", f.ToDate.Value.Date); }
            if (areaScope != null)
                sb.Append(" AND t.AreaId IN (").Append(string.Join(", ", areaScope.Select((a, i) => { cmd.Parameters.AddWithValue("@S" + i, a); return "@S" + i; }))).Append(')');
            cmd.CommandText = @"
SELECT t.Id, t.ItemId, i.ItemCode, i.Name, LTRIM(RTRIM(u.UnitName)), t.TransactionType, t.Quantity, t.BalanceBefore, t.BalanceAfter, t.TransactionDate,
       t.RequestId, r.RequestNo, t.AreaId, a.Name, t.PolyhouseId, p.Name, v.Name, t.Reference, t.UnitCost, t.Remarks, cu.Name, t.CreatedAt
FROM dbo.StoreTransactions t
JOIN dbo.StoreItems i ON i.Id = t.ItemId
JOIN dbo.UnitMaster u ON u.UnitId = i.UnitId
LEFT JOIN dbo.StoreRequests r ON r.Id = t.RequestId
LEFT JOIN dbo.Area a ON a.Id = t.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
LEFT JOIN dbo.Vendors v ON v.Id = t.VendorId
LEFT JOIN dbo.IMSUsers cu ON cu.Id = t.CreatedById" + sb + " ORDER BY t.Id DESC";
            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                list.Add(new StoreTransaction
                {
                    Id = rd.GetInt32(0), ItemId = rd.GetInt32(1), ItemCode = rd.GetString(2), ItemName = rd.GetString(3), UnitName = S(rd, 4),
                    TransactionType = rd.GetString(5), Quantity = rd.GetDecimal(6), BalanceBefore = rd.GetDecimal(7), BalanceAfter = rd.GetDecimal(8),
                    TransactionDate = rd.GetDateTime(9), RequestId = I(rd, 10), RequestNo = S(rd, 11), AreaId = I(rd, 12), AreaName = S(rd, 13),
                    PolyhouseId = I(rd, 14), PolyhouseName = S(rd, 15), VendorName = S(rd, 16), Reference = S(rd, 17),
                    UnitCost = rd.IsDBNull(18) ? null : rd.GetDecimal(18), Remarks = S(rd, 19), CreatedByName = S(rd, 20), CreatedAt = Utc(rd, 21)!.Value,
                });
            return list;
        }

        // Where items are: per item and Area / Polyhouse -- out on issue, pending return, kept,
        // permanently transferred, consumed. areaScope null = every Area.
        public async Task<List<StoreHolding>> GetHoldingsAsync(IReadOnlyCollection<int>? areaScope, int? areaId = null, int? polyhouseId = null, int? itemId = null)
        {
            var list = new List<StoreHolding>();
            if (areaScope != null && areaScope.Count == 0)
                return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var where = new StringBuilder(" WHERE 1 = 1");
            if (areaId.HasValue) { where.Append(" AND h.AreaId = @Area"); cmd.Parameters.AddWithValue("@Area", areaId.Value); }
            if (polyhouseId.HasValue) { where.Append(" AND h.PolyhouseId = @Polyhouse"); cmd.Parameters.AddWithValue("@Polyhouse", polyhouseId.Value); }
            if (itemId.HasValue) { where.Append(" AND h.ItemId = @Item"); cmd.Parameters.AddWithValue("@Item", itemId.Value); }
            if (areaScope != null)
                where.Append(" AND h.AreaId IN (").Append(string.Join(", ", areaScope.Select((a, i) => { cmd.Parameters.AddWithValue("@S" + i, a); return "@S" + i; }))).Append(')');
            cmd.CommandText = @"
WITH h AS (
    SELECT l.ItemId, r.AreaId, r.PolyhouseId,
           CASE WHEN l.SettledAt IS NULL THEN l.IssuedQty ELSE 0 END AS OutOnIssue,
           l.ReturnDeclaredQty AS PendingReturn, l.KeptQty AS Kept, CAST(0 AS DECIMAL(18,3)) AS Transferred, l.ConsumedQty AS Consumed
    FROM dbo.StoreRequestLines l JOIN dbo.StoreRequests r ON r.Id = l.RequestId
    WHERE l.IssuedQty > 0
    UNION ALL
    SELECT t.ItemId, t.AreaId, t.PolyhouseId, 0, 0, 0, -t.Quantity, 0
    FROM dbo.StoreTransactions t WHERE t.TransactionType = 'PERMANENT_TRANSFER'
)
SELECT h.ItemId, i.ItemCode, i.Name, LTRIM(RTRIM(u.UnitName)), i.ItemType, h.AreaId, a.Name, h.PolyhouseId, p.Name,
       SUM(h.OutOnIssue), SUM(h.PendingReturn), SUM(h.Kept), SUM(h.Transferred), SUM(h.Consumed)
FROM h
JOIN dbo.StoreItems i ON i.Id = h.ItemId
JOIN dbo.UnitMaster u ON u.UnitId = i.UnitId
JOIN dbo.Area a ON a.Id = h.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = h.PolyhouseId" + where + @"
GROUP BY h.ItemId, i.ItemCode, i.Name, u.UnitName, i.ItemType, h.AreaId, a.Name, h.PolyhouseId, p.Name
ORDER BY a.Name, p.Name, i.Name";
            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                list.Add(new StoreHolding
                {
                    ItemId = rd.GetInt32(0), ItemCode = rd.GetString(1), ItemName = rd.GetString(2), UnitName = S(rd, 3), ItemType = rd.GetString(4),
                    AreaId = rd.GetInt32(5), AreaName = S(rd, 6), PolyhouseId = I(rd, 7), PolyhouseName = S(rd, 8),
                    OutOnIssue = rd.GetDecimal(9), PendingReturn = rd.GetDecimal(10), Kept = rd.GetDecimal(11), Transferred = rd.GetDecimal(12), Consumed = rd.GetDecimal(13),
                });
            return list;
        }

        // Per item: ledger sum (must equal AvailableQuantity -- the database enforces it).
        public async Task<Dictionary<int, decimal>> GetLedgerSumsAsync()
        {
            var map = new Dictionary<int, decimal>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT ItemId, SUM(Quantity) FROM dbo.StoreTransactions GROUP BY ItemId", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                map[r.GetInt32(0)] = r.GetDecimal(1);
            return map;
        }

        private static DateTime? Utc(SqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
        private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        private static int? I(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

        private static StoreItem MapItem(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), ItemCode = r.GetString(1), Name = r.GetString(2), CategoryId = r.GetInt32(3), CategoryName = r.GetString(4),
            Description = S(r, 5), UnitId = r.GetInt32(6), UnitName = S(r, 7), ItemType = r.GetString(8), MinimumStock = r.GetDecimal(9),
            AvailableQuantity = r.GetDecimal(10), Location = S(r, 11), IsActive = r.GetBoolean(12), Remarks = S(r, 13),
            CreatedAt = Utc(r, 14)!.Value, UpdatedAt = Utc(r, 15), UpdatedByName = S(r, 16), HasMovements = r.GetInt32(17) == 1,
        };

        private static StoreRequest MapRequest(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), RequestNo = r.GetString(1), AreaId = r.GetInt32(2), AreaName = S(r, 3), PolyhouseId = I(r, 4), PolyhouseName = S(r, 5),
            Purpose = r.GetString(6), Status = r.GetString(7), RequestedById = r.GetInt32(8), RequestedByName = S(r, 9), RequestedAt = Utc(r, 10)!.Value,
            ReviewedByName = S(r, 11), ReviewedAt = Utc(r, 12), ReviewRemarks = S(r, 13), IssuedByName = S(r, 14), IssuedAt = Utc(r, 15),
            ClosedAt = Utc(r, 16), CancelledByName = S(r, 17), CancelledAt = Utc(r, 18), CancelReason = S(r, 19),
            LineCount = r.GetInt32(20), PendingReturns = r.GetInt32(21), UnsettledLines = r.GetInt32(22),
        };
    }
}
