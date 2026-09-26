using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.InternalTransfer
{
    public class EditModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaAccessService _areaAccessService;

        public EditModel(InternalTransferRepository internalTransferRepo, AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaAccessService = areaAccessService;
        }

        // Phase 20/Phase F (spec item 11: "Cannot: edit another Area's
        // transfer"): true when the current user may access EITHER the
        // Source or Destination Area of a transfer -- symmetric with
        // GetByAreaAsync's own "sender or destination" definition of
        // "belongs to this Area". This is a pre-existing gap dating back
        // to Phase 8 (InternalTransfer/Edit never had ANY Area check, for
        // any StockType) -- closed now because Phase F's own security
        // requirements are specifically about not letting a Growing
        // Partner or Outlet Supervisor edit/cancel a transfer that isn't
        // theirs, and Cancel lives on this generic page for every
        // StockType, including the new GrowingPartnerToOutlet one.
        private bool CanAccessTransfer(InternalTransferModel t)
            // F1: CanAccessArea(null) is "allowed", so a Cutting transfer
            // (DestinationAreaId is NULL until transplanted) was editable
            // and cancellable by EVERY user. Null Areas no longer grant
            // access; the Main Office Area it waits at now counts too.
            => _areaAccessService.CanAccessAnyArea(User, t.SourceAreaId, t.DestinationAreaId, t.PendingConfirmationAreaId);

        // StockType / source / destination / Quantity are permanently
        // immutable -- they already moved real stock. Editing here only
        // touches Responsible Person / Supervisor / Remarks. The only
        // way to undo the stock effect is the explicit Cancel action,
        // which reverses both ledgers atomically
        // (InternalTransferRepository.CancelAsync).
        [BindProperty]
        public InternalTransferModel InternalTransfer { get; set; } = new();


        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _internalTransferRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/InternalTransfer/Index");

            if (!CanAccessTransfer(existing))
            {
                TempData["Error"] = "You are not authorized to view or edit this Internal Transfer record.";
                return RedirectToPage("/Production/InternalTransfer/Index");
            }

            InternalTransfer = existing;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("InternalTransfer.TransferCode");
            ModelState.Remove("InternalTransfer.CreatedBy");
            ModelState.Remove("InternalTransfer.StockType");
            ModelState.Remove("InternalTransfer.SourceEmptyPotInventoryId");
            ModelState.Remove("InternalTransfer.SourcePottedPlantStockId");
            ModelState.Remove("InternalTransfer.SourceAreaId");
            ModelState.Remove("InternalTransfer.DestinationAreaId");
            ModelState.Remove("InternalTransfer.Quantity");
            ModelState.Remove("InternalTransfer.Status");

            var existing = await _internalTransferRepo.GetByIdAsync(InternalTransfer.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Internal Transfer record not found.");
                await LoadDropdownsAsync();
                return Page();
            }

            if (!CanAccessTransfer(existing))
            {
                TempData["Error"] = "You are not authorized to edit this Internal Transfer record.";
                return RedirectToPage("/Production/InternalTransfer/Index");
            }

            if (existing.Status == "Cancelled")
            {
                ModelState.AddModelError(string.Empty, "This Internal Transfer is already Cancelled and cannot be edited further.");
                InternalTransfer = existing;
                await LoadDropdownsAsync();
                return Page();
            }

            InternalTransfer.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _internalTransferRepo.UpdateDetailsAsync(InternalTransfer);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Internal Transfer.");
                InternalTransfer.TransferCode = existing.TransferCode;
                InternalTransfer.StockType = existing.StockType;
                InternalTransfer.PotSize = existing.PotSize;
                InternalTransfer.SpeciesName = existing.SpeciesName;
                InternalTransfer.PlantTypeName = existing.PlantTypeName;
                InternalTransfer.SourceAreaName = existing.SourceAreaName;
                InternalTransfer.DestinationAreaName = existing.DestinationAreaName;
                InternalTransfer.Quantity = existing.Quantity;
                InternalTransfer.Status = existing.Status;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Internal Transfer {existing.TransferCode} updated successfully.";
            return RedirectToPage("/Production/InternalTransfer/Index");
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            // Fetch first, check Area access against the RECORD's own
            // Source/Destination Area (never a posted value) before
            // allowing a reversal that touches two stock pools at once --
            // same pattern as PotProduction/Edit's OnPostCancelAsync
            // (Phase E).
            var target = await _internalTransferRepo.GetByIdAsync(id);
            if (target == null)
            {
                TempData["Error"] = "Internal Transfer record not found.";
                return RedirectToPage("/Production/InternalTransfer/Index");
            }
            if (!CanAccessTransfer(target))
            {
                TempData["Error"] = "You are not authorized to cancel this Internal Transfer record.";
                return RedirectToPage("/Production/InternalTransfer/Index");
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _internalTransferRepo.CancelAsync(id, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Internal Transfer.";
                return RedirectToPage("/Production/InternalTransfer/Edit", new { id });
            }

            TempData["Success"] = "Internal Transfer cancelled. Stock moved back to the source Area.";
            return RedirectToPage("/Production/InternalTransfer/Index");
        }

        private async Task LoadDropdownsAsync()
        {
        }
    }
}
