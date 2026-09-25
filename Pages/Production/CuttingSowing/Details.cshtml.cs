using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using CuttingSowingModel = PlantStockManager.Models.CuttingSowing;

namespace PlantStockManager.Pages.Production.CuttingSowing
{
    public class DetailsModel : PageModel
    {
        private readonly CuttingSowingRepository _cuttingSowingRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(CuttingSowingRepository cuttingSowingRepo, AreaAccessService areaAccessService)
        {
            _cuttingSowingRepo = cuttingSowingRepo;
            _areaAccessService = areaAccessService;
        }

        public CuttingSowingModel? CuttingSowing { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            CuttingSowing = await _cuttingSowingRepo.GetByIdAsync(id);
            if (CuttingSowing == null)
                return RedirectToPage("/Production/CuttingSowing/Index");

            // Block viewing another Area's record by direct URL/id --
            // never trust the posted id, only the AreaId just read back
            // off the real, freshly-loaded row.
            if (!_areaAccessService.CanAccessArea(User, CuttingSowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Cutting Sowing record.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }

            return Page();
        }
    }
}
