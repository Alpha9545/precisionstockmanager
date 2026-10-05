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
        private readonly BookingOwnershipService _ownership;

        public BookingDetailsModel(SeedlingFulfilmentRepository repo, BookingOwnershipService ownership)
        {
            _repo = repo;
            _ownership = ownership;
        }

        // "Own bookings only" users: another user's booking is answered as "not found".
        private async Task<IActionResult?> RejectIfNotOwnedAsync()
        {
            if (await _ownership.CanAccessBookingIdAsync(User, Id, _repo))
                return null;
            TempData["Error"] = BookingOwnershipService.NotFoundMessage;
            return RedirectToPage("/Bookings/Fulfilment");
        }

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

        // The whole customer order this line belongs to (Booking #OrderId): every
        // variety line with its own quantities and status; the selected line (Id) is
        // the one the Reserve / Release / Cancel / Revise actions below work on.
        public List<SeedlingBookingSummary> OrderLines { get; set; } = new();
        public Dictionary<int, ReadyStockAvailability> AvailabilityBySpecies { get; set; } = new();

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
            if (await RejectIfNotOwnedAsync() is { } rejected) return rejected;
            var (ok, message) = await _repo.ReserveAsync(Id, ReserveQuantity, Actor);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        public async Task<IActionResult> OnPostReleaseAllAsync()
        {
            if (await RejectIfNotOwnedAsync() is { } rejected) return rejected;
            var (ok, message) = await _repo.ReleaseAllAsync(Id, Actor);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        public async Task<IActionResult> OnPostCancelAsync()
        {
            if (await RejectIfNotOwnedAsync() is { } rejected) return rejected;
            var (ok, message) = await _repo.CancelAsync(Id, CancelReason, Actor);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        private async Task<bool> LoadAsync()
        {
            Booking = await _repo.GetBookingAsync(Id);
            if (Booking == null || !_ownership.CanAccessBooking(User, Booking.OrderBookedById))
            {
                Booking = null;
                return false;
            }
            if (Booking.SpeciesId.HasValue)
                Availability = await _repo.GetAvailabilityAsync(Booking.SpeciesId.Value);
            Allocations = await _repo.GetAllocationsAsync(Id);
            Dispatches = await _repo.GetDispatchesAsync(Id);
            Revisions = await _repo.GetRevisionsAsync(Id);
            OrderLines = await _repo.GetOrderLinesAsync(Booking.OrderId);
            foreach (var speciesId in OrderLines.Where(l => l.SpeciesId.HasValue).Select(l => l.SpeciesId!.Value).Distinct())
                AvailabilityBySpecies[speciesId] = speciesId == Booking.SpeciesId && Availability != null
                    ? Availability
                    : await _repo.GetAvailabilityAsync(speciesId);
            ReserveQuantity = (int)Math.Min(Booking.UnreservedQuantity, Availability?.AvailableQuantity ?? 0);
            return true;
        }
    }
}
