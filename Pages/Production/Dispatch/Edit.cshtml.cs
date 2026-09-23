using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using DispatchModel = PlantStockManager.Models.Dispatch;

namespace PlantStockManager.Pages.Production.Dispatch
{
    // Phase 21/Phase G: added AreaAccessService checks to every verb --
    // Get/Post/Cancel -- mirroring the identical fix on
    // PottedPlantBooking/Edit.cshtml.cs and
    // Pages/Production/InternalTransfer/Edit.cshtml.cs (Phase F) for the
    // same structural reason (a generic Edit/Cancel page with no prior
    // Area check at all).
    public class EditModel : PageModel
    {
        private readonly DispatchRepository _dispatchRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public EditModel(DispatchRepository dispatchRepo, EmployeeRepository employeeRepo, AreaAccessService areaAccessService)
        {
            _dispatchRepo = dispatchRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        // PottedPlantBookingId/PottedPlantStockId/Species/PotSize/Area/
        // Quantity are permanently immutable -- they already moved real
        // stock. Editing here only touches Responsible Person/Supervisor/
        // Remarks. The only way to undo the dispatch is the explicit
        // Cancel action, which reverses both ledger entries atomically
        // (DispatchRepository.CancelAsync).
        [BindProperty]
        public DispatchModel Dispatch { get; set; } = new();

        public List<Employee> PersonOptions { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _dispatchRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/Dispatch/Index");

            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to view or edit this Dispatch.";
                return RedirectToPage("/Production/Dispatch/Index");
            }

            Dispatch = existing;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Dispatch.DispatchCode");
            ModelState.Remove("Dispatch.CreatedBy");
            ModelState.Remove("Dispatch.PottedPlantBookingId");
            ModelState.Remove("Dispatch.PottedPlantStockId");
            ModelState.Remove("Dispatch.SpeciesId");
            ModelState.Remove("Dispatch.PotSize");
            ModelState.Remove("Dispatch.Quantity");
            ModelState.Remove("Dispatch.Status");

            var existing = await _dispatchRepo.GetByIdAsync(Dispatch.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Dispatch not found.");
                await LoadDropdownsAsync();
                return Page();
            }

            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Dispatch.";
                return RedirectToPage("/Production/Dispatch/Index");
            }

            if (existing.Status == "Cancelled")
            {
                ModelState.AddModelError(string.Empty, "This Dispatch is already Cancelled and cannot be edited further.");
                Dispatch = existing;
                await LoadDropdownsAsync();
                return Page();
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            Dispatch.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _dispatchRepo.UpdateDetailsAsync(Dispatch);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Dispatch.");
                Dispatch.DispatchCode = existing.DispatchCode;
                Dispatch.PottedPlantBookingId = existing.PottedPlantBookingId;
                Dispatch.PottedPlantStockId = existing.PottedPlantStockId;
                Dispatch.SpeciesId = existing.SpeciesId;
                Dispatch.SpeciesName = existing.SpeciesName;
                Dispatch.PlantTypeName = existing.PlantTypeName;
                Dispatch.PotSize = existing.PotSize;
                Dispatch.AreaName = existing.AreaName;
                Dispatch.Quantity = existing.Quantity;
                Dispatch.Status = existing.Status;
                Dispatch.BookingCode = existing.BookingCode;
                Dispatch.CustomerName = existing.CustomerName;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Dispatch {existing.DispatchCode} updated successfully.";
            return RedirectToPage("/Production/Dispatch/Index");
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            // Fetch first, check Area access against the RECORD's own
            // Area (never a posted value) before reversing a stock
            // movement that touches two pools' worth of ledger entries.
            var target = await _dispatchRepo.GetByIdAsync(id);
            if (target == null)
            {
                TempData["Error"] = "Dispatch not found.";
                return RedirectToPage("/Production/Dispatch/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, target.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Dispatch.";
                return RedirectToPage("/Production/Dispatch/Index");
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _dispatchRepo.CancelAsync(id, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Dispatch.";
                return RedirectToPage("/Production/Dispatch/Edit", new { id });
            }

            TempData["Success"] = "Dispatch cancelled. Physical stock and the reservation for this dispatched amount have both been restored on the Booking.";
            return RedirectToPage("/Production/Dispatch/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            PersonOptions = await _employeeRepo.GetAllActiveUsers();
        }
    }
}
