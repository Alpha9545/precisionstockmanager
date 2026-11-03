using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-11-03: is 2026-11-03_FieldWorkDays.sql applied to THIS database? Without it the Field Work pages explain that the
    // update is missing, the Start Day gate lets everyone through and the auto-close job skips. Caching as
    // MaterialTransferSchema: a positive answer per server + database for good, a negative one for NegativeTtl only.
    public static class FieldWorkSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.FieldDays', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldPhotos', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldDayEvents', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldWorkConsents', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_FieldDays_Guard', 'TR') IS NOT NULL"
            + " AND EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'FieldWork.Day') THEN 1 ELSE 0 END";

        public const string MigrationMessage =
            "Field Work needs the database update 2026-11-03_FieldWorkDays.sql, which is not applied yet.";

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
