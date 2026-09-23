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

namespace PlantStockManager.Pages.Production.MainOfficeIssue
{
    // "Growing Partner Pending Receipts" -- the confirmation queue on the
    // RECEIVING side (Phase 18 / Phase C). A Growing Partner Area's
    // supervisor either confirms receipt (-> ConfirmReceipt.cshtml, which
    // finishes the whole transfer in one step -- no separate Transplant
    // stage, unlike Cutting) or rejects it outright here.
    //
    // Area-scoped like MotherPlant/Index (Phase B): a user without
    // cross-Area access only ever sees pending receipts for Areas they are
    // actually assigned to, never every Growing Partner's queue by
    // default.
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

        public List<Area> PartnerAreas { get; set; } = new();
        public List<InternalTransferModel> Pending { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view receipts for the selected Area.";
                return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts");
            }

            SelectedAreaId = areaId;

            var allPartnerAreas = (await _areaRepo.GetAllAreas()).Where(a => a.GrowingPartnerId.HasValue).ToList();
            PartnerAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allPartnerAreas
                : allPartnerAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            if (areaId.HasValue)
            {
                Pending = await _internalTransferRepo.GetPendingMainOfficeIssuesAsync(areaId);
            }
            else if (_areaAccessService.HasFullAreaAccess(User))
            {
                Pending = await _internalTransferRepo.GetPendingMainOfficeIssuesAsync(null);
            }
            else
            {
                // No single Area picked and no cross-Area access -- only
                // ever show what this user's own accessible Area(s)
                // actually hold, mirroring MotherPlant/Index's in-memory
                // filter rather than trusting the (absent) areaId alone.
                var accessibleIds = _areaAccessService.GetAccessibleAreaIds(User);
                var all = await _internalTransferRepo.GetPendingMainOfficeIssuesAsync(null);
                Pending = all.Where(t => t.DestinationAreaId.HasValue && accessibleIds.Contains(t.DestinationAreaId.Value)).ToList();
            }

            return Page();
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string reason, int? areaId)
        {
            // Re-verify against the transfer's ACTUAL destination Area
            // before rejecting -- catches a tampered transfer Id (e.g.
            // someone else's pending receipt) even if the form's own
            // areaId field was left alone.
            var transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (transfer == null || transfer.StockType != "MainOfficeIssue" || !_areaAccessService.CanAccessArea(User, transfer.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to reject that transfer.";
                return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts", new { areaId });
            }

            var (success, message) = await _internalTransferRepo.RejectAsync(id, reason, User.Identity?.Name ?? "System");
            TempData[success ? "Success" : "Error"] = success ? "Transfer rejected." : (message ?? "Failed to reject.");
            return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts", new { areaId });
        }
    }
}
