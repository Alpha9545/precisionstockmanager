using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using OutletSaleModel = PlantStockManager.Models.OutletSale;

namespace PlantStockManager.Pages.Production.OutletSale
{
    // Outlet Sales History: every completed multi-item Direct Sale,
    // Area-scoped like every other list page.
    public class IndexModel : PageModel
    {
        private readonly OutletSaleRepository _saleRepo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(OutletSaleRepository saleRepo, AreaAccessService areaAccess)
        {
            _saleRepo = saleRepo;
            _areaAccess = areaAccess;
        }

        public List<OutletSaleModel> Sales { get; set; } = new();

        public async Task OnGetAsync()
        {
            var all = await _saleRepo.GetAllAsync();
            Sales = _areaAccess.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccess.CanAccessArea(User, s.OutletAreaId)).ToList();
        }
    }
}
