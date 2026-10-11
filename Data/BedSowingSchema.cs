using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-12: is Database/Migrations/2026-10-12_BedSowing.sql applied to THIS database?
    // Shared queries (sowing lists, reports, dashboards) read the Bed Sowing tables only when it
    // is, so the application keeps working on a database the migration has not reached yet.
    // A positive answer is cached per server + database (the migration is never undone while
    // bed sowings exist -- its rollback refuses); a negative one for NegativeTtl only, so a
    // migration applied while the application runs is picked up within that time and the
    // one-command reports (Daily Operations Report) stay one command.
    public static class BedSowingSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string ProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.BedSowingOperations', 'U') IS NOT NULL AND COL_LENGTH('dbo.SeedSowings', 'BedSowingId') IS NOT NULL"
            + " AND COL_LENGTH('dbo.MaterialTransactions', 'TransactionDate') IS NOT NULL AND OBJECT_ID('dbo.BedSowingAuditLog', 'U') IS NOT NULL THEN 1 ELSE 0 END";

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

        // 2026-10-13_TraysToSowingApprovals.sql applied? (transplants make tray batches in Sowing
        // Approvals; Ready Date from the transplant date; no bed Ready Date). Same caching rules.
        private static readonly ConcurrentDictionary<string, bool> TraysReady = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> TraysNotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string TraysProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.ReadyStockTransplants', 'OutputSeedSowingId') IS NOT NULL THEN 1 ELSE 0 END";

        public static async Task<bool> TraysToApprovalsAsync(SqlConnection conn, SqlTransaction? tx = null)
        {
            var key = conn.DataSource + "|" + conn.Database;
            if (TraysReady.ContainsKey(key))
                return true;
            if (TraysNotReadyUntilUtc.TryGetValue(key, out var until) && until > DateTime.UtcNow)
                return false;
            using var cmd = new SqlCommand(TraysProbeSql, conn, tx);
            var ok = (int)(await cmd.ExecuteScalarAsync())! == 1;
            if (ok)
            {
                TraysReady[key] = true;
                TraysNotReadyUntilUtc.TryRemove(key, out _);
            }
            else
                TraysNotReadyUntilUtc[key] = DateTime.UtcNow + NegativeTtl;
            return ok;
        }

        // Sowing ACTIVITY (seed put into the ground), for the sowing counts / quantities of
        // reports and dashboards: every non-cancelled dbo.SeedSowings row EXCEPT the tray batches
        // made by a transplant -- from a bed (its seed was counted when the bed was sown) or from
        // received Ready Stock (2026-10-13; those plants were sown long before) -- plus
        // every non-cancelled bed sowing on its own sowing date. Columns: SowingDate, SourceType,
        // QuantitySown, AreaId, SpeciesId. Without the migration: SeedSowings only (unchanged).
        public static string SowingActivitySql(bool bedReady) => bedReady
            ? @"(SELECT sw.SowingDate, sw.SourceType, sw.QuantitySown, sw.AreaId, sw.SpeciesId
     FROM dbo.SeedSowings sw WHERE sw.Status <> 'Cancelled' AND sw.SourceType NOT IN (N'BedSowing', N'Transplant')
     UNION ALL
     SELECT CAST(b.SowingDate AS datetime2), N'BedSowing', b.Quantity, b.AreaId, b.SpeciesId
     FROM dbo.BedSowings b WHERE b.Status <> N'Cancelled')"
            : @"(SELECT sw.SowingDate, sw.SourceType, sw.QuantitySown, sw.AreaId, sw.SpeciesId
     FROM dbo.SeedSowings sw WHERE sw.Status <> 'Cancelled')";
    }
}
