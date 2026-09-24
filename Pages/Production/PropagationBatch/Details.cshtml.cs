using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PropagationBatch
{
    public class DetailsModel : PageModel
    {
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public DetailsModel(PropagationBatchRepository propagationBatchRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _propagationBatchRepo = propagationBatchRepo;
        }

        public PropagationBatchModel? PropagationBatch { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            PropagationBatch = await _propagationBatchRepo.GetByIdAsync(id);
            if (PropagationBatch == null)
                return RedirectToPage("/Production/PropagationBatch/Index");

            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): block opening another Area's record by id.
            if (!await _areaScope.CanAccessAsync(User, PropagationBatch.MotherPlantId, PropagationBatch.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Propagation Batch record.";
                return RedirectToPage("/Production/PropagationBatch/Index");
            }

            return Page();
        }
    }
}
