using Microsoft.Data.SqlClient;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    public sealed class FertilizerTransactionRow
    {
        public int UsageId { get; set; }
        public DateTime IssueDate { get; set; }
        public int StockId { get; set; }
        public int FertilizerId { get; set; }
        public string FertilizerName { get; set; } = "";
        public string? FertilizerType { get; set; }
        public string? BatchNumber { get; set; }
        public decimal Quantity { get; set; }
        public string UnitName { get; set; } = "";
        public int? SourceId { get; set; }
        public string SourceName { get; set; } = "";
        public int? ReceivedById { get; set; }
        public string? ReceivedByUserName { get; set; }
        public bool? ReceivedByActive { get; set; }
        public string? ReceivedByText { get; set; }
        public int? EnteredById { get; set; }
        public string? EnteredByName { get; set; }
        public bool? EnteredByActive { get; set; }
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; }

        public string TransactionType => FertilizerIssueRules.TransactionType;
        public string ReceiverLabel => FertilizerIssueRules.ReceiverLabel(ReceivedByUserName, ReceivedByActive, ReceivedByText);
        // true when the receiver is only the older typed name (no user was selected)
        public bool ReceiverIsTypedName => string.IsNullOrWhiteSpace(ReceivedByUserName) && !string.IsNullOrWhiteSpace(ReceivedByText);
        public string EnteredByLabel => FertilizerIssueRules.EnteredByLabel(EnteredByName, EnteredByActive);
    }

    public sealed record FertilizerFilterOption(string Value, string Label);

    public sealed class FertilizerTransactionOptions
    {
        public List<FertilizerFilterOption> Fertilizers { get; } = new();
        public List<FertilizerFilterOption> Sources { get; } = new();
        public List<FertilizerFilterOption> Receivers { get; } = new();
        public List<FertilizerFilterOption> EnteredBy { get; } = new();
    }

    public sealed record FertilizerIssueResult(bool Success, string? Message, bool Duplicate = false, int UsageId = 0);

    // Fertilizer issues (Correction #8): the history search, its dropdown options, and the issue itself.
    //
    // There is NO Area column on fertilizer stock or issues -- fertilizer is one shared store -- so the history is not Area
    // restricted; access is the page permission (Fertilizer.View / Fertilizer.Enter, FeatureAuthorizationConventions), which is
    // unchanged. The filters below only ever narrow what that permission already allows.
    public class FertilizerTransactionRepository
    {
        private readonly DatabaseHelper _db;
        public FertilizerTransactionRepository(DatabaseHelper db) => _db = db;

        private const string BaseSelect = @"
SELECT fu.UsageId, fu.IssueDate, fu.UsedQuantity, fu.ReceivedBy, fu.ReceivedById, ru.Name, ru.IsActive,
       fu.EnteredById, eu.Name, eu.IsActive, fu.Remarks, fu.CreatedAt,
       fs.StockId, fs.FertilizerId, fm.FertilizerName, ft.TypeName, fs.BatchNumber, um.UnitName, fs.SourceId, src.SourceName
FROM dbo.FertilizerUsage fu
INNER JOIN dbo.FertilizerStock fs ON fs.StockId = fu.StockId
INNER JOIN dbo.FertilizerMaster fm ON fm.FertilizerId = fs.FertilizerId
LEFT JOIN dbo.FertilizerType ft ON ft.FertilizerTypeId = fm.FertilizerTypeId
INNER JOIN dbo.UnitMaster um ON um.UnitId = fs.UnitId
LEFT JOIN dbo.FertilizerSource src ON src.SourceId = fs.SourceId
LEFT JOIN dbo.IMSUsers ru ON ru.Id = fu.ReceivedById
LEFT JOIN dbo.IMSUsers eu ON eu.Id = fu.EnteredById";

        // All filters are optional, combine with AND and are parameters (never concatenated); the database evaluates them.
        public async Task<List<FertilizerTransactionRow>> SearchAsync(FertilizerTransactionFilter filter)
        {
            var list = new List<FertilizerTransactionRow>();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + @"
WHERE (@From IS NULL OR fu.IssueDate >= @From)
  AND (@To IS NULL OR fu.IssueDate <= @To)
  AND (@FertilizerId IS NULL OR fs.FertilizerId = @FertilizerId)
  AND (@SourceId IS NULL OR fs.SourceId = @SourceId)
  AND (@RecvMode IS NULL
       OR (@RecvMode = N'none' AND fu.ReceivedById IS NULL AND LTRIM(RTRIM(ISNULL(fu.ReceivedBy, N''))) = N'')
       OR (@RecvMode = N'user' AND fu.ReceivedById = @RecvUserId)
       OR (@RecvMode = N'text' AND fu.ReceivedById IS NULL AND LTRIM(RTRIM(fu.ReceivedBy)) = @RecvText))
  AND (@EnteredMode IS NULL
       OR (@EnteredMode = N'none' AND fu.EnteredById IS NULL)
       OR (@EnteredMode = N'user' AND fu.EnteredById = @EnteredUserId))
ORDER BY fu.IssueDate DESC, fu.UsageId DESC
OPTION (RECOMPILE)", conn);
            cmd.Parameters.AddWithValue("@From", (object?)filter.From?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)filter.To?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FertilizerId", (object?)filter.FertilizerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceId", (object?)filter.SourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RecvMode", filter.ReceiverKind switch
            {
                FertilizerTransactionFilter.ReceiverMode.NotRecorded => "none",
                FertilizerTransactionFilter.ReceiverMode.User => "user",
                FertilizerTransactionFilter.ReceiverMode.Text => "text",
                _ => DBNull.Value
            });
            cmd.Parameters.AddWithValue("@RecvUserId", filter.ReceiverUserId);
            cmd.Parameters.Add("@RecvText", System.Data.SqlDbType.NVarChar, 300).Value = (object?)filter.ReceiverText ?? DBNull.Value;
            cmd.Parameters.AddWithValue("@EnteredMode", filter.EnteredByNotRecorded ? "none" : filter.EnteredByUserId.HasValue ? "user" : DBNull.Value);
            cmd.Parameters.AddWithValue("@EnteredUserId", (object?)filter.EnteredByUserId ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new FertilizerTransactionRow
                {
                    UsageId = r.GetInt32(0),
                    IssueDate = r.GetDateTime(1),
                    Quantity = r.GetDecimal(2),
                    ReceivedByText = r.IsDBNull(3) ? null : r.GetString(3),
                    ReceivedById = r.IsDBNull(4) ? null : r.GetInt32(4),
                    ReceivedByUserName = r.IsDBNull(5) ? null : r.GetString(5),
                    ReceivedByActive = r.IsDBNull(6) ? null : r.GetBoolean(6),
                    EnteredById = r.IsDBNull(7) ? null : r.GetInt32(7),
                    EnteredByName = r.IsDBNull(8) ? null : r.GetString(8),
                    EnteredByActive = r.IsDBNull(9) ? null : r.GetBoolean(9),
                    Remarks = r.IsDBNull(10) ? null : r.GetString(10),
                    CreatedAt = r.GetDateTime(11),
                    StockId = r.GetInt32(12),
                    FertilizerId = r.GetInt32(13),
                    FertilizerName = r.GetString(14).Trim(),
                    FertilizerType = r.IsDBNull(15) ? null : r.GetString(15).Trim(),
                    BatchNumber = r.IsDBNull(16) ? null : r.GetString(16),
                    UnitName = r.GetString(17).Trim(),
                    SourceId = r.IsDBNull(18) ? null : r.GetInt32(18),
                    SourceName = r.IsDBNull(19) ? "" : r.GetString(19).Trim()
                });
            }
            return list;
        }

        // The dropdown choices are the values that ACTUALLY occur in the recorded issues (nothing is offered that can only
        // return an empty page). Receivers are the selected users plus -- for issues made before receivers were selected --
        // the distinct typed names; "Not recorded" is always available.
        public async Task<FertilizerTransactionOptions> GetFilterOptionsAsync()
        {
            var options = new FertilizerTransactionOptions();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            async Task Load(string sql, Action<SqlDataReader> add)
            {
                using var cmd = new SqlCommand(sql, conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) add(r);
            }

            await Load(@"SELECT DISTINCT fs.FertilizerId, RTRIM(fm.FertilizerName) FROM dbo.FertilizerUsage fu
                         INNER JOIN dbo.FertilizerStock fs ON fs.StockId = fu.StockId
                         INNER JOIN dbo.FertilizerMaster fm ON fm.FertilizerId = fs.FertilizerId ORDER BY 2, 1",
                r => options.Fertilizers.Add(new FertilizerFilterOption(r.GetInt32(0).ToString(), r.GetString(1))));
            await Load(@"SELECT DISTINCT fs.SourceId, RTRIM(src.SourceName) FROM dbo.FertilizerUsage fu
                         INNER JOIN dbo.FertilizerStock fs ON fs.StockId = fu.StockId
                         INNER JOIN dbo.FertilizerSource src ON src.SourceId = fs.SourceId ORDER BY 2, 1",
                r => options.Sources.Add(new FertilizerFilterOption(r.GetInt32(0).ToString(), r.GetString(1))));

            options.Receivers.Add(new FertilizerFilterOption(FertilizerTransactionFilter.NotRecorded, FertilizerIssueRules.NotRecordedLabel));
            await Load(@"SELECT DISTINCT u.Id, u.Name, ISNULL(u.IsActive, 0) FROM dbo.FertilizerUsage fu
                         INNER JOIN dbo.IMSUsers u ON u.Id = fu.ReceivedById ORDER BY u.Name, u.Id",
                r => options.Receivers.Add(new FertilizerFilterOption(FertilizerTransactionFilter.UserPrefix + r.GetInt32(0),
                    FertilizerIssueRules.ReceiverLabel(r.GetString(1), r.GetBoolean(2), null))));
            // older issues: the name that was typed (trimmed; same name in a different case is one entry)
            await Load(@"SELECT MIN(LTRIM(RTRIM(fu.ReceivedBy))) FROM dbo.FertilizerUsage fu
                         WHERE fu.ReceivedById IS NULL AND LTRIM(RTRIM(ISNULL(fu.ReceivedBy, N''))) <> N''
                         GROUP BY LTRIM(RTRIM(fu.ReceivedBy)) ORDER BY 1",
                r => options.Receivers.Add(new FertilizerFilterOption(FertilizerTransactionFilter.TextPrefix + r.GetString(0),
                    r.GetString(0) + " (typed name, older entry)")));

            options.EnteredBy.Add(new FertilizerFilterOption(FertilizerTransactionFilter.NotRecorded, FertilizerIssueRules.NotRecordedLabel));
            await Load(@"SELECT DISTINCT u.Id, u.Name, ISNULL(u.IsActive, 0) FROM dbo.FertilizerUsage fu
                         INNER JOIN dbo.IMSUsers u ON u.Id = fu.EnteredById ORDER BY u.Name, u.Id",
                r => options.EnteredBy.Add(new FertilizerFilterOption(r.GetInt32(0).ToString(),
                    FertilizerIssueRules.EnteredByLabel(r.GetString(1), r.GetBoolean(2)))));
            return options;
        }

        // The people an issue can be handed to: every ACTIVE user (fertilizer has no Area, so no Area limit). Ids and names come
        // from dbo.IMSUsers; the page offers exactly this list and the issue re-checks the chosen id against it.
        public async Task<List<FertilizerFilterOption>> GetReceiverChoicesAsync()
        {
            var list = new List<FertilizerFilterOption>();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, Name FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Name, Id", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FertilizerFilterOption(r.GetInt32(0).ToString(), r.GetString(1).Trim()));
            return list;
        }

        // Issue fertilizer from a stock batch. One transaction: the batch row is locked (UPDLOCK, HOLDLOCK) BEFORE the balance
        // is read, so two people / two clicks cannot both issue the same stock; the receiver must be an active user; an identical
        // issue saved within FertilizerIssueRules.DuplicateWindowSeconds is treated as the same form submitted twice and nothing
        // is written. The stock balance rule is the one that was already in place (LatestAvailableQuantity - quantity, and
        // IsUtilized when it reaches zero).
        public async Task<FertilizerIssueResult> IssueAsync(int stockId, decimal quantity, DateTime issueDate, int? receiverId, string? remarks, int? enteredById)
        {
            var (ok, error) = FertilizerIssueRules.ValidateIssue(quantity, issueDate, receiverId);
            if (!ok) return new FertilizerIssueResult(false, error);

            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                decimal available;
                using (var stockCmd = new SqlCommand(
                    "SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WITH (UPDLOCK, HOLDLOCK) WHERE StockId = @Id", conn, tx))
                {
                    stockCmd.Parameters.AddWithValue("@Id", stockId);
                    var value = await stockCmd.ExecuteScalarAsync();
                    if (value == null || value == DBNull.Value)
                    {
                        tx.Rollback();
                        return new FertilizerIssueResult(false, "That fertilizer stock was not found.");
                    }
                    available = (decimal)value;
                }

                string? receiverName;
                using (var recvCmd = new SqlCommand("SELECT Name FROM dbo.IMSUsers WHERE Id = @Id AND IsActive = 1", conn, tx))
                {
                    recvCmd.Parameters.AddWithValue("@Id", receiverId!.Value);
                    receiverName = (await recvCmd.ExecuteScalarAsync()) as string;
                }
                if (string.IsNullOrWhiteSpace(receiverName))
                {
                    tx.Rollback();
                    return new FertilizerIssueResult(false, "The chosen receiver is not an active user. Choose someone from the list.");
                }

                // same form submitted twice: report the first issue, write nothing
                using (var dupCmd = new SqlCommand(@"
SELECT TOP 1 UsageId FROM dbo.FertilizerUsage
WHERE StockId = @S AND UsedQuantity = @Q AND IssueDate = @D AND ReceivedById = @R
  AND ((@E IS NULL AND EnteredById IS NULL) OR EnteredById = @E)
  AND CreatedAt >= DATEADD(SECOND, -@Window, SYSDATETIME())
ORDER BY UsageId DESC", conn, tx))
                {
                    dupCmd.Parameters.AddWithValue("@S", stockId);
                    dupCmd.Parameters.AddWithValue("@Q", quantity);
                    dupCmd.Parameters.AddWithValue("@D", issueDate.Date);
                    dupCmd.Parameters.AddWithValue("@R", receiverId.Value);
                    dupCmd.Parameters.AddWithValue("@E", (object?)enteredById ?? DBNull.Value);
                    dupCmd.Parameters.AddWithValue("@Window", FertilizerIssueRules.DuplicateWindowSeconds);
                    var existing = await dupCmd.ExecuteScalarAsync();
                    if (existing != null && existing != DBNull.Value)
                    {
                        tx.Rollback();
                        return new FertilizerIssueResult(true, "This issue was already recorded a moment ago; it was not saved a second time.", true, (int)existing);
                    }
                }

                if (quantity > available)
                {
                    tx.Rollback();
                    return new FertilizerIssueResult(false, $"Used quantity exceeds available stock ({available:0.###} available).");
                }

                int usageId;
                using (var insertCmd = new SqlCommand(@"
INSERT INTO dbo.FertilizerUsage (StockId, UsedQuantity, IssueDate, ReceivedBy, ReceivedById, EnteredById, Remarks)
OUTPUT INSERTED.UsageId
VALUES (@S, @Q, @D, @RName, @R, @E, @Rm)", conn, tx))
                {
                    insertCmd.Parameters.AddWithValue("@S", stockId);
                    insertCmd.Parameters.AddWithValue("@Q", quantity);
                    insertCmd.Parameters.AddWithValue("@D", issueDate.Date);
                    insertCmd.Parameters.AddWithValue("@RName", receiverName.Trim());
                    insertCmd.Parameters.AddWithValue("@R", receiverId.Value);
                    insertCmd.Parameters.AddWithValue("@E", (object?)enteredById ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@Rm", string.IsNullOrWhiteSpace(remarks) ? DBNull.Value : remarks.Trim());
                    usageId = (int)(await insertCmd.ExecuteScalarAsync())!;
                }

                using (var updateCmd = new SqlCommand(@"
UPDATE dbo.FertilizerStock
SET LatestAvailableQuantity = LatestAvailableQuantity - @Q,
    IsUtilized = CASE WHEN LatestAvailableQuantity - @Q = 0 THEN 1 ELSE 0 END
WHERE StockId = @Id", conn, tx))
                {
                    updateCmd.Parameters.AddWithValue("@Q", quantity);
                    updateCmd.Parameters.AddWithValue("@Id", stockId);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                tx.Commit();
                return new FertilizerIssueResult(true, null, false, usageId);
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                throw;
            }
        }
    }
}
