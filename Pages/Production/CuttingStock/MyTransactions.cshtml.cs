using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
// Alias required: Pages/Production/InternalTransfer/ makes "InternalTransfer"
// a sibling namespace under PlantStockManager.Pages.Production, which
// shadows the bare Models.InternalTransfer type (CS0118). Same fix already
// used elsewhere in this codebase (see PotProduction/CreateFromCutting.cshtml.cs).
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // "My Transactions" -- every Cutting-type Internal Transfer where the
    // selected Area is either the sender or the (eventual) destination,
    // across the whole lifecycle (PendingConfirmation, Rejected,
    // ConfirmedAwaitingTransplant, Transplanted).
    public class MyTransactionsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public MyTransactionsModel(InternalTransferRepository internalTransferRepo, AreaRepository areaRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
        }

        public List<Area> Areas { get; set; } = new();
        public List<InternalTransferModel> Transactions { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            // F1: any Area's transaction history was readable by URL.
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view transactions for the selected Area.";
                return RedirectToPage("/Production/CuttingStock/MyTransactions");
            }

            SelectedAreaId = areaId;
            Areas = _areaAccessService.FilterByArea(User, await _areaRepo.GetAllAreas(), a => (int?)a.Id);
            Transactions = areaId.HasValue
                ? (await _internalTransferRepo.GetByAreaAsync(areaId.Value)).Where(t => t.StockType == "Cutting").ToList()
                : new List<InternalTransferModel>();
            return Page();
        }
    }
}
