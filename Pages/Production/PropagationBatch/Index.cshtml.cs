using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PropagationBatch
{
    public class IndexModel : PageModel
    {
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public IndexModel(PropagationBatchRepository propagationBatchRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _propagationBatchRepo = propagationBatchRepo;
        }

        public List<PropagationBatchModel> PropagationBatches { get; set; } = new();

        public decimal TotalPlanted => PropagationBatches.Sum(p => p.Quantity);
        public decimal TotalSurvived => PropagationBatches.Sum(p => p.SurvivedQuantity);
        public decimal TotalLoss => PropagationBatches.Sum(p => p.LossQuantity);

        public async Task OnGetAsync()
        {
            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): previously every Area's
            // records were listed to every user.
            PropagationBatches = await _areaScope.FilterAsync(User, await _propagationBatchRepo.GetAllAsync(), r => (int?)r.MotherPlantId, r => r.AreaId);
        }
    }
}
