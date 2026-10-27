using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // Phase 23 (Phase I): Area-scoped from the start (mirrors every
    // Phase E+ list page's own AreaAccessService wiring) -- a user
    // without cross-Area access only ever sees Sowing records for
    // Areas they are actually assigned to.
    // 2026-10-27: Category (Plant Type) and Source filters (Services/SeedSowingListFilter.cs), validated here and applied in
    // the repository's SQL; the Area scope below is unchanged and still applied to every row.
    public class IndexModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)
        private readonly PlantTypeRepository _plantTypeRepo;

        public IndexModel(SeedSowingRepository seedSowingRepo, SeedlingAreaScope areaAccessService, PlantTypeRepository plantTypeRepo)
        {
            _seedSowingRepo = seedSowingRepo;
            _areaAccessService = areaAccessService;
            _plantTypeRepo = plantTypeRepo;
        }

        // Raw query values; only Filter (after Normalize) is ever used to query.
        [BindProperty(SupportsGet = true, Name = "category")] public int? Category { get; set; }
        [BindProperty(SupportsGet = true, Name = "source")] public string? Source { get; set; }

        public SeedSowingListFilter Filter { get; private set; } = SeedSowingListFilter.None;
        public string? FilterNote { get; private set; }
        public List<PlantType> Categories { get; private set; } = new();

        public List<SeedSowingModel> Sowings { get; set; } = new();

        public decimal TotalSown => Sowings.Where(s => s.Status != "Cancelled").Sum(s => s.QuantitySown);

        public async Task OnGetAsync()
        {
            Categories = (await _plantTypeRepo.GetAllPlantTypes()).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            (Filter, FilterNote) = SeedSowingListFilter.Normalize(Category, Source, Categories.Select(t => t.Id));

            var all = await _seedSowingRepo.GetAllAsync(plantTypeId: Filter.PlantTypeId, sourceType: Filter.SourceType);

            Sowings = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }
    }
}
