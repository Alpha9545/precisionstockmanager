using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    public class DetailsModel : PageModel
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(MotherPlantRepository motherPlantRepo, CuttingStockRepository cuttingStockRepo, AreaAccessService areaAccessService)
        {
            _motherPlantRepo = motherPlantRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _areaAccessService = areaAccessService;
        }

        public MotherPlantModel? MotherPlant { get; set; }

        // Phase 3: "Actual Cutting Quantity" + recent history -- every
        // Cutting Stock 'Harvest' transaction recorded for THIS Mother
        // Plant's own Species+Area since its Planting Date (Model B has no
        // MotherPlantId link on the harvest record itself; see
        // CuttingStockRepository.GetHarvestSummaryAsync). Empty when the
        // Mother Plant has no Area assigned -- there is nothing to
        // attribute the harvest to.
        public decimal ActualCuttingQuantity { get; set; }
        public List<CuttingStockTransaction> RecentHarvests { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            MotherPlant = await _motherPlantRepo.GetByIdAsync(id);
            if (MotherPlant == null)
                return RedirectToPage("/Production/MotherPlant/Index");

            // Phase 17/B: block viewing another Area's record by direct
            // URL/id (e.g. /Production/MotherPlant/Details/5), the classic
            // tampering vector rule 5/10 call out specifically.
            if (!_areaAccessService.CanAccessArea(User, MotherPlant.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Mother Plant record.";
                return RedirectToPage("/Production/MotherPlant/Index");
            }

            if (MotherPlant.AreaId.HasValue)
            {
                (ActualCuttingQuantity, RecentHarvests) = await _cuttingStockRepo.GetHarvestSummaryAsync(
                    MotherPlant.SpeciesId, MotherPlant.AreaId.Value, MotherPlant.PlantingDate);
            }

            return Page();
        }
    }
}
