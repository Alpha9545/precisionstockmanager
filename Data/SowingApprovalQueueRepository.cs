using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // 2026-10-19: the batches waiting for Supervisor Approval, as ONE read used by every list of them --
    // Seed Production > Sowing Approvals, Mother Plant & Cutting > Sowing Approvals and the home page counters -- so they can
    // never disagree. Read only: approving stays ReadyConfirmation/Confirm -> ReadyConfirmationRepository.ConfirmAsync (the one
    // approval transaction and the one Ready Stock posting), which re-checks everything under the batch's lock.
    //   Visible  = in the viewer's Area / workflow scope (SowingApprovalRules.IsVisible) -- never filtered to the viewer's own
    //              assignments. The Area scope comes from the caller's claims, never from the request.
    //   Approve  = SowingApprovalRules.Evaluate (the same checks as the approval, in the same order).
    public class SowingApprovalQueueRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly SeedSowingRepository _sowingRepo;
        private readonly ReadyConfirmationRepository _confirmationRepo;

        public SowingApprovalQueueRepository(DatabaseHelper dbHelper, SeedSowingRepository sowingRepo, ReadyConfirmationRepository confirmationRepo)
        {
            _dbHelper = dbHelper;
            _sowingRepo = sowingRepo;
            _confirmationRepo = confirmationRepo;
        }

        // seedlingScope: SeedlingAreaScope.CanAccessArea; strictAreaScope: AreaAccessService.CanAccessArea (Mother Plant work).
        public sealed record Viewer(int? UserId, string? UserName, bool HasConfirmPermission, Func<int, bool> SeedlingScope, Func<int, bool> StrictAreaScope);

        public sealed record Item(SeedSowing Sowing, string Category, string? AreaType, SowingApprovalRules.Eligibility Eligibility,
            int? SourceReadyStockId, string? SourceBatchNo)
        {
            public decimal Trays => Sowing.NumberOfTrays ?? 0;
            // "Waiting for approval" / "Growing -- approve from ..." / "Partly approved" (display)
            public string Status => Sowing.ConfirmedReadyQuantity > 0 ? "Partly approved"
                : Eligibility.WaitingForReadyDate || (Sowing.ExpectedReadyDate.HasValue && Sowing.ExpectedReadyDate.Value.Date > AppClock.Today && Sowing.IsTransplantedBatch)
                    ? "Growing (Ready Date not reached)"
                    : "Waiting for approval";
        }

        public sealed record Queue(List<Item> Items, SowingApprovalRules.Settings Settings);

        // category: one of SowingApprovalRules.Categories, MotherPlantCategories (both Mother Plant workflows -- 2026-10-21), or
        // null for every category. A Mother Plant transplant batch / Direct Sowing is listed only when its Area really is a
        // Mother Plant Area.
        public const string MotherPlantCategories = "MotherPlant*";
        // approvableOnly (2026-10-20, Mother Plant & Cutting > Sowing Approvals > Waiting for approval): keep only the batches THIS
        // viewer is authorised to approve (SowingApprovalRules.Eligibility.Authorized -- the same checks as the approval itself,
        // with the identity from the server-side principal); every other batch is left out, not shown as "Not your approval".
        // A display filter only: the Confirm page, ConfirmAsync and the database triggers still check every approval.
        // filter (2026-10-22, Sowing Approvals' Variety / Sowing Date): applied in the pending-batch SQL, BEFORE the scope and
        // approvableOnly checks below -- it can only narrow the list, never add a batch the viewer would not otherwise see.
        public async Task<Queue> GetPendingAsync(Viewer viewer, string? category = null, bool approvableOnly = false, SowingListFilter? filter = null)
        {
            var approval = await _confirmationRepo.GetApprovalSettingsAsync();
            var settings = new SowingApprovalRules.Settings(approval.Bed, approval.BedAnyDate, approval.TransplantAreaScope, approval.TransplantAnyDate);
            var pending = await _sowingRepo.GetReadyForConfirmationAsync(filter);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var areaTypes = new Dictionary<int, string?>();
            using (var cmd = new SqlCommand("SELECT Id, AreaType FROM dbo.Area", conn))
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    areaTypes[r.GetInt32(0)] = r.IsDBNull(1) ? null : r.GetString(1);

            var visible = new List<(SeedSowing Sowing, string Category, string? AreaType)>();
            foreach (var s in pending)
            {
                var c = SowingApprovalRules.CategoryOfBatch(s);
                if (category != null && c != category && !(category == MotherPlantCategories && SowingApprovalRules.IsMotherPlantCategory(c)))
                    continue;
                var areaType = areaTypes.TryGetValue(s.AreaId, out var t) ? t : null;
                if (SowingApprovalRules.IsMotherPlantCategory(c) && areaType != MotherPlantAreaSchema.MotherPlantAreaType)
                    continue;
                if (!SowingApprovalRules.IsVisible(c, s.AreaId, viewer.SeedlingScope, viewer.StrictAreaScope))
                    continue;
                visible.Add((s, c, areaType));
            }

            // Area Supervisor check, once per Area (needed for Mother Plant transplant batches under TransplantAreaScope, and for
            // Mother Plant Area Direct Sowings always -- 2026-10-21).
            var areaApprover = new Dictionary<int, bool>();
            if (viewer.HasConfirmPermission && viewer.UserId.HasValue)
                foreach (var areaId in visible.Where(v => v.Category == SowingApprovalRules.CategoryMotherPlantDirectSowing
                                                          || (settings.TransplantAreaScope && v.Category == SowingApprovalRules.CategoryMotherPlantTransplant))
                             .Select(v => v.Sowing.AreaId).Distinct())
                    areaApprover[areaId] = await ReadyConfirmationRepository.IsAreaApproverAsync(conn, null, viewer.UserId.Value, areaId);

            var sourceIds = visible.Where(v => v.Sowing.TransplantSourceReadyStockId.HasValue).Select(v => v.Sowing.TransplantSourceReadyStockId!.Value).Distinct().ToList();
            var sourceBatchNo = await BatchNumbersAsync(conn, sourceIds);

            var today = AppClock.Today;
            var items = visible.Select(v => new Item(v.Sowing, v.Category, v.AreaType,
                    SowingApprovalRules.Evaluate(v.Sowing, viewer.UserId, viewer.UserName, viewer.HasConfirmPermission,
                        areaApprover.TryGetValue(v.Sowing.AreaId, out var ok) && ok, settings, today),
                    v.Sowing.TransplantSourceReadyStockId,
                    v.Sowing.TransplantSourceReadyStockId is int id && sourceBatchNo.TryGetValue(id, out var no) ? no : null))
                .Where(i => !approvableOnly || i.Eligibility.Authorized)
                .ToList();
            return new Queue(items, settings);
        }

        // 2026-10-22: the Variety dropdown of Sowing Approvals -- the varieties of the batches a viewer's (unfiltered) queue lists,
        // so it never names a variety only seen in another Area's batches.
        public static List<VarietyOption> VarietiesOf(IEnumerable<Item> items)
            => items.GroupBy(i => i.Sowing.SpeciesId)
                .Select(g => new VarietyOption(g.Key, g.First().Sowing.SpeciesName.Trim(), g.First().Sowing.PlantTypeName))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Id)
                .ToList();

        // 2026-10-20 (owner requirements): approval HISTORY -- one row per dbo.ReadyConfirmations record, with the RECORDED approver
        // (ApprovedById, written from the signed-in user's claim inside the approval transaction and checked by
        // TR_ReadyConfirmations_AssignedSupervisor) -- never inferred from the batch's creator, its Area or a role.
        //   scope  HistoryScope.ApprovedBy(id)  that user's own approvals only (non-administrators: "Approved by me")
        //          HistoryScope.All             every user's approvals (administrators only -- SowingApprovalVisibility.History)
        //          HistoryScope.Nobody          nothing
        //   motherPlantOnly  Mother Plant & Cutting page: Transplant batches and (2026-10-21) Direct Sowings of Mother Plant Areas only.
        // The scope comes from the server-side principal (the pages bind no identity). A batch stays listed wherever its Ready
        // Stock is now; cancelled approvals stay listed, marked.
        public sealed record ApprovedBatch(int ConfirmationId, string ConfirmationCode, DateTime ApprovedAtUtc, string Status,
            int SeedSowingId, string SowingCode, int? TransplantId, string? TransplantCode, DateTime? TransplantDate,
            string? SpeciesName, string? PlantTypeName, string AreaName, string CavityType, int? ActualTrays, decimal ApprovedQuantity,
            decimal WastageQuantity, DateTime? ExpectedReadyDate, int ReadyStockId, string? ReadyStockBatchNo, string? ReadyStockAreaName,
            int? SourceReadyStockId, string? SourceBatchNo, int? ApproverId = null, string? ApproverName = null, string? SourceType = null,
            bool MotherPlantDirect = false)
        {
            public bool ApprovedBeforeReadyDate => ExpectedReadyDate.HasValue && AppClock.ToIndiaTime(ApprovedAtUtc).Date < ExpectedReadyDate.Value.Date;
            public string Category => SowingApprovalRules.CategoryOf(SourceType, MotherPlantDirect);
        }

        public async Task<List<ApprovedBatch>> GetApprovalHistoryAsync(HistoryScope scope, bool motherPlantOnly)
        {
            var list = new List<ApprovedBatch>();
            if (!scope.IncludesAllApprovers && scope.ApproverId is null)
                return list;                                                  // Nobody: never "everything" by accident
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var transplants = await BedSowingSchema.TraysToApprovalsAsync(conn);   // ReadyStockTransplants.OutputSeedSowingId (2026-10-13)
            var mpDirect = await WorkflowCorrectionsSchema.MotherPlantDirectSowingAsync(conn);   // SeedSowings.IsMotherPlantDirectSowing (2026-10-21)
            if (motherPlantOnly && !transplants && !mpDirect)
                return list;
            var sql = @"
SELECT rc.Id, rc.ConfirmationCode, rc.ConfirmationDate, rc.Status,
       sw.Id, sw.SowingCode, " + (transplants ? "rt.Id, rt.TransplantCode, rt.TransplantDate," : "CAST(NULL AS INT), CAST(NULL AS NVARCHAR(30)), CAST(NULL AS DATE),") + @"
       ps.Name, pt.Name, a.Name, sw.CavityType, rc.ActualTrayQuantity, rc.ConfirmedQuantity, rc.WastageQuantity, sw.ExpectedReadyDate,
       rc.ReadyStockId, rs.BatchNo, ra.Name, " + (transplants ? "rt.SourceReadyStockId, src.BatchNo," : "CAST(NULL AS INT), CAST(NULL AS NVARCHAR(50)),") + @"
       rc.ApprovedById, ap.Name, sw.SourceType, " + (mpDirect ? "sw.IsMotherPlantDirectSowing" : "CAST(0 AS BIT)") + @"
FROM dbo.ReadyConfirmations rc
JOIN dbo.SeedSowings sw ON sw.Id = rc.SeedSowingId
JOIN dbo.Area a ON a.Id = sw.AreaId
JOIN dbo.PlantSpecies ps ON ps.Id = sw.SpeciesId
LEFT JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
LEFT JOIN dbo.IMSUsers ap ON ap.Id = rc.ApprovedById
LEFT JOIN dbo.ReadyStock rs ON rs.Id = rc.ReadyStockId
LEFT JOIN dbo.Area ra ON ra.Id = rs.AreaId" + (transplants ? @"
LEFT JOIN dbo.ReadyStockTransplants rt ON rt.OutputSeedSowingId = sw.Id
LEFT JOIN dbo.ReadyStock src ON src.Id = rt.SourceReadyStockId" : "") + @"
WHERE (@All = 1 OR rc.ApprovedById = @Approver)
  AND (@MpOnly = 0 OR (a.AreaType = N'MotherPlant' AND (sw.SourceType = N'Transplant'" + (mpDirect ? " OR sw.IsMotherPlantDirectSowing = 1" : "") + @")))
ORDER BY rc.ConfirmationDate DESC, rc.Id DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@All", scope.IncludesAllApprovers ? 1 : 0);
            cmd.Parameters.AddWithValue("@Approver", (object?)scope.ApproverId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MpOnly", motherPlantOnly ? 1 : 0);
            using var r = await cmd.ExecuteReaderAsync();
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            while (await r.ReadAsync())
                list.Add(new ApprovedBatch(r.GetInt32(0), r.GetString(1), r.GetDateTime(2), r.GetString(3),
                    r.GetInt32(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt32(6), S(7), r.IsDBNull(8) ? null : r.GetDateTime(8),
                    S(9), S(10), r.GetString(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetInt32(13), r.GetDecimal(14), r.GetDecimal(15),
                    r.IsDBNull(16) ? null : r.GetDateTime(16), r.GetInt32(17), S(18), S(19), r.IsDBNull(20) ? null : r.GetInt32(20), S(21),
                    r.IsDBNull(22) ? null : r.GetInt32(22), S(23), S(24), r.GetBoolean(25)));
            return list;
        }

        // One supervisor's own Mother Plant approvals (kept for callers that name the approver explicitly).
        public Task<List<ApprovedBatch>> GetMotherPlantApprovedByAsync(int approverId)
            => GetApprovalHistoryAsync(HistoryScope.ApprovedBy(approverId), motherPlantOnly: true);

        private static async Task<Dictionary<int, string>> BatchNumbersAsync(SqlConnection conn, List<int> ids)
        {
            var map = new Dictionary<int, string>();
            if (ids.Count == 0)
                return map;
            using var cmd = new SqlCommand(
                $"SELECT Id, BatchNo FROM dbo.ReadyStock WHERE Id IN ({string.Join(", ", ids.Select((_, i) => "@p" + i))})", conn);
            for (var i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue("@p" + i, ids[i]);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                map[r.GetInt32(0)] = r.GetString(1);
            return map;
        }
    }
}
