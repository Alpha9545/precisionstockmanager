using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;
using CuttingSowingModel = PlantStockManager.Models.CuttingSowing;
using PotProductionBatchModel = PlantStockManager.Models.PotProductionBatch;

namespace PlantStockManager.Pages.Production.ReadyAlerts
{
    // Phase 24 (Phase J): Ready Alerts. NOTIFICATION/ALERT FUNCTIONALITY
    // ONLY -- read-only, query-driven, over the existing dbo.SeedSowings
    // (Phase 23/I) + the just-extended dbo.PlantSpecies.ReadyStockDays
    // (this phase). No new table, no new Status value, and (critically)
    // no write of any kind happens anywhere on this page: viewing an
    // alert never changes a Sowing's Status, never creates Ready Stock,
    // and never touches SeedStock/PottedPlantStock. See Decision 21 in
    // PROJECT_DOCUMENTATION.md for the full reasoning.
    //
    // Deliberately placed under its own Pages/Production/ReadyAlerts/
    // folder (the user's own recommended location) -- distinct both in
    // name and in URL from the pre-existing, untouched legacy
    // Pages/Data/ReadyStock.cshtml(.cs) (now removed; it reported the
    // old Inventory/SeedEntries pipeline), mirroring the exact
    // "SeedSowing, never bare Sowing" naming precedent Phase I already
    // set for the identical reason.
    //
    // Phase 5 follow-up: Cutting Sowing batches (Data/
    // CuttingSowingRepository.cs) are alerted here too, in their OWN
    // three lists/tables -- never merged into the Seed lists (do not mix
    // seed stock and cutting stock), reusing the SAME classification
    // function (SeedSowingRepository.ClassifyReadyAlert takes only
    // primitive status/date/window arguments -- it has no seed-specific
    // logic, so it is called unchanged for Cutting Sowing candidates too).
    public class IndexModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly CuttingSowingRepository _cuttingSowingRepo;
        private readonly PotProductionBatchRepository _potProductionBatchRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)
        private readonly AreaAccessService _cuttingAreaAccessService; // Cutting Sowing / Pot Production Batch use the regular Area scope (Phase 3/4/5/31 convention)

        public IndexModel(
            SeedSowingRepository seedSowingRepo, CuttingSowingRepository cuttingSowingRepo,
            PotProductionBatchRepository potProductionBatchRepo,
            SeedlingAreaScope areaAccessService, AreaAccessService cuttingAreaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _cuttingSowingRepo = cuttingSowingRepo;
            _potProductionBatchRepo = potProductionBatchRepo;
            _areaAccessService = areaAccessService;
            _cuttingAreaAccessService = cuttingAreaAccessService;
        }

        // Judgment call, per the Phase J spec's own instruction ("before
        // hardcoding 3, inspect whether the project already has an
        // alert-window configuration" -- inspected, none exists anywhere
        // in this app). Defaults to the spec's own illustrative example
        // (3 days), shared with Details.cshtml.cs via
        // SeedSowingRepository.DefaultReadySoonWindowDays so the two
        // pages can never disagree, and overridable per-request via
        // ?WindowDays=N without any config/schema change.
        [BindProperty(SupportsGet = true)]
        public int WindowDays { get; set; } = SeedSowingRepository.DefaultReadySoonWindowDays;

        public List<SeedSowingModel> Overdue { get; set; } = new();
        public List<SeedSowingModel> ReadyToday { get; set; } = new();
        public List<SeedSowingModel> ReadySoon { get; set; } = new();

        public List<CuttingSowingModel> CuttingOverdue { get; set; } = new();
        public List<CuttingSowingModel> CuttingReadyToday { get; set; } = new();
        public List<CuttingSowingModel> CuttingReadySoon { get; set; } = new();

        public List<PotProductionBatchModel> PotBatchOverdue { get; set; } = new();
        public List<PotProductionBatchModel> PotBatchReadyToday { get; set; } = new();
        public List<PotProductionBatchModel> PotBatchReadySoon { get; set; } = new();

        public async Task OnGetAsync()
        {
            if (WindowDays <= 0)
                WindowDays = SeedSowingRepository.DefaultReadySoonWindowDays;

            var today = DateTime.Today;
            var horizon = today.AddDays(WindowDays);

            // SQL-level filter (Status = 'Sown' AND ExpectedReadyDate
            // populated AND <= horizon) -- avoids loading every
            // historical Sowing, per the Phase J spec's performance
            // requirement. A Cancelled Sowing can never be returned by
            // this query, regardless of its ExpectedReadyDate.
            var candidates = await _seedSowingRepo.GetAlertCandidatesAsync(horizon);

            // Same Area-scoping convention as every Phase E+ list page
            // (and SeedSowing/Index.cshtml.cs itself): a user without
            // full/cross-Area access only ever sees alerts for Areas
            // they can actually access. UI-only filtering is never
            // relied on elsewhere in this app, and isn't here either --
            // AreaAccessService is the actual boundary.
            var accessible = _areaAccessService.HasFullAreaAccess(User)
                ? candidates
                : candidates.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();

            foreach (var sowing in accessible)
            {
                var category = SeedSowingRepository.ClassifyReadyAlert(sowing.Status, sowing.ExpectedReadyDate, today, WindowDays);
                switch (category)
                {
                    case "Overdue":
                        Overdue.Add(sowing);
                        break;
                    case "ReadyToday":
                        ReadyToday.Add(sowing);
                        break;
                    case "ReadySoon":
                        ReadySoon.Add(sowing);
                        break;
                    // "None" cannot occur here in practice -- the SQL
                    // filter already excluded Cancelled rows and rows
                    // further out than the horizon -- but is handled
                    // safely (simply not shown) if it ever did.
                }
            }

            var cuttingCandidates = await _cuttingSowingRepo.GetAlertCandidatesAsync(horizon);
            var cuttingAccessible = _cuttingAreaAccessService.HasFullAreaAccess(User)
                ? cuttingCandidates
                : cuttingCandidates.Where(s => _cuttingAreaAccessService.CanAccessArea(User, s.AreaId)).ToList();

            foreach (var sowing in cuttingAccessible)
            {
                var category = SeedSowingRepository.ClassifyReadyAlert(sowing.Status, sowing.ExpectedReadyDate, today, WindowDays);
                switch (category)
                {
                    case "Overdue":
                        CuttingOverdue.Add(sowing);
                        break;
                    case "ReadyToday":
                        CuttingReadyToday.Add(sowing);
                        break;
                    case "ReadySoon":
                        CuttingReadySoon.Add(sowing);
                        break;
                }
            }

            var potBatchCandidates = await _potProductionBatchRepo.GetAlertCandidatesAsync(horizon);
            var potBatchAccessible = _cuttingAreaAccessService.HasFullAreaAccess(User)
                ? potBatchCandidates
                : potBatchCandidates.Where(b => _cuttingAreaAccessService.CanAccessArea(User, b.AreaId)).ToList();

            foreach (var batch in potBatchAccessible)
            {
                var category = SeedSowingRepository.ClassifyReadyAlert(batch.Status, batch.ExpectedReadyDate, today, WindowDays, activeStatus: "InProduction");
                switch (category)
                {
                    case "Overdue":
                        PotBatchOverdue.Add(batch);
                        break;
                    case "ReadyToday":
                        PotBatchReadyToday.Add(batch);
                        break;
                    case "ReadySoon":
                        PotBatchReadySoon.Add(batch);
                        break;
                }
            }
        }
    }
}
