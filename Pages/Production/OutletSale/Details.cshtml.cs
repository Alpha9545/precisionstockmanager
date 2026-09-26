using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using OutletSaleModel = PlantStockManager.Models.OutletSale;

namespace PlantStockManager.Pages.Production.OutletSale
{
    public class DetailsModel : PageModel
    {
        private readonly OutletSaleRepository _saleRepo;
        private readonly AreaAccessService _areaAccess;

        public DetailsModel(OutletSaleRepository saleRepo, AreaAccessService areaAccess)
        {
            _saleRepo = saleRepo;
            _areaAccess = areaAccess;
        }

        public OutletSaleModel Sale { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var sale = await _saleRepo.GetByIdAsync(id);
            if (sale == null || !_areaAccess.CanAccessArea(User, sale.OutletAreaId))
            {
                TempData["Error"] = "Sale not found, or it belongs to an Outlet you cannot access.";
                return RedirectToPage("/Production/OutletSale/Index");
            }
            Sale = sale;
            return Page();
        }
    }
}
