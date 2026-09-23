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
    // "Pending Transplants" -- Cutting transfers whose receipt Main
    // Office has confirmed but which have not yet been routed to a
    // Destination Polyhouse. A transfer sits here for however long it
    // takes; it is never dropped and never silently counted as done.
    public class PendingTransplantsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;

        public PendingTransplantsModel(InternalTransferRepository internalTransferRepo, AreaRepository areaRepo)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
        }

        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<InternalTransferModel> Pending { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task OnGetAsync(int? areaId)
        {
            SelectedAreaId = areaId;
            MainOfficeAreas = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            Pending = await _internalTransferRepo.GetPendingTransplantsAsync(areaId);
        }
    }
}
