using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedStockModel = PlantStockManager.Models.SeedStock;
using SeedStockTransactionModel = PlantStockManager.Models.SeedStockTransaction;

namespace PlantStockManager.Pages.Production.SeedStock
{
    public class DetailsModel : PageModel
    {
        private readonly SeedStockRepository _seedStockRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public DetailsModel(SeedStockRepository seedStockRepo, SeedlingAreaScope areaAccessService)
        {
            _seedStockRepo = seedStockRepo;
            _areaAccessService = areaAccessService;
        }

        public SeedStockModel? SeedStock { get; set; }
        public List<SeedStockTransactionModel> Transactions { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            SeedStock = await _seedStockRepo.GetByIdAsync(id);
            if (SeedStock == null)
                return RedirectToPage("/Production/SeedStock/Index");

            if (!_areaAccessService.CanAccessArea(User, SeedStock.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Seed Stock record.";
                return RedirectToPage("/Production/SeedStock/Index");
            }

            Transactions = await _seedStockRepo.GetTransactionsAsync(id);
            return Page();
        }
    }
}
