using Microsoft.AspNetCore.Mvc.RazorPages;
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

        public MyTransactionsModel(InternalTransferRepository internalTransferRepo, AreaRepository areaRepo)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
        }

        public List<Area> Areas { get; set; } = new();
        public List<InternalTransferModel> Transactions { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task OnGetAsync(int? areaId)
        {
            SelectedAreaId = areaId;
            Areas = await _areaRepo.GetAllAreas();
            Transactions = areaId.HasValue
                ? (await _internalTransferRepo.GetByAreaAsync(areaId.Value)).Where(t => t.StockType == "Cutting").ToList()
                : new List<InternalTransferModel>();
        }
    }
}
