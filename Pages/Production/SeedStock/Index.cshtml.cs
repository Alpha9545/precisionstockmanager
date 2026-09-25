using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedStockModel = PlantStockManager.Models.SeedStock;

namespace PlantStockManager.Pages.Production.SeedStock
{
    // Phase 22/Phase H: lists every Seed Stock pool (Main Office's own
    // pools AND every Polyhouse/Growing Area's pool, once seeded by a
    // confirmed Seed Issue) -- Area-scoped from the start, mirroring
    // every stock Index page since Phase B/E (PottedPlantStock/
    // EmptyPotInventory/Index): a full-access user sees everything, an
    // Area-scoped user only sees pools for their own accessible
    // Area(s). Answers spec item 14 ("how much of Variety X seed is
    // available at Polyhouse Y").
    public class IndexModel : PageModel
    {
        private readonly SeedStockRepository _seedStockRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public IndexModel(SeedStockRepository seedStockRepo, SeedlingAreaScope areaAccessService)
        {
            _seedStockRepo = seedStockRepo;
            _areaAccessService = areaAccessService;
        }

        public List<SeedStockModel> SeedStocks { get; set; } = new();

        public decimal TotalPhysical => SeedStocks.Sum(s => s.PhysicalQuantity);
        public decimal TotalAvailable => SeedStocks.Sum(s => s.AvailableQuantity);

        public async Task OnGetAsync()
        {
            var all = await _seedStockRepo.GetAllAsync();
            SeedStocks = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }
    }
}
