using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.BedSowing
{
    // 2026-10-12: every bed sowing of the user's Areas with its lineage figures
    // (Original = Remaining + Transplanted + Lost). Read-only.
    public class IndexModel : PageModel
    {
        private readonly BedSowingRepository _repo;
        private readonly SeedlingAreaScope _scope;

        public IndexModel(BedSowingRepository repo, SeedlingAreaScope scope)
        {
            _repo = repo;
            _scope = scope;
        }

        public bool SchemaReady { get; private set; }
        public string Show { get; private set; } = "open";
        public List<Models.BedSowing> Beds { get; private set; } = new();

        public async Task OnGetAsync(string? show)
        {
            Show = show == "all" ? "all" : "open";
            SchemaReady = await _repo.SchemaReadyAsync();
            if (!SchemaReady)
                return;
            Beds = await _repo.ListAsync(_scope.GetAccessibleAreaIdsOrNull(User), openOnly: Show == "open");
        }
    }
}
