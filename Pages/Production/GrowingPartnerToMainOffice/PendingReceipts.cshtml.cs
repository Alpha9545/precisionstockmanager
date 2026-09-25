using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.GrowingPartnerToMainOffice
{
    // "Main Office Pending Receipts" -- the confirmation queue on the
    // RECEIVING side (Phase 32/Phase 7). A Main Office Area's supervisor
    // either confirms receipt (-> ConfirmReceipt.cshtml, which finishes
    // the whole transfer in one step -- no separate Transplant stage) or
    // rejects it outright here. Mirrors
    // GrowingPartnerToOutlet/PendingReceipts exactly, filtered to Main
    // Office Areas / this StockType instead.
    public class PendingReceiptsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public PendingReceiptsModel(
            InternalTransferRepository internalTransferRepo,
            AreaRepository areaRepo,
            AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
            _areaAccessService = areaAccessService;
        }

        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<InternalTransferModel> Pending { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view receipts for the selected Area.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts");
            }

            SelectedAreaId = areaId;

            var allMainOfficeAreas = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            MainOfficeAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allMainOfficeAreas
                : allMainOfficeAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            if (areaId.HasValue)
            {
                Pending = await _internalTransferRepo.GetPendingGrowingPartnerToMainOfficeAsync(areaId);
            }
            else if (_areaAccessService.HasFullAreaAccess(User))
            {
                Pending = await _internalTransferRepo.GetPendingGrowingPartnerToMainOfficeAsync(null);
            }
            else
            {
                var accessibleIds = _areaAccessService.GetAccessibleAreaIds(User);
                var all = await _internalTransferRepo.GetPendingGrowingPartnerToMainOfficeAsync(null);
                Pending = all.Where(t => t.DestinationAreaId.HasValue && accessibleIds.Contains(t.DestinationAreaId.Value)).ToList();
            }

            return Page();
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string reason, int? areaId)
        {
            var transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (transfer == null || transfer.StockType != "GrowingPartnerToMainOffice" || !_areaAccessService.CanAccessArea(User, transfer.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to reject that transfer.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts", new { areaId });
            }

            var (success, message) = await _internalTransferRepo.RejectAsync(id, reason, User.Identity?.Name ?? "System");
            TempData[success ? "Success" : "Error"] = success ? "Transfer rejected." : (message ?? "Failed to reject.");
            return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts", new { areaId });
        }
    }
}
