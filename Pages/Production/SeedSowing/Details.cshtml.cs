using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    public class DetailsModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public DetailsModel(SeedSowingRepository seedSowingRepo, SeedlingAreaScope areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _areaAccessService = areaAccessService;
        }

        public SeedSowingModel? SeedSowing { get; set; }

        // Phase 24 (Phase J): the same date-derived, read-only alert
        // category shown on the Ready Alerts list, computed via the one
        // shared SeedSowingRepository.ClassifyReadyAlert(...) function
        // (never a second, independently-hardcoded copy of this logic).
        // "None" for a Cancelled Sowing or one with no ExpectedReadyDate.
        public string AlertCategory { get; set; } = "None";

        public async Task<IActionResult> OnGetAsync(int id)
        {
            SeedSowing = await _seedSowingRepo.GetByIdAsync(id);
            if (SeedSowing == null)
                return RedirectToPage("/Production/SeedSowing/Index");

            // Block viewing another Area's Sowing record by direct
            // URL/id -- the same pre-existing-gap-closing pattern every
            // Phase E+ Details page already applies. Per the Phase J
            // spec's own "Sowing ID -> Load actual SeedSowing -> Read
            // actual AreaId -> CanAccessArea" requirement: the posted id
            // is never trusted, only the AreaId just read back off the
            // real, freshly-loaded row.
            if (!_areaAccessService.CanAccessArea(User, SeedSowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Sowing record.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }

            AlertCategory = SeedSowingRepository.ClassifyReadyAlert(
                SeedSowing.Status, SeedSowing.ExpectedReadyDate, DateTime.Today, SeedSowingRepository.DefaultReadySoonWindowDays);

            return Page();
        }
    }
}
