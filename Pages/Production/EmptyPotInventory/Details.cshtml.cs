using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using EmptyPotInventoryModel = PlantStockManager.Models.EmptyPotInventory;
using EmptyPotInventoryTransactionModel = PlantStockManager.Models.EmptyPotInventoryTransaction;

namespace PlantStockManager.Pages.Production.EmptyPotInventory
{
    public class DetailsModel : PageModel
    {
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(EmptyPotInventoryRepository emptyPotInventoryRepo, AreaAccessService areaAccessService)
        {
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _areaAccessService = areaAccessService;
        }

        public EmptyPotInventoryModel? EmptyPotInventory { get; set; }
        public List<EmptyPotInventoryTransactionModel> Transactions { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            EmptyPotInventory = await _emptyPotInventoryRepo.GetByIdAsync(id);
            if (EmptyPotInventory == null)
                return RedirectToPage("/Production/EmptyPotInventory/Index");

            if (!_areaAccessService.CanAccessArea(User, EmptyPotInventory.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Empty Pot Inventory record.";
                return RedirectToPage("/Production/EmptyPotInventory/Index");
            }

            Transactions = await _emptyPotInventoryRepo.GetTransactionsAsync(id);
            return Page();
        }
    }
}
