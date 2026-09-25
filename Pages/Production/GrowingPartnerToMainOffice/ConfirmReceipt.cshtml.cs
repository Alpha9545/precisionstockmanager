using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.GrowingPartnerToMainOffice
{
    // Confirms receipt of a pending 'GrowingPartnerToMainOffice' transfer
    // -- finishes the WHOLE transfer in one step
    // (ConfirmGrowingPartnerToMainOfficeAsync both releases InTransit and
    // moves the actual stock), because the destination Main Office Area
    // was already fixed at creation. Mirrors
    // GrowingPartnerToOutlet/ConfirmReceipt.cshtml.cs exactly.
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
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts");
            }

            if (!_areaAccessService.CanAccessArea(User, Transfer!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that transfer.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts");
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
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts");
            }
            if (!_areaAccessService.CanAccessArea(User, Transfer!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that transfer.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts");
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

            var (success, message) = await _internalTransferRepo.ConfirmGrowingPartnerToMainOfficeAsync(id, ConfirmedQuantity, DiscrepancyReason, userId, modifiedBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to confirm receipt.");
                return Page();
            }

            TempData["Success"] = "Receipt confirmed -- stock has been credited to Main Office.";
            return RedirectToPage("/Production/GrowingPartnerToMainOffice/PendingReceipts");
        }

        private static bool IsValidPendingReceipt(InternalTransferModel? t)
            => t != null && t.StockType == "GrowingPartnerToMainOffice" && t.Status == "PendingConfirmation";
    }
}
