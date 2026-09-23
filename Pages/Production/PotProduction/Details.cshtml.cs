using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PotProductionModel = PlantStockManager.Models.PotProduction;

namespace PlantStockManager.Pages.Production.PotProduction
{
    public class DetailsModel : PageModel
    {
        private readonly PotProductionRepository _potProductionRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(PotProductionRepository potProductionRepo, AreaAccessService areaAccessService)
        {
            _potProductionRepo = potProductionRepo;
            _areaAccessService = areaAccessService;
        }

        public PotProductionModel? PotProduction { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            PotProduction = await _potProductionRepo.GetByIdAsync(id);
            if (PotProduction == null)
                return RedirectToPage("/Production/PotProduction/Index");

            // Phase E: same pre-existing gap closed as Index above -- block
            // viewing another Area's Production record by direct URL/id.
            if (!_areaAccessService.CanAccessArea(User, PotProduction.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Pot Production record.";
                return RedirectToPage("/Production/PotProduction/Index");
            }

            return Page();
        }
    }
}
