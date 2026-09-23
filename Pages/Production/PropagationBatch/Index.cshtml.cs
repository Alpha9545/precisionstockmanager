using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PropagationBatch
{
    public class IndexModel : PageModel
    {
        private readonly PropagationBatchRepository _propagationBatchRepo;

        public IndexModel(PropagationBatchRepository propagationBatchRepo)
        {
            _propagationBatchRepo = propagationBatchRepo;
        }

        public List<PropagationBatchModel> PropagationBatches { get; set; } = new();

        public decimal TotalPlanted => PropagationBatches.Sum(p => p.Quantity);
        public decimal TotalSurvived => PropagationBatches.Sum(p => p.SurvivedQuantity);
        public decimal TotalLoss => PropagationBatches.Sum(p => p.LossQuantity);

        public async Task OnGetAsync()
        {
            PropagationBatches = await _propagationBatchRepo.GetAllAsync();
        }
    }
}
