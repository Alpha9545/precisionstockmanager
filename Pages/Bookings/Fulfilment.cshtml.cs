using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Bookings
{
    // Phase C: booking fulfilment report -- booked / reserved / dispatched /
    // remaining, status + detailed stage, fulfilment pipeline. Historical
    // bookings (legacy Inventory fulfilment) are shown read-only.
    //   Read: Booking.View | Dispatch.View | Reports.View
    public class FulfilmentModel : PageModel
    {
        private readonly SeedlingFulfilmentRepository _repo;
        public FulfilmentModel(SeedlingFulfilmentRepository repo) => _repo = repo;

        [BindProperty(SupportsGet = true)] public string? Status { get; set; } = "Pending";
        [BindProperty(SupportsGet = true)] public string? Search { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public string? Stage { get; set; }

        public List<SeedlingBookingSummary> Bookings { get; set; } = new();
        public List<string> Stages { get; set; } = new();

        public async Task OnGetAsync()
        {
            var status = string.IsNullOrWhiteSpace(Status) || Status == "All" ? null : Status;
            var list = await _repo.ListBookingsAsync(status, Search, From, To, null);
            Stages = list.Select(b => b.Stage).Distinct().OrderBy(s => s).ToList();
            Bookings = string.IsNullOrWhiteSpace(Stage) ? list : list.Where(b => b.Stage == Stage).ToList();
        }
    }
}
