using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using CuttingPlanModel = PlantStockManager.Models.CuttingPlan;

namespace PlantStockManager.Pages.Production.CuttingPlan
{
    public class DetailsModel : PageModel
    {
        private readonly CuttingPlanRepository _cuttingPlanRepo;

        public DetailsModel(CuttingPlanRepository cuttingPlanRepo)
        {
            _cuttingPlanRepo = cuttingPlanRepo;
        }

        public CuttingPlanModel? CuttingPlan { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            CuttingPlan = await _cuttingPlanRepo.GetByIdAsync(id);
            if (CuttingPlan == null)
                return RedirectToPage("/Production/CuttingPlan/Index");

            return Page();
        }
    }
}
