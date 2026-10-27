using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-22: is 2026-10-24_ReadyStockOvergrowth.sql applied to THIS database? (dbo.ReadyStock Overgrowth columns, the
    // append-only dbo.ReadyStockOvergrowthLog and the dispatch-line snapshot.) Without it every Ready Stock screen works as
    // before: no batch is Overgrowth, no badge is shown and nothing can be marked. Caching as WorkflowCorrectionsSchema: a
    // positive answer per server + database for good, a negative one for NegativeTtl only.
    public static class ReadyStockOvergrowthSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.ReadyStock', 'IsOvergrowth') IS NOT NULL"
            + " AND OBJECT_ID('dbo.CK_ReadyStock_Overgrowth', 'C') IS NOT NULL"
            + " AND OBJECT_ID('dbo.ReadyStockOvergrowthLog', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_ReadyStockOvergrowthLog_AppendOnly', 'TR') IS NOT NULL"
            + " AND COL_LENGTH('dbo.SeedlingDispatchLines', 'IsOvergrowth') IS NOT NULL THEN 1 ELSE 0 END";

        public const string MigrationMessage =
            "Overgrowth marking needs the database update 2026-10-24_ReadyStockOvergrowth.sql, which is not applied yet.";

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

        // The Overgrowth columns of a dbo.ReadyStock row aliased "rs", or their "never marked" values without the migration --
        // so one SELECT works on both. Columns: IsOvergrowth, OvergrowthMarkedAt, OvergrowthMarkedByName, OvergrowthReason.
        public static string Columns(bool ready, string alias = "rs") => ready
            ? $"{alias}.IsOvergrowth, {alias}.OvergrowthMarkedAt, ogu.Name AS OvergrowthMarkedByName, {alias}.OvergrowthReason"
            : "CAST(0 AS BIT) AS IsOvergrowth, CAST(NULL AS DATETIME2) AS OvergrowthMarkedAt, CAST(NULL AS NVARCHAR(100)) AS OvergrowthMarkedByName, CAST(NULL AS NVARCHAR(200)) AS OvergrowthReason";

        // The join Columns(ready: true) needs (the marking user's name).
        public static string Join(bool ready, string alias = "rs") => ready
            ? $" LEFT JOIN dbo.IMSUsers ogu ON ogu.Id = {alias}.OvergrowthMarkedById"
            : "";

        // Just the flag of a ReadyStock row (for lists that show the badge only).
        public static string Flag(bool ready, string alias = "rs") => ready ? $"{alias}.IsOvergrowth" : "CAST(0 AS BIT)";
    }
}
