using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using PottedPlantStockTransactionModel = PlantStockManager.Models.PottedPlantStockTransaction;

namespace PlantStockManager.Pages.Production.PottedPlantStock
{
    public class DetailsModel : PageModel
    {
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(PottedPlantStockRepository pottedPlantStockRepo, AreaAccessService areaAccessService)
        {
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _areaAccessService = areaAccessService;
        }

        public PottedPlantStockModel? Stock { get; set; }
        public List<PottedPlantStockTransactionModel> Transactions { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Stock = await _pottedPlantStockRepo.GetByIdAsync(id);
            if (Stock == null)
                return RedirectToPage("/Production/PottedPlantStock/Index");

            // Phase E (spec item 12): block viewing another Area's stock by
            // direct URL/id -- the same tampering vector MotherPlant/Details
            // (Phase 17/B) already guards against.
            if (!_areaAccessService.CanAccessArea(User, Stock.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Potted Plant Stock record.";
                return RedirectToPage("/Production/PottedPlantStock/Index");
            }

            Transactions = await _pottedPlantStockRepo.GetTransactionsAsync(id);
            return Page();
        }
    }
}
