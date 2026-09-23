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

namespace PlantStockManager.Pages.Production.GrowingPartnerToOutlet
{
    // "Outlet Pending Receipts" -- the confirmation queue on the RECEIVING
    // side (Phase 20 / Phase F). An Outlet Area's supervisor either
    // confirms receipt (-> ConfirmReceipt.cshtml, which finishes the whole
    // transfer in one step -- no separate Transplant stage) or rejects it
    // outright here. Mirrors MainOfficeIssue/PendingReceipts exactly,
    // filtered to Outlet Areas / this StockType instead.
    //
    // Area-scoped like MotherPlant/Index (Phase B): a user without
    // cross-Area access only ever sees pending receipts for Outlet Area(s)
    // they are actually assigned to -- an Outlet Supervisor never sees
    // another Outlet's queue by default, and a Growing Partner Supervisor
    // (who has no AreaAccess claim for any Outlet Area) sees nothing here
    // at all, which is exactly the sender/receiver separation spec item 12
    // requires.
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

        public List<Area> OutletAreas { get; set; } = new();
        public List<InternalTransferModel> Pending { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view receipts for the selected Area.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts");
            }

            SelectedAreaId = areaId;

            var allOutletAreas = await _areaRepo.GetByAreaTypesAsync("Outlet");
            OutletAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allOutletAreas
                : allOutletAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            if (areaId.HasValue)
            {
                Pending = await _internalTransferRepo.GetPendingGrowingPartnerToOutletAsync(areaId);
            }
            else if (_areaAccessService.HasFullAreaAccess(User))
            {
                Pending = await _internalTransferRepo.GetPendingGrowingPartnerToOutletAsync(null);
            }
            else
            {
                // No single Area picked and no cross-Area access -- only
                // ever show what this user's own accessible Outlet
                // Area(s) actually hold, mirroring MotherPlant/Index's
                // in-memory filter rather than trusting the (absent)
                // areaId alone.
                var accessibleIds = _areaAccessService.GetAccessibleAreaIds(User);
                var all = await _internalTransferRepo.GetPendingGrowingPartnerToOutletAsync(null);
                Pending = all.Where(t => t.DestinationAreaId.HasValue && accessibleIds.Contains(t.DestinationAreaId.Value)).ToList();
            }

            return Page();
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string reason, int? areaId)
        {
            // Re-verify against the transfer's ACTUAL destination Area
            // before rejecting -- catches a tampered transfer Id (e.g.
            // someone else's pending receipt) even if the form's own
            // areaId field was left alone. This is also what rejects Test
            // 6 (an Outlet Supervisor trying to confirm/reject another
            // Outlet's transfer) and Test 7 (a Growing Partner Supervisor
            // trying to act on their own outgoing transfer, since their
            // AreaAccess claim never covers the destination Outlet Area).
            var transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (transfer == null || transfer.StockType != "GrowingPartnerToOutlet" || !_areaAccessService.CanAccessArea(User, transfer.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to reject that transfer.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts", new { areaId });
            }

            var (success, message) = await _internalTransferRepo.RejectAsync(id, reason, User.Identity?.Name ?? "System");
            TempData[success ? "Success" : "Error"] = success ? "Transfer rejected." : (message ?? "Failed to reject.");
            return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts", new { areaId });
        }
    }
}
