using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-15: is Database/Migrations/2026-10-15_PotProductionFromReadyStock.sql applied to THIS database?
    // When it is, a pot production batch may take its plants from a Ready Stock batch received in a Mother Plant
    // Area for the purpose "Pot Production" (dbo.PotProductionBatches.SourceReadyStockId) instead of Cutting Stock.
    // Without it every page keeps the Cutting -> Pots behaviour only. Caching as MotherPlantAreaSchema: a positive
    // answer per server + database for good, a negative one for NegativeTtl only.
    public static class PotBatchSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string MigrationMessage =
            "Pot Production from Ready Stock needs the database update 2026-10-15_PotProductionFromReadyStock.sql.";

        public const string ProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.PotProductionBatches', 'SourceReadyStockId') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_PotBatches_ReadyStockSource', 'TR') IS NOT NULL THEN 1 ELSE 0 END";

        public static async Task<bool> ReadyStockSourceAsync(SqlConnection conn, SqlTransaction? tx = null)
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

        public static async Task<bool> ReadyStockSourceAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            return await ReadyStockSourceAsync(conn);
        }
    }
}
