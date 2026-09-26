using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.Cutting
{
    // Cutting Production register (what was cut, from which Mother Plant, when).
    public class IndexModel : PageModel
    {
        private readonly CuttingProductionRepository _repo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(CuttingProductionRepository repo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _areaAccess = areaAccess;
        }

        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<CuttingProduction> Items { get; set; } = new();

        public async Task OnGetAsync(DateTime? from, DateTime? to)
        {
            To = (to ?? DateTime.Today).Date;
            From = (from ?? To.AddDays(-30)).Date;
            Items = _areaAccess.FilterByArea(User, await _repo.GetAllAsync(From, To), c => (int?)c.AreaId);
        }
    }
}
