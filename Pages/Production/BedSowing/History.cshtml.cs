using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.BedSowing
{
    // 2026-10-12: every bed transplant and bed loss (reversed ones included, marked) of the user's
    // Areas, by business date. Read-only.
    public class HistoryModel : PageModel
    {
        private readonly BedSowingRepository _repo;
        private readonly SeedlingAreaScope _scope;

        public HistoryModel(BedSowingRepository repo, SeedlingAreaScope scope)
        {
            _repo = repo;
            _scope = scope;
        }

        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }

        public bool SchemaReady { get; private set; }
        public List<BedSowingOperation> Operations { get; private set; } = new();

        public async Task OnGetAsync()
        {
            From ??= AppClock.Today.AddDays(-30);
            To ??= AppClock.Today;
            SchemaReady = await _repo.SchemaReadyAsync();
            if (SchemaReady)
                Operations = await _repo.GetHistoryAsync(_scope.GetAccessibleAreaIdsOrNull(User), From, To);
        }
    }
}
