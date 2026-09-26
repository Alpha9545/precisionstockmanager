using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.PotBatch
{
    // Pot production batches with their readiness (Upcoming / Due soon /
    // Due today / Overdue / Ready).
    public class IndexModel : PageModel
    {
        private readonly PotBatchRepository _batchRepo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(PotBatchRepository batchRepo, AreaAccessService areaAccess)
        {
            _batchRepo = batchRepo;
            _areaAccess = areaAccess;
        }

        public List<PotProductionBatch> Batches { get; set; } = new();
        public string Show { get; set; } = "open";

        public async Task OnGetAsync(string? show)
        {
            Show = show == "all" ? "all" : "open";
            var all = _areaAccess.FilterByArea(User, await _batchRepo.GetAllAsync(Show == "open" ? PlantStockManager.Services.PotBatchRules.InProduction : null), b => (int?)b.AreaId);
            Batches = all;
        }
    }
}
