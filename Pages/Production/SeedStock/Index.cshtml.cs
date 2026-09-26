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

        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly SeedSourcesRepository _seedSourceRepo;

        public IndexModel(SeedStockRepository seedStockRepo, SeedlingAreaScope areaAccessService,
            PlantTypeRepository plantTypeRepo, SeedSourcesRepository seedSourceRepo)
        {
            _seedStockRepo = seedStockRepo;
            _areaAccessService = areaAccessService;
            _plantTypeRepo = plantTypeRepo;
            _seedSourceRepo = seedSourceRepo;
        }

        // Phase D: 1,000+ varieties -- filter in SQL by variety / crop /
        // colour / supplier / lot instead of one long list.
        public string? Search { get; set; }
        public int? PlantTypeId { get; set; }
        public int? SeedSourceId { get; set; }
        public bool OnlyAvailable { get; set; }
        public List<PlantStockManager.Models.PlantType> PlantTypes { get; set; } = new();
        public List<PlantStockManager.Models.SeedSource> SeedSources { get; set; } = new();

        public List<SeedStockModel> SeedStocks { get; set; } = new();

        public decimal TotalPhysical => SeedStocks.Sum(s => s.PhysicalQuantity);
        public decimal TotalAvailable => SeedStocks.Sum(s => s.AvailableQuantity);

        public async Task OnGetAsync(string? search, int? plantTypeId, int? seedSourceId, bool onlyAvailable = false)
        {
            Search = search;
            PlantTypeId = plantTypeId;
            SeedSourceId = seedSourceId;
            OnlyAvailable = onlyAvailable;
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            SeedSources = await _seedSourceRepo.GetAllSeedSources();
            var all = await _seedStockRepo.SearchAsync(search, plantTypeId, seedSourceId, onlyAvailable, max: 1000);
            SeedStocks = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }
    }
}
