using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PottedPlantBookingModel = PlantStockManager.Models.PottedPlantBooking;

namespace PlantStockManager.Pages.Production.PottedPlantBooking
{
    // Phase 21/Phase G: added AreaAccessService checks to every verb --
    // Get/Post/Cancel -- closing the same pre-existing "no Area check at
    // all" gap this page has had since Phase 9, mirroring the fix already
    // applied to Pages/Production/InternalTransfer/Edit.cshtml.cs in
    // Phase F for the identical structural reason (a generic Edit/Cancel
    // page that any Area-scoped user could otherwise reach for ANY
    // record by direct id/URL or POST).
    public class EditModel : PageModel
    {
        private readonly PottedPlantBookingRepository _bookingRepo;
        private readonly BookingRepository _legacyBookingRepo; // reused read-only for States/Districts
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public EditModel(
            PottedPlantBookingRepository bookingRepo,
            BookingRepository legacyBookingRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _bookingRepo = bookingRepo;
            _legacyBookingRepo = legacyBookingRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        // PottedPlantStockId/Quantity are permanently immutable -- they
        // already reserved real stock. Editing here only touches
        // customer/delivery/advance/remarks details. The only way to
        // undo the reservation is the explicit Cancel action, which
        // releases it atomically (PottedPlantBookingRepository.CancelAsync).
        [BindProperty]
        public PottedPlantBookingModel Booking { get; set; } = new();

        public List<State> States { get; set; } = new();
        public List<District> Districts { get; set; } = new();
        public List<Employee> BookedByOptions { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _bookingRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/PottedPlantBooking/Index");

            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to view or edit this Booking.";
                return RedirectToPage("/Production/PottedPlantBooking/Index");
            }

            Booking = existing;
            await LoadDropdownsAsync(existing.StateId);
            return Page();
        }

        public async Task<JsonResult> OnGetDistrictsByStateAsync(int stateId)
        {
            var districts = await _legacyBookingRepo.GetByStateAsync(stateId);
            return new JsonResult(districts);
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Booking.BookingCode");
            ModelState.Remove("Booking.CreatedBy");
            ModelState.Remove("Booking.PottedPlantStockId");
            ModelState.Remove("Booking.SpeciesId");
            ModelState.Remove("Booking.PotSize");
            ModelState.Remove("Booking.Quantity");
            ModelState.Remove("Booking.Status");

            var existing = await _bookingRepo.GetByIdAsync(Booking.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Booking not found.");
                await LoadDropdownsAsync(Booking.StateId);
                return Page();
            }

            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Booking.";
                return RedirectToPage("/Production/PottedPlantBooking/Index");
            }

            if (existing.Status == "Cancelled")
            {
                ModelState.AddModelError(string.Empty, "This Booking is already Cancelled and cannot be edited further.");
                Booking = existing;
                await LoadDropdownsAsync(existing.StateId);
                return Page();
            }

            if (Booking.AdvanceTaken && (Booking.AdvanceTakenAmount == null || Booking.AdvanceTakenAmount <= 0))
                ModelState.AddModelError("Booking.AdvanceTakenAmount", "Advance Amount is required when Advance Taken is checked.");
            if (string.IsNullOrWhiteSpace(Booking.CustomerName))
                ModelState.AddModelError("Booking.CustomerName", "Customer Name is required.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync(Booking.StateId);
                return Page();
            }

            Booking.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _bookingRepo.UpdateDetailsAsync(Booking);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Booking.");
                Booking.BookingCode = existing.BookingCode;
                Booking.PottedPlantStockId = existing.PottedPlantStockId;
                Booking.SpeciesId = existing.SpeciesId;
                Booking.SpeciesName = existing.SpeciesName;
                Booking.PlantTypeName = existing.PlantTypeName;
                Booking.PotSize = existing.PotSize;
                Booking.AreaName = existing.AreaName;
                Booking.Quantity = existing.Quantity;
                Booking.Status = existing.Status;
                await LoadDropdownsAsync(Booking.StateId);
                return Page();
            }

            TempData["Success"] = $"Booking {existing.BookingCode} updated successfully.";
            return RedirectToPage("/Production/PottedPlantBooking/Index");
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            // Fetch first, check Area access against the RECORD's own
            // Area (never a posted value) before releasing a reservation.
            var target = await _bookingRepo.GetByIdAsync(id);
            if (target == null)
            {
                TempData["Error"] = "Booking not found.";
                return RedirectToPage("/Production/PottedPlantBooking/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, target.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Booking.";
                return RedirectToPage("/Production/PottedPlantBooking/Index");
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _bookingRepo.CancelAsync(id, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Booking.";
                return RedirectToPage("/Production/PottedPlantBooking/Edit", new { id });
            }

            TempData["Success"] = "Booking cancelled. Reserved stock released back to Available.";
            return RedirectToPage("/Production/PottedPlantBooking/Index");
        }

        private async Task LoadDropdownsAsync(int? stateId)
        {
            States = await _legacyBookingRepo.GetAllStatesAsync();
            Districts = stateId.HasValue ? await _legacyBookingRepo.GetByStateAsync(stateId.Value) : new List<District>();
            BookedByOptions = await _employeeRepo.GetAllActiveUsers();
        }
    }
}
