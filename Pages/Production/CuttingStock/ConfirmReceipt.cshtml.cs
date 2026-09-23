using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
// Alias required: this file's own namespace is nested under
// PlantStockManager.Pages.Production, which also has a sibling
// namespace member named "InternalTransfer" (Pages/Production/
// InternalTransfer/*.cshtml.cs) -- that nested namespace shadows the
// bare "InternalTransfer" simple name ahead of the Models.InternalTransfer
// type imported via the plain "using PlantStockManager.Models;" above,
// producing CS0118 ("is a namespace but is used like a type"). Same
// established fix already used elsewhere in this codebase for the
// identical problem (see PotProduction/CreateFromCutting.cshtml.cs's
// "PotProductionModel" alias, ReadyConfirmation/History.cshtml.cs's/
// ReadyAlerts/Index.cshtml.cs's "SeedSowingModel" alias).
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // Step 1 of 2 of Main Office's confirmation (Model B). Records
    // ConfirmedQuantity/discrepancy only -- no stock moves here. On
    // success this IMMEDIATELY redirects to the Transplant screen, per
    // the approved UX: "the user should not need to search for another
    // page."
    public class ConfirmReceiptModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;

        public ConfirmReceiptModel(InternalTransferRepository internalTransferRepo)
        {
            _internalTransferRepo = internalTransferRepo;
        }

        public InternalTransferModel? Transfer { get; set; }

        [BindProperty]
        public decimal ConfirmedQuantity { get; set; }

        [BindProperty]
        public string? DiscrepancyReason { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (Transfer == null || Transfer.StockType != "Cutting" || Transfer.Status != "PendingConfirmation")
            {
                TempData["Error"] = "That transfer is not awaiting confirmation.";
                return RedirectToPage("/Production/CuttingStock/PendingConfirmations");
            }

            ConfirmedQuantity = Transfer.Quantity;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            Transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (Transfer == null || Transfer.StockType != "Cutting" || Transfer.Status != "PendingConfirmation")
            {
                TempData["Error"] = "That transfer is not awaiting confirmation.";
                return RedirectToPage("/Production/CuttingStock/PendingConfirmations");
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

            var (success, message) = await _internalTransferRepo.ConfirmReceiptAsync(id, ConfirmedQuantity, DiscrepancyReason, userId, modifiedBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to confirm receipt.");
                return Page();
            }

            // Immediately open the Transplant screen -- no extra menu hop.
            return RedirectToPage("/Production/CuttingStock/Transplant", new { id });
        }
    }
}
