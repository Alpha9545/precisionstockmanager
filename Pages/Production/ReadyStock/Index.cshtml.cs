using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.ReadyStock
{
    // Phase B: approved Ready Stock, one row per sowing batch, traceable to
    // Batch, Species, Variety, Area, Polyhouse, Sowing Date and Approved By.
    // Read-only (permission ReadyStock.View). Area-scoped with
    // AreaAccessService exactly like every other list page.
    // Phase C: shows Reserved (bookings), Dispatched, Physical and Available.
    public class IndexModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public IndexModel(ReadyStockRepository readyStockRepo, SeedlingAreaScope areaAccessService)
        {
            _readyStockRepo = readyStockRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty(SupportsGet = true)]
        public bool IncludeEmpty { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        public List<ReadyStockModel> Items { get; set; } = new();

        public decimal TotalQuantity => Items.Sum(i => i.PhysicalQuantity);

        public async Task OnGetAsync()
        {
            var all = await _readyStockRepo.GetAllAsync();
            IEnumerable<ReadyStockModel> q = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(r => _areaAccessService.CanAccessArea(User, r.AreaId));

            if (!IncludeEmpty)
                q = q.Where(r => r.PhysicalQuantity > 0);

            if (!string.IsNullOrWhiteSpace(Search))
            {
                var term = Search.Trim();
                q = q.Where(r =>
                    (r.SowingCode ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.SpeciesName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.PlantTypeName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.AreaName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.PolyhouseName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            Items = q.OrderByDescending(r => r.SowingDate).ThenBy(r => r.SowingCode).ToList();
        }
    }
}
