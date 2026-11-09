using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-11-07: is Database/Migrations/2026-11-07_BedTransplantDays.sql applied to THIS database
    // (dbo.PlantSpecies.BedTransplantDays + CK_PlantSpecies_BedTransplantDays)? Without it Bed Sowing Alerts shows a notice,
    // the dashboard shows no Bed Sowing Alerts figures and Plant Master hides the field -- no transplant date is ever
    // assumed. Caching as BedSowingSchema (positive for good, negative for NegativeTtl).
    public static class BedTransplantScheduleSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') IS NOT NULL"
            + " AND OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') IS NOT NULL THEN 1 ELSE 0 END";

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
    }
}
