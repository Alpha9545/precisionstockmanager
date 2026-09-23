using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using ActualCuttingModel = PlantStockManager.Models.ActualCutting;

namespace PlantStockManager.Pages.Production.ActualCutting
{
    public class DetailsModel : PageModel
    {
        private readonly ActualCuttingRepository _actualCuttingRepo;

        public DetailsModel(ActualCuttingRepository actualCuttingRepo)
        {
            _actualCuttingRepo = actualCuttingRepo;
        }

        public ActualCuttingModel? ActualCutting { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            ActualCutting = await _actualCuttingRepo.GetByIdAsync(id);
            if (ActualCutting == null)
                return RedirectToPage("/Production/ActualCutting/Index");

            return Page();
        }
    }
}
