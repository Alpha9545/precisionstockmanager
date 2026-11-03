using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-11-03: is 2026-11-03_FieldWorkCustomers.sql (Field Work Phase 2: customers, visits, notes, follow-ups) applied to
    // THIS database? Without it the customer / visit / follow-up pages explain that the update is missing and the follow-up
    // badge stays hidden; Phase 1 (Start Day / End Day) is unaffected. Caching as FieldWorkSchema.
    public static class FieldCustomerSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.FieldCustomers', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldVisits', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldVisitItems', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldVisitPhotos', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldCustomerNotes', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldFollowUps', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.FieldCustomerEvents', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_FieldVisits_Guard', 'TR') IS NOT NULL"
            + " AND COL_LENGTH('dbo.FieldPhotos', 'ConsentReminderVersion') IS NOT NULL"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose')) LIKE N'%VisitPhoto%' THEN 1 ELSE 0 END";

        public const string MigrationMessage =
            "Field Work customers need the database update 2026-11-03_FieldWorkCustomers.sql, which is not applied yet.";

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
