using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-14: is Database/Migrations/2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql applied to
    // THIS database? When it is:
    //   * empty trays of a Mother Plant Area are held in ONE pool per Area + cavity (no Polyhouse) --
    //     TrayStockRepository resolves every Mother Plant Area pool to it;
    //   * a Ready Stock transfer to a Mother Plant Area records its purpose (Pot Production / Transplant
    //     into a different cavity tray) and needs no destination Polyhouse; transplanting needs none either.
    // Without it every page and repository keeps the earlier (Polyhouse) behaviour. Caching as BedSowingSchema:
    // a positive answer per server + database for good, a negative one for NegativeTtl only.
    public static class MotherPlantAreaSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string MotherPlantAreaType = "MotherPlant";

        public const string MigrationMessage =
            "This needs the database update 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql (Mother Plant Area trays per Area, transfer purpose).";

        public const string ProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_TrayStock_PoolLocation', 'TR') IS NOT NULL THEN 1 ELSE 0 END";

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
