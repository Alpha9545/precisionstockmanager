using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Fertilizer
{
    // FERTILIZER TRANSACTIONS (Correction #8): every fertilizer issue -- Date, Fertilizer, Type, Batch, Quantity, Unit, Source (the
    // supplier of the batch), Received By, Entered By and Remark. Read-only: nothing is written here.
    //
    // Filters are GET query-string values, all applied by the database together (AND) in
    // FertilizerTransactionRepository.SearchAsync. "Clear filters" is a plain link back to the page (no query string).
    // Unknown / malformed values are ignored with a notice; values that are not in the dropdowns are dropped the same way.
    //
    // ACCESS: fertilizer stock and issues have no Area (one shared store), so there is no Area filter and no Area restriction;
    // the page permission (Fertilizer.View, FeatureAuthorizationConventions -- unchanged) is the boundary, and a query-string
    // value can only ever narrow what that permission already shows.
    public class TransactionsModel : PageModel
    {
        private readonly FertilizerTransactionRepository _repo;
        public TransactionsModel(FertilizerTransactionRepository repo) => _repo = repo;

        public List<FertilizerTransactionRow> Rows { get; set; } = new();
        public FertilizerTransactionOptions Options { get; set; } = new();
        public List<string> Notices { get; set; } = new();
        public bool FilterActive { get; set; }

        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public int? FertilizerId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SourceId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Receiver { get; set; }
        [BindProperty(SupportsGet = true)] public string? EnteredBy { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var filter = new FertilizerTransactionFilter
            {
                From = From, To = To, FertilizerId = FertilizerId, SourceId = SourceId, Receiver = Receiver, EnteredBy = EnteredBy
            };
            Notices = filter.Normalize();

            Options = await _repo.GetFilterOptionsAsync();
            Notices.AddRange(filter.RestrictTo(
                Options.Fertilizers.Select(o => int.Parse(o.Value)),
                Options.Sources.Select(o => int.Parse(o.Value)),
                Options.Receivers.Select(o => o.Value),
                Options.EnteredBy.Select(o => o.Value)));

            // what will be searched is what stays on screen
            From = filter.From; To = filter.To; FertilizerId = filter.FertilizerId; SourceId = filter.SourceId;
            Receiver = filter.Receiver; EnteredBy = filter.EnteredBy;
            FilterActive = filter.HasAnyFilter;

            Rows = await _repo.SearchAsync(filter);
            return Page();
        }
    }
}
