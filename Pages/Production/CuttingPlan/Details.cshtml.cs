using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using CuttingPlanModel = PlantStockManager.Models.CuttingPlan;

namespace PlantStockManager.Pages.Production.CuttingPlan
{
    public class DetailsModel : PageModel
    {
        private readonly CuttingPlanRepository _cuttingPlanRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public DetailsModel(CuttingPlanRepository cuttingPlanRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _cuttingPlanRepo = cuttingPlanRepo;
        }

        public CuttingPlanModel? CuttingPlan { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            CuttingPlan = await _cuttingPlanRepo.GetByIdAsync(id);
            if (CuttingPlan == null)
                return RedirectToPage("/Production/CuttingPlan/Index");

            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): block opening another Area's record by id.
            if (!await _areaScope.CanAccessAsync(User, CuttingPlan.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to view this Cutting Plan record.";
                return RedirectToPage("/Production/CuttingPlan/Index");
            }

            return Page();
        }
    }
}
