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
    // Confirms receipt of a pending 'GrowingPartnerToOutlet' transfer --
    // unlike Cutting's two-step confirm-then-transplant, this finishes the
    // WHOLE transfer in one step (ConfirmGrowingPartnerToOutletAsync both
    // releases InTransit and moves the actual stock), because the
    // destination Outlet Area was already fixed at creation. Mirrors
    // MainOfficeIssue/ConfirmReceipt.cshtml.cs exactly.
    public class ConfirmReceiptModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaAccessService _areaAccessService;

        public ConfirmReceiptModel(InternalTransferRepository internalTransferRepo, AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaAccessService = areaAccessService;
        }

        public InternalTransferModel? Transfer { get; set; }

        [BindProperty]
        public decimal ConfirmedQuantity { get; set; }

        [BindProperty]
        public string? DiscrepancyReason { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (!IsValidPendingReceipt(Transfer))
            {
                TempData["Error"] = "That transfer is not awaiting confirmation.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts");
            }

            // Tampering vector: a valid transfer Id whose destination Area
            // this user was never assigned to (direct URL, Id guessed/
            // incremented) -- this is also what rejects Test 6 (another
            // Outlet's transfer) and Test 7 (the sending Growing Partner
            // Supervisor trying to confirm their own outgoing transfer,
            // since their own AreaAccess claim is for the SOURCE Area, not
            // this DestinationAreaId). Re-checked again on POST below.
            if (!_areaAccessService.CanAccessArea(User, Transfer!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that transfer.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts");
            }

            ConfirmedQuantity = Transfer.Quantity;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            Transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (!IsValidPendingReceipt(Transfer))
            {
                TempData["Error"] = "That transfer is not awaiting confirmation.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts");
            }
            if (!_areaAccessService.CanAccessArea(User, Transfer!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that transfer.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts");
            }

            if (ConfirmedQuantity < 0)
                ModelState.AddModelError(nameof(ConfirmedQuantity), "Confirmed quantity cannot be negative.");
            if (ConfirmedQuantity != Transfer.Quantity && string.IsNullOrWhiteSpace(DiscrepancyReason))
                ModelState.AddModelError(nameof(DiscrepancyReason), "A reason is required when the confirmed quantity differs from the sent quantity.");

            if (!ModelState.IsValid)
                return Page();

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var modifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _internalTransferRepo.ConfirmGrowingPartnerToOutletAsync(id, ConfirmedQuantity, DiscrepancyReason, userId, modifiedBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to confirm receipt.");
                return Page();
            }

            TempData["Success"] = "Receipt confirmed -- stock has been credited to your Outlet.";
            return RedirectToPage("/Production/GrowingPartnerToOutlet/PendingReceipts");
        }

        private static bool IsValidPendingReceipt(InternalTransferModel? t)
            => t != null && t.StockType == "GrowingPartnerToOutlet" && t.Status == "PendingConfirmation";
    }
}
