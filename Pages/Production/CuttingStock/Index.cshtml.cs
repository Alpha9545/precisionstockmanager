using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // Cutting Stock by variety and Area:
    //   Physical   = cuttings held in the Area
    //   In transit = sent to Main Office, not yet confirmed
    //   Available  = Physical - In transit (can be sent, tray-sown or potted)
    public class IndexModel : PageModel
    {
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(CuttingStockRepository cuttingStockRepo, AreaAccessService areaAccess)
        {
            _cuttingStockRepo = cuttingStockRepo;
            _areaAccess = areaAccess;
        }

        public List<CuttingStockModel> Stock { get; set; } = new();
        public string? Search { get; set; }
        public bool ShowEmpty { get; set; }

        public async Task OnGetAsync(string? search, bool showEmpty = false)
        {
            Search = search;
            ShowEmpty = showEmpty;
            var all = _areaAccess.FilterByArea(User, await _cuttingStockRepo.GetAllAsync(), s => (int?)s.AreaId);
            if (!showEmpty)
                all = all.Where(s => s.PhysicalQuantity > 0).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var t = search.Trim();
                all = all.Where(s => (s.SpeciesName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.PlantTypeName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.SpeciesColor ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.AreaName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            Stock = all;
        }
    }
}
