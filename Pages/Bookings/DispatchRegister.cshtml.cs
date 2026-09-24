using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
        public DispatchRegisterModel(SeedlingFulfilmentRepository repo) => _repo = repo;

        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public string? Search { get; set; }

        public List<SeedlingDispatchLine> Lines { get; set; } = new();

        public async Task OnGetAsync()
        {
            From ??= DateTime.Today.AddDays(-30);
            To ??= DateTime.Today;
            Lines = await _repo.GetDispatchRegisterAsync(From.Value, To.Value, Search);
        }
    }
}
