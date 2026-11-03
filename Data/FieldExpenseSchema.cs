using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-11-03: is 2026-11-03_FieldWorkExpenses.sql (Field Work Phase 4: expenses with a bill photo) applied to THIS
    // database? Without it the expense pages explain that the update is missing (and refuse every action);
    // Phases 1-3 are unaffected. Caching as FieldCustomerSchema (positive for the process, negative for 2 minutes).
    public static class FieldExpenseSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.FieldExpenses', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldExpenseEvents', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_FieldExpenses_Guard', 'TR') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_FieldExpenseEvents_Guard', 'TR') IS NOT NULL"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose')) LIKE N'%ExpenseBill%'"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder')) LIKE N'%ExpenseBill%' THEN 1 ELSE 0 END";

        public const string MigrationMessage =
            "Field Work expenses need the database update 2026-11-03_FieldWorkExpenses.sql, which is not applied yet.";

        public static async Task<bool> ReadyAsync(SqlConnection conn, SqlTransaction? tx = null)
        {
            var key = conn.DataSource + "|" + conn.Database;
            if (Ready.ContainsKey(key))
                return true;
            if (NotReadyUntilUtc.TryGetValue(key, out var until) && until > DateTime.UtcNow)
                return false;
            using var cmd = new SqlCommand(ProbeSql, conn, tx);
            var ok = (int)(await cmd.ExecuteScalarAsync())! == 1;
            if (ok)
            {
                Ready[key] = true;
                NotReadyUntilUtc.TryRemove(key, out _);
            }
            else
                NotReadyUntilUtc[key] = DateTime.UtcNow + NegativeTtl;
            return ok;
        }

        public static async Task<bool> ReadyAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            return await ReadyAsync(conn);
        }
    }
}
