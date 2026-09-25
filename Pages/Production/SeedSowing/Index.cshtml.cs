using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // Phase 23 (Phase I): Area-scoped from the start (mirrors every
    // Phase E+ list page's own AreaAccessService wiring) -- a user
    // without cross-Area access only ever sees Sowing records for
    // Areas they are actually assigned to.
    public class IndexModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public IndexModel(SeedSowingRepository seedSowingRepo, SeedlingAreaScope areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _areaAccessService = areaAccessService;
        }

        public List<SeedSowingModel> Sowings { get; set; } = new();

        public decimal TotalSown => Sowings.Where(s => s.Status != "Cancelled").Sum(s => s.QuantitySown);

        public async Task OnGetAsync()
        {
            var all = await _seedSowingRepo.GetAllAsync();

            Sowings = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }
    }
}
