using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
// Alias required: this file's own namespace is nested under
// PlantStockManager.Pages.Production, which also has a sibling
// namespace member named "InternalTransfer" (Pages/Production/
// InternalTransfer/*.cshtml.cs) -- that nested namespace shadows the
// bare "InternalTransfer" simple name ahead of the Models.InternalTransfer
// type imported via the plain "using PlantStockManager.Models;" above,
// producing CS0118 ("is a namespace but is used like a type"). Same
// established fix already used elsewhere in this codebase (see
// PotProduction/CreateFromCutting.cshtml.cs's "PotProductionModel" alias,
// ReadyConfirmation/History.cshtml.cs's/ReadyAlerts/Index.cshtml.cs's
// "SeedSowingModel" alias).
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // Step 1 of 2 of Main Office's confirmation (Model B). Phase 4: the
    // ONLY quantity input is Actual Ready Trays -- Actual Seedlings
    // (ConfirmedQuantity) and Wastage are always derived server-side from
    // the transfer's own stored CavityType/NumberOfTrays/Quantity via
    // DirectSowingRules.ComputeTrayApproval, exactly mirroring Ready
    // Confirmation's approval-by-trays (Pages/Production/ReadyConfirmation/
    // Confirm.cshtml.cs) -- nothing the browser computes is ever trusted.
    // On success this IMMEDIATELY redirects to the Transplant screen, per
    // the approved UX: "the user should not need to search for another
    // page."
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

        // The confirming Main Office/Sowing Supervisor's ONLY quantity
        // input: complete trays actually ready. Seedlings, wastage and
        // cavity are never posted -- they are derived on the server from
        // the stored transfer (DirectSowingRules.ComputeTrayApproval).
        // Bound as decimal so a fractional value is rejected with a clear
        // message, not a silent binding failure.
        [BindProperty]
        public decimal ActualReadyTrays { get; set; }

        // Required whenever Wastage (= Quantity sent - Actual Seedlings) > 0.
        [BindProperty]
        public string? WastageReason { get; set; }

        public IReadOnlyList<string> WastageReasons => DirectSowingRules.WastageReasons;

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

            ActualReadyTrays = Transfer.NumberOfTrays ?? 0;
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

            // Same rule the repository re-applies under lock (cavity and
            // trays sent come from the stored transfer, never from the form).
            var (ok, _, actualSeedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                Transfer.Quantity, Transfer.NumberOfTrays, Transfer.CavityType, alreadyReady: 0, alreadyWasted: 0, ActualReadyTrays, WastageReason);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, error!);
                return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var modifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _internalTransferRepo.ConfirmReceiptAsync(id, ActualReadyTrays, WastageReason, userId, modifiedBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to confirm receipt.");
                return Page();
            }

            TempData["Success"] = $"Confirmed {ActualReadyTrays:N0} trays x {Transfer.CavityType} = {actualSeedlings:N0} cuttings received; wastage {wastage:N0} ({wastagePct:0.00}%).";
            // Immediately open the Transplant screen -- no extra menu hop.
            return RedirectToPage("/Production/CuttingStock/Transplant", new { id });
        }

        // Live preview for the confirmation form. Takes ONLY the transfer
        // id (route) and a tray count; the cavity, trays sent and
        // quantity sent are read from the stored transfer -- the browser
        // cannot supply them.
        public async Task<JsonResult> OnGetTrayPreviewAsync(int id, decimal trays)
        {
            var transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (transfer == null || !IsAuthorizedFor(transfer))
                return new JsonResult(new { ok = false, seedlings = (decimal?)null, wastage = 0m, wastagePercent = 0m, error = "Transfer not found." });
            // The reason does not change the numbers; a valid placeholder keeps
            // the preview from reporting "reason required" before one is chosen.
            var (ok, _, seedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                transfer.Quantity, transfer.NumberOfTrays, transfer.CavityType, alreadyReady: 0, alreadyWasted: 0, trays, DirectSowingRules.WastageReasons[0]);
            return new JsonResult(new { ok, seedlings = ok ? seedlings : (decimal?)null, wastage, wastagePercent = wastagePct, error });
        }
    }
}
