using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    public class DetailsModel : PageModel
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(MotherPlantRepository motherPlantRepo, AreaAccessService areaAccessService)
        {
            _motherPlantRepo = motherPlantRepo;
            _areaAccessService = areaAccessService;
        }

        public MotherPlantModel? MotherPlant { get; set; }

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

            return Page();
        }
    }
}
