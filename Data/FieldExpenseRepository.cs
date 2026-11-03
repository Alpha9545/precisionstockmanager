using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using static PlantStockManager.Data.FieldSql;

namespace PlantStockManager.Data
{
    // Field Work Phase 4 (2026-11-03): expenses (dbo.FieldExpenses + dbo.FieldExpenseEvents, bill photos in dbo.FieldPhotos).
    // Every write runs in ONE SqlTransaction; a status change locks the expense row (UPDLOCK, HOLDLOCK) and the service's
    // decision is applied to the LOCKED status. TR_FieldExpenses_Guard re-checks photo ownership, immutability and the
    // one-way status change. Parameterized SQL only.
    public class FieldExpenseRepository
    {
        public const string PhotoPurpose = "ExpenseBill";

        private readonly DatabaseHelper _db;

        public FieldExpenseRepository(DatabaseHelper db) => _db = db;

        public Task<bool> SchemaReadyAsync() => FieldExpenseSchema.ReadyAsync(_db);

        private const string SelectExpense = @"
SELECT e.Id, e.UserId, " + "ISNULL(NULLIF(LTRIM(RTRIM(u.Name)), N''), u.Username)" + @" AS EmployeeName, e.ExpenseDate, e.Category, e.Amount, e.Note,
       e.BillPhotoId, e.Status, e.CancelReason, e.CancelledAtUtc, e.DecidedByUserId,
       " + "ISNULL(NULLIF(LTRIM(RTRIM(d.Name)), N''), d.Username)" + @" AS DecidedByName, e.DecidedAtUtc, e.DecisionComment, e.CreatedAtUtc
FROM dbo.FieldExpenses e
JOIN dbo.IMSUsers u ON u.Id = e.UserId
LEFT JOIN dbo.IMSUsers d ON d.Id = e.DecidedByUserId";

        private static FieldExpense Read(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            UserId = r.GetInt32(1),
            EmployeeName = r.GetString(2),
            ExpenseDate = r.GetDateTime(3),
            Category = r.GetString(4),
            Amount = r.GetDecimal(5),
            Note = Str(r, 6),
            BillPhotoId = r.GetInt32(7),
            Status = r.GetString(8),
            CancelReason = Str(r, 9),
            CancelledAtUtc = Dt(r, 10),
            DecidedByUserId = IntOrNull(r, 11),
            DecidedByName = Str(r, 12),
            DecidedAtUtc = Dt(r, 13),
            DecisionComment = Str(r, 14),
            CreatedAtUtc = r.GetDateTime(15),
        };

        // ---- reading --------------------------------------------------------------------------------------------------

        public async Task<FieldExpense?> GetAsync(int id)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(SelectExpense + " WHERE e.Id = @Id;", conn);
            cmd.Parameters.Add(Int("@Id", id));
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Read(r) : null;
        }

        // The stored file of the expense's bill photo (only that photo -- the photo id comes from the expense row).
        public async Task<string?> GetBillFileAsync(int expenseId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT p.StoredFileName FROM dbo.FieldExpenses e JOIN dbo.FieldPhotos p ON p.Id = e.BillPhotoId
WHERE e.Id = @Id AND p.Purpose = 'ExpenseBill' AND p.OwnerUserId = e.UserId;", conn);
            cmd.Parameters.Add(Int("@Id", expenseId));
            return await cmd.ExecuteScalarAsync() as string;
        }

        public async Task<List<FieldExpenseEvent>> ListEventsAsync(int expenseId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT ev.Id, ev.Action, ev.FromStatus, ev.ToStatus, ev.ByUserId, " + NameOf("u") + @", ev.AtUtc, ev.Comment
FROM dbo.FieldExpenseEvents ev JOIN dbo.IMSUsers u ON u.Id = ev.ByUserId
WHERE ev.ExpenseId = @Id ORDER BY ev.Id;", conn);
            cmd.Parameters.Add(Int("@Id", expenseId));
            var list = new List<FieldExpenseEvent>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldExpenseEvent
                {
                    Id = r.GetInt32(0), Action = r.GetString(1), FromStatus = Str(r, 2), ToStatus = r.GetString(3), ByUserId = r.GetInt32(4),
                    ByName = r.GetString(5), AtUtc = r.GetDateTime(6), Comment = Str(r, 7),
                });
            return list;
        }

        private static void AddFilter(SqlCommand cmd, DateTime from, DateTime to, int? userId, string? category, string? status)
        {
            cmd.Parameters.Add(Date("@From", from));
            cmd.Parameters.Add(Date("@To", to));
            cmd.Parameters.Add(Int("@U", userId));
            cmd.Parameters.Add(Text("@Cat", category, 10));
            cmd.Parameters.Add(Text("@St", status, 10));
        }

        private const string FilterWhere = @"
