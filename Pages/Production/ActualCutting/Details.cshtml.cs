using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using ActualCuttingModel = PlantStockManager.Models.ActualCutting;

namespace PlantStockManager.Pages.Production.ActualCutting
{
    public class DetailsModel : PageModel
    {
        private readonly ActualCuttingRepository _actualCuttingRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public DetailsModel(ActualCuttingRepository actualCuttingRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _actualCuttingRepo = actualCuttingRepo;
        }

        public ActualCuttingModel? ActualCutting { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            ActualCutting = await _actualCuttingRepo.GetByIdAsync(id);
            if (ActualCutting == null)
                return RedirectToPage("/Production/ActualCutting/Index");

            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): block opening another Area's record by id.
            if (!await _areaScope.CanAccessAsync(User, ActualCutting.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to view this Actual Cutting record.";
                return RedirectToPage("/Production/ActualCutting/Index");
            }

            return Page();
        }
    }
}
