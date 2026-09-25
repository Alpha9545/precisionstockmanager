using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PotProductionBatchModel = PlantStockManager.Models.PotProductionBatch;

namespace PlantStockManager.Pages.Production.PotProductionBatch
{
    public class IndexModel : PageModel
    {
        private readonly PotProductionBatchRepository _batchRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(PotProductionBatchRepository batchRepo, AreaAccessService areaAccessService)
        {
            _batchRepo = batchRepo;
            _areaAccessService = areaAccessService;
        }

        public List<PotProductionBatchModel> Batches { get; set; } = new();

        public async Task OnGetAsync()
        {
            var all = await _batchRepo.GetAllAsync();

            Batches = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(b => _areaAccessService.CanAccessArea(User, b.AreaId)).ToList();
        }
    }
}
