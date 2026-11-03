using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-11-03: is 2026-11-03_FieldWorkVehicleOptional.sql applied to THIS database (dbo.FieldDays.VehicleType NULLable)?
    // Without it Start Day keeps showing and requiring the vehicle type -- no invented default is ever written. Caching as
    // FieldWorkSchema (positive for good, negative for NegativeTtl).
    public static class FieldDayVehicleSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.FieldDays', 'U') IS NOT NULL"
            + " AND COLUMNPROPERTY(OBJECT_ID('dbo.FieldDays'), 'VehicleType', 'AllowsNull') = 1"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_FieldDays_Guard')) LIKE N'%ISNULL(i.VehicleType%' THEN 1 ELSE 0 END";

        public static async Task<bool> OptionalAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            var key = conn.DataSource + "|" + conn.Database;
            if (Ready.ContainsKey(key))
                return true;
            if (NotReadyUntilUtc.TryGetValue(key, out var until) && until > DateTime.UtcNow)
                return false;
            using var cmd = new SqlCommand(ProbeSql, conn);
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
