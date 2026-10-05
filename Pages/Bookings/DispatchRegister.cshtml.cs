using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Bookings
{
    // Phase C: seedling dispatch history (one row per batch line) with batch,
    // Area, Polyhouse and substitution details.
    //   Read: Dispatch.View | Booking.View | Reports.View
    public class DispatchRegisterModel : PageModel
    {
        private readonly SeedlingFulfilmentRepository _repo;
        private readonly BookingOwnershipService _ownership;

        public DispatchRegisterModel(SeedlingFulfilmentRepository repo, BookingOwnershipService ownership)
        {
            _repo = repo;
            _ownership = ownership;
        }

        // "Own bookings only" users (BookingOwnershipService): only dispatches of their own bookings, in SQL.
        public bool ShowingOwnBookingsOnly { get; set; }

        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public string? Search { get; set; }

        public List<SeedlingDispatchLine> Lines { get; set; } = new();

        public async Task OnGetAsync()
        {
            From ??= DateTime.Today.AddDays(-30);
            To ??= DateTime.Today;
            var owner = _ownership.OwnerFilter(User);
            ShowingOwnBookingsOnly = owner.HasValue;
            Lines = await _repo.GetDispatchRegisterAsync(From.Value, To.Value, Search, owner);
        }
    }
}
