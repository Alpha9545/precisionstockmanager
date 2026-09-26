using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
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
    // Phase D: Main Office confirms what actually arrived. One step: the
    // received cuttings become Main Office Cutting Stock and any shortfall is
    // recorded as transit loss (InternalTransferRepository.ConfirmReceiptAsync).
    public class ConfirmReceiptModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaAccessService _areaAccessService;

        public ConfirmReceiptModel(InternalTransferRepository internalTransferRepo, AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaAccessService = areaAccessService;
        }

        // F1: only a user assigned to the Main Office Area the transfer is
        // waiting at (or a cross-Area role) may view/confirm it -- the
        // transfer id was previously the only input.
        private bool IsAuthorizedFor(InternalTransferModel transfer)
            => _areaAccessService.CanAccessRequiredArea(User, transfer.PendingConfirmationAreaId);

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
            if (!IsAuthorizedFor(Transfer))
            {
                TempData["Error"] = "You are not authorized to confirm that transfer.";
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
            if (!IsAuthorizedFor(Transfer))
            {
                TempData["Error"] = "You are not authorized to confirm that transfer.";
                return RedirectToPage("/Production/CuttingStock/PendingConfirmations");
            }

            var (valid, _, validationError) = PlantStockManager.Services.CuttingRules.ConfirmDelivery(Transfer.Quantity, ConfirmedQuantity, DiscrepancyReason);
            if (!valid)
                ModelState.AddModelError(string.Empty, validationError!);

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

            var loss = Transfer.Quantity - ConfirmedQuantity;
            TempData["Success"] = $"Delivery {Transfer.TransferCode} confirmed: {ConfirmedQuantity:N0} cuttings added to Main Office Cutting Stock"
                + (loss > 0 ? $"; {loss:N0} recorded as transit loss." : ".");
            return RedirectToPage("/Production/CuttingStock/PendingConfirmations");
        }
    }
}
