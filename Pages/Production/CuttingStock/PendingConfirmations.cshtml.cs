using Microsoft.AspNetCore.Mvc;
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
    // "Main Office Pending Cutting" -- the confirmation queue. Main Office
    // either confirms receipt (-> ConfirmReceipt.cshtml, which immediately
    // opens the Transplant screen) or rejects it outright here.
    public class PendingConfirmationsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;

        public PendingConfirmationsModel(InternalTransferRepository internalTransferRepo, AreaRepository areaRepo)
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
            Pending = await _internalTransferRepo.GetPendingConfirmationsAsync(areaId);
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string reason, int? areaId)
        {
            var (success, message) = await _internalTransferRepo.RejectAsync(id, reason, User.Identity?.Name ?? "System");
            TempData[success ? "Success" : "Error"] = success ? "Transfer rejected." : (message ?? "Failed to reject.");
            return RedirectToPage("/Production/CuttingStock/PendingConfirmations", new { areaId });
        }
    }
}