WHERE e.ExpenseDate BETWEEN @From AND @To AND (@U IS NULL OR e.UserId = @U) AND (@Cat IS NULL OR e.Category = @Cat)";

        // Newest first. userId = null: every employee (managers only -- the service decides).
        public async Task<List<FieldExpense>> ListAsync(DateTime from, DateTime to, int? userId, string? category, string? status, int maxRows)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(SelectExpense.Replace("SELECT e.Id", "SELECT TOP (@Max) e.Id") + FilterWhere
                + " AND (@St IS NULL OR e.Status = @St) ORDER BY e.ExpenseDate DESC, e.Id DESC;", conn);
            AddFilter(cmd, from, to, userId, category, status);
            cmd.Parameters.Add(Int("@Max", maxRows));
            var list = new List<FieldExpense>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(Read(r));
            return list;
        }

        // Per employee over the date / employee / category filter: approved and pending (Submitted) separately. The status
        // filter is deliberately not applied -- the totals ARE split by status.
        public async Task<List<FieldExpenseTotal>> TotalsAsync(DateTime from, DateTime to, int? userId, string? category)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT e.UserId, " + NameOf("u") + @" AS EmployeeName,
       SUM(CASE WHEN e.Status = 'Approved' THEN e.Amount ELSE 0 END), SUM(CASE WHEN e.Status = 'Approved' THEN 1 ELSE 0 END),
       SUM(CASE WHEN e.Status = 'Submitted' THEN e.Amount ELSE 0 END), SUM(CASE WHEN e.Status = 'Submitted' THEN 1 ELSE 0 END)
FROM dbo.FieldExpenses e JOIN dbo.IMSUsers u ON u.Id = e.UserId" + FilterWhere + @" AND e.Status IN ('Approved', 'Submitted')
GROUP BY e.UserId, u.Name, u.Username
ORDER BY EmployeeName;", conn);
            AddFilter(cmd, from, to, userId, category, null);
            var list = new List<FieldExpenseTotal>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldExpenseTotal(r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.GetInt32(3), r.GetDecimal(4), r.GetInt32(5)));
            return list;
        }

        // The employee filter on the manager list: active holders of FieldWork.Day, plus anyone who has an expense.
        public async Task<List<FieldEmployeeOption>> ListExpenseEmployeesAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT u.Id, " + NameOf("u") + @" AS Name
FROM dbo.IMSUsers u
WHERE (u.IsActive = 1 AND EXISTS (SELECT 1 FROM dbo.UserRoles ur
                                  JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                                  JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                  WHERE ur.UserId = u.Id AND p.Code = N'FieldWork.Day'))
   OR EXISTS (SELECT 1 FROM dbo.FieldExpenses e WHERE e.UserId = u.Id)
