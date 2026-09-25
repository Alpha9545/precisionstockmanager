using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using CuttingSowingModel = PlantStockManager.Models.CuttingSowing;

namespace PlantStockManager.Pages.Production.CuttingSowing
{
    // Phase 5: Area-scoped like every other Production list page -- a
    // user without cross-Area access only ever sees Cutting Sowing
    // records for Areas they are actually assigned to.
    public class IndexModel : PageModel
    {
        private readonly CuttingSowingRepository _cuttingSowingRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(CuttingSowingRepository cuttingSowingRepo, AreaAccessService areaAccessService)
        {
            _cuttingSowingRepo = cuttingSowingRepo;
            _areaAccessService = areaAccessService;
        }

        public List<CuttingSowingModel> Sowings { get; set; } = new();

        public decimal TotalSown => Sowings.Where(s => s.Status != "Cancelled").Sum(s => s.QuantitySown);

        public async Task OnGetAsync()
        {
            var all = await _cuttingSowingRepo.GetAllAsync();

            Sowings = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }
    }
}
