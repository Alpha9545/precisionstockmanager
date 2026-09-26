using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using DispatchModel = PlantStockManager.Models.Dispatch;

namespace PlantStockManager.Pages.Production.Dispatch
{
    // Phase 21/Phase G: (1) partial dispatch -- Dispatch.Quantity is now a
    // caller-supplied amount (defaulting to the Booking's full remaining
    // quantity client-side, but editable), validated against
    // RemainingQuantity by DispatchRepository.InsertAsync under the
    // Booking row's own lock; (2) Area authorization -- the dropdown only
    // offers Bookings the user can access, and OnPostAsync independently
    // re-fetches the ACTUAL Booking for the posted PottedPlantBookingId
    // and checks CanAccessArea against its real AreaId before calling
    // InsertAsync, exactly mirroring the identical fix in
    // PottedPlantBooking/Create.cshtml.cs (never trust a posted Id).
    public class CreateModel : PageModel
    {
        private readonly DispatchRepository _dispatchRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(DispatchRepository dispatchRepo, AreaAccessService areaAccessService)
        {
            _dispatchRepo = dispatchRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public DispatchModel Dispatch { get; set; } = new();

        // Only Bookings still 'Pending' or 'PartiallyDispatched' (i.e.
        // something remains reserved) AND belonging to an Area the current
        // user can access are offered. Species/PotSize/Area are always
        // derived server-side from the chosen Booking, never trusted from
        // the client; Quantity is the one field the caller DOES supply
        // (the amount to dispatch now, which may be a partial amount) --
        // see DispatchRepository.InsertAsync.
        public List<PlantStockManager.Models.PottedPlantBooking> DispatchableBookings { get; set; } = new();

        public async Task OnGetAsync()
        {
            Dispatch.DispatchDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Dispatch.DispatchCode");
            ModelState.Remove("Dispatch.CreatedBy");
            ModelState.Remove("Dispatch.Status");
            ModelState.Remove("Dispatch.PottedPlantStockId"); // server-derived from the selected Booking
            ModelState.Remove("Dispatch.SpeciesId");           // server-derived
            ModelState.Remove("Dispatch.PotSize");             // server-derived
            ModelState.Remove("Dispatch.AreaId");              // server-derived

            if (Dispatch.PottedPlantBookingId <= 0)
                ModelState.AddModelError("Dispatch.PottedPlantBookingId", "Booking is required.");
            if (Dispatch.Quantity <= 0)
                ModelState.AddModelError("Dispatch.Quantity", "Quantity must be greater than zero.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Never trust the posted PottedPlantBookingId's implied Area
            // -- re-fetch the actual Booking and check its REAL AreaId.
            var targetBooking = await _dispatchRepo.GetDispatchableBookingsAsync();
            var booking = targetBooking.FirstOrDefault(b => b.Id == Dispatch.PottedPlantBookingId);
            if (booking == null)
            {
                ModelState.AddModelError("Dispatch.PottedPlantBookingId", "Selected Booking is no longer dispatchable (already fully Dispatched or Cancelled).");
                await LoadDropdownsAsync();
                return Page();
            }
            if (!_areaAccessService.CanAccessArea(User, booking.AreaId))
            {
                ModelState.AddModelError(string.Empty, "You are not authorized to dispatch stock for the selected Booking's Area.");
                await LoadDropdownsAsync();
                return Page();
            }

            Dispatch.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _dispatchRepo.InsertAsync(Dispatch, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Dispatch.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Dispatch {Dispatch.DispatchCode} recorded successfully. Stock and reservation both released.";
            return RedirectToPage("/Production/Dispatch/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var all = await _dispatchRepo.GetDispatchableBookingsAsync();
            DispatchableBookings = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(b => _areaAccessService.CanAccessArea(User, b.AreaId)).ToList();
        }
    }
}
