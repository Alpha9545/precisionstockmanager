using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PropagationBatch
{
    public class DetailsModel : PageModel
    {
        private readonly PropagationBatchRepository _propagationBatchRepo;

        public DetailsModel(PropagationBatchRepository propagationBatchRepo)
        {
            _propagationBatchRepo = propagationBatchRepo;
        }

        public PropagationBatchModel? PropagationBatch { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            PropagationBatch = await _propagationBatchRepo.GetByIdAsync(id);
            if (PropagationBatch == null)
                return RedirectToPage("/Production/PropagationBatch/Index");

            return Page();
        }
    }
}
