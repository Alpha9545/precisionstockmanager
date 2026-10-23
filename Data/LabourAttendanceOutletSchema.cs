using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-23: is 2026-10-26_LabourAttendanceOutlet.sql applied to THIS database? (TR_DailyLabourAttendance_Location then
    // accepts Outlet Areas, recorded Area-wise.) Without it Labour Attendance excludes Outlet exactly as before. Only ATTENDANCE
    // changes: manual labour counts and overtime keep excluding Outlet. Caching as ReadyStockOvergrowthSchema: a positive answer
    // per server + database for good, a negative one for NegativeTtl only.
    public static class LabourAttendanceOutletSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_DailyLabourAttendance_Location')) LIKE N'%[[]2026-10-26 OutletAttendance]%' THEN 1 ELSE 0 END";

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
