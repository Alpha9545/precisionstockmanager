using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Bookings
{
    // Phase C: one seedling booking (dbo.Bookings) with its Ready Stock
    // reservation, batch allocations, dispatch history and revision history.
    //   Read : Booking.View | Dispatch.View
    //   Write: Booking.Enter  (Reserve, Release reservation, Cancel)
    // (FeatureAuthorizationConventions -- System Administrator has full access.)
    public class BookingDetailsModel : PageModel
    {
        private readonly SeedlingFulfilmentRepository _repo;

        public BookingDetailsModel(SeedlingFulfilmentRepository repo) => _repo = repo;

        [BindProperty(SupportsGet = true)]
        public int Id { get; set; }

        [BindProperty]
        public int ReserveQuantity { get; set; }

        [BindProperty]
        public string? CancelReason { get; set; }

        public SeedlingBookingSummary? Booking { get; set; }
        public ReadyStockAvailability? Availability { get; set; }
        public List<BookingBatchAllocation> Allocations { get; set; } = new();
        public List<SeedlingDispatch> Dispatches { get; set; } = new();
        public List<BookingRevision> Revisions { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            if (!await LoadAsync())
            {
                TempData["Error"] = "Booking not found.";
                return RedirectToPage("/Bookings/Fulfilment");
            }
            return Page();
        }

        private SeedlingFulfilmentRepository.Actor Actor => new(User.Identity?.Name, User.GetUserId());

        public async Task<IActionResult> OnPostReserveAsync()
        {
            var (ok, message) = await _repo.ReserveAsync(Id, ReserveQuantity, Actor);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        public async Task<IActionResult> OnPostReleaseAllAsync()
        {
            var (ok, message) = await _repo.ReleaseAllAsync(Id, Actor);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        public async Task<IActionResult> OnPostCancelAsync()
        {
            var (ok, message) = await _repo.CancelAsync(Id, CancelReason, Actor);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        private async Task<bool> LoadAsync()
        {
            Booking = await _repo.GetBookingAsync(Id);
            if (Booking == null) return false;
            if (Booking.SpeciesId.HasValue)
                Availability = await _repo.GetAvailabilityAsync(Booking.SpeciesId.Value);
            Allocations = await _repo.GetAllocationsAsync(Id);
            Dispatches = await _repo.GetDispatchesAsync(Id);
            Revisions = await _repo.GetRevisionsAsync(Id);
            ReserveQuantity = (int)Math.Min(Booking.UnreservedQuantity, Availability?.AvailableQuantity ?? 0);
            return true;
        }
    }
}
