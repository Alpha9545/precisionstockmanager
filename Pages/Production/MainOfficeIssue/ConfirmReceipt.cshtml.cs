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
    // Confirms receipt of a pending 'MainOfficeIssue' transfer -- unlike
    // Cutting's two-step confirm-then-transplant, this finishes the WHOLE
    // transfer in one step (ConfirmMainOfficeIssueAsync both releases
    // InTransit and moves the actual stock), because the destination Area
    // was already fixed at creation.
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
                return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts");
            }

            // Tampering vector: a valid transfer Id whose destination Area
            // this user was never assigned to (direct URL, Id guessed/
            // incremented). Re-checked again on POST below.
            if (!_areaAccessService.CanAccessArea(User, Transfer!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that transfer.";
                return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts");
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
                return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts");
            }
            if (!_areaAccessService.CanAccessArea(User, Transfer!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that transfer.";
                return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts");
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

            var (success, message) = await _internalTransferRepo.ConfirmMainOfficeIssueAsync(id, ConfirmedQuantity, DiscrepancyReason, userId, modifiedBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to confirm receipt.");
                return Page();
            }

            TempData["Success"] = "Receipt confirmed -- stock has been credited to your Area.";
            return RedirectToPage("/Production/MainOfficeIssue/PendingReceipts");
        }

        private static bool IsValidPendingReceipt(InternalTransferModel? t)
            => t != null && t.StockType == "MainOfficeIssue" && t.Status == "PendingConfirmation";
    }
}
