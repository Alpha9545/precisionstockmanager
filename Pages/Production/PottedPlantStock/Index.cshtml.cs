using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;

namespace PlantStockManager.Pages.Production.PottedPlantStock
{
    public class IndexModel : PageModel
    {
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(PottedPlantStockRepository pottedPlantStockRepo, AreaAccessService areaAccessService)
        {
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _areaAccessService = areaAccessService;
        }

        public List<PottedPlantStockModel> Stocks { get; set; } = new();

        public decimal TotalPhysical => Stocks.Sum(s => s.PhysicalQuantity);
        public decimal TotalReserved => Stocks.Sum(s => s.ReservedQuantity);
        public decimal TotalAvailable => Stocks.Sum(s => s.AvailableQuantity);

        public string? Search { get; set; }
        public bool ShowEmpty { get; set; }

        public async Task OnGetAsync(string? search, bool showEmpty = false)
        {
            Search = search;
            ShowEmpty = showEmpty;
            var all = await _pottedPlantStockRepo.GetAllAsync();
            if (!showEmpty)
                all = all.Where(s => s.PhysicalQuantity > 0).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var t = search.Trim();
                all = all.Where(s => (s.SpeciesName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.PlantTypeName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.SpeciesColor ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || s.PotSize.Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.AreaName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.BatchCodes ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Phase E (spec item 12): a Growing Partner Supervisor must only
            // see PottedPlantStock belonging to Areas they are scoped to.
            // Same in-memory post-filter pattern as MotherPlant/Index
            // (Phase 17/B) -- a full-access user (Admin/Management/
            // MainOfficeOfficer) still sees every row unfiltered.
            Stocks = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }
    }
}
