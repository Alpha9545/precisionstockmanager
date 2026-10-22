using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-16: are the 2026-10-16 workflow-correction migrations applied to THIS database? Each one is probed
    // separately (they can be approved and applied one at a time); without one, its feature keeps the earlier
    // behaviour. Caching as BedSowingSchema: a positive answer per server + database for good, a negative one
    // for NegativeTtl only.
    //   BedApprover        2026-10-16_BedSowingApprover.sql          -- Bed Sowing tray batches approved only by the
    //                                                                  designated approver (dbo.BedSowingApprover)
    //   TransplantWaste    2026-10-16_MotherPlantTransplantWaste.sql -- Mother Plant transplanting records the
    //                                                                  untransplanted remainder as waste (with a reason)
    //   PotToken           2026-10-16_PotProductionSubmissionToken.sql -- a pot batch is started once per form
    public static class WorkflowCorrectionsSchema
    {
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> NotReadyUntilUtc = new(StringComparer.OrdinalIgnoreCase);

        public const string BedApproverProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.BedSowingApprover', 'U') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_ReadyConfirmations_BedApprovalAudit', 'TR') IS NOT NULL THEN 1 ELSE 0 END";
        public const string TransplantWasteProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.ReadyStockTransplants', 'WasteReason') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_RsTransplants_NewRow', 'TR') IS NOT NULL THEN 1 ELSE 0 END";
        public const string PotTokenProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.PotProductionBatches', 'SubmissionToken') IS NOT NULL THEN 1 ELSE 0 END";

        // 2026-10-17: 2026-10-17_BedSowingApprovalAnyDate.sql (a Bed Sowing tray batch is approved on any date) and
        // 2026-10-17_TransplantApprovalAreaScope.sql (a Transplant tray batch only by its own Area's supervisor).
        public const string BedAnyDateProbeSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate', 'TR') IS NOT NULL"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) NOT LIKE N'%52178%' THEN 1 ELSE 0 END";
        public const string TransplantAreaScopeProbeSql =
            "SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) LIKE N'%TransplantApproverInBatchArea%' THEN 1 ELSE 0 END";

        // 2026-10-20_TransplantApprovalAnyDate.sql: a Mother Plant Area Transplant tray batch is approved on any date
        // (TR_ReadyConfirmations_BedReadyDate keeps only "it must have a Ready Date").
        public const string TransplantAnyDateProbeSql =
            "SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) LIKE N'%TransplantApprovalAnyDate%' THEN 1 ELSE 0 END";
        public static Task<bool> TransplantAnyDateAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, TransplantAnyDateProbeSql);

        // 2026-10-21_MotherPlantDirectSowing.sql: Mother Plant & Cutting > Direct Sowing (SeedSowings.IsMotherPlantDirectSowing,
        // approved by an Area Supervisor of the batch's Mother Plant Area). Without it the page records nothing.
        public const string MotherPlantDirectSowingProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NOT NULL"
            + " AND OBJECT_ID('dbo.TR_SeedSowings_MotherPlantDirectSowing', 'TR') IS NOT NULL"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) LIKE N'%MotherPlantDirectSowing%'"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole')) LIKE N'%MotherPlantDirectSowing%' THEN 1 ELSE 0 END";
        public const string MotherPlantDirectSowingMigrationMessage =
            "Direct Sowing in a Mother Plant Area needs the database update 2026-10-21_MotherPlantDirectSowing.sql, which is not applied yet.";
        public static Task<bool> MotherPlantDirectSowingAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, MotherPlantDirectSowingProbeSql);
        public static async Task<bool> MotherPlantDirectSowingAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            return await MotherPlantDirectSowingAsync(conn);
        }

        // 2026-10-22_BedSowingEdit.sql: an untouched Bed Sowing's date / variety / seed lot / quantity may be edited in place
        // (TR_BedSowings_Guard, with the seed ledger reconciled). Without it only Remarks can be edited.
        public const string BedSowingEditProbeSql =
            "SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_BedSowings_Guard')) LIKE N'%BedSowingEdit%' THEN 1 ELSE 0 END";
        public const string BedSowingEditMigrationMessage =
            "Changing the sowing date, variety, seed lot or quantity needs the database update 2026-10-22_BedSowingEdit.sql, which is not applied yet. Only Remarks can be edited.";
        public static Task<bool> BedSowingEditAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, BedSowingEditProbeSql);
        public static async Task<bool> BedSowingEditAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            return await BedSowingEditAsync(conn);
        }

        public const string PottedSaleTokenProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.Dispatches', 'SubmissionToken') IS NOT NULL THEN 1 ELSE 0 END";

        public const string BedApproverMigrationMessage =
            "This needs the database update 2026-10-16_BedSowingApprover.sql (Bed Sowing batches approved by the designated approver).";
        public const string TransplantWasteMigrationMessage =
            "Closing seedlings as waste only needs the database update 2026-10-16_MotherPlantTransplantWaste.sql.";

        public static Task<bool> BedApproverAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, BedApproverProbeSql);
        public static Task<bool> TransplantWasteAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, TransplantWasteProbeSql);
        public static Task<bool> PotTokenAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, PotTokenProbeSql);
        public static Task<bool> BedAnyDateAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, BedAnyDateProbeSql);
        public static Task<bool> TransplantAreaScopeAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, TransplantAreaScopeProbeSql);
        public static Task<bool> PottedSaleTokenAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, PottedSaleTokenProbeSql);
        // 2026-10-18_SeedlingDirectSaleSubmissionToken.sql: direct tray sales (SeedlingFulfilmentRepository.DirectTraySaleAsync)
        public const string SeedlingSaleTokenProbeSql =
            "SELECT CASE WHEN COL_LENGTH('dbo.SeedlingDispatches', 'SubmissionToken') IS NOT NULL THEN 1 ELSE 0 END";
        public static Task<bool> SeedlingSaleTokenAsync(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, SeedlingSaleTokenProbeSql);
        public static async Task<bool> SeedlingSaleTokenAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            return await SeedlingSaleTokenAsync(conn);
        }

        // 2026-10-19_Cavity70.sql: the database accepts "70 Cavity" trays (every cavity CHECK constraint and trigger
        // widened, Cocopeat / Pitmos consumption rows added). Without it 70 is never offered and never stocked.
        public const string Cavity70ProbeSql =
            "SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.CK_TrayStock_TraySize')) LIKE N'%70 Cavity%'"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_CavityType')) LIKE N'%70 Cavity%'"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays')) LIKE N'%70 Cavity%'"
            + " AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_RsTransplants_NewRow')) LIKE N'%70 Cavity%' THEN 1 ELSE 0 END";
        public const string Cavity70MigrationMessage =
            "70 Cavity trays need the database update 2026-10-19_Cavity70.sql, which is not applied yet.";
        public static Task<bool> Cavity70Async(SqlConnection conn, SqlTransaction? tx = null) => ProbeAsync(conn, tx, Cavity70ProbeSql);

        // The cavities a form may OFFER on this database: the closed list (DirectSowingRules.CavityTypes), minus
        // "70 Cavity" until 2026-10-19_Cavity70.sql is applied.
        public static async Task<IReadOnlyList<string>> SelectableCavitiesAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            return await Cavity70Async(conn)
                ? Services.DirectSowingRules.CavityTypes
                : Services.DirectSowingRules.CavityTypes.Where(c => c != Services.DirectSowingRules.Cavity70).ToList();
        }

        // null when this database can store trays of the cavity; otherwise why not (checked before any write).
        public static async Task<string?> CavityNotSupportedAsync(SqlConnection conn, SqlTransaction? tx, string? cavityType)
            => cavityType == Services.DirectSowingRules.Cavity70 && !await Cavity70Async(conn, tx) ? Cavity70MigrationMessage : null;

        public static async Task<bool> TransplantWasteAsync(DatabaseHelper db)
        {
            using var conn = db.GetConnection();
            await conn.OpenAsync();
            return await TransplantWasteAsync(conn);
        }

        private static async Task<bool> ProbeAsync(SqlConnection conn, SqlTransaction? tx, string probeSql)
        {
            var key = probeSql + "|" + conn.DataSource + "|" + conn.Database;
            if (Ready.ContainsKey(key))
                return true;
            if (NotReadyUntilUtc.TryGetValue(key, out var until) && until > DateTime.UtcNow)
                return false;
            using var cmd = new SqlCommand(probeSql, conn, tx);
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
