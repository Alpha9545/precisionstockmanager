using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-23: is 2026-10-25_MaterialStockTransfers.sql applied to THIS database? (dbo.MaterialTransfers, the TRANSFER_OUT /
    // TRANSFER_IN ledger types and the trigger versions that allow them.) Without it the Material Transfers page explains that
    // the update is missing and every other Material Stock screen works as before. Caching as ReadyStockOvergrowthSchema: a
    // positive answer per server + database for good, a negative one for NegativeTtl only.
    public static class MaterialTransferSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.MaterialTransfers', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_MaterialTransfers_Rules', 'TR') IS NOT NULL"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialTransactions_Type')) LIKE N'%TRANSFER[_]IN%'"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialTransactions_Ledger')) LIKE N'%51937%'"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialStock_Rules')) LIKE N'%MotherPlant%' THEN 1 ELSE 0 END";

        public const string MigrationMessage =
            "Material transfers need the database update 2026-10-25_MaterialStockTransfers.sql, which is not applied yet.";

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