ORDER BY Name;", conn);
            var list = new List<FieldEmployeeOption>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldEmployeeOption(r.GetInt32(0), r.GetString(1)));
            return list;
        }

        public async Task<(int Id, int UserId)?> FindByTokenAsync(Guid token)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, UserId FROM dbo.FieldExpenses WHERE SubmissionToken = @T;", conn);
            cmd.Parameters.Add(new SqlParameter("@T", SqlDbType.UniqueIdentifier) { Value = token });
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? (r.GetInt32(0), r.GetInt32(1)) : null;
        }

        // ---- writes ---------------------------------------------------------------------------------------------------

        public sealed record PhotoInput(string StoredFileName, int SizeBytes);

        public sealed record CreateCommand(int UserId, DateTime ExpenseDate, string Category, decimal Amount, string? Note, PhotoInput Photo,
            int ReminderVersion, Guid Token);

        public sealed record CreateResult(int ExpenseId, bool Replay);

        // The bill photo row, the expense and its 'Submitted' event -- all or nothing. A repeated submit of the same form
        // (same token, also concurrently: the token lookup takes a key-range lock) returns the expense it already saved.
        public async Task<CreateResult> CreateAsync(CreateCommand c)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                using (var replay = new SqlCommand("SELECT Id, UserId FROM dbo.FieldExpenses WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T;", conn, tx))
                {
                    replay.Parameters.Add(new SqlParameter("@T", SqlDbType.UniqueIdentifier) { Value = c.Token });
                    using var r = await replay.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        var (id, owner) = (r.GetInt32(0), r.GetInt32(1));
                        await r.CloseAsync();
                        if (owner != c.UserId)
                            throw new InvalidOperationException("This form was already used. Open Add Expense again.");
                        await tx.CommitAsync();
                        return new CreateResult(id, true);
                    }
                }

                var photoId = await InsertPhotoAsync(conn, tx, c.UserId, PhotoPurpose, c.Photo.StoredFileName, c.Photo.SizeBytes, null, null, null, c.ReminderVersion);
                int expenseId;
                using (var cmd = new SqlCommand(@"
DECLARE @Out TABLE (Id INT);
INSERT INTO dbo.FieldExpenses (UserId, ExpenseDate, Category, Amount, Note, BillPhotoId, SubmissionToken)
OUTPUT inserted.Id INTO @Out
VALUES (@U, @D, @Cat, @Amt, @Note, @P, @T);
SELECT Id FROM @Out;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@U", c.UserId));
                    cmd.Parameters.Add(Date("@D", c.ExpenseDate));
                    cmd.Parameters.Add(Text("@Cat", c.Category, 10));
                    cmd.Parameters.Add(Decimal("@Amt", c.Amount, 10, 2));
                    cmd.Parameters.Add(NText("@Note", c.Note, 200));
                    cmd.Parameters.Add(Int("@P", photoId));
                    cmd.Parameters.Add(new SqlParameter("@T", SqlDbType.UniqueIdentifier) { Value = c.Token });
                    expenseId = (int)(await cmd.ExecuteScalarAsync())!;
                }
                await InsertEventAsync(conn, tx, expenseId, "Submitted", null, "Submitted", c.UserId, null);
                await tx.CommitAsync();
                return new CreateResult(expenseId, false);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                // a concurrent submit of the same form won the race: that expense is the answer
                if (await FindByTokenAsync(c.Token) is { } earlier && earlier.UserId == c.UserId)
                    return new CreateResult(earlier.Id, true);
                throw new InvalidOperationException("This expense was already saved (the form was submitted twice). Open My Expenses.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        private static async Task InsertEventAsync(SqlConnection conn, SqlTransaction tx, int expenseId, string action, string? from, string to, int byUserId,
            string? comment)
        {
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.FieldExpenseEvents (ExpenseId, Action, FromStatus, ToStatus, ByUserId, Comment) VALUES (@E, @A, @F, @To, @By, @C);", conn, tx);
            cmd.Parameters.Add(Int("@E", expenseId));
            cmd.Parameters.Add(Text("@A", action, 10));
            cmd.Parameters.Add(Text("@F", from, 10));
            cmd.Parameters.Add(Text("@To", to, 10));
            cmd.Parameters.Add(Int("@By", byUserId));
            cmd.Parameters.Add(NText("@C", comment, 300));
            await cmd.ExecuteNonQueryAsync();
        }

        // Locks the expense and passes its CURRENT status and owner to check (null = go ahead, otherwise the refusal).
        private static async Task<(string Status, int OwnerId)> LockAsync(SqlConnection conn, SqlTransaction tx, int expenseId)
        {
            using var cmd = new SqlCommand("SELECT Status, UserId FROM dbo.FieldExpenses WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", conn, tx);
            cmd.Parameters.Add(Int("@Id", expenseId));
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("Expense not found.");
            return (r.GetString(0), r.GetInt32(1));
        }

        public async Task CancelAsync(int expenseId, int userId, string reason, Func<string, int, string?> check)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var (status, owner) = await LockAsync(conn, tx, expenseId);
                if (check(status, owner) is { } refusal)
                    throw new InvalidOperationException(refusal);
                using (var cmd = new SqlCommand(@"
UPDATE dbo.FieldExpenses SET Status = 'Cancelled', CancelReason = @R, CancelledAtUtc = SYSUTCDATETIME()
WHERE Id = @Id AND Status = 'Submitted';", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", expenseId));
                    cmd.Parameters.Add(NText("@R", reason, 200));
                    if (await cmd.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("The expense changed meanwhile. Open it again.");
                }
                await InsertEventAsync(conn, tx, expenseId, "Cancelled", status, "Cancelled", userId, reason);
                await tx.CommitAsync();
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        public async Task DecideAsync(int expenseId, int deciderId, bool approve, string? comment, Func<string, int, string?> check)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var (status, owner) = await LockAsync(conn, tx, expenseId);
                if (check(status, owner) is { } refusal)
                    throw new InvalidOperationException(refusal);
                var to = approve ? "Approved" : "Rejected";
                using (var cmd = new SqlCommand(@"
UPDATE dbo.FieldExpenses SET Status = @To, DecidedByUserId = @By, DecidedAtUtc = SYSUTCDATETIME(), DecisionComment = @C
WHERE Id = @Id AND Status = 'Submitted';", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", expenseId));
                    cmd.Parameters.Add(Text("@To", to, 10));
                    cmd.Parameters.Add(Int("@By", deciderId));
                    cmd.Parameters.Add(NText("@C", comment, 300));
                    if (await cmd.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("The expense changed meanwhile. Open it again.");
                }
                await InsertEventAsync(conn, tx, expenseId, to, status, to, deciderId, comment);
                await tx.CommitAsync();
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }
    }
}
